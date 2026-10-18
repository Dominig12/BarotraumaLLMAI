using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using LlmAgents.Llm;

namespace LlmAgents.Agents;

/// <summary>
/// The director (settings "director"): one request for the whole crew instead of one per bot. While it is on, the
/// events that would make a bot think about its own work (a new danger, a finished or failed plan, the round start,
/// the end of danger, a long plan) go to the director instead, and it answers with assign(bot, goal, steps) for the
/// bots that need new work. The plans run exactly like a bot's own (<see cref="ApplyPlan"/>).
///
/// A player's word stays with the bot: chat orders, the command menu, messages and full AI control are handled by the
/// bot's own model as before, and the director does not touch a bot while it carries out a player's order. When the
/// director fails several times in a row, the bots think for themselves again for a while.
/// </summary>
public sealed partial class AgentManager
{
    readonly List<DirectorEvent> directorEvents = new();
    readonly List<(double At, string User, string Assistant)> directorHistory = new();
    readonly List<string> directorNotes = new();
    readonly List<string> directorResults = new();
    bool directorPending;
    /// <summary>Number of the latest director request (a late answer to an older one is dropped).</summary>
    int directorSeq;
    double directorLastRequestAt = double.NegativeInfinity;
    double directorStartedAt = double.NaN;
    double directorFallbackUntil = double.NegativeInfinity;
    int directorFailures;
    int directorDecisions;
    string? directorLastSummary;
    double directorLastAt = double.NaN;

    const string DirectorSource = "director";

    /// <summary>The director is on and has not failed lately.</summary>
    public bool DirectorActive => Settings.Director.Enabled && clock() >= directorFallbackUntil;

    public int DirectorDecisions => directorDecisions;

    /// <summary>Why the director may not give this bot work (null = it may).</summary>
    string? NotAssignable(BotAgent a)
    {
        if (a.AiControl) return "under full AI control, decides itself";
        if (a.Pending) return "thinking about a player's order or message";
        if (a.HasPlayerOrder) return "a player's order is waiting";
        if (a.CurrentGoal is { } g && IsPlayersGoal(g))
        {
            // a player's order is not kept forever: after goalTimeoutMinutes the bot is the director's again
            double timeout = Settings.Global.GoalTimeoutMinutes;
            if (timeout <= 0 || clock() - g.SetAt <= timeout * 60)
                return $"carrying out an order ({g.Source}: {Tools.Clip(Quoted(GoalText(g.Text)), 80)})";
        }
        return null;
    }

    static bool IsPlayersGoal(AgentGoal g) =>
        g.Player != null || g.Source.StartsWith("player", StringComparison.Ordinal) || g.Source.Contains("in command");

    /// <summary>The director decides this bot's work right now.</summary>
    bool IsDirected(BotAgent a) => DirectorActive && NotAssignable(a) == null;

    /// <summary>Events that stay with the bot itself: what players and the crew say to it, and its own lookups.</summary>
    static bool BotKeeps(EventKind k) => k is EventKind.Order or EventKind.Control or EventKind.MenuOrder or EventKind.Mail
        or EventKind.Retry or EventKind.Lookup;

    /// <summary>Moves the bot's events about its work to the director.</summary>
    void RouteToDirector(BotAgent agent)
    {
        if (agent.Events.Count == 0 || !IsDirected(agent)) return;
        foreach (var e in agent.Events.Where(e => !BotKeeps(e.Kind)).ToList())
        {
            agent.Events.Remove(e);
            if (e.Kind == EventKind.Heartbeat) continue;   // the director has its own routine look
            AddDirectorEvent(e.Kind, agent.Name, e.Text, e.At);
        }
    }

