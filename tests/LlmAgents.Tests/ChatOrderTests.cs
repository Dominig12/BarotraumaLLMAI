using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using LlmAgents.Agents;
using LlmAgents.Llm;
using Xunit;

namespace LlmAgents.Tests;

/// <summary>Player chat orders: dispatcher picks recipients, then every recipient handles the order itself.</summary>
public class ChatOrderTests
{
    readonly FakeClock clock = new();
    readonly FakeGame game = new FakeGame()
        .AddBot(1, "Ivan Petrov", "captain")
        .AddBot(2, "Max Volkov", "mechanic")
        .AddBot(3, "Oleg Sidorov", "mechanic")
        .AddBot(4, "Anna Kuznetsova", "assistant");

    /// <summary>Records which bot got which playerOrders; dispatcher answers are scripted.</summary>
    sealed class Llm
    {
        public Func<string, string> Dispatch = _ => Fixtures.OpenAiToolResponse((ChatDispatcher.ToolName, "{\"recipients\":[]}"));
        public HttpStatusCode DispatchStatus = HttpStatusCode.OK;
        public readonly ConcurrentQueue<string> DispatchPrompts = new();
        public readonly ConcurrentQueue<(string Bot, List<string> Orders, string System)> Decisions = new();

        public FakeHandler Handler() => new(body =>
        {
            var msgs = body["messages"]!.AsArray();
            var content = msgs[^1]!["content"]!.GetValue<string>();
            bool isDispatch = body["tools"]!.AsArray().Any(t => t!["function"]!["name"]!.GetValue<string>() == ChatDispatcher.ToolName);
            if (isDispatch)
            {
                DispatchPrompts.Enqueue(content);
                return (DispatchStatus, DispatchStatus == HttpStatusCode.OK ? Dispatch(content) : "down");
            }
            var obs = JsonNode.Parse(content)!;
            var orders = obs["playerOrders"]?.AsArray().Select(x => x!.GetValue<string>()).ToList() ?? new();
            Decisions.Enqueue((obs["name"]!.GetValue<string>(), orders, msgs[0]!["content"]!.GetValue<string>()));
            return (HttpStatusCode.OK, orders.Count > 0
                ? Fixtures.OpenAiToolResponse(("say", "{\"text\":\"Есть!\"}"), ("fix_leaks", "{}"))
                : Fixtures.OpenAiToolResponse(("wait", "{}")));
        });

        public List<string> OrdersOf(string bot) => Decisions.Where(d => d.Bot == bot).SelectMany(d => d.Orders).ToList();
    }

    (AgentManager, Llm) Create(string extraRoot = "")
    {
        var settings = Fixtures.Settings(extraRoot: extraRoot);
        var llm = new Llm();
        return (new AgentManager(settings, game, new LlmRouter(settings, llm.Handler(), clock.Utc), clock.Get), llm);
    }

    async Task Run(AgentManager mgr, double seconds)
    {
        for (double t = 0; t < seconds; t += 0.5)
        {
            mgr.Update();
            await mgr.WhenIdle();
            clock.Advance(0.5);
        }
    }

    static ChatCommand Chat(string text, params int[] listeners) =>
        new("Daniil", "Captain", text, listeners.Length > 0 ? listeners : new[] { 1, 2, 3, 4 }, ViaRadio: true);

    [Fact]
    public async Task DispatcherPicksRecipientAndOnlyTheyGetTheOrder()
    {
        var (mgr, llm) = Create();
        llm.Dispatch = _ => Fixtures.OpenAiToolResponse((ChatDispatcher.ToolName, "{\"recipients\":[\"Anna Kuznetsova\"],\"reason\":\"named\"}"));
        await Run(mgr, 1);                         // initial decisions done
        mgr.SubmitChat(Chat("Анна, заделай течь в балласте"));
        await Run(mgr, 2);

        var prompt = llm.DispatchPrompts.Single();
        Assert.Contains("Анна, заделай течь", prompt);
        Assert.Contains("Oleg Sidorov | job: mechanic", prompt);

        var order = Assert.Single(llm.OrdersOf("Anna Kuznetsova"));
        Assert.Contains("Daniil (Captain) on radio: \"Анна, заделай течь в балласте\"", order);
        Assert.Empty(llm.OrdersOf("Max Volkov"));
        Assert.Empty(llm.OrdersOf("Ivan Petrov"));
        Assert.Contains("4:fix_leaks", game.Actions);
        Assert.Contains("4:say Есть!", game.Actions);
    }

