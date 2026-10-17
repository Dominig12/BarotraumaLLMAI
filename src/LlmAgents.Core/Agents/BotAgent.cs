using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using LlmAgents.Llm;

namespace LlmAgents.Agents;

/// <summary>An order waiting for the bot's next decision.</summary>
/// <param name="Authority">true = from a human player (may replace orders players gave through the command menu);
/// false = from an AI crew member in command (binding, but player orders still come first).</param>
/// <param name="From">Who gave it (a player's name), for feedback when it cannot be carried out.</param>
public sealed record PendingOrder(string Text, bool Authority, string? From = null);

/// <summary>What the bot is working toward. Source: "player Daniil", "you", "Rosendo (in command)"...
/// Player: the human player who gave the order (told when it is done or failed).</summary>
public sealed record AgentGoal(string Text, string Source, double SetAt, string? Player = null);

/// <summary>An order the bot got earlier (shown with its age, so an old order does not read like a fresh one).</summary>
public sealed record PastOrder(string Text, double At);

/// <summary>
/// State of one LLM-driven bot. Everything except the inbox and the order queue (which other code may fill at any
/// time) is used on the game thread only; LLM requests work on copies and their results are applied back on the
/// game thread.
/// </summary>
public sealed class BotAgent
{
    public int Id { get; }
    public string Name { get; set; }
    public string Job { get; set; }
    /// <summary>The game's personality trait (used when no persona is set).</summary>
    public string? Trait { get; set; }

    public BotAgent(BotHandle h)
    {
        Id = h.Id;
        Name = h.Name;
        Job = h.Job;
        Trait = h.Trait;
    }

    // ------------------------------------------------------------------ scheduling

    /// <summary>A request for this bot is running.</summary>
    public bool Pending { get; set; }
    /// <summary>Number of the bot's latest request (answers to older ones are dropped).</summary>
    public int RequestSeq { get; set; }
    public double LastRequestAt { get; set; } = double.NegativeInfinity;
    /// <summary>After a failed request: not before this time.</summary>
    public double RetryNotBefore { get; set; } = double.NegativeInfinity;
    public int FailedRequests { get; set; }
    /// <summary>After a reply cut off by the token limit (thinking models): the next request may use this many output tokens.</summary>
    public int OutputBoost { get; set; }

    /// <summary>Things that happened since the last decision (see <see cref="EventKind"/>).</summary>
    public readonly List<AgentEvent> Events = new();

    public void AddEvent(EventKind kind, string text, double at)
    {
        // the same thing twice (two breaches in one room, the same order event) is one event
        if (Events.Any(e => e.Kind == kind && e.Text == text)) return;
        Events.Add(new AgentEvent(kind, Tools.Clip(text, 240), at));
        while (Events.Count > 12) Events.RemoveAt(Events.FindIndex(e => e.Kind == Events.Max(x => x.Kind)));
    }

    public bool HasEvent(EventKind kind) => Events.Any(e => e.Kind == kind);

    /// <summary>Situation flags seen recently (flag -> last time seen); a flag that newly appears wakes the bot.</summary>
    public readonly Dictionary<string, double> FlagsSeen = new();
    public bool InDanger { get; set; }
    public double DangerGoneSince { get; set; } = double.NaN;
    public double NextSituationCheckAt { get; set; }
    /// <summary>The latest observation (from the situation check), reused when a decision starts right after it.</summary>
    public Observation? LastObservation { get; set; }
    public double LastObservationAt { get; set; } = double.NegativeInfinity;

    /// <summary>The last decision changed something (an order, a task, an item...), not only talk or "already doing that".</summary>
    public bool ActedSinceDecision { get; set; }
    /// <summary>Heartbeat decisions in a row that changed nothing; each one doubles the time to the next heartbeat.</summary>
    public int IdleStreak { get; set; }

    // ------------------------------------------------------------------ full AI control

    /// <summary>A player handed this bot full AI control (mod order in the command menu).</summary>
    public bool AiControl { get; set; }
    public string? ControlBy { get; set; }

    // ------------------------------------------------------------------ status

    public string? LastDecision { get; set; }
    public double LastDecisionAt { get; set; } = double.NaN;
    public string? LastWhy { get; set; }
    public int Decisions;
    readonly Queue<string> recentErrors = new();

    public void AddError(string line)
    {
        lock (sync)
        {
            recentErrors.Enqueue(Tools.Clip(line, 200));
            while (recentErrors.Count > 3) recentErrors.Dequeue();
        }
    }

    public List<string> RecentErrors() { lock (sync) return recentErrors.ToList(); }

    readonly Queue<double> sentMessages = new();

    /// <summary>Sliding one-minute window of private messages sent by this bot (anti ping-pong).</summary>
    public bool TryConsumeMessageQuota(double now, int perMinute)
    {
        if (perMinute <= 0) return true;
        while (sentMessages.Count > 0 && sentMessages.Peek() <= now - 60) sentMessages.Dequeue();
        if (sentMessages.Count >= perMinute) return false;
        sentMessages.Enqueue(now);
        return true;
    }