    void AddDirectorEvent(EventKind kind, string bot, string text, double at)
    {
        // the same news from several bots ("the danger is over") is one line
        int i = directorEvents.FindIndex(x => x.Kind == kind && x.Text == text);
        if (i >= 0)
        {
            var old = directorEvents[i];
            if (bot.Length > 0 && !old.Bot.Split(", ").Contains(bot))
                directorEvents[i] = old with { Bot = old.Bot.Length == 0 ? bot : old.Bot + ", " + bot };
            return;
        }
        directorEvents.Add(new DirectorEvent(kind, bot, text, at));
    }

    /// <summary>The director stopped answering: the events it was holding go back to the bots.</summary>
    void ReturnEventsToBots()
    {
        foreach (var e in directorEvents)
            foreach (var name in e.Bot.Split(", ", StringSplitOptions.RemoveEmptyEntries))
                if (agents.Values.FirstOrDefault(a => a.Name == name) is { } a && Accepts(a, e.Kind)) a.AddEvent(e.Kind, e.Text, e.At);
        directorEvents.Clear();
    }

    static double DirectorDelay(EventKind k) => k switch
    {
        EventKind.Danger or EventKind.TaskFailed => 1,   // a burst of alarms lands in one request
        _ => 2,
    };

    void ScheduleDirector(double now)
    {
        var d = Settings.Director;
        if (!d.Enabled || directorPending || now < directorFallbackUntil) return;
        if (holdForDispatch) return;   // players first
        if (double.IsNaN(directorStartedAt)) directorStartedAt = now;
        var directed = agents.Values.Where(IsDirected).ToList();
        if (directed.Count == 0) return;

        if (directorEvents.Count == 0)
        {
            // a routine look only when someone has nothing to do (a crew busy with good plans needs no request)
            var idleBots = directed.Where(a => Director.HasNoWork(a, a.LastObservation)).ToList();
            bool idle = idleBots.Count > 0;
            if (d.IdleCheckSeconds <= 0 || !idle || now - directorStartedAt < 10 || now - directorLastRequestAt < d.IdleCheckSeconds) return;
            AddDirectorEvent(EventKind.Heartbeat, "", "routine look: " + string.Join(", ", idleBots.Select(a => a.Name)) + (idleBots.Count == 1 ? " has" : " have") + " no work", now);
        }

        int tier = directorEvents.Min(e => EventPolicy.Tier(e.Kind));
        double dueAt = directorEvents.Min(e => e.At + DirectorDelay(e.Kind));
        double gap = tier <= 1 ? Math.Min(5, d.MinSecondsBetweenCalls) : d.MinSecondsBetweenCalls;
        if (now < dueAt || now - directorLastRequestAt < gap) return;

        string provider = string.IsNullOrEmpty(d.Provider) ? Settings.DefaultProvider : d.Provider;
        if (!router.TryReserve(provider, out var reservation, out _, priority: tier <= 1)) return;   // next tick
        try { StartDirector(reservation!, now); }
        catch (Exception e)
        {
            reservation!.Dispose();
            directorPending = false;
            Log.Error("[director] could not start: " + e);
        }
    }

    Observation ObserveForDirector(BotAgent a, double now)
    {
        if (a.LastObservation != null && now - a.LastObservationAt < 1.0) return a.LastObservation;
        var o = game.Observe(a.Id);
        a.LastObservation = o;
        a.LastObservationAt = now;
        return o;
    }

