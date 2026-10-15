using System;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using LlmAgents.Agents;
using LlmAgents.Llm;
using Xunit;

namespace LlmAgents.Tests;

public class RouterTests
{
    static readonly LlmTurn[] Turns = { new("user", "{}") };

    [Fact]
    public async Task UsesPreferredProvider()
    {
        var h = new FakeHandler(_ => (HttpStatusCode.OK, Fixtures.OpenAiToolResponse(("wait", "{}"))));
        using var router = new LlmRouter(Fixtures.Settings(), h);
        var r = await router.CompleteAsync("a", "s", Turns, Tools.All, CancellationToken.None);
        Assert.Equal(RouteStatus.Ok, r.Status);
        Assert.Equal("a", r.Provider);
        Assert.StartsWith("http://a.test/", h.Requests.Single().Url);
    }

    [Fact]
    public async Task FallsBackWhenPreferredFails()
    {
        var h = new FakeHandler((req, _) => Task.FromResult(req.RequestUri!.Host == "a.test"
            ? (HttpStatusCode.InternalServerError, "boom")
            : (HttpStatusCode.OK, Fixtures.OpenAiToolResponse(("wait", "{}")))));
        using var router = new LlmRouter(Fixtures.Settings(), h);
        var r = await router.CompleteAsync("a", "s", Turns, Tools.All, CancellationToken.None);
        Assert.Equal(RouteStatus.Ok, r.Status);
        Assert.Equal("b", r.Provider);
        Assert.Equal(2, h.Requests.Count);
    }

    [Fact]
    public async Task FallsBackWhenPreferredIsRateLimited()
    {
        var providers = @"[
          { ""name"": ""a"", ""type"": ""openai"", ""baseUrl"": ""http://a.test/v1"", ""model"": ""m"", ""requestsPerMinute"": 1 },
          { ""name"": ""b"", ""type"": ""openai"", ""baseUrl"": ""http://b.test/v1"", ""model"": ""m"" } ]";
        var h = new FakeHandler(_ => (HttpStatusCode.OK, Fixtures.OpenAiToolResponse(("wait", "{}"))));
        using var router = new LlmRouter(Fixtures.Settings(providers: providers), h);
        Assert.Equal("a", (await router.CompleteAsync("a", "s", Turns, Tools.All, default)).Provider);
        Assert.Equal("b", (await router.CompleteAsync("a", "s", Turns, Tools.All, default)).Provider);
    }

    [Fact]
    public async Task ReportsFailureWhenAllProvidersFail()
    {
        var h = new FakeHandler(_ => (HttpStatusCode.BadGateway, "nope"));
        using var router = new LlmRouter(Fixtures.Settings(), h);
        var r = await router.CompleteAsync("a", "s", Turns, Tools.All, default);
        Assert.Equal(RouteStatus.Failed, r.Status);
        Assert.Contains("502", r.Detail);
    }

    [Fact]
    public async Task GlobalBudgetStopsEverything()
    {
        var h = new FakeHandler(_ => (HttpStatusCode.OK, Fixtures.OpenAiToolResponse(("wait", "{}"))));
        using var router = new LlmRouter(Fixtures.Settings(", \"dailyRequestBudget\": 1"), h);
        Assert.Equal(RouteStatus.Ok, (await router.CompleteAsync("a", "s", Turns, Tools.All, default)).Status);
        var r = await router.CompleteAsync("a", "s", Turns, Tools.All, default);
        Assert.Equal(RouteStatus.Limited, r.Status);
        Assert.Contains("daily budget", r.Detail);
        Assert.Single(h.Requests);
    }

    [Fact]
    public async Task UnknownPreferredUsesFallback()
    {
        var h = new FakeHandler(_ => (HttpStatusCode.OK, Fixtures.OpenAiToolResponse(("wait", "{}"))));
        using var router = new LlmRouter(Fixtures.Settings(), h);
        Assert.Equal("b", (await router.CompleteAsync("missing", "s", Turns, Tools.All, default)).Provider);
    }

    [Fact]
    public async Task CancellationPropagates()
    {
        var h = new FakeHandler(async (_, _, ct) => { await Task.Delay(5000, ct); return (HttpStatusCode.OK, "{}"); });
        using var router = new LlmRouter(Fixtures.Settings(), h);
        using var cts = new CancellationTokenSource(100);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => router.CompleteAsync("a", "s", Turns, Tools.All, cts.Token));
        Assert.Equal(0, router.GlobalGate.Active); // slots released
    }
}
