using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using LlmAgents.Agents;
using LlmAgents.Llm;
using Xunit;

namespace LlmAgents.Tests;

public class AgentManagerTests
{
    readonly FakeClock clock = new();
    readonly FakeGame game = new();

    /// <summary>Scripted LLM: per bot name, what to answer. Records the observation each bot received.</summary>
    sealed class ScriptedLlm
    {
        public readonly ConcurrentDictionary<string, Func<JsonObject, string>> ByBot = new();
        public readonly ConcurrentQueue<(string Bot, JsonObject Observation, JsonArray Messages)> Seen = new();
        public Func<JsonObject, string> Default = _ => Fixtures.OpenAiToolResponse(("wait", "{}"));
        /// <summary>Answer for dispatcher (route_message) requests; gets the user prompt text.</summary>
        public Func<string, string> Dispatcher = _ => Fixtures.OpenAiToolResponse((ChatDispatcher.ToolName, "{\"recipients\":[]}"));
        public readonly ConcurrentQueue<string> DispatchPrompts = new();

        public FakeHandler Handler() => new(body =>
        {
            var msgs = body["messages"]!.AsArray();
            if (body["tools"]!.AsArray().Any(t => t!["function"]!["name"]!.GetValue<string>() == ChatDispatcher.ToolName))
            {
                var prompt = msgs[^1]!["content"]!.GetValue<string>();
                DispatchPrompts.Enqueue(prompt);
                return (HttpStatusCode.OK, Dispatcher(prompt));
            }
            var obs = (JsonObject)JsonNode.Parse(Fixtures.Obs(msgs[^1]!["content"]!.GetValue<string>()))!;
            var bot = obs["name"]!.GetValue<string>();
            Seen.Enqueue((bot, obs, msgs));
            var f = ByBot.TryGetValue(bot, out var x) ? x : Default;
            return (HttpStatusCode.OK, f(obs));
        });
    }

    (AgentManager mgr, ScriptedLlm llm) Create(string? global = null, string extraRoot = "")
    {
        var settings = Fixtures.Settings(global, extraRoot: extraRoot);
        var llm = new ScriptedLlm();
        var mgr = new AgentManager(settings, game, new LlmRouter(settings, llm.Handler(), clock.Utc), clock.Get);
        return (mgr, llm);
    }

    async Task Step(AgentManager mgr, double seconds = 0.5)
    {
        mgr.Update();
        await mgr.WhenIdle();
        clock.Advance(seconds);
    }