    void StartDirector(LlmRouter.Reservation reservation, double now)
    {
        // copied, and taken only once the request is built: a failing game call loses nothing
        var events = directorEvents.ToList();
        var results = directorResults.ToList();
        var bots = agents.Values.Where(a => game.IsAlive(a.Id)).OrderBy(a => a.Name)
            .Select(a => new DirectedBot(a, ObserveForDirector(a, now), NotAssignable(a))).ToList();

        string user = Director.Summary(events, bots, directorNotes, results, now, ConversationFor(null, now).TakeLast(8).ToList());
        string system = Director.SystemPrompt(Settings.Bots.SpeechLanguage);
        var turns = new List<LlmTurn>();
        foreach (var (at, u, a) in directorHistory)
        {
            turns.Add(new LlmTurn("user", $"({ModStatus.Ago(now - at)}) {u}"));
            turns.Add(new LlmTurn("assistant", a));
        }
        turns.Add(new LlmTurn("user", user));
        string historyLine = Director.HistoryLine(events);

        directorEvents.Clear();
        directorResults.Clear();
        directorPending = true;
        int seq = ++directorSeq;
        directorLastRequestAt = now;
        CountRequest();
        if (Settings.Global.LogPrompts) Log.Info($"[director] -> {reservation.Provider}: {user}");
        else Log.Info($"[director] asked ({historyLine})");

        var token = cts.Token;
        var r0 = router;
        int gen = generation;
        Track(Task.Run(async () =>
        {
            RouteResult r;
            try { r = await r0.CompleteAsync(reservation, system, turns, Director.ToolSpecs, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch (Exception e) { r = new RouteResult(RouteStatus.Failed, null, reservation.Provider, e.Message); }
            OnGameThread(gen, t =>
            {
                if (seq == directorSeq) ApplyDirector(events, results, historyLine, r, t);
                else Log.Warn("[director] a late answer to an old request was dropped");
            });
        }));
    }

    void ApplyDirector(List<DirectorEvent> events, List<string> results, string historyLine, RouteResult r, double now)
    {
        directorPending = false;
        CountTokens(r);
        var d = Settings.Director;
        if (r.Status != RouteStatus.Ok || r.Response == null)
        {
            directorFailures++;
            Log.Warn($"[director] no decision ({r.Status}): {r.Detail}");
            directorEvents.InsertRange(0, events.Where(e => e.Kind != EventKind.Heartbeat));
            directorResults.InsertRange(0, results);
            if (directorFailures >= Math.Max(1, d.FallbackAfterFailures))
            {
                directorFailures = 0;
                directorFallbackUntil = now + Math.Max(10, d.FallbackSeconds);
                Log.Warn($"[director] failed {Math.Max(1, d.FallbackAfterFailures)} times in a row: the bots think for themselves for {Math.Max(10, d.FallbackSeconds):0} s");
                ReturnEventsToBots();
            }
            return;
        }
        directorFailures = 0;
        directorDecisions++;

        var calls = r.Response.ToolCalls.ToList();
        var summary = calls.Count == 0 ? "(no tool call)" : string.Join("; ", calls.Select(c => $"{c.Name}({Tools.Clip(Tools.ArgsToString(c), 300)})"));
        directorHistory.Add((now, historyLine, summary));
        directorLastSummary = summary;
        directorLastAt = now;
        while (directorHistory.Count > Math.Max(0, d.HistoryTurns)) directorHistory.RemoveAt(0);
        Log.Info($"[director] <- {r.Provider}: {summary}");
        if (r.Response.Truncated) WarnTruncated(r.Provider, r.Response);

        if (calls.Count == 0)
        {
            directorResults.Add("your last reply had no tool call, so nothing changed: reply with assign / note / carry_on calls");
            if (events.Any(e => EventPolicy.Tier(e.Kind) <= 1)) foreach (var e in events) AddDirectorEvent(e.Kind, e.Bot, e.Text, now);
            return;
        }
        foreach (var call in calls)
        {
            string result;
            try { result = RunDirectorCall(call, now); }
            catch (Exception e) { result = "error: " + e.Message; Log.Error("[director] " + call.Name + " failed: " + e); }
            if (result.Length > 0) directorResults.Add($"{call.Name}({Tools.Clip(Tools.ArgsToString(call), 120)}) -> {result}");
        }
    }

    string RunDirectorCall(ToolCall call, double now)
    {
        var spec = Director.ToolSpecs.FirstOrDefault(t => t.Name == call.Name);
        if (spec != null && Tools.ValidateAgainstSchema(call.Arguments, spec.Parameters) is { } invalid) return "error: " + invalid;
        switch (call.Name)
        {
            case "carry_on":
                return "";
            case "note":
            {
                var text = Tools.Str(call.Arguments, "text");
                if (string.IsNullOrWhiteSpace(text)) return "error: missing 'text'";
                directorNotes.Add(Tools.Clip(text.Trim(), 160));
                while (directorNotes.Count > Director.MaxNotes) directorNotes.RemoveAt(0);
                return "";
            }
            case "assign":
                return Assign(call, now);
            default:
                return Tools.Def(call.Name) != null
                    ? $"error: '{call.Name}' is a bot tool: give it to a bot with assign(bot, goal, steps=[{{\"do\":\"{call.Name}\",\"args\":{{...}}}}])"
                    : $"error: unknown tool '{call.Name}'";
        }
    }

    string Assign(ToolCall call, double now)
    {
        var name = Tools.Str(call.Arguments, "bot");
        var bot = Director.FindBot(agents.Values.Where(a => game.IsAlive(a.Id)), name);
        if (bot == null)
            return $"error: no AI bot named '{name}' (bots: {string.Join(", ", agents.Values.Select(a => a.Name))})";
        if (NotAssignable(bot) is { } why) return $"error: {bot.Name} is not assignable ({why})";
        var goal = Tools.Str(call.Arguments, "goal") ?? "work from the director";
        var stepsNode = call.Arguments["steps"];
        if (stepsNode == null) return "error: missing 'steps'";
        if (stepsNode is not JsonArray steps) return "error: 'steps' must be an array";
        var plan = new ToolCall("plan", new JsonObject { ["steps"] = steps.DeepClone() });
        if (Tools.ValidateArguments(plan) is { } invalid) return "error: " + invalid;
        try { PlanTree.Build(steps.DeepClone(), PlanToolNames); }
        catch (FormatException e) { return "error: the plan could not be read: " + Tools.Clip(e.Message, 200) + ". Send it again, fixed"; }

        bot.LogEvent("director: " + goal);
        Log.Info($"[{bot.Name}] director assigns: {goal}");
        Guarded(bot, "plan", () => ApplyPlan(bot, new List<ToolCall> { plan }, authority: false, new AgentGoal(goal, DirectorSource, now), now));
        bot.LastDecision = "director: " + Tools.Clip(goal, 100);
        bot.Stats.DirectorAssignments++;
        bot.AddActivity("director", "assigned: " + Tools.Clip(goal, 200), clock());
        bot.LastDecisionAt = now;
        return bot.Busy ? "ok, " + bot.Name + " is on it" : "ok, done at once: " + Tools.Clip(string.Join(" | ", bot.RecentResults(2)), 200);
    }

    /// <summary>The director gave this plan: it is over, so is its goal.</summary>
    void EndDirectorGoal(BotAgent agent, double now)
    {
        if (agent.CurrentGoal?.Source == DirectorSource) agent.SetGoal(null, now);
    }

    void ResetDirector()
    {
        directorEvents.Clear();
        directorHistory.Clear();
        directorNotes.Clear();
        directorResults.Clear();
        directorPending = false;
        directorLastRequestAt = double.NegativeInfinity;
        directorStartedAt = double.NaN;
        directorFallbackUntil = double.NegativeInfinity;
        directorFailures = 0;
        directorLastSummary = null;
        directorLastAt = double.NaN;
    }

    FeatureStatus? DirectorFeature()
    {
        if (!Settings.Director.Enabled) return null;
        double now = clock();
        if (now < directorFallbackUntil)
            return new FeatureStatus("Директор", false, $"не отвечает, боты думают сами ещё {directorFallbackUntil - now:0} с");
        return new FeatureStatus("Директор", true, $"решений: {directorDecisions}" +
            (double.IsNegativeInfinity(directorLastRequestAt) ? "" : $", последний запрос {ModStatus.Ago(now - directorLastRequestAt)}"));
    }
}
