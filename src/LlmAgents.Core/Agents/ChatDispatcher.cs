using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using LlmAgents.Llm;

namespace LlmAgents.Agents;

/// <summary>A chat message from a human player, with the bots that could actually hear it.</summary>
public sealed record ChatCommand(string SenderName, string? SenderJob, string Text, IReadOnlyList<int> ListenerIds, bool ViaRadio, string? SenderRoom = null);

/// <summary>One bot as the dispatcher sees it.</summary>
public sealed record RosterEntry(int Id, string Name, string Job, string? Room, string CurrentOrder);

/// <summary>
/// Step 1 of a chat order: an LLM decides which AI crew members the message is meant for.
/// Step 2 (in AgentManager): every recipient gets the order and handles it with its own LLM call.
/// </summary>
public static class ChatDispatcher
{
    public const string ToolName = "route_message";

    public static readonly ToolSpec Tool = new(ToolName,
        "Choose which AI crew members the player's message is meant for.",
        new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["recipients"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject { ["type"] = "string" },
                    ["description"] = "Exact names from the roster. Empty if the message is not addressed to the AI crew.",
                },
                ["reason"] = new JsonObject { ["type"] = "string", ["description"] = "Very short explanation" },
            },
            ["required"] = new JsonArray("recipients"),
        });

    public const string SystemPrompt = @"You are the communications officer of a submarine crew in the game Barotrauma.
A human player wrote a message in the chat. Decide which AI crew members (from the roster) it is meant for, and call route_message.

Rules:
- The message can be in any language (often Russian). Names may be shortened, declined or misspelled.
- Addressed by name -> those people.
- Addressed by job (e.g. 'mechanics', 'механики', 'инженер', 'доктор', 'security', 'охрана', 'ассистенты') -> every roster member with that job.
  Jobs: captain = капитан; engineer = инженер; mechanic = механик; securityofficer = охрана/офицер безопасности; medicaldoctor = медик/врач/доктор; assistant = ассистент/помощник.
- Singular job ('mechanic, fix the pump' / 'механик, почини') when several share that job -> pick the single most suitable one (closest to the task, or idle).
- 'everyone', 'all', 'crew', 'все', 'экипаж', 'всем' -> everybody in the roster.
- A plural command with no addressee (Russian verbs ending in -те: 'разберите', 'идите', 'несите'; 'guys', 'ребята', 'все за мной')
  -> everybody, or everybody whose job fits the task.
