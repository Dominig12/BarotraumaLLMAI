using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using LlmAgents.Llm;

namespace LlmAgents.Agents;

/// <summary>State of one LLM-driven bot: memory, inbox, history, scheduling flags.</summary>
public sealed class BotAgent
{
    public int Id { get; }
    public string Name { get; set; }
    public string Job { get; set; }

    // Scheduling. Touched from the game thread; Pending is also cleared by the worker task.
    volatile bool pending;
    public bool Pending { get => pending; set => pending = value; }
    public double LastRequestAt { get; set; } = double.NegativeInfinity;
    public double NextUrgencyCheckAt { get; set; }
    public bool WakeUp { get; set; }

    /// <summary>Set by the worker when a request could not be made (limits/outage): retry soon, the turn was not used.</summary>
    volatile bool retrySoon;
    public bool RetrySoon { get => retrySoon; set => retrySoon = value; }

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

    public const int MaxNoteLength = 200;
    public const int MaxResults = 6;
    public const int MaxInbox = 10;

    public BotAgent(BotHandle h)
    {
        Id = h.Id;
        Name = h.Name;
        Job = h.Job;
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
    public void Restore(IEnumerable<string> inboxMessages, IEnumerable<string> actionResults)
    {
        var pendingInbox = DrainInbox();
        foreach (var m in inboxMessages) inbox.Enqueue(m);
        foreach (var m in pendingInbox) inbox.Enqueue(m);
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