    [Fact]
    public async Task OrderToAJobReachesEveryBotWithThatJob()
    {
        var (mgr, llm) = Create();
        llm.Dispatch = _ => Fixtures.OpenAiToolResponse((ChatDispatcher.ToolName, "{\"recipients\":[\"Max Volkov\",\"Oleg Sidorov\"]}"));
        await Run(mgr, 1);
        mgr.SubmitChat(Chat("Механики, чините насосы"));
        await Run(mgr, 2);

        Assert.Contains("also sent to: Oleg Sidorov", Assert.Single(llm.OrdersOf("Max Volkov")));
        Assert.Contains("also sent to: Max Volkov", Assert.Single(llm.OrdersOf("Oleg Sidorov")));
        Assert.Empty(llm.OrdersOf("Anna Kuznetsova"));
    }

    [Fact]
    public async Task PlayerOrderIsHandledImmediatelyDespiteMinimumGap()
    {
        var (mgr, llm) = Create();
        llm.Dispatch = _ => Fixtures.OpenAiToolResponse((ChatDispatcher.ToolName, "{\"recipients\":[\"Max Volkov\"]}"));
        mgr.Update(); await mgr.WhenIdle();       // t=0: everyone decided, min gap is 2 s
        clock.Advance(0.2);
        mgr.SubmitChat(Chat("Макс, за мной"));
        var t0 = clock.Now;
        while (llm.OrdersOf("Max Volkov").Count == 0 && clock.Now - t0 < 5)
        {
            mgr.Update(); await mgr.WhenIdle(); clock.Advance(0.1);
        }
        Assert.Single(llm.OrdersOf("Max Volkov"));
        Assert.True(clock.Now - t0 <= 1.3, $"took {clock.Now - t0:0.0}s");
    }

    [Fact]
    public async Task OrderIsRememberedAfterItWasHandled()
    {
        var (mgr, llm) = Create();
        llm.Dispatch = _ => Fixtures.OpenAiToolResponse((ChatDispatcher.ToolName, "{\"recipients\":[\"Max Volkov\"]}"));
        await Run(mgr, 1);
        mgr.SubmitChat(Chat("Макс, держи реактор"));
        await Run(mgr, 2);
        Assert.Contains(mgr.GetAgent(2)!.Notes(), n => n.StartsWith("Player order:") && n.Contains("держи реактор"));
    }

    [Fact]
    public async Task SingleListenerSkipsTheDispatcher()
    {
        var (mgr, llm) = Create();
        await Run(mgr, 1);
        mgr.SubmitChat(Chat("эй, помоги", 3));
        await Run(mgr, 1.5);
        Assert.Empty(llm.DispatchPrompts);
        Assert.Single(llm.OrdersOf("Oleg Sidorov"));
    }

    [Fact]
    public async Task NobodyHeardIt()
    {
        var (mgr, llm) = Create();
        mgr.SubmitChat(new ChatCommand("Daniil", null, "hello?", Array.Empty<int>(), false));
        await Run(mgr, 2);
        Assert.Empty(llm.DispatchPrompts);
        Assert.All(llm.Decisions, d => Assert.Empty(d.Orders));
    }

    [Fact]
    public async Task ChatterGoesToNobody()
    {
        var (mgr, llm) = Create();
        llm.Dispatch = _ => Fixtures.OpenAiToolResponse((ChatDispatcher.ToolName, "{\"recipients\":[],\"reason\":\"small talk\"}"));
        await Run(mgr, 1);
        mgr.SubmitChat(Chat("lol"));
        await Run(mgr, 2);
        Assert.Single(llm.DispatchPrompts);
        Assert.All(llm.Decisions, d => Assert.Empty(d.Orders));
    }

    [Fact]
    public async Task DispatcherOutageFallsBackToNamesInTheText()
    {
        var (mgr, llm) = Create();
        llm.DispatchStatus = HttpStatusCode.InternalServerError;
        await Run(mgr, 1);
        mgr.SubmitChat(Chat("Oleg, check the pumps"));
        await Run(mgr, 8);   // both providers are paused ~5 s after the dispatcher errors
        Assert.Single(llm.OrdersOf("Oleg Sidorov"));
        Assert.Empty(llm.OrdersOf("Max Volkov"));

        mgr.SubmitChat(Chat("everybody to the command room"));   // no name -> everyone who heard it
        await Run(mgr, 25);
        Assert.Equal(4, llm.Decisions.Count(d => d.Orders.Any(o => o.Contains("command room"))));
    }