    [Fact]
    public async Task DecidesThenExecutesOnNextUpdate()
    {
        game.AddBot(1, "Ann", "mechanic", o => o.Leaks.Add("Engine Room [h10]"));
        var (mgr, llm) = Create();
        llm.ByBot["Ann"] = _ => Fixtures.OpenAiToolResponse(("fix_leaks", "{}"), ("say", "{\"text\":\"Welding!\"}"));

        await Step(mgr);
        Assert.Empty(game.Actions);              // answer arrives asynchronously...
        await Step(mgr);
        Assert.Equal(new[] { "1:fix_leaks", "1:say Welding!" }, game.Actions); // ...and runs on the game thread

        var seen = llm.Seen.Single();
        Assert.Equal("mechanic", seen.Observation["job"]!.GetValue<string>());
        Assert.Equal("system", seen.Messages[0]!["role"]!.GetValue<string>());
        Assert.Contains("Ann", seen.Messages[0]!["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task RespectsDecisionIntervalAndFeedsBackResults()
    {
        game.AddBot(1, "Ann", "mechanic");
        var (mgr, llm) = Create();
        llm.ByBot["Ann"] = _ => Fixtures.OpenAiToolResponse(("go_to_room", "{\"room\":\"Nowhere\"}"));

        for (int i = 0; i < 19; i++) await Step(mgr); // 9.5 s: one decision only
        Assert.Single(llm.Seen);
        for (int i = 0; i < 2; i++) await Step(mgr);  // passes the 10 s interval
        Assert.Equal(2, llm.Seen.Count);

        var second = llm.Seen.Last();
        var results = second.Observation["lastActionResults"]!.AsArray().Select(x => x!.GetValue<string>()).ToList();
        Assert.Contains(results, r => r.Contains("error: unknown room"));
        // history: system + (user, assistant) + current user
        Assert.Equal(4, second.Messages.Count);
        Assert.Equal("assistant", second.Messages[2]!["role"]!.GetValue<string>());
        Assert.Contains("go_to_room", second.Messages[2]!["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task UrgentSituationTriggersEarlyDecision()
    {
        game.AddBot(1, "Ann", "mechanic");
        var (mgr, llm) = Create();
        await Step(mgr);
        Assert.Single(llm.Seen);

        for (int i = 0; i < 4; i++) await Step(mgr); // 2 s later: min gap passed, nothing urgent
        Assert.Single(llm.Seen);

        game.Observations[1].RoomFires = 2;
        for (int i = 0; i < 3; i++) await Step(mgr); // within ~urgencyCheckSeconds
        Assert.Equal(2, llm.Seen.Count);
        Assert.Equal(2, llm.Seen.Last().Observation["roomFires"]!.GetValue<int>());
    }

    [Fact]
    public async Task MinimumGapBetweenCallsEvenWhenUrgent()
    {
        game.AddBot(1, "Ann", "mechanic", o => o.HealthPercent = 10);
        var (mgr, llm) = Create();
        for (int i = 0; i < 3; i++) await Step(mgr); // 1.5 s < minSecondsBetweenCallsPerBot (2)
        Assert.Single(llm.Seen);
    }

    [Fact]
    public async Task BotsMessageEachOtherAndRecipientWakesUp()
    {
        game.AddBot(1, "Ann", "captain").AddBot(2, "Bob", "engineer");
        var (mgr, llm) = Create();
        llm.ByBot["Ann"] = _ => Fixtures.OpenAiToolResponse(("message_crew", "{\"to\":\"bob\",\"text\":\"Check the reactor\"}"));

        await Step(mgr); // both decide
        await Step(mgr); // Ann's message is delivered
        for (int i = 0; i < 4; i++) await Step(mgr); // 2 s later: mail does not wake Bob at once (no "Roger!" ping-pong)
        Assert.Single(llm.Seen, s => s.Bot == "Bob");
        for (int i = 0; i < 8; i++) await Step(mgr); // by half the decision interval (5 s) he reads it

        var bobCalls = llm.Seen.Where(s => s.Bot == "Bob").ToList();
        Assert.Equal(2, bobCalls.Count);
        var inbox = bobCalls[1].Observation["inbox"]!.AsArray().Select(x => x!.GetValue<string>());
        Assert.Contains("Ann: Check the reactor", inbox);
        Assert.Single(llm.Seen, s => s.Bot == "Ann"); // Ann has no reason to call again yet
    }

    [Fact]
    public async Task MessageToUnknownCrewIsAnError()
    {
        game.AddBot(1, "Ann", "captain");
        var (mgr, _) = Create();
        mgr.Update();
        var ann = mgr.GetAgent(1)!;
        Assert.StartsWith("error:", mgr.DeliverMessage(ann, "Zed", "hi"));
        Assert.StartsWith("error:", mgr.DeliverMessage(ann, "all", "hi"));
        await mgr.WhenIdle();
    }

    [Fact]
    public async Task ProviderOutageKeepsVanillaBehaviour()
    {
        game.AddBot(1, "Ann", "mechanic");
        var settings = Fixtures.Settings();
        var handler = new FakeHandler(_ => (HttpStatusCode.ServiceUnavailable, "down"));
        var mgr = new AgentManager(settings, game, new LlmRouter(settings, handler, clock.Utc), clock.Get);
        for (int i = 0; i < 30; i++) await Step(mgr);
        Assert.Empty(game.Actions);
        Assert.False(mgr.GetAgent(1)!.Pending);
        Assert.True(handler.Requests.Count >= 2); // keeps retrying on schedule, primary + fallback
    }

    [Fact]
    public async Task MaxActionsPerDecisionIsEnforced()
    {
        game.AddBot(1, "Ann", "mechanic");
        var (mgr, llm) = Create();
        llm.ByBot["Ann"] = _ => Fixtures.OpenAiToolResponse(
            ("fix_leaks", "{}"), ("repair_systems", "{}"), ("extinguish_fires", "{}"), ("wait", "{}"), ("clean_up", "{}"));
        await Step(mgr);
        await Step(mgr);
        Assert.Equal(3, game.Actions.Count);
    }

    [Fact]
    public async Task DeadBotsAreDroppedAndTheirCommandsIgnored()
    {
        game.AddBot(1, "Ann", "mechanic");
        var (mgr, llm) = Create();
        llm.ByBot["Ann"] = _ => Fixtures.OpenAiToolResponse(("fix_leaks", "{}"));
        await Step(mgr);
        game.Dead.Add(1);
        await Step(mgr);
        Assert.Empty(game.Actions);
        Assert.Empty(mgr.Agents);
    }

    [Fact]
    public async Task ResetDropsLateAnswers()
    {
        game.AddBot(1, "Ann", "mechanic");
        var settings = Fixtures.Settings();
        var gate = new SemaphoreSlim(0);
        var handler = new FakeHandler(async (_, _, ct) =>
        {
            await gate.WaitAsync(ct);
            return (HttpStatusCode.OK, Fixtures.OpenAiToolResponse(("fix_leaks", "{}")));
        });
        var mgr = new AgentManager(settings, game, new LlmRouter(settings, handler, clock.Utc), clock.Get);
        mgr.Update();              // request in flight
        mgr.Reset();               // new round
        gate.Release(5);
        await mgr.WhenIdle();
        clock.Advance(1);
        mgr.Update();
        Assert.Empty(game.Actions);
    }

    [Fact]
    public async Task DisabledJobsAndGlobalSwitch()
    {
        game.AddBot(1, "Ann", "assistant").AddBot(2, "Bob", "mechanic");
        var (mgr, llm) = Create();
        mgr.Settings.Bots.DisabledJobs.Add("assistant");
        await Step(mgr);
        Assert.Equal(new[] { "Bob" }, llm.Seen.Select(s => s.Bot));

        var (off, offLlm) = Create(extraRoot: "\"enabled\": false,");
        await Step(off);
        Assert.Empty(offLlm.Seen);
    }

    [Fact]
    public async Task ProviderByJobRoutesRequests()
    {
        game.AddBot(1, "Ann", "captain").AddBot(2, "Bob", "mechanic");
        var settings = Fixtures.Settings();
        settings.Bots.ProviderByJob["captain"] = "b";
        var handler = new FakeHandler(_ => (HttpStatusCode.OK, Fixtures.OpenAiToolResponse(("wait", "{}"))));
        var mgr = new AgentManager(settings, game, new LlmRouter(settings, handler, clock.Utc), clock.Get);
        await Step(mgr);
        var byHost = handler.Requests.ToDictionary(
            r => JsonNode.Parse(Fixtures.Obs(r.Body["messages"]!.AsArray()[^1]!["content"]!.GetValue<string>()))!["name"]!.GetValue<string>(),
            r => new Uri(r.Url).Host);
        Assert.Equal("b.test", byHost["Ann"]);
        Assert.Equal("a.test", byHost["Bob"]);
        Assert.Equal("m-b", handler.Requests.First(r => r.Url.Contains("b.test")).Body["model"]!.GetValue<string>());
    }

    [Fact]
    public async Task ExtraPromptByJobIsAppended()
    {
        game.AddBot(1, "Ann", "captain");
        var (mgr, llm) = Create();
        mgr.Settings.Bots.ExtraPromptByJob["Captain"] = "KEEP THE SUB ON COURSE";
        await Step(mgr);
        Assert.Contains("KEEP THE SUB ON COURSE", llm.Seen.Single().Messages[0]!["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task ObservationIsNotTakenEveryTick()
    {
        game.AddBot(1, "Ann", "mechanic");
        var (mgr, _) = Create();
        for (int i = 0; i < 16; i++) await Step(mgr); // 8 s of game time, 16 ticks
        Assert.InRange(game.ObserveCalls, 1, 8);
    }

    [Fact]
    public async Task BusySlotsDoNotCostTheTurnOrTheInbox()
    {
        game.AddBot(1, "Ann", "captain").AddBot(2, "Bob", "engineer");
        var providers = @"[ { ""name"": ""a"", ""type"": ""openai"", ""baseUrl"": ""http://a.test/v1"", ""model"": ""m"", ""maxConcurrent"": 1 } ]";
        var settings = ModSettings.Parse(Fixtures.Config(providersOverride: providers).Replace("\"fallbackProvider\": \"b\"", "\"fallbackProvider\": \"\""));
        var release = new SemaphoreSlim(0);
        var seen = new ConcurrentQueue<string>();
        var handler = new FakeHandler(async (_, body, ct) =>
        {
            var obs = JsonNode.Parse(Fixtures.Obs(body["messages"]!.AsArray()[^1]!["content"]!.GetValue<string>()))!;
            seen.Enqueue(obs["name"]!.GetValue<string>());
            await release.WaitAsync(ct);
            return (HttpStatusCode.OK, Fixtures.OpenAiToolResponse(("wait", "{}")));
        });
        var mgr = new AgentManager(settings, game, new LlmRouter(settings, handler, clock.Utc), clock.Get);

        mgr.Update();                           // Ann takes the only slot, Bob is skipped without losing his turn
        SpinWait.SpinUntil(() => !seen.IsEmpty && !mgr.GetAgent(2)!.Pending, 2000);
        Assert.Equal(new[] { "Ann" }, seen);
        Assert.False(mgr.GetAgent(2)!.Pending);
        mgr.DeliverMessage(mgr.GetAgent(1)!, "Bob", "reactor!");

        release.Release();
        await mgr.WhenIdle();
        clock.Advance(1.5);                    // > retrySeconds, < decision interval
        mgr.Update();
        SpinWait.SpinUntil(() => seen.Count >= 2, 2000);
        Assert.Equal(new[] { "Ann", "Bob" }, seen);
        release.Release();
        await mgr.WhenIdle();
    }

    [Fact]
    public async Task InboxSurvivesFailedRequest()
    {
        game.AddBot(1, "Ann", "captain").AddBot(2, "Bob", "engineer");
        var settings = Fixtures.Settings();
        bool down = true;
        var inboxSeen = new ConcurrentQueue<string>();
        var handler = new FakeHandler(body =>
        {
            var obs = JsonNode.Parse(Fixtures.Obs(body["messages"]!.AsArray()[^1]!["content"]!.GetValue<string>()))!;
            if (obs["name"]!.GetValue<string>() == "Bob" && obs["inbox"] is JsonArray a)
                foreach (var m in a) inboxSeen.Enqueue(m!.GetValue<string>());
            return down ? (HttpStatusCode.ServiceUnavailable, "") : (HttpStatusCode.OK, Fixtures.OpenAiToolResponse(("wait", "{}")));
        });
        var mgr = new AgentManager(settings, game, new LlmRouter(settings, handler, clock.Utc), clock.Get);
        mgr.Update();
        await mgr.WhenIdle();
        mgr.DeliverMessage(mgr.GetAgent(1)!, "Bob", "pump the ballast");
        clock.Advance(2.5); mgr.Update(); await mgr.WhenIdle();   // Bob reads it, provider is down
        down = false;
        clock.Advance(11); mgr.Update(); await mgr.WhenIdle();    // next try still carries the message
        // while the providers were paused the message waited in the inbox; it reaches the model once they recover
        Assert.Contains("Ann: pump the ballast", inboxSeen);
    }

    [Fact]
    public void MessagesAreRateLimitedPerBot()
    {
        game.AddBot(1, "Ann", "captain").AddBot(2, "Bob", "engineer");
        var (mgr, _) = Create();
        mgr.Update();
        var ann = mgr.GetAgent(1)!;
        for (int i = 0; i < 3; i++) Assert.StartsWith("ok", mgr.DeliverMessage(ann, "Bob", "m" + i));
        Assert.StartsWith("error:", mgr.DeliverMessage(ann, "Bob", "too many"));
        clock.Advance(61);
        Assert.StartsWith("ok", mgr.DeliverMessage(ann, "Bob", "again"));
    }

    [Fact]
    public async Task GemmaTextRepliesAreExecutedAndHistoryStaysClean()
    {
        game.AddBot(1, "Alan Beltran", "medicaldoctor");
        var (mgr, llm) = Create();
        int n = 0;
        llm.ByBot["Alan Beltran"] = _ => n++ == 0
            ? Fixtures.OpenAiTextResponse("go_to_room{room:<|\"|>Engine Room [h10]<|\"|>}<tool_call|>")
            : Fixtures.OpenAiTextResponse("// Okay, I'm thinking about what to do next, the reactor is fine.");
        await Step(mgr); await Step(mgr);
        Assert.Contains("1:goto Engine Room [h10]", game.Actions);

        for (int i = 0; i < 21; i++) await Step(mgr);   // second decision: prose only
        for (int i = 0; i < 21; i++) await Step(mgr);   // third decision sees the feedback
        var third = llm.Seen.Where(s => s.Bot == "Alan Beltran").ElementAt(2);
        var assistantTurns = third.Messages.Where(m => m!["role"]!.GetValue<string>() == "assistant").Select(m => m!["content"]!.GetValue<string>()).ToList();
        Assert.Equal(new[] { "go_to_room(room=\"Engine Room [h10]\")", "(no tool call)" }, assistantTurns);
        Assert.Contains(third.Observation["lastActionResults"]!.AsArray(), x => x!.GetValue<string>().Contains("no valid tool call"));
    }
}
