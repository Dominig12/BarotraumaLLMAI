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

    string? Check(DateTime now)
    {
        if (now.Date != day) { day = now.Date; dayCount = 0; }
        Prune(now);
        if (dailyBudget > 0 && dayCount >= dailyBudget) return "daily budget exhausted";
        if (rpm > 0 && requests.Count >= rpm) return "requests/minute limit";
        if (tpm > 0)
        {
            long sum = 0;
            foreach (var t in tokens) sum += t.Tokens;
            if (sum >= tpm) return "tokens/minute limit";
        }
        if (active >= maxConcurrent) return "all slots busy";
        return null;
    }

    public Lease? TryEnter(out string denyReason)
    {
        lock (sync)
        {
            var now = utcNow();
            var deny = Check(now);
            if (deny != null) { denyReason = deny; return null; }

            active++;
            requests.Enqueue(now);
            dayCount++;
            denyReason = "";
            return new Lease(this);
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
