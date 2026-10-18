using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using LlmAgents.Agents;
using LlmAgents.Llm;
using Xunit;

namespace LlmAgents.Tests;

/// <summary>Canonical selection tokens and literal label matching; natural language belongs to the model.</summary>
public class ItemWordsTests
{
    [Theory]
    [InlineData("medical", new[] { ItemGroup.Medical })]
    [InlineData("ammo", new[] { ItemGroup.Ammo })]
    [InlineData("diving_suit", new[] { ItemGroup.DivingSuit })]
    [InlineData("diving_gear", new[] { ItemGroup.DivingGear })]
    [InlineData("diving_mask", new[] { ItemGroup.DivingMask })]
    [InlineData("reactor_fuel", new[] { ItemGroup.ReactorFuel })]
    [InlineData("tool", new[] { ItemGroup.Tool })]
    [InlineData("oxygen", new[] { ItemGroup.Oxygen })]
    [InlineData("медикаменты", new ItemGroup[0])]
    [InlineData("оружие и патроны", new ItemGroup[0])]
    [InlineData("diving gear", new ItemGroup[0])]
    [InlineData("tools", new ItemGroup[0])]
    [InlineData("бинт", new ItemGroup[0])]
    [InlineData("костюм", new ItemGroup[0])]                          // just clothes
    [InlineData("сварочное топливо", new ItemGroup[0])]                // the name says it, not reactor fuel
    [InlineData("снаряжение", new ItemGroup[0])]
    public void KindsOfItems(string query, ItemGroup[] expected) =>
        Assert.Equal(expected, ItemWords.Groups(query));

    [Theory]
    [InlineData("скафандры водолазные", "Водолазный скафандр", false)]
    [InlineData("fuel rods", "Fuel Rod", false)]
    [InlineData("fuel rod", "Fuel Rod", true)]
    [InlineData("водолазный скафандр", "Водолазный скафандр", true)]
    [InlineData("сварочное топливо", "Бак со сварочным топливом", false)]
    [InlineData("Водолазный костюм", "Водолазный скафандр", false)]
    [InlineData("бинт", "Морфин", false)]
    [InlineData("spare fuel rod", "Fuel Rod", false)]
    public void LiteralWords(string query, string name, bool match) =>
        Assert.Equal(match, ItemWords.WordsMatch(query, new[] { name }));

    [Theory]
    [InlineData("Lorriane Connors", true)]
    [InlineData("lorriane", false)]
    [InlineData("self", true)]
    [InlineData("себе", false)]
    [InlineData("me", false)]
    [InlineData("в инвентарь", false)]
    [InlineData("Смертельное Рандеву", false)]
    [InlineData("medical cabinet", false)]
    public void TheBotItself(string to, bool self) => Assert.Equal(self, ItemWords.IsSelf(to, "Lorriane Connors"));

    [Fact]
    public void CopiedIdentifier()
    {
        Assert.Equal("antibloodloss1", ItemWords.BracketId("Физраствор [antibloodloss1]"));
        Assert.Null(ItemWords.BracketId("Физраствор"));
    }
}

/// <summary>Fixes from the round where bots fought over diving suits and the medic could not take medicine.</summary>
public class ItemPlanTests
{
    readonly FakeClock clock = new();
    readonly FakeGame game = new();
    readonly ConcurrentQueue<JsonObject> bodies = new();
    Func<JsonObject, string> reply = _ => Fixtures.OpenAiToolResponse(("carry_on", "{}"));