    [Fact]
    public async Task PrefixFiltersMessages()
    {
        var (mgr, llm) = Create("\"chat\": { \"prefix\": \"!\" },");
        llm.Dispatch = _ => Fixtures.OpenAiToolResponse((ChatDispatcher.ToolName, "{\"recipients\":[\"all\"]}"));
        await Run(mgr, 1);
        mgr.SubmitChat(Chat("just talking"));
        await Run(mgr, 1);
        Assert.Empty(llm.DispatchPrompts);
        mgr.SubmitChat(Chat("! все в командный отсек"));
        await Run(mgr, 2);
        Assert.Contains("\"все в командный отсек\"", llm.DispatchPrompts.Single());
        Assert.Equal(4, llm.Decisions.Count(d => d.Orders.Count > 0));
    }

    [Fact]
    public async Task ChatCanBeDisabled()
    {
        var (mgr, llm) = Create("\"chat\": { \"enabled\": false },");
        mgr.SubmitChat(Chat("всем стоять"));
        await Run(mgr, 2);
        Assert.Empty(llm.DispatchPrompts);
        Assert.Equal(0, mgr.PendingChatCount);
    }

    [Fact]
    public async Task UndispatchableChatExpires()
    {
        var providers = @"[ { ""name"": ""a"", ""type"": ""openai"", ""baseUrl"": ""http://a.test/v1"", ""model"": ""m"", ""requestsPerMinute"": 4, ""maxConcurrent"": 10 } ]";
        var settings = ModSettings.Parse(Fixtures.Config(providersOverride: providers).Replace("\"fallbackProvider\": \"b\"", "\"fallbackProvider\": \"\""));
        var llm = new Llm();
        var mgr = new AgentManager(settings, game, new LlmRouter(settings, llm.Handler(), clock.Utc), clock.Get);
        mgr.Update(); await mgr.WhenIdle();       // 4 bots use the whole per-minute budget
        mgr.SubmitChat(Chat("hello crew"));
        await Run(mgr, 35);                        // > chat.maxAgeSeconds (30) and still within the minute
        Assert.Equal(0, mgr.PendingChatCount);
        Assert.Empty(llm.DispatchPrompts);
    }

    [Fact]
    public async Task PromptExplainsPlayerOrders()
    {
        var (mgr, llm) = Create();
        await Run(mgr, 0.5);
        Assert.Contains("playerOrders", llm.Decisions.First().System);
    }
}

public class ChatDispatcherTests
{
    static readonly List<RosterEntry> Roster = new()
    {
        new(1, "Ivan Petrov", "captain", "Command [h1]", "none"),
        new(2, "Max Volkov", "mechanic", "Engine [h2]", "none"),
        new(3, "Oleg Sidorov", "mechanic", "Ballast [h3]", "fixleaks"),
    };

    static LlmResponse Answer(string args) =>
        new("", new[] { new ToolCall(ChatDispatcher.ToolName, (JsonObject)JsonNode.Parse(args)!) }, 0, 0);

    [Theory]
    [InlineData("{\"recipients\":[\"Max Volkov\"]}", "Max Volkov")]
    [InlineData("{\"recipients\":[\"max volkov\",\"Oleg\"]}", "Max Volkov,Oleg Sidorov")]
    [InlineData("{\"recipients\":[\"mechanic\"]}", "Max Volkov,Oleg Sidorov")]
    [InlineData("{\"recipients\":[\"all\"]}", "Ivan Petrov,Max Volkov,Oleg Sidorov")]
    [InlineData("{\"recipients\":\"Ivan Petrov, Max Volkov\"}", "Ivan Petrov,Max Volkov")]
    [InlineData("{\"recipients\":[\"Nobody Here\"]}", "")]
    [InlineData("{\"recipients\":[]}", "")]
    public void ParsesRecipients(string args, string expected)
    {
        var r = ChatDispatcher.ParseRecipients(Answer(args), Roster)!;
        Assert.Equal(expected, string.Join(",", r.Select(x => x.Name)));
    }

    [Fact]
    public void NoToolCallMeansNoRoute()
    {
        Assert.Null(ChatDispatcher.ParseRecipients(new LlmResponse("hm", Array.Empty<ToolCall>(), 0, 0), Roster));
        Assert.Null(ChatDispatcher.ParseRecipients(null, Roster));
    }

    [Fact]
    public void FallbackUsesNamesElseEveryone()
    {
        var cmd = new ChatCommand("P", null, "Volkov, go to engine", new[] { 1, 2, 3 }, true);
        Assert.Equal(new[] { "Max Volkov" }, ChatDispatcher.Fallback(cmd, Roster).Select(r => r.Name));
        Assert.Equal(3, ChatDispatcher.Fallback(cmd with { Text = "everyone hold" }, Roster).Count);
    }

