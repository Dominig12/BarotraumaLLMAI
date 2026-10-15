using System;
using LlmAgents.Llm;
using Xunit;

namespace LlmAgents.Tests;

public class LlmGateTests
{
    DateTime now = new(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Concurrency()
    {
        var g = new LlmGate(2, 0, utcNow: () => now);
        var a = g.TryEnter(out _);
        var b = g.TryEnter(out _);
        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.Null(g.TryEnter(out var why));
        Assert.Equal("all slots busy", why);
        a!.Dispose();
        a.Dispose(); // double dispose must not free two slots
        Assert.Equal(1, g.Active);
        Assert.NotNull(g.TryEnter(out _));
        Assert.Null(g.TryEnter(out _));
    }

    [Fact]
    public void RequestsPerMinuteSlidingWindow()
    {
        var g = new LlmGate(10, 3, utcNow: () => now);
        for (int i = 0; i < 3; i++) g.TryEnter(out _)!.Dispose();
        Assert.Null(g.TryEnter(out var why));
        Assert.Equal("requests/minute limit", why);
        now = now.AddSeconds(59);
        Assert.Null(g.TryEnter(out _));
        now = now.AddSeconds(1.5);
        Assert.NotNull(g.TryEnter(out _));
    }

    [Fact]
    public void TokensPerMinute()
    {
        var g = new LlmGate(10, 0, tokensPerMinute: 1000, utcNow: () => now);
        g.TryEnter(out _)!.Dispose();
        g.ReportTokens(999);
        Assert.NotNull(g.TryEnter(out _));
        g.ReportTokens(5);
        Assert.Null(g.TryEnter(out var why));
        Assert.Equal("tokens/minute limit", why);
        now = now.AddMinutes(1.01);
        Assert.NotNull(g.TryEnter(out _));
    }

    [Fact]
    public void DailyBudgetResetsAtUtcMidnight()
    {
        now = new DateTime(2026, 10, 15, 23, 59, 0, DateTimeKind.Utc);
        var g = new LlmGate(10, 0, dailyBudget: 2, utcNow: () => now);
        g.TryEnter(out _)!.Dispose();
        g.TryEnter(out _)!.Dispose();
        Assert.Null(g.TryEnter(out var why));
        Assert.Equal("daily budget exhausted", why);
        now = now.AddMinutes(2);
        Assert.NotNull(g.TryEnter(out _));
        Assert.Equal(1, g.UsedToday);
    }
}