    AgentManager Create(string global = "", Func<JsonObject, string>? raw = null)
    {
        var settings = Fixtures.Settings(@", ""maxActionsPerDecision"": 6, ""maxPlanSeconds"": 120, ""planStepTimeoutSeconds"": 30" + global);
        var handler = new FakeHandler(body =>
        {
            bodies.Enqueue(body);
            return (HttpStatusCode.OK, (raw ?? reply)(body));
        });
        return new AgentManager(settings, game, new LlmRouter(settings, handler, clock.Utc), clock.Get);
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

    [Theory]
    [InlineData("{\"item\":\"medical\",\"count\":5}", "1:take medical x5")]
    [InlineData("{\"item\":\"Бинт\"}", "1:take Бинт x1")]
    [InlineData("{\"item\":\"coilgunammobox\",\"count\":8}", "1:take coilgunammobox x8")]
    [InlineData("{\"item\":\"tools\",\"count\":3,\"from\":\"Электрика [h140]\"}", "1:take tools x3 from Электрика [h140]")]
    [InlineData("{\"item\":\"tools\",\"from\":\" \"}", "1:take tools x1")]
    public void TakeItemArguments(string args, string action)
    {
        game.AddBot(1, "Lorriane", "medicaldoctor");
        var agent = new BotAgent(new BotHandle(1, "Lorriane", "medicaldoctor"));
        var r = Tools.Execute(new ToolCall("take_item", (JsonObject)JsonNode.Parse(args)!), agent, game, Create());
        Assert.StartsWith("ok, started", r);
        Assert.Equal(action, game.Actions.Last());
    }

    [Theory]
    [InlineData("pick_up(item=\"bandage\", count=2)")]
    [InlineData("take(item=\"bandage\", count=2)")]
    public void TakeAliasesRequireAnExplicitCanonicalToolCall(string text)
    {
        var calls = TextToolCallParser.Parse(text, Tools.All);
        Assert.Empty(calls);
    }

    [Fact]
    public async Task TakingItemsIsATaskThePlanWaitsFor()
    {
        game.AddBot(1, "Lorriane", "medicaldoctor");
        var mgr = Create();
        int n = 0;
        reply = _ => n++ == 0
            ? Fixtures.OpenAiToolResponse(("take_item", "{\"item\":\"medical\",\"count\":4}"), ("follow", "{\"target\":\"Daniil\"}"))
            : Fixtures.OpenAiToolResponse(("carry_on", "{}"));
        await Run(mgr, 3);
        Assert.Equal(new[] { "1:take medical x4" }, game.Actions);   // follows only once the medicine is taken

        game.PendingReports.Add(new GameReport(1, "task 'take medical' ok: took Бинт x2, Морфин x2 (in your inventory now)"));
        await Run(mgr, 1);
        Assert.Equal("1:follow Daniil", game.Actions.Last());
    }

    [Fact]
    public async Task AFailedLastStepIsReportedOnceAsFailed()
    {
        game.AddBot(1, "Lorriane", "medicaldoctor");
        var mgr = Create();
        await Run(mgr, 3);
        int n = 0;
        reply = _ => n++ == 0
            ? Fixtures.OpenAiToolResponse(("take_item", "{\"item\":\"medical\",\"count\":4}"), ("go_to_room", "{\"room\":\"Nowhere\"}"))
            : Fixtures.OpenAiToolResponse(("carry_on", "{}"));
        mgr.GetAgent(1)!.ReceivePlayerOrder("Daniil: medic, take medicine and come", from: "Daniil");
        await Run(mgr, 3);
        game.PendingReports.Add(new GameReport(1, "task 'take medical' ok: took Бинт x4 (in your inventory now)"));
        await Run(mgr, 2);

        var notices = game.Notices.Select(x => x.Text).Where(x => x.Contains("Lorriane:")).ToList();
        Assert.Contains(notices, x => x.Contains("не получилось"));
        Assert.DoesNotContain(notices, x => x.Contains("выполнено"));
    }

    [Fact]
    public async Task ARoomThatIsTheDestinationKeepsItsOrder()
    {
        game.AddBot(1, "Ann", "mechanic");
        var mgr = Create();
        int n = 0;
        reply = _ => n++ == 0
            ? Fixtures.OpenAiToolResponse(("go_to_room", "{\"room\":\"Command Room [h11]\"}"), ("say", "{\"text\":\"On my way\"}"))
            : Fixtures.OpenAiToolResponse(("carry_on", "{}"));
        await Run(mgr, 3);
        game.Observations[1].Room = "Command Room [h11]";
        await Run(mgr, 2);
        Assert.Contains("1:say On my way", game.Actions);
        Assert.DoesNotContain("1:end travel", game.Actions);           // "go there" then talk: stay there
    }

    static string CutOff() => new JsonObject
    {
        ["choices"] = new JsonArray
        {
            new JsonObject
            {
                ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = "" },
                ["finish_reason"] = "length",
            },
        },
        ["usage"] = new JsonObject
        {
            ["prompt_tokens"] = 100, ["completion_tokens"] = 800,
            ["completion_tokens_details"] = new JsonObject { ["reasoning_tokens"] = 800 },
        },
    }.ToJsonString();

