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
public sealed record RosterEntry(int Id, string Name, string Job, string? Room, string CurrentOrder, string? Goal = null);

/// <summary>
/// Step 1 of a chat message: a model decides which AI crew members it is meant for, seeing the recent conversation.
/// Step 2 (in AgentManager): every recipient gets it and handles it with its own model.
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
            ["additionalProperties"] = false,
        });

    /// <summary>Expose the exact currently audible recipients in the schema as well as in the context.</summary>
    public static ToolSpec ToolFor(IReadOnlyList<RosterEntry> roster)
    {
        var schema = (JsonObject)Tool.Parameters.DeepClone();
        schema["properties"]!["recipients"]!["items"]!["enum"] = new JsonArray(roster.Select(r => (JsonNode?)JsonValue.Create(r.Name)).ToArray());
        return new ToolSpec(ToolName, Tool.Description, schema);
    }

    public const string SystemPrompt = @"You are the communications officer of a submarine crew in the game Barotrauma.
A human player wrote a message in the chat. Interpret its meaning in context and decide which AI crew members (from the roster)
it is meant for. Call route_message exactly once with recipients as a JSON array of exact, complete roster names.
The runtime does not expand jobs, 'all', nicknames or partial names: resolve those yourself and enumerate every intended person.
Use [] only when nobody in the roster should receive it. Never answer with prose or carry out the requested game action here.

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
- Use the recent conversation. A message without a name usually continues it: a follow-up ('и потом скажи, что там',
  'ты чего копаешься', 'нет, другой', 'а теперь...') goes to the crew member the sender was just talking with (who got
  their last message or answered them), unless the content clearly means someone else. 'там', 'это', 'он' refer to it.
- An answer to a question a crew member asked the sender goes to that crew member.
- A greeting or small talk to everyone ('привет всем', 'hi all') -> only ONE crew member answers (the captain if present).
- 'someone' / 'кто-нибудь' or an order with no addressee -> the single most suitable crew member for the task (right job, nearby, not busy).
- Plain chatter, jokes, or messages clearly for other humans -> empty recipients.
- Only use names that are in the roster.";

    public static string UserPrompt(ChatCommand cmd, IReadOnlyList<RosterEntry> roster, IReadOnlyList<string>? conversation = null)
    {
        var sb = new StringBuilder();
        if (conversation is { Count: > 0 })
        {
            sb.Append("Recent conversation (newest last; '->' = who it was for):\n");
            foreach (var l in conversation) sb.Append("- ").Append(l).Append('\n');
            sb.Append('\n');
        }
        sb.Append("Message from ").Append(cmd.SenderName);
        if (!string.IsNullOrEmpty(cmd.SenderJob)) sb.Append(" (").Append(cmd.SenderJob).Append(')');
        sb.Append(cmd.ViaRadio ? " over the radio" : " out loud").Append(":\n\"").Append(cmd.Text).Append("\"\n\nRoster (AI crew who heard it):\n");
        foreach (var r in roster)
            sb.Append("- ").Append(r.Name).Append(" | job: ").Append(r.Job)
              .Append(" | room: ").Append(r.Room ?? "outside").Append(" | current order: ").Append(r.CurrentOrder)
              .Append(r.Goal != null ? " | working on: " + Tools.Clip(r.Goal, 120) : "").Append('\n');
        return sb.ToString();
    }

    /// <summary>Maps the LLM answer to roster entries. Returns null if the answer had no usable route_message call.</summary>
    public static List<RosterEntry>? ParseRecipients(LlmResponse? response, IReadOnlyList<RosterEntry> roster)
    {
        if (response?.ToolCalls.Count != 1 || response.ToolCalls[0].Name != ToolName) return null;
        var call = response.ToolCalls[0];
        if (call.Arguments["recipients"] is not JsonArray names) return null;
        var result = new List<RosterEntry>();
        foreach (var raw in names)
        {
            if (raw is not JsonValue value || !value.TryGetValue<string>(out var name)) return null;
            var match = roster.FirstOrDefault(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            // An invalid recipient is a malformed answer, not a deliberate decision that nobody should receive it.
            if (match == null) return null;
            if (!result.Contains(match)) result.Add(match);
        }
        return result;
    }

    /// <summary>What every listener gets when the dispatcher could not tell who a message is for.</summary>
    public static string FormatUnsure(ChatCommand cmd, RosterEntry me)
    {
        var who = cmd.SenderJob != null ? $"{cmd.SenderName} ({cmd.SenderJob})" : cmd.SenderName;
        var where = cmd.SenderRoom != null ? $", in {cmd.SenderRoom}" : "";
        return $"{who}{(cmd.ViaRadio ? " on radio" : "")}{where}: \"{cmd.Text}\" ('me' = {cmd.SenderName}) [heard by everyone nearby; " +
               "it is not clear who it is for: look at 'conversation'. Answer or act only if it is meant for you (your name, your job, " +
               "or it continues your talk with them); otherwise carry_on and leave it to the others]";
    }

    /// <summary>Text the recipient bot sees in 'playerOrders'.</summary>
    public static string FormatOrder(ChatCommand cmd, IReadOnlyList<RosterEntry> recipients, RosterEntry me)
    {
        var others = recipients.Where(r => r.Id != me.Id).Select(r => r.Name).ToList();
        var who = cmd.SenderJob != null ? $"{cmd.SenderName} ({cmd.SenderJob})" : cmd.SenderName;
        var also = others.Count == 0 ? "" : $" [also sent to: {string.Join(", ", others)}]";
        var where = cmd.SenderRoom != null ? $", in {cmd.SenderRoom}" : "";
        return $"{who}{(cmd.ViaRadio ? " on radio" : "")}{where}: \"{cmd.Text}\"{also} ('me' = {cmd.SenderName})";
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