    // ------------------------------------------------------------------ inbox, orders, results

    readonly object sync = new();
    readonly ConcurrentQueue<string> inbox = new();
    readonly ConcurrentQueue<PendingOrder> playerOrders = new();
    readonly List<string> results = new();

    public const int MaxNoteLength = 200;
    public const int MaxResults = 10;
    public const int MaxInbox = 10;
    public const int MaxRoundLog = 40;
    public const int MaxPastOrders = 4;

    public bool HasMail => !inbox.IsEmpty;

    public void Receive(string message)
    {
        inbox.Enqueue(message);
        while (inbox.Count > MaxInbox && inbox.TryDequeue(out _)) { }
    }

    public List<string> DrainInbox()
    {
        var list = new List<string>();
        while (inbox.TryDequeue(out var m)) list.Add(m);
        return list;
    }

    /// <summary>A chat order that was routed to this bot (or an order from an AI crew member in command).</summary>
    public bool HasPlayerOrder => !playerOrders.IsEmpty;

    public void ReceivePlayerOrder(string order, bool authority = true, string? from = null)
    {
        System.Threading.Interlocked.Increment(ref Stats.OrdersReceived);
        AddActivity("order", order, Clock?.Invoke() ?? double.NaN);
        playerOrders.Enqueue(new PendingOrder(order, authority, from));
        while (playerOrders.Count > MaxInbox && playerOrders.TryDequeue(out _)) { }
    }

    public List<PendingOrder> DrainOrders()
    {
        var list = new List<PendingOrder>();
        while (playerOrders.TryDequeue(out var m)) list.Add(m);
        return list;
    }

    /// <summary>Who gave the orders still waiting (for "your order is waiting" notices).</summary>
    public List<string> OrderSenders() => playerOrders.Select(o => o.From).Where(f => f != null).Select(f => f!).Distinct().ToList();

    /// <summary>When the senders were last told that the orders wait for a free request.</summary>
    public double LimitToldAt { get; set; } = double.NegativeInfinity;

    public void RecordResult(string line)
    {
        lock (sync)
        {
            results.Add(line);
            while (results.Count > MaxResults) results.RemoveAt(0);
        }
    }

    /// <summary>The latest results, left in place for the bot's next decision.</summary>
    public List<string> RecentResults(int n)
    {
        lock (sync) return results.TakeLast(n).ToList();
    }

    public List<string> TakeResults()
    {
        lock (sync)
        {
            var r = results.ToList();
            results.Clear();
            return r;
        }
    }

    /// <summary>Puts back what was taken for a request that never reached a model.</summary>
    public void Restore(IEnumerable<string> inboxMessages, IEnumerable<string> actionResults, IEnumerable<PendingOrder>? orders = null,
        IEnumerable<AgentEvent>? events = null)
    {
        var pendingInbox = DrainInbox();
        foreach (var m in inboxMessages) inbox.Enqueue(m);
        foreach (var m in pendingInbox) inbox.Enqueue(m);
        if (orders != null)
        {
            var pendingOrders = DrainOrders();
            foreach (var m in orders) playerOrders.Enqueue(m);
            foreach (var m in pendingOrders) playerOrders.Enqueue(m);
        }
        lock (sync)
        {
            results.InsertRange(0, actionResults);
            while (results.Count > MaxResults) results.RemoveAt(0);
        }
        if (events != null)
            foreach (var e in events.Reverse())
                if (!Events.Any(x => x.Kind == e.Kind && x.Text == e.Text)) Events.Insert(0, e);
    }

    // ------------------------------------------------------------------ notes, past orders, round log

    readonly List<(string Text, double At)> notes = new();

    /// <summary>The bot's own notes (remember).</summary>
    public void AddNote(string note, int max, double at = double.NaN)
    {
        lock (sync)
        {
            notes.Add((Tools.Clip(note, MaxNoteLength), at));
            while (notes.Count > Math.Max(1, max))
            {
                // out of the prompt, not out of memory: recall still finds it
                oldNotes.Add(notes[0]);
                notes.RemoveAt(0);
            }
            while (oldNotes.Count > 200) oldNotes.RemoveAt(0);
        }
    }

    readonly List<(string Text, double At)> oldNotes = new();

    public List<string> Notes() { lock (sync) return notes.Select(n => n.Text).ToList(); }

    /// <summary>Every note of this round, also the ones no longer shown, oldest first.</summary>
    public List<(string Text, double At)> AllNotes() { lock (sync) return oldNotes.Concat(notes).ToList(); }

    /// <summary>Notes for the model, older ones with their age.</summary>
    public List<string> Notes(double now)
    {
        lock (sync)
            return notes.Select(n => !double.IsNaN(n.At) && now - n.At >= 60 ? $"({ModStatus.Ago(now - n.At)}) {n.Text}" : n.Text).ToList();
    }

    readonly List<PastOrder> pastOrders = new();

    public void AddPastOrder(string text, double at)
    {
        pastOrders.Add(new PastOrder(Tools.Clip(text, MaxNoteLength), at));
        while (pastOrders.Count > MaxPastOrders) pastOrders.RemoveAt(0);
    }