    [Fact]
    public async Task AReplyCutOffWhileThinkingIsRetriedWithMoreRoom()
    {
        game.AddBot(1, "Ann", "medicaldoctor");
        int n = 0;
        var mgr = Create(raw: _ => n++ == 0 ? CutOff() : Fixtures.OpenAiToolResponse(("carry_on", "{}")));
        await Run(mgr, 10);     // round start: cut off, then the retry
        mgr.GetAgent(1)!.ReceivePlayerOrder("Daniil: Ann, grab medicine", from: "Daniil");
        await Run(mgr, 3);

        var tokens = bodies.Select(b => b["max_tokens"]!.GetValue<int>()).ToList();
        Assert.True(tokens.Count >= 3, string.Join(",", tokens));
        Assert.Equal(800, tokens[0]);
        Assert.Equal(1600, tokens[1]);   // the retry: twice the room
        Assert.Equal(1600, tokens[2]);   // the provider keeps the larger budget: it thinks that long every time
    }
    [Fact]
    public async Task AReplyCutOffAgainGetsMoreRoomAgain()
    {
        game.AddBot(1, "Ann", "medicaldoctor");
        int n = 0;
        var mgr = Create(raw: _ => n++ < 2 ? CutOff() : Fixtures.OpenAiToolResponse(("carry_on", "{}")));
        await Run(mgr, 3);
        mgr.GetAgent(1)!.ReceivePlayerOrder("Daniil: Ann, grab medicine", from: "Daniil");
        await Run(mgr, 15);

        var tokens = bodies.Select(b => b["max_tokens"]!.GetValue<int>()).ToList();
        Assert.True(tokens.Count >= 3, string.Join(",", tokens));
        Assert.Equal(new[] { 800, 1600, 3200 }, tokens.Take(3));   // cut off twice: the order is not given up
        Assert.DoesNotContain(game.Notices, x => x.Text.Contains("не может выполнить"));
    }
}

/// <summary>Marks (middle mouse button) and unpacking a crate.</summary>
public class MarksAndUnpackTests
{
    [Theory]
    [InlineData("storage", true)]
    [InlineData("на место", false)]
    [InlineData("по местам", false)]
    [InlineData("away", false)]
    [InlineData("its place", false)]
    [InlineData("медицинский шкаф", false)]
    [InlineData("Daniil", false)]
    public void ItsPlace(string to, bool expected) => Assert.Equal(expected, ItemWords.IsItsPlace(to));

    [Theory]
    [InlineData("{\"container\":\"metal crate\"}", "1:unpack metal crate")]
    [InlineData("{\"container\":\"marked\",\"carry\":false}", "1:unpack marked (no carry)")]
    [InlineData("{\"container\":\"ящик\"}", "1:unpack ящик")]
    public void UnpackArguments(string args, string action)
    {
        var game = new FakeGame();
        game.AddBot(1, "Tracy", "assistant");
        var agent = new BotAgent(new BotHandle(1, "Tracy", "assistant"));
        var settings = Fixtures.Settings();
        using var mgr = new AgentManager(settings, game, new LlmRouter(settings, new FakeHandler(_ => (HttpStatusCode.OK, Fixtures.OpenAiToolResponse(("carry_on", "{}"))))), () => 0);
        var r = Tools.Execute(new ToolCall("unpack", (JsonObject)JsonNode.Parse(args)!), agent, game, mgr);
        Assert.StartsWith("ok, started", r);
        Assert.Equal(action, game.Actions.Last());
    }

    [Fact]
    public void UnpackIsATaskThePlanWaitsFor() => Assert.Contains("unpack", AgentManager.TaskTools);

    [Fact]
    public void MarksAreShownToTheModel()
    {
        var o = new Observation { Name = "Tracy", Marks = new() { "for the AI: Металлический ящик [metalcrate] in Грузовой отсек [h3] (marked by Daniil, 5s ago)" } };
        Assert.Contains("\"marks\"", o.ToCompactJson());
        Assert.DoesNotContain("\"marks\"", new Observation { Name = "Tracy" }.ToCompactJson());   // nothing marked: not shown
    }
}

