using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using LlmAgents.Agents;
using LlmAgents.Llm;
using LlmAgents.SettingsUi;
using Xunit;

namespace LlmAgents.Tests;

/// <summary>Fixes from the third in-game log: cut-off replies, mistyped commands, lost chat orders, idle chatter, proxies.</summary>
public class Log3FixesTests
{
    readonly FakeClock clock = new();
    readonly FakeGame game = new();

    [Theory]
    [InlineData("{\"calls\": [{\"name\": \"say\", \"arguments\":")]
    [InlineData("{\"calls\": [{\"name\": \"say\", \"arguments")]
    [InlineData("{\"calls\": [{\"name\": \"say\",")]
    public void CutOffJsonDoesNotBecomeASayCall(string raw)
    {
        var calls = TextToolCallParser.Parse(raw, Tools.All);
        Assert.DoesNotContain(calls, c => c.Name == "say");
    }

    [Fact]
    public void CutOffJsonKeepsTheCompleteCalls()
    {
        var calls = TextToolCallParser.Parse("{\"calls\": [{\"name\": \"say\", \"arguments\": {\"text\": \"Иду.\"}}, {\"name\": \"go_to_room\", \"arguments\": {\"room\": \"Инж", Tools.All);
        Assert.Equal("say", Assert.Single(calls).Name);
        Assert.Equal("Иду.", calls[0].Arguments["text"]!.GetValue<string>());
    }

    [Fact]
    public void TruncationIsDetectedForEveryProviderType()
    {
        var openai = OpenAiCompatibleProvider.ParseResponse(new JsonObject
        {
            ["choices"] = new JsonArray { new JsonObject { ["message"] = new JsonObject { ["content"] = "{\"calls\": [" }, ["finish_reason"] = "length" } },
            ["usage"] = new JsonObject { ["prompt_tokens"] = 10, ["completion_tokens"] = 300, ["completion_tokens_details"] = new JsonObject { ["reasoning_tokens"] = 280 } },
        }.ToJsonString(), Tools.All);
        Assert.True(openai.Truncated);
        Assert.Equal(280, openai.ReasoningTokens);

        var ollama = OllamaProvider.ParseResponse("{\"message\":{\"content\":\"x\"},\"done_reason\":\"length\"}", Tools.All);
        Assert.True(ollama.Truncated);

        var claude = AnthropicProvider.ParseResponse("{\"type\":\"message\",\"content\":[{\"type\":\"text\",\"text\":\"x\"}],\"stop_reason\":\"max_tokens\"}", Tools.All);
        Assert.True(claude.Truncated);

        var fine = OpenAiCompatibleProvider.ParseResponse(Fixtures.OpenAiToolResponse(("wait", "{}")), Tools.All);
        Assert.False(fine.Truncated);
    }

    [Fact]
    public async Task CutOffReplyIsReportedToTheBot()
    {
        game.AddBot(1, "Ann", "mechanic");
        int n = 0;
        var seen = new ConcurrentQueue<JsonObject>();
        var settings = Fixtures.Settings();
        var handler = new FakeHandler(body =>
        {
            var user = body["messages"]!.AsArray()[^1]!["content"]!.GetValue<string>();
            seen.Enqueue((JsonObject)JsonNode.Parse(Fixtures.Obs(user))!);
            return (HttpStatusCode.OK, n++ == 0
                ? new JsonObject { ["choices"] = new JsonArray { new JsonObject { ["message"] = new JsonObject { ["content"] = "{\"calls\": [{\"name\": \"say\", \"arguments\":" }, ["finish_reason"] = "length" } } }.ToJsonString()
                : Fixtures.OpenAiToolResponse(("wait", "{}")));
        });
        var mgr = new AgentManager(settings, game, new LlmRouter(settings, handler, clock.Utc), clock.Get);
        for (int i = 0; i < 30; i++) { mgr.Update(); await mgr.WhenIdle(); clock.Advance(0.5); }

        Assert.DoesNotContain(game.Actions, a => a.StartsWith("1:say"));
        Assert.True(seen.Count >= 2);
        Assert.Contains(seen.ElementAt(1)["lastActionResults"]!.AsArray(), r => r!.GetValue<string>().Contains("cut off"));
    }

