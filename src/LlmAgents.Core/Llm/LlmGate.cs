using System;
using System.Collections.Generic;
using System.Threading;

namespace LlmAgents.Llm;

/// <summary>
/// Non-blocking limiter: concurrency, requests/minute, tokens/minute, daily budget.
/// Callers never wait for a slot: if the gate is closed the decision is skipped and the
/// bot keeps running on vanilla AI (or the fallback provider is tried).
/// </summary>
public sealed class LlmGate
{
    readonly int maxConcurrent, rpm, tpm, dailyBudget;
    readonly Func<DateTime> utcNow;
    readonly Queue<DateTime> requests = new();
    readonly Queue<(DateTime At, int Tokens)> tokens = new();
    readonly object sync = new();
    DateTime day;
    int dayCount, active;

    public LlmGate(int maxConcurrent, int requestsPerMinute, int tokensPerMinute = 0, int dailyBudget = 0, Func<DateTime>? utcNow = null)
    {
        this.maxConcurrent = Math.Max(1, maxConcurrent);
        rpm = requestsPerMinute;
        tpm = tokensPerMinute;
        this.dailyBudget = dailyBudget;
        this.utcNow = utcNow ?? (() => DateTime.UtcNow);
        day = this.utcNow().Date;
    }

    public int Active { get { lock (sync) return active; } }
    public int UsedToday { get { lock (sync) return dayCount; } }

    /// <summary>True if <see cref="TryEnter"/> would currently succeed. Does not reserve anything.</summary>
    public bool HasCapacity
    {
        get
        {
            lock (sync) return Check(utcNow()) == null;
        }
    }

    /// <summary>Part of the per-minute and daily limits kept for priority requests (players' orders, chat routing),
    /// so routine thinking can never use up the budget a player's order needs.</summary>
    static int Reserve(int limit, int share) => limit >= share ? Math.Max(1, limit / share) : 0;

    string? Check(DateTime now, bool priority = true)
    {
        if (now.Date != day) { day = now.Date; dayCount = 0; }
        Prune(now);
        if (dailyBudget > 0 && dayCount >= dailyBudget) return "daily budget exhausted";
        if (dailyBudget > 0 && !priority && dayCount >= dailyBudget - Reserve(dailyBudget, 20)) return "daily budget: the rest is kept for orders";
        if (rpm > 0 && requests.Count >= rpm) return "requests/minute limit";
        if (rpm > 0 && !priority && requests.Count >= rpm - Reserve(rpm, 5)) return "requests/minute limit (the rest is kept for orders)";
        if (tpm > 0)
        {
            long sum = 0;
            foreach (var t in tokens) sum += t.Tokens;
            if (sum >= tpm) return "tokens/minute limit";
        }
        if (active >= maxConcurrent) return "all slots busy";
        return null;
    }

    /// <param name="priority">false = routine work: leaves part of the limits to priority requests.</param>
    public Lease? TryEnter(out string denyReason, bool priority = true)
    {
        lock (sync)
        {
            var now = utcNow();
            var deny = Check(now, priority);
            if (deny != null) { denyReason = deny; return null; }

            active++;
            requests.Enqueue(now);
            dayCount++;
            denyReason = "";
            return new Lease(this);
        }
    }

    /// <summary>Takes over the requests of the last minute and today's count from the gate this one replaces (settings
    /// were reloaded): saving the settings must not reset a daily budget or a per-minute limit.</summary>
    public void CarryOver(LlmGate old)
    {
        if (ReferenceEquals(old, this)) return;
        DateTime[] reqs; (DateTime, int)[] toks; DateTime oldDay; int oldCount;
        lock (old.sync) { reqs = old.requests.ToArray(); toks = old.tokens.ToArray(); oldDay = old.day; oldCount = old.dayCount; }
        lock (sync)
        {
            foreach (var r in reqs) requests.Enqueue(r);
            foreach (var t in toks) tokens.Enqueue(t);
            if (oldDay == day) dayCount += oldCount;
        }
    }

    public void ReportTokens(int count)
    {
        if (count <= 0) return;
        lock (sync) tokens.Enqueue((utcNow(), count));
    }

    void Release()
    {
        lock (sync) active = Math.Max(0, active - 1);
    }

    void Prune(DateTime now)
    {
        var cutoff = now.AddMinutes(-1);
        while (requests.Count > 0 && requests.Peek() <= cutoff) requests.Dequeue();
        while (tokens.Count > 0 && tokens.Peek().At <= cutoff) tokens.Dequeue();
    }

    public sealed class Lease : IDisposable
    {
        LlmGate? owner;
        internal Lease(LlmGate owner) { this.owner = owner; }
        public void Dispose() => Interlocked.Exchange(ref owner, null)?.Release();
    }
}
