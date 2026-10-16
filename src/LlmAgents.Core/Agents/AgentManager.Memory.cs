using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using LlmAgents.Llm;
using LlmAgents.Memory;

namespace LlmAgents.Agents;

/// <summary>Long-term memory: what bots see of it, the tools that change it, and the end-of-round summaries.</summary>
public sealed partial class AgentManager
{
    // ---------------------------------------------------------------- long-term memory

    MemoryView? MemoryFor(BotAgent agent, Observation obs)
    {
        var m = Settings.Memory;
        var bot = memory.Bot(agent.Name);
        var playersAboard = obs.Crew.Where(c => c.IsPlayer).Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var prefs = memory.Read(d => d.Players
            .Where(kv => playersAboard.Contains(kv.Key))
            .SelectMany(kv => kv.Value.Select(p => $"{kv.Key}: {p}"))
            .Take(12).ToList());
        var crewNames = obs.Crew.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var view = new MemoryView
        {
            Facts = bot?.Facts.Count > 0 ? bot.Facts.ToList() : null,
            PastRounds = bot?.Journal.Count > 0 ? bot.Journal.Skip(Math.Max(0, bot.Journal.Count - Math.Max(0, m.JournalInPrompt))).ToList() : null,
            // people aboard first, then the rest
            Relationships = bot?.Relationships.Count > 0
                ? bot.Relationships.OrderByDescending(kv => crewNames.Contains(kv.Key)).Take(10)
                    .Select(kv => $"{kv.Key}: {kv.Value.Feeling}" + (kv.Value.Note.Length > 0 ? " - " + kv.Value.Note : "")).ToList()
                : null,
            PlayerPreferences = prefs.Count > 0 ? prefs : null,
        };
        if (view.PastRounds?.Count == 0) view.PastRounds = null;
        return view.IsEmpty ? null : view;
    }

    public string Remember(BotAgent agent, string note, bool longTerm)
    {
        agent.AddNote(note, Settings.Global.MaxNotes, clock());
        if (!longTerm) return "ok";
        if (!MemoryOn) return "ok (long-term memory is disabled, kept for this round)";
        memory.AddFact(agent.Name, agent.Job, note, Settings.Memory.MaxFacts);
        return "ok, kept for future rounds";
    }

    public string Forget(BotAgent agent, string text)
    {
        if (!MemoryOn) return "error: long-term memory is disabled";
        int n = memory.ForgetFact(agent.Name, text);
        return n == 0 ? $"error: no long-term fact contains '{text}'" : $"ok, forgot {n}";
    }

    public string NoteRelationship(BotAgent agent, string person, string feeling, string note)
    {
        if (!MemoryOn) return "error: long-term memory is disabled";
        if (person.Equals(agent.Name, StringComparison.OrdinalIgnoreCase)) return "error: that is you";
        memory.SetRelationship(agent.Name, agent.Job, person.Trim(), feeling, note, Settings.Memory.MaxRelationships);
        agent.LogEvent($"feel {feeling} about {person}: {note}");
        return "ok";
    }

    public string NotePreference(BotAgent agent, string player, string preference)
    {
        if (!MemoryOn) return "error: long-term memory is disabled";
        memory.AddPreference(player.Trim(), preference, Settings.Memory.MaxPreferencesPerPlayer);
        return "ok, the whole AI crew will know";
    }

    /// <summary>
    /// Round over (game thread, before <see cref="Reset"/>): every bot writes a short summary of the round into its
    /// journal. One LLM call per bot when there is capacity, otherwise a plain summary from the round log.
    /// </summary>
    public void EndRound()
    {
        if (!MemoryOn) return;
        var m = Settings.Memory;
        foreach (var agent in agents.Values.ToList())
        {
            var log = agent.RoundLog();
            var notes = agent.Notes();
            if (log.Count == 0 && notes.Count == 0)
            {
                memory.AddJournal(agent.Name, agent.Job, "", m.MaxJournal, countRound: true);
                continue;
            }
            string fallback = PlainSummary(log);
            string provider = string.IsNullOrEmpty(m.SummaryProvider) ? ProviderFor(agent) : m.SummaryProvider;
            if (!m.RoundSummary)
            {
                memory.AddJournal(agent.Name, agent.Job, fallback, m.MaxJournal, countRound: true);
                continue;
            }
            var user = "Events of the round:\n- " + string.Join("\n- ", log) +
                       (notes.Count > 0 ? "\nYour notes:\n- " + string.Join("\n- ", notes) : "");
            var system = $"You are {agent.Name}, {agent.Job} on a submarine in Barotrauma. The round just ended. " +
                         $"Write ONE or two sentences (max {MemoryStore.MaxJournalLength} characters) for your long-term memory: what happened, " +
                         "who did what, what you learned. First person, past tense, in the language the crew speaks. Plain text only, no tool calls.";
            string name = agent.Name, job = agent.Job, scope = memory.Scope;   // the campaign this round belonged to
            int max = m.MaxJournal;
            Track(Task.Run(async () =>
            {
                string entry = fallback;
                try
                {
                    // Slots may be busy (local models often allow one request at a time): wait for one, but not forever.
                    LlmRouter.Reservation? reservation = null;
                    var deadline = DateTime.UtcNow.AddSeconds(SummaryWaitSeconds);
                    while (!router.TryReserve(provider, out reservation, out _, priority: false))
                    {
                        if (DateTime.UtcNow >= deadline) throw new TimeoutException("no free LLM slot");
                        await Task.Delay(500, cts.Token).ConfigureAwait(false);
                    }
                    CountRequest();
                    var r = await router.CompleteAsync(reservation!, system, new[] { new LlmTurn("user", user) }, Array.Empty<ToolSpec>(), cts.Token).ConfigureAwait(false);
                    var text = r.Status == RouteStatus.Ok ? JsonUtil.OneLine(r.Response?.Text ?? "").Trim().Trim('"') : "";
                    if (text.Length >= 10) entry = text;
                    else Log.Warn($"[{name}] round summary failed ({r.Status}): {r.Detail}; using the plain one");
                }
                catch (OperationCanceledException) { }
                catch (Exception e) { Log.Warn($"[{name}] round summary failed: {e.Message}; using the plain one"); }
                memory.AddJournal(name, job, entry, max, countRound: true, scope);
                memory.Flush();
                Log.Info($"[{name}] journal: {entry}");
            }));
        }
        memory.Flush();
    }

    static string PlainSummary(List<string> log)
    {
        if (log.Count == 0) return "";
        // orders and finished tasks say the most about a round
        var important = log.Where(l => l.StartsWith("order") || l.StartsWith("task") || l.Contains("full control")).ToList();
        var pick = (important.Count > 0 ? important : log).TakeLast(4);
        return "Last round: " + string.Join("; ", pick);
    }
}