/// <summary>give_order: every vanilla order, also the ones without a tool of their own.</summary>
public class GiveOrderTests
{
    static (FakeGame, BotAgent, AgentManager) Setup()
    {
        var game = new FakeGame();
        game.AddBot(1, "Sophie", "mechanic");
        var settings = Fixtures.Settings();
        var mgr = new AgentManager(settings, game, new LlmRouter(settings, new FakeHandler(_ => (HttpStatusCode.OK, Fixtures.OpenAiToolResponse(("carry_on", "{}"))))), () => 0);
        return (game, new BotAgent(new BotHandle(1, "Sophie", "mechanic")), mgr);
    }

    [Theory]
    [InlineData("{\"order\":\"repairelectrical\"}", "1:order repairelectrical [p3]")]
    [InlineData("{\"order\":\"Charge_Batteries\",\"priority\":2}", "1:order charge_batteries [p2]")]
    [InlineData("{\"order\":\"steer\",\"option\":\"NavigateTactical\"}", "1:order steer:navigatetactical [p3]")]
    [InlineData("{\"order\":\"loaditems\",\"target\":\"Арсенал [h44]\"}", "1:order loaditems @Арсенал [h44] [p3]")]
    public void Arguments(string args, string action)
    {
        var (game, agent, mgr) = Setup();
        using var _ = mgr;
        var r = Tools.Execute(new ToolCall("give_order", (JsonObject)JsonNode.Parse(args)!), agent, game, mgr);
        Assert.StartsWith("ok", r);
        Assert.Equal(action, game.Actions.Last());
    }

    [Fact]
    public void OrderIsRequired()
    {
        var (game, agent, mgr) = Setup();
        using var _ = mgr;
        Assert.StartsWith("error", Tools.Execute(new ToolCall("give_order", new JsonObject()), agent, game, mgr));
    }

    [Fact]
    public void OrdersCanBeInspected()
    {
        var (game, agent, mgr) = Setup();
        using var _ = mgr;
        var r = Tools.Execute(new ToolCall("inspect", new JsonObject { ["kind"] = "orders" }), agent, game, mgr);
        Assert.DoesNotContain("error", r);
    }

    [Fact]
    public void OrderAliasDoesNotCreateAToolCall() => Assert.Empty(TextToolCallParser.Parse("order(order=\"repairmechanical\")", Tools.All));

    [Fact]
    public void OfferedToTheModel() => Assert.Contains(Tools.All, t => t.Name == "give_order");
}

/// <summary>Ballast flora: seen, woken for, attacked with a chosen tool.</summary>
public class FloraTests
{
    const string Flora = "ballast flora: 34 branches - 30 in Балласт [h5] (root), 4 in Насосная [h6]. It spreads and attacks; burn it: attack(target='ballast flora') with a welding tool or a flamer";

    static Observation Obs(string room) => new() { Name = "Sophie", Room = room, Infestations = new() { Flora } };

    [Theory]
    [InlineData("mechanic", "Engine [h2]", true, false)]
    [InlineData("medicaldoctor", "Medbay [h4]", false, false)]
    [InlineData("medicaldoctor", "Балласт [h5]", true, true)]        // it grows right next to them: everyone
    [InlineData("captain", "Command [h1]", true, false)]
    public void WhoIsWoken(string job, string room, bool woken, bool danger)
    {
        var flag = Situation.Assess(Obs(room), job, aiControl: false).FirstOrDefault(f => f.Key == "flora");
        Assert.Equal(woken, flag != null);
        if (flag != null) Assert.Equal(danger, flag.Danger);
    }

    [Fact]
    public void ShownToTheModel() => Assert.Contains("\"infestations\"", Obs("Engine [h2]").ToCompactJson());