    [Theory]
    [InlineData("Максим, к реактору", "")]                 // 'Максим' must not match 'Max'... stems differ (maks vs max)
    [InlineData("Олег, проверь насосы", "Oleg Sidorov")]
    [InlineData("Олегу передай: насосы", "Oleg Sidorov")]
    [InlineData("Иван и Олег, за мной", "Ivan Petrov,Oleg Sidorov")]
    [InlineData("Volkov to engine", "Max Volkov")]
    [InlineData("Волков, в машинное", "Max Volkov")]
    public void FallbackMatchesNamesAcrossAlphabets(string text, string expected)
    {
        var cmd = new ChatCommand("P", null, text, new[] { 1, 2, 3 }, true);
        var r = ChatDispatcher.Fallback(cmd, Roster).Select(x => x.Name).ToList();
        Assert.Equal(expected == "" ? Roster.Select(x => x.Name).ToList() : expected.Split(',').ToList(), r);
    }

    [Fact]
    public void PromptListsRosterWithJobsAndRooms()
    {
        var p = ChatDispatcher.UserPrompt(new ChatCommand("Daniil", "Captain", "механики сюда", new[] { 1 }, false), Roster);
        Assert.Contains("Daniil (Captain) out loud", p);
        Assert.Contains("- Oleg Sidorov | job: mechanic | room: Ballast [h3] | current order: fixleaks", p);
    }
}

public class FairSchedulingTests
{
    /// <summary>
    /// One LLM slot, each request takes 3 s, 6 bots that each want to think every 10 s:
    /// demand exceeds capacity, and every bot must still get a fair share (nobody starves).
    /// </summary>
    [Fact]
    public async Task NobodyStarvesWithFewSlots()
    {
        var clock = new FakeClock();
        var game = new FakeGame();
        for (int i = 1; i <= 6; i++) game.AddBot(i, $"Bot{i}", i <= 2 ? "mechanic" : "assistant");
        var providers = @"[ { ""name"": ""a"", ""type"": ""openai"", ""baseUrl"": ""http://a.test/v1"", ""model"": ""m"", ""maxConcurrent"": 1 } ]";
        var settings = ModSettings.Parse(Fixtures.Config(providersOverride: providers).Replace("\"fallbackProvider\": \"b\"", "\"fallbackProvider\": \"\""));
        var counts = new ConcurrentDictionary<string, int>();
        var handler = new FakeHandler(async (_, body, ct) =>
        {
            var obs = JsonNode.Parse(body["messages"]!.AsArray()[^1]!["content"]!.GetValue<string>())!;
            counts.AddOrUpdate(obs["name"]!.GetValue<string>(), 1, (_, v) => v + 1);
            var end = clock.Now + 3;
            while (clock.Now < end) await Task.Delay(1, ct);
            return (HttpStatusCode.OK, Fixtures.OpenAiToolResponse(("wait", "{}")));
        });
        var mgr = new AgentManager(settings, game, new LlmRouter(settings, handler, clock.Utc), clock.Get);

        for (int i = 0; i < 240; i++) // 120 s of game time
        {
            mgr.Update();
            await Task.Delay(2);
            clock.Advance(0.5);
        }
        clock.Advance(10);
        await mgr.WhenIdle();

        var perBot = Enumerable.Range(1, 6).Select(i => counts.GetValueOrDefault($"Bot{i}")).ToList();
        Assert.True(perBot.Min() >= 5, "per bot: " + string.Join(",", perBot));
        Assert.True(perBot.Max() - perBot.Min() <= 2, "per bot: " + string.Join(",", perBot));
    }

    [Fact]
    public void BotsWithTheSameJobAndNamePrefixAreAddressedCorrectly()
    {
        var game = new FakeGame().AddBot(1, "Ann", "captain").AddBot(2, "Max Volkov", "mechanic").AddBot(3, "Max Ivanov", "mechanic");
        var settings = Fixtures.Settings();
        var mgr = new AgentManager(settings, game, new LlmRouter(settings, new FakeHandler(_ => (HttpStatusCode.OK, Fixtures.OpenAiToolResponse(("wait", "{}"))))), () => 0);
        mgr.Update();
        var ann = mgr.GetAgent(1)!;
        Assert.Equal("ok (sent to 2)", mgr.DeliverMessage(ann, "mechanic", "pumps"));
        Assert.Contains("ambiguous", mgr.DeliverMessage(ann, "Max", "hi"));
        Assert.Equal("ok", mgr.DeliverMessage(ann, "Max Ivanov", "hi"));
        Assert.Equal(2, mgr.GetAgent(3)!.DrainInbox().Count);
        Assert.Single(mgr.GetAgent(2)!.DrainInbox());
    }
}