    [Fact]
    public void ExtraBodyIsMergedIntoRequests()
    {
        var cfg = new ProviderConfig { Name = "x", Model = "m", ExtraBody = new JsonObject { ["reasoning"] = new JsonObject { ["enabled"] = false } } };
        var req = new LlmRequest("sys", new[] { new LlmTurn("user", "hi") }, Tools.All, 100, 0.5);
        Assert.False(OpenAiCompatibleProvider.BuildBody(cfg, req)["reasoning"]!["enabled"]!.GetValue<bool>());
        Assert.NotNull(OllamaProvider.BuildBody(cfg, req)["reasoning"]);
        Assert.NotNull(AnthropicProvider.BuildBody(cfg, req)["reasoning"]);
    }

    [Theory]
    [InlineData("http://127.0.0.1:10809", true)]
    [InlineData("socks5://127.0.0.1:1080", true)]
    [InlineData("", true)]
    [InlineData("127.0.0.1:1080", false)]
    [InlineData("ftp://x:1", false)]
    public void ProxyIsValidated(string proxy, bool ok)
    {
        var json = Fixtures.Config(providersOverride: $@"[{{ ""name"": ""a"", ""type"": ""openai"", ""baseUrl"": ""http://a.test/v1"", ""model"": ""m"", ""proxy"": ""{proxy}"" }},
            {{ ""name"": ""b"", ""type"": ""openai"", ""baseUrl"": ""http://b.test/v1"", ""model"": ""m"" }}]");
        if (ok)
        {
            var s = ModSettings.Parse(json);
            using var router = new LlmRouter(s); // real handlers with the proxy
            Assert.Equal(proxy, s.Providers[0].Proxy);
        }
        else Assert.ThrowsAny<Exception>(() => ModSettings.Parse(json));
    }

    [Theory]
    [InlineData("!ддь ыефегы", "!llm status")]
    [InlineData("!ДДЬ ыефегы", "!LLM status")]
    [InlineData("!llm status", "!llm status")]
    [InlineData("!ии статус", "!ии статус")]
    [InlineData("привет", "привет")]
    public void CommandsTypedInTheWrongLayoutAreUnderstood(string typed, string meant)
    {
        Assert.Equal(meant, SettingsService.NormalizeCommand(typed));
        Assert.Equal(meant.StartsWith("!"), SettingsService.IsCommand(typed));
    }

    [Fact]
    public async Task CommandLikeMessagesAreNotOrders()
    {
        game.AddBot(1, "Ann", "mechanic");
        var settings = Fixtures.Settings();
        int dispatches = 0;
        var handler = new FakeHandler(body =>
        {
            if (body["tools"]!.AsArray().Any(t => t!["function"]!["name"]!.GetValue<string>() == ChatDispatcher.ToolName)) dispatches++;
            return (HttpStatusCode.OK, Fixtures.OpenAiToolResponse(("wait", "{}")));
        });
        var mgr = new AgentManager(settings, game, new LlmRouter(settings, handler, clock.Utc), clock.Get);
        mgr.Update();
        mgr.SubmitChat(new ChatCommand("Daniil", null, "!kick bob", new[] { 1 }, true));
        for (int i = 0; i < 6; i++) { mgr.Update(); await mgr.WhenIdle(); clock.Advance(0.5); }
        Assert.Equal(0, dispatches);
        Assert.False(mgr.GetAgent(1)!.HasPlayerOrder);
    }

    [Fact]
    public async Task OrderGoesToEveryListenerWhenTheDispatcherGetsNoSlot()
    {
        game.AddBot(1, "Ann", "mechanic").AddBot(2, "Bob", "engineer");
        // two requests in total: the bots' first looks use them up, the dispatcher never gets a slot
        var settings = Fixtures.Settings(@", ""dailyRequestBudget"": 2");
        var handler = new FakeHandler(_ => (HttpStatusCode.OK, Fixtures.OpenAiToolResponse(("wait", "{}"))));
        var mgr = new AgentManager(settings, game, new LlmRouter(settings, handler, clock.Utc), clock.Get);
        for (int i = 0; i < 5; i++) { mgr.Update(); await mgr.WhenIdle(); clock.Advance(0.5); }
        mgr.SubmitChat(new ChatCommand("Daniil", null, "Bob, разбери сварочный аппарат", new[] { 1, 2 }, true));
        for (int i = 0; i < 30; i++) { mgr.Update(); await mgr.WhenIdle(); clock.Advance(0.5); }

        Assert.Equal(0, mgr.PendingChatCount);
        // delivered (no slot to act on it yet), not dropped; no rule guesses from the name: both heard it, each will decide
        Assert.True(mgr.GetAgent(2)!.HasPlayerOrder);
        Assert.True(mgr.GetAgent(1)!.HasPlayerOrder);
    }

    [Fact]
    public async Task IdleBotThinksLessOftenUntilSomethingHappens()
    {
        game.AddBot(1, "Ann", "mechanic");
        var settings = Fixtures.Settings(@", ""maxIdleIntervalSeconds"": 40");
        settings.Bots.Autonomy = "always";   // heartbeat every 10 s when nothing happens
        var times = new ConcurrentQueue<double>();
        var handler = new FakeHandler(_ =>
        {
            times.Enqueue(clock.Now);
            return (HttpStatusCode.OK, Fixtures.OpenAiToolResponse(("say", "{\"text\":\"Жду\"}"), ("wait", "{}")));
        });
        game.SimpleOrderResult = "ok (already doing that)";
        var mgr = new AgentManager(settings, game, new LlmRouter(settings, handler, clock.Utc), clock.Get);
        for (int i = 0; i < 400; i++) { mgr.Update(); await mgr.WhenIdle(); clock.Advance(0.5); }   // 200 s

        var gaps = times.Zip(times.Skip(1), (a, b) => b - a).ToList();
        Assert.True(gaps[0] < 12, string.Join(", ", gaps));          // the normal 10 s first
        Assert.True(gaps[^1] >= 39, string.Join(", ", gaps));        // then up to the 40 s limit
        Assert.True(times.Count <= 9, string.Join(", ", gaps));      // instead of ~20 requests

        // an order brings the normal pace back
        int before = times.Count;
        mgr.GetAgent(1)!.ReceivePlayerOrder("Daniil: fix the leaks");
        game.SimpleOrderResult = "ok";
        for (int i = 0; i < 4; i++) { mgr.Update(); await mgr.WhenIdle(); clock.Advance(0.5); }
        Assert.Equal(before + 1, times.Count);
        Assert.Equal(0, mgr.GetAgent(1)!.IdleStreak);
    }

    [Fact]
    public async Task ComeToIsAOneTimeTaskAndOldOrdersShowTheirAge()
    {
        game.AddBot(1, "Ann", "mechanic");
        var settings = Fixtures.Settings(@", ""goalTimeoutMinutes"": 3");
        var seen = new ConcurrentQueue<JsonObject>();
        int n = 0;
        var handler = new FakeHandler(body =>
        {
            var user = body["messages"]!.AsArray()[^1]!["content"]!.GetValue<string>();
            seen.Enqueue((JsonObject)JsonNode.Parse(Fixtures.Obs(user))!);
            return (HttpStatusCode.OK, n++ == 1
                ? Fixtures.OpenAiToolResponse(("say", "{\"text\":\"Иду\"}"), ("come_to", "{\"target\":\"Daniil\"}"), ("repair_systems", "{}"))
                : Fixtures.OpenAiToolResponse(("wait", "{}")));
        });
        var mgr = new AgentManager(settings, game, new LlmRouter(settings, handler, clock.Utc), clock.Get);
        async Task Run(double s) { for (double t = 0; t < s; t += 0.5) { mgr.Update(); await mgr.WhenIdle(); clock.Advance(0.5); } }

        await Run(1);
        mgr.GetAgent(1)!.ReceivePlayerOrder("Daniil (Engineer) on radio: \"Все ко мне\" ('me' = Daniil) Hint: probably come_to(target=\"Daniil\").");
        await Run(3);
        Assert.Contains("1:come to Daniil", game.Actions);
        Assert.DoesNotContain(game.Actions, a => a.Contains("repair_systems")); // waits until the task reports
        var goal = mgr.GetAgent(1)!.Goal!;
        Assert.Contains("Все ко мне", goal);
        Assert.DoesNotContain("Hint", goal);

        game.PendingReports.Add(new GameReport(1, "task 'come to Daniil' ok: you came to Daniil and stayed 90s. This order is DONE"));
        await Run(2);
        Assert.Contains("1:repair_systems", game.Actions);

        // minutes later the old order is shown with its age, and the stale goal is dropped
        await Run(200);
        mgr.GetAgent(1)!.ReceivePlayerOrder("Daniil: status?");
        await Run(2);
        var last = seen.Last();
        Assert.Contains(last["earlierOrders"]!.AsArray(), x => x!.GetValue<string>().StartsWith("(") && x.GetValue<string>().Contains("Все ко мне"));
        Assert.Contains(seen, o => o["lastActionResults"] is JsonArray r && r.Any(x => x!.GetValue<string>().Contains("expired")));
        Assert.Null(last["goal"]);
    }
}