    public IReadOnlyList<PastOrder> PastOrders => pastOrders;

    readonly List<string> roundLog = new();

    /// <summary>Notable events of this round (orders, task results, messages), for the end-of-round summary.</summary>
    public void LogEvent(string line)
    {
        lock (sync)
        {
            roundLog.Add(Tools.Clip(line, 200));
            // keep the start of the round and the latest events
            if (roundLog.Count > MaxRoundLog) roundLog.RemoveAt(MaxRoundLog / 4);
        }
    }

    public List<string> RoundLog() { lock (sync) return roundLog.ToList(); }

    // ------------------------------------------------------------------ goal and plan

    /// <summary>What the bot is working toward (set_goal, or a player's order). Shown every turn until done or replaced,
    /// so the intention survives the short history window.</summary>
    public AgentGoal? CurrentGoal { get; private set; }
    public string? Goal => CurrentGoal?.Text;
    public double GoalSetAt => CurrentGoal?.SetAt ?? 0;

    public void SetGoal(string? goal, double now, string source = "you", string? player = null)
    {
        CurrentGoal = string.IsNullOrWhiteSpace(goal) ? null : new AgentGoal(Tools.Clip(goal.Trim(), MaxNoteLength), source, now, player);
    }

    /// <summary>The plan being carried out (null = none): no new decision unless something important happens.</summary>
    public ActivePlan? Plan { get; private set; }

    public bool Busy => Plan != null;

    public void SetPlan(ActivePlan plan) => Plan = plan;

    public void ClearPlan() => Plan = null;

    public string? PlanText() => Plan?.Describe();

    /// <summary>Tool of the task the plan is waiting for (or last waited for): craft, equip, come_to...</summary>
    public string? TaskTool { get; set; }
    /// <summary>A reply that failed at once was answered with one more request; the next failure gets none (no loops).</summary>
    public int ErrorStreak { get; set; }

    readonly Dictionary<ToolGroup, double> usedGroups = new();

    /// <summary>A tool of this group was used: the group stays in the bot's requests for a while.</summary>
    public void UsedTool(string tool, double now)
    {
        if (Tools.Def(tool) is { } d) usedGroups[d.Group] = now;
    }

    public ToolGroup StickyGroups(double now) =>
        usedGroups.Where(kv => now - kv.Value < ToolSelection.StickySeconds).Aggregate((ToolGroup)0, (acc, kv) => acc | kv.Key);

    // ------------------------------------------------------------------ monitoring

    /// <summary>What the bot did lately, for the in-game monitor: decisions, calls with results, task reports, orders.</summary>
    public sealed record ActivityEntry(double At, string Kind, string Text);

    public const int MaxActivity = 60;
    /// <summary>The manager's clock (times of activity entries recorded from outside the manager).</summary>
    public Func<double>? Clock { get; set; }
    readonly List<ActivityEntry> activity = new();

    public void AddActivity(string kind, string text, double at)
    {
        lock (sync)
        {
            activity.Add(new ActivityEntry(at, kind, Tools.Clip(text, 400)));
            while (activity.Count > MaxActivity) activity.RemoveAt(0);
        }
    }

    public List<ActivityEntry> Activity() { lock (sync) return activity.ToList(); }

    /// <summary>Counters for the monitor (this round).</summary>
    public readonly BotStats Stats = new();

    // ------------------------------------------------------------------ history

    readonly List<(double At, string User, string Assistant)> history = new();

    /// <summary>Earlier turns for the next request, each with its age: "(2 min ago) why: ...".</summary>
    public List<LlmTurn> HistorySnapshot(double now = double.NaN)
    {
        lock (sync)
        {
            var list = new List<LlmTurn>();
            foreach (var (at, user, assistant) in history)
            {
                var age = double.IsNaN(now) || double.IsNaN(at) ? "" : $"({ModStatus.Ago(now - at)}) ";
                list.Add(new LlmTurn("user", age + user));
                list.Add(new LlmTurn("assistant", assistant));
            }
            return list;
        }
    }

    public void AddExchange(string user, string assistantSummary, int maxTurns, double at = double.NaN)
    {
        lock (sync)
        {
            history.Add((at, user, assistantSummary));
            while (history.Count > Math.Max(0, maxTurns)) history.RemoveAt(0);
        }
    }
}

/// <summary>A bot's counters for the monitor. Game thread only.</summary>
public sealed class BotStats
{
    public int Requests, FailedRequests, TokensIn, TokensOut, ReasoningTokens;
    public double LatencySum;
    public int Answered;
    public int ToolCalls, ToolErrors, TasksDone, TasksFailed, OrdersReceived, DirectorAssignments;
    public readonly Dictionary<string, int> PerTool = new();

    public double AvgLatency => Answered == 0 ? 0 : LatencySum / Answered;

    public void Call(string tool, bool error)
    {
        ToolCalls++;
        if (error) ToolErrors++;
        PerTool[tool] = PerTool.TryGetValue(tool, out var n) ? n + 1 : 1;
    }
}
