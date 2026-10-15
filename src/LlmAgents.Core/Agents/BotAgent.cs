using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using LlmAgents.Llm;

namespace LlmAgents.Agents;

/// <summary>An order waiting for the bot's next decision.</summary>
public sealed record PendingOrder(string Text, bool Authority);

/// <summary>One step of a multi-step reply. Authority = the reply carried out a human player's order.</summary>
public sealed record PlanStep(ToolCall Call, bool Authority);

public enum PlanWait { None, Task, Arrival }

/// <summary>State of one LLM-driven bot: memory, inbox, history, scheduling flags.</summary>
public sealed class BotAgent
{
    public int Id { get; }
    public string Name { get; set; }
    public string Job { get; set; }
    /// <summary>The game's personality trait (used when no persona is set).</summary>
    public string? Trait { get; set; }

    // Scheduling. Touched from the game thread; Pending is also cleared by the worker task.
    volatile bool pending;
    public bool Pending { get => pending; set => pending = value; }
    public double LastRequestAt { get; set; } = double.NegativeInfinity;
    public double NextUrgencyCheckAt { get; set; }
    public bool WakeUp { get; set; }

    /// <summary>Set by the worker when a request could not be made (limits/outage): retry soon, the turn was not used.</summary>
    volatile bool retrySoon;
    public bool RetrySoon { get => retrySoon; set => retrySoon = value; }

    /// <summary>A player handed this bot full AI control (mod order in the command menu).</summary>
    public bool AiControl { get; set; }
    public string? ControlBy { get; set; }

    // For !llm status / the settings window. Written by the worker thread, read on the game thread: plain values only.
    public volatile string? LastDecision;
    public double LastDecisionAt = double.NaN;
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

    readonly object sync = new();
    readonly List<string> notes = new();
    readonly List<LlmTurn> history = new();
    readonly List<string> results = new();
    readonly ConcurrentQueue<string> inbox = new();
    readonly ConcurrentQueue<PendingOrder> playerOrders = new();

    public const int MaxNoteLength = 200;
    public const int MaxResults = 10;
    public const int MaxInbox = 10;
    public const int MaxRoundLog = 40;

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

    public BotAgent(BotHandle h)
    {
        Id = h.Id;
        Name = h.Name;
        Job = h.Job;
        Trait = h.Trait;
    }

    readonly List<double> noteTimes = new();

    public void AddNote(string note, int max, double at = double.NaN)
    {
        lock (sync)
        {
            notes.Add(Tools.Clip(note, MaxNoteLength));
            noteTimes.Add(at);
            while (notes.Count > Math.Max(1, max)) { notes.RemoveAt(0); noteTimes.RemoveAt(0); }
        }
    }

    public List<string> Notes() { lock (sync) return notes.ToList(); }

    /// <summary>Notes for the model, older ones with their age ("(4 min ago) Player order: ..."), so an old order
    /// does not read like a fresh one.</summary>
    public List<string> Notes(double now)
    {
        lock (sync)
            return notes.Select((n, i) => !double.IsNaN(noteTimes[i]) && now - noteTimes[i] >= 60
                ? $"({ModStatus.Ago(now - noteTimes[i])}) {n}" : n).ToList();
    }

    public bool HasMail => !inbox.IsEmpty;

    public void Receive(string message)
    {
        inbox.Enqueue(message);
        while (inbox.Count > MaxInbox && inbox.TryDequeue(out _)) { }
    }

    /// <summary>A chat order from a human player that was routed to this bot. Processed before anything else.</summary>
    public bool HasPlayerOrder => !playerOrders.IsEmpty;

    /// <param name="authority">true = from a human player (may replace orders players gave through the command menu);
    /// false = from an AI crew member in command (binding, but player orders still come first).</param>
    public void ReceivePlayerOrder(string order, bool authority = true)
    {
        playerOrders.Enqueue(new PendingOrder(order, authority));
        while (playerOrders.Count > MaxInbox && playerOrders.TryDequeue(out _)) { }
    }

    public List<PendingOrder> DrainOrders()
    {
        var list = new List<PendingOrder>();
        while (playerOrders.TryDequeue(out var m)) list.Add(m);
        return list;
    }

    public List<string> DrainPlayerOrders() => DrainOrders().Select(o => o.Text).ToList();

    public List<string> DrainInbox()
    {
        var list = new List<string>();
        while (inbox.TryDequeue(out var m)) list.Add(m);
        return list;
    }

    public void RecordResult(string line)
    {
        lock (sync)
        {
            results.Add(line);
            while (results.Count > MaxResults) results.RemoveAt(0);
        }
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
    public void Restore(IEnumerable<string> inboxMessages, IEnumerable<string> actionResults, IEnumerable<PendingOrder>? orders = null)
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
    }

    // ---------------------------------------------------------------- goal and plan (game thread only)

    /// <summary>What the bot set out to do (set_goal, or a player's order). Shown every turn until done or replaced,
    /// so the intention survives the short history window.</summary>
    public string? Goal { get; private set; }
    public double GoalSetAt { get; private set; }

    public void SetGoal(string? goal, double now)
    {
        Goal = string.IsNullOrWhiteSpace(goal) ? null : Tools.Clip(goal.Trim(), MaxNoteLength);
        GoalSetAt = now;
    }

    /// <summary>Steps of the last reply that still wait for their turn (carried out one after another).</summary>
    public readonly List<PlanStep> Plan = new();
    public PlanWait Waiting { get; set; } = PlanWait.None;
    /// <summary>go_to_room step: the room to reach before the next step.</summary>
    public string? WaitRoom { get; set; }
    public double WaitingSince { get; set; }
    public double PlanStartedAt { get; set; }
    /// <summary>Urgent conditions already known when the plan was made: only new ones interrupt it.</summary>
    public string PlanUrgency { get; set; } = "";
    /// <summary>Urgency flags of the observation the current decision was made from.</summary>
    public string DecisionUrgency { get; set; } = "";
    public double NextArrivalCheckAt { get; set; }

    /// <summary>The last decision changed something (an order, a task, an item...), not only talk or "already doing that".</summary>
    public bool ActedSinceDecision { get; set; }
    /// <summary>Routine decisions in a row that changed nothing; each one stretches the time to the next routine decision.</summary>
    public int IdleStreak { get; set; }

    /// <summary>Carrying out a plan or waiting for a task: no new decision unless something important happens.</summary>
    public bool Busy => Plan.Count > 0 || Waiting != PlanWait.None;

    public string? PlanText()
    {
        if (!Busy) return null;
        var parts = new List<string>();
        if (Waiting == PlanWait.Task) parts.Add("now: waiting for the current task");
        else if (Waiting == PlanWait.Arrival) parts.Add($"now: walking to {WaitRoom}");
        parts.AddRange(Plan.Select((p, i) => $"{i + 1}. {p.Call.Name}({Tools.ArgsToString(p.Call)})"));
        return string.Join("; ", parts);
    }

    public void ClearPlan()
    {
        Plan.Clear();
        Waiting = PlanWait.None;
        WaitRoom = null;
    }

    public List<LlmTurn> HistorySnapshot() { lock (sync) return history.ToList(); }

    public void AddExchange(string observationJson, string assistantSummary, int maxTurns)
    {
        lock (sync)
        {
            history.Add(new LlmTurn("user", observationJson));
            history.Add(new LlmTurn("assistant", assistantSummary));
            while (history.Count > Math.Max(0, maxTurns) * 2) history.RemoveAt(0);
        }
    }
}
