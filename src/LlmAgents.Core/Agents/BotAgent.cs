using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using LlmAgents.Llm;

namespace LlmAgents.Agents;

/// <summary>An order waiting for the bot's next decision.</summary>
public sealed record PendingOrder(string Text, bool Authority);

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
    public const int MaxResults = 6;
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

    public void AddNote(string note, int max)
    {
        lock (sync)
        {
            notes.Add(Tools.Clip(note, MaxNoteLength));
            while (notes.Count > Math.Max(1, max)) notes.RemoveAt(0);
        }
    }

    public List<string> Notes() { lock (sync) return notes.ToList(); }

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