- The sender is a human player and is never in the roster. 'me', 'мне', 'ко мне', 'за мной' mean the sender.
- A greeting or small talk to everyone ('привет всем', 'hi all') -> only ONE crew member answers (the captain if present).
- 'someone' / 'кто-нибудь' or an order with no addressee -> the single most suitable crew member for the task (right job, nearby, not busy).
- Plain chatter, jokes, or messages clearly for other humans -> empty recipients.
- Only use names that are in the roster.";

    public static string UserPrompt(ChatCommand cmd, IReadOnlyList<RosterEntry> roster)
    {
        var sb = new StringBuilder();
        sb.Append("Message from ").Append(cmd.SenderName);
        if (!string.IsNullOrEmpty(cmd.SenderJob)) sb.Append(" (").Append(cmd.SenderJob).Append(')');
        sb.Append(cmd.ViaRadio ? " over the radio" : " out loud").Append(":\n\"").Append(cmd.Text).Append("\"\n\nRoster (AI crew who heard it):\n");
        foreach (var r in roster)
            sb.Append("- ").Append(r.Name).Append(" | job: ").Append(r.Job)
              .Append(" | room: ").Append(r.Room ?? "outside").Append(" | current order: ").Append(r.CurrentOrder).Append('\n');
        return sb.ToString();
    }

    /// <summary>Maps the LLM answer to roster entries. Returns null if the answer had no usable route_message call.</summary>
    public static List<RosterEntry>? ParseRecipients(LlmResponse? response, IReadOnlyList<RosterEntry> roster)
    {
        var call = response?.ToolCalls.FirstOrDefault(c => c.Name == ToolName);
        if (call == null) return null;

        var names = new List<string>();
        switch (call.Arguments["recipients"])
        {
            case JsonArray arr:
                foreach (var n in arr) if (n is JsonValue v && v.TryGetValue<string>(out var s)) names.Add(s);
                break;
            case JsonValue v when v.TryGetValue<string>(out var s):
                names.AddRange(s.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries));
                break;
        }

        var result = new List<RosterEntry>();
        foreach (var raw in names)
        {
            var n = raw.Trim();
            if (n.Length == 0) continue;
            if (IsEveryone(n)) return roster.ToList();
            var match = roster.FirstOrDefault(r => r.Name.Equals(n, StringComparison.OrdinalIgnoreCase))
                     ?? roster.FirstOrDefault(r => r.Name.Contains(n, StringComparison.OrdinalIgnoreCase) || n.Contains(r.Name, StringComparison.OrdinalIgnoreCase));
            if (match != null) { if (!result.Contains(match)) result.Add(match); continue; }
            // the model answered with a job instead of names
            foreach (var r in roster.Where(r => r.Job.Equals(n, StringComparison.OrdinalIgnoreCase)))
                if (!result.Contains(r)) result.Add(r);
        }
        return result;
    }

    static readonly string[] Everyone = { "all", "everyone", "everybody", "crew", "все", "всем", "экипаж" };

    static bool IsEveryone(string s) => Everyone.Any(e => s.Equals(e, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Used when the dispatcher LLM is unavailable: names mentioned in the text; an order for everybody to all who
    /// heard it; a one-person job to the most suitable bot (see <see cref="ChatRouter.Fallback"/>).
    /// </summary>
    public static List<RosterEntry> Fallback(ChatCommand cmd, IReadOnlyList<RosterEntry> roster) => ChatRouter.Fallback(cmd, roster);

    /// <summary>Text the recipient bot sees in 'playerOrders'.</summary>
    public static string FormatOrder(ChatCommand cmd, IReadOnlyList<RosterEntry> recipients, RosterEntry me)
    {
        var others = recipients.Where(r => r.Id != me.Id).Select(r => r.Name).ToList();
        var who = cmd.SenderJob != null ? $"{cmd.SenderName} ({cmd.SenderJob})" : cmd.SenderName;
        var also = others.Count == 0 ? "" : $" [also sent to: {string.Join(", ", others)}]";
        var where = cmd.SenderRoom != null ? $", in {cmd.SenderRoom}" : "";
        var hint = OrderHints.Suggest(cmd.Text, cmd.SenderName, cmd.SenderRoom);
        return $"{who}{(cmd.ViaRadio ? " on radio" : "")}{where}: \"{cmd.Text}\"{also} ('me' = {cmd.SenderName})" +
               (hint != null ? $" Hint: probably {hint}." : "");
    }
}

/// <summary>Minimal Russian -> Latin transliteration for matching names typed in the other alphabet.</summary>
public static class Translit
{
    static readonly Dictionary<char, string> Map = new()
    {
        ['а'] = "a", ['б'] = "b", ['в'] = "v", ['г'] = "g", ['д'] = "d", ['е'] = "e", ['ё'] = "e", ['ж'] = "zh", ['з'] = "z",
        ['и'] = "i", ['й'] = "y", ['к'] = "k", ['л'] = "l", ['м'] = "m", ['н'] = "n", ['о'] = "o", ['п'] = "p", ['р'] = "r",
        ['с'] = "s", ['т'] = "t", ['у'] = "u", ['ф'] = "f", ['х'] = "kh", ['ц'] = "ts", ['ч'] = "ch", ['ш'] = "sh", ['щ'] = "shch",
        ['ъ'] = "", ['ы'] = "y", ['ь'] = "", ['э'] = "e", ['ю'] = "yu", ['я'] = "ya",
    };

    public static string ToLatin(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s.ToLowerInvariant()) sb.Append(Map.TryGetValue(ch, out var l) ? l : ch.ToString());
        return sb.ToString();
    }
}