    [Theory]
    [InlineData("{\"target\":\"ballast flora\",\"weapon\":\"welding tool\"}", "1:attack ballast flora with welding tool")]
    [InlineData("{\"target\":\"Crawler\"}", "1:attack Crawler")]
    [InlineData("{\"target\":\"Crawler [c42]\"}", "1:attack Crawler [c42]")]
    public void Arguments(string args, string action)
    {
        var game = new FakeGame();
        game.AddBot(1, "Sophie", "mechanic");
        var settings = Fixtures.Settings();
        using var mgr = new AgentManager(settings, game, new LlmRouter(settings, new FakeHandler(_ => (HttpStatusCode.OK, Fixtures.OpenAiToolResponse(("carry_on", "{}"))))), () => 0);
        var r = Tools.Execute(new ToolCall("attack", (JsonObject)JsonNode.Parse(args)!), new BotAgent(new BotHandle(1, "Sophie", "mechanic")), game, mgr);
        Assert.StartsWith("ok", r);
        Assert.Equal(action, game.Actions.Last());
    }

    [Fact]
    public void AttackIsATask() => Assert.Contains("attack", AgentManager.TaskTools);
}

/// <summary>Inventory management: loading, refilling, putting away.</summary>
public class InventoryToolTests
{
    static (FakeGame, BotAgent, AgentManager) Setup()
    {
        var game = new FakeGame();
        game.AddBot(1, "Sophie", "mechanic");
        var settings = Fixtures.Settings();
        var mgr = new AgentManager(settings, game, new LlmRouter(settings, new FakeHandler(_ => (HttpStatusCode.OK, Fixtures.OpenAiToolResponse(("carry_on", "{}"))))), () => 0);
        return (game, new BotAgent(new BotHandle(1, "Sophie", "mechanic")), mgr);
    }

    [Theory]
    [InlineData("put_in", "{\"item\":\"welding fuel\",\"into\":\"welding tool\"}", "1:put welding fuel into welding tool")]
    [InlineData("put_in", "{\"item\":\"oxygen tank\",\"into\":\"diving suit\"}", "1:put oxygen tank into diving suit")]
    [InlineData("refill", "{}", "1:refill")]
    [InlineData("refill", "{\"item\":\"welding tool\"}", "1:refill welding tool")]
    [InlineData("tidy_inventory", "{\"keep\":\"bandage, welding fuel\"}", "1:tidy keep bandage, welding fuel")]
    public void Arguments(string tool, string args, string action)
    {
        var (game, agent, mgr) = Setup();
        using var _ = mgr;
        var r = Tools.Execute(new ToolCall(tool, (JsonObject)JsonNode.Parse(args)!), agent, game, mgr);
        Assert.StartsWith("ok", r);
        Assert.Equal(action, game.Actions.Last());
    }

    [Fact]
    public void PutInNeedsBoth()
    {
        var (game, agent, mgr) = Setup();
        using var _ = mgr;
        Assert.StartsWith("error", Tools.Execute(new ToolCall("put_in", new JsonObject { ["item"] = "fuel" }), agent, game, mgr));
    }

    [Theory]
    [InlineData("reload(item=\"revolver\")")]
    [InlineData("insert(item=\"battery\", into=\"flashlight\")")]
    [InlineData("stow()")]
    public void TextAliasesDoNotSelectInventoryTools(string text) => Assert.Empty(TextToolCallParser.Parse(text, Tools.All));

    [Fact]
    public void TheModelSeesFreeSpace() =>
        Assert.Contains("inventorySpace", new Observation { Name = "Sophie", InventorySpace = "3 free slots; hands: right Welding Tool, left empty" }.ToCompactJson());

    [Fact]
    public void TheyAreTasks()
    {
        foreach (var t in new[] { "put_in", "refill", "tidy_inventory" }) Assert.Contains(t, AgentManager.TaskTools);
    }
}

public class SuitTankTests
{
    [Theory]
    [InlineData("Водолазный скафандр on (oxygen 8%)", true)]
    [InlineData("Водолазный скафандр on (NO oxygen tank)", true)]
    [InlineData("Водолазный скафандр on (oxygen 80%)", false)]
    [InlineData("no diving gear on", false)]
    public void AnEmptyTankWakesTheBot(string gear, bool woken) =>
        Assert.Equal(woken, Situation.Assess(new Observation { Name = "A", Gear = gear }, "mechanic", false).Any(f => f.Key == "tank"));
}
