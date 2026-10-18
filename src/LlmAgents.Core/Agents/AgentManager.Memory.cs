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
            LastRoundNotes = bot?.LastRoundNotes.Count > 0 ? bot.LastRoundNotes.ToList() : null,
        };
        if (view.PastRounds?.Count == 0) view.PastRounds = null;
        return view.IsEmpty ? null : view;
    }

    /// <summary>recall(query): what the bot heard, did and knows, best matches first.</summary>
    public string Recall(BotAgent agent, string? query)
    {
        double now = clock();
        var items = new List<(string Source, string Text, double Age)>();
        foreach (var l in conversation.All.Where(l => l.HeardByBot(agent.Id)))
            items.Add(("talk", ConversationLog.Format(l, now, agent.Name), now - l.At));
        foreach (var (text, at) in agent.AllNotes())
            items.Add(("note", text, double.IsNaN(at) ? 1e6 : now - at));
        var log = agent.RoundLog();
        for (int i = 0; i < log.Count; i++) items.Add(("done", log[i], 1e5 + (log.Count - i)));   // no times: order only
        if (MemoryOn && memory.Bot(agent.Name) is { } m)
        {
            foreach (var f in m.Facts) items.Add(("fact", f, 2e6));
            foreach (var j in m.Journal) items.Add(("past round", j, 3e6));
            foreach (var n in m.LastRoundNotes) items.Add(("note, last round", n, 2.5e6));
            foreach (var (person, rel) in m.Relationships) items.Add(("relationship", $"{person}: {rel.Feeling}" + (rel.Note.Length > 0 ? " - " + rel.Note : ""), 2e6));
        }
        if (MemoryOn)
            foreach (var p in memory.Read(d => d.Players.SelectMany(kv => kv.Value.Select(v => $"{kv.Key}: {v}")).ToList()))
                items.Add(("player wants", p, 2e6));
        return RecallText(items, query);
    }

    /// <summary>The search behind recall: word stems, any alphabet; the latest first among equal matches.</summary>
    public static string RecallText(List<(string Source, string Text, double Age)> items, string? query)
    {
        if (items.Count == 0) return "you remember nothing yet this round";
        static string Stem(string w) { w = Translit.ToLatin(w.ToLowerInvariant()); return w.Length > 5 ? w[..5] : w; }
        static IEnumerable<string> Words(string s) =>
            s.Split(new[] { ' ', ',', '.', '!', '?', ':', ';', '"', '\'', '(', ')', '[', ']', '-', '«', '»', '/' }, StringSplitOptions.RemoveEmptyEntries)
             .Where(w => w.Length >= 3).Select(Stem);
        List<(string Source, string Text, double Age)> pick;
        if (string.IsNullOrWhiteSpace(query))
            pick = items.OrderBy(i => i.Age).Take(10).ToList();
        else
        {
            var q = Words(query).Distinct().ToList();
            if (q.Count == 0) q = new List<string> { Stem(query.Trim()) };
            pick = items.Select(i => (i, score: Words(i.Text).Distinct().Count(q.Contains)))
                .Where(t => t.score > 0).OrderByDescending(t => t.score).ThenBy(t => t.i.Age).Take(10).Select(t => t.i).ToList();
            if (pick.Count == 0) return $"nothing about '{query}' in what you remember; try other words, or recall() for the latest";
        }
        var text = string.Join("\n", pick.Select(i => $"[{i.Source}] {i.Text}"));
        return Tools.Clip(text, Tools.MaxInspectLength);
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
            // what the bot wrote down goes along into the next round as it is: nothing it chose to remember is lost
            if (notes.Count > 0) memory.SetLastRoundNotes(agent.Name, agent.Job, notes);
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
            string name = agent.Name, job = agent.Job, scope = memory.Scope;   // the campaign this round belonged to
            int maxJournal = m.MaxJournal, maxFacts = m.MaxFacts;
            Track(Task.Run(async () =>
            {
                string entry = fallback;
                var lessons = new List<string>();
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
                    var r = await router.CompleteAsync(reservation!, SummaryPrompt(name, job), new[] { new LlmTurn("user", user) },
                        Array.Empty<ToolSpec>(), cts.Token).ConfigureAwait(false);
                    var (summary, keep) = ParseSummary(r.Status == RouteStatus.Ok ? r.Response?.Text : null);
                    if (summary.Length >= 10) entry = summary;
                    else Log.Warn($"[{name}] round summary failed ({r.Status}): {r.Detail}; using the plain one");
                    lessons = keep;
                }
                catch (OperationCanceledException) { }
                catch (Exception e) { Log.Warn($"[{name}] round summary failed: {e.Message}; using the plain one"); }
                memory.AddJournal(name, job, entry, maxJournal, countRound: true, scope);
                foreach (var fact in lessons) memory.AddFact(name, job, fact, maxFacts, scope);
                memory.Flush();
                Log.Info($"[{name}] journal: {entry}" + (lessons.Count > 0 ? $" | kept: {string.Join(" | ", lessons)}" : ""));
            }));
        }
        memory.Flush();
    }

    public const int MaxLessonsPerRound = 3;

    static string SummaryPrompt(string name, string job) =>
        $"You are {name}, {job} on a submarine in Barotrauma. The round just ended. Write for your long-term memory, " +
        "in the language the crew speaks, plain text, no tool calls:\n" +
        $"First line: one or two sentences (max {MemoryStore.MaxJournalLength - 100} characters), first person, past tense: what happened, who did what.\n" +
        $"Then up to {MaxLessonsPerRound} lines starting with \"- \": lasting facts worth knowing in later rounds on this sub " +
        "(where spare tanks, suits or tools are kept, what worked or failed, what a person is good at or wants). " +
        "Not how this round went, not positions or water levels. No lines if there is nothing lasting.";

    /// <summary>The summary's first line(s) and its "- " lines (lasting facts).</summary>
    public static (string Summary, List<string> Lessons) ParseSummary(string? text)
    {
        var lessons = new List<string>();
        var summary = new List<string>();
        foreach (var raw in (text ?? "").Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("- ") || line.StartsWith("* ") || line.StartsWith("• "))
            {
                var fact = line.Substring(2).Trim().Trim('"');
                if (fact.Length >= 8 && lessons.Count < MaxLessonsPerRound) lessons.Add(fact);
            }
            else if (lessons.Count == 0) summary.Add(line);
        }
        return (string.Join(" ", summary).Trim().Trim('"'), lessons);
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
