using System.Linq;
using System.Net;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using LlmAgents.Agents;
using LlmAgents.Llm;
using Xunit;

namespace LlmAgents.Tests;

public class ItemToolsTests
{
    readonly FakeGame game = new FakeGame().AddBot(1, "Tyler Sexton", "medicaldoctor");
    readonly AgentManager mgr;
    readonly BotAgent tyler;

    public ItemToolsTests()
    {
        mgr = new AgentManager(Fixtures.Settings(), game, new LlmRouter(Fixtures.Settings()), () => 0);
        tyler = new BotAgent(game.Bots[0]);
    }

    string Run(string name, string args = "{}") =>
        Tools.Execute(new ToolCall(name, (JsonObject)JsonNode.Parse(args)!), tyler, game, mgr);

    [Theory]
    [InlineData("equip", "{\"item\":\"diving suit\"}", "1:equip diving suit")]
    [InlineData("refuel_reactor", "{}", "1:refuel")]
    [InlineData("craft", "{\"item\":\"fuel rod\"}", "1:craft fuel rod x1")]
    [InlineData("craft", "{\"item\":\"bandage\",\"amount\":50}", "1:craft bandage x24")]
    [InlineData("craft", "{\"item\":\"bandage\",\"amount\":\"3\"}", "1:craft bandage x3")]
    [InlineData("deconstruct", "{\"item\":\"old wrench\"}", "1:deconstruct old wrench")]
    [InlineData("treat", "{\"target\":\"Mose Giorgio\"}", "1:treat Mose Giorgio")]
    [InlineData("treat", "{\"target\":\"me\",\"item\":\"bandage\"}", "1:treat Tyler Sexton with bandage")]
    [InlineData("unequip", "{\"item\":\"welding tool\"}", "1:unequip welding tool")]
    [InlineData("drop", "{\"item\":\"empty tank\"}", "1:drop empty tank")]
    [InlineData("move_item", "{\"item\":\"bandage\",\"to\":\"Mose Giorgio\"}", "1:move bandage -> Mose Giorgio")]
    [InlineData("move_item", "{\"item\":\"harpoon\",\"to\":\"Daniil\",\"count\":6}", "1:move harpoon -> Daniil x6")]
    [InlineData("move_item", "{\"item\":\"steel\",\"to\":\"cabinet\",\"count\":99}", "1:move steel -> cabinet x32")]
    [InlineData("keep_gear", "{\"on\":true}", "1:keepgear True")]
    [InlineData("keep_gear", "{\"on\":\"off\"}", "1:keepgear False")]
    public void MapsToGame(string tool, string args, string expected)
    {
        Assert.StartsWith("ok", Run(tool, args));
        Assert.Equal(expected, game.Actions.Single());
    }

    [Fact]
    public void InspectIsClippedAndValidated()
    {
        var r = Run("inspect", "{\"kind\":\"patient\",\"query\":\"Mose\"}");
        Assert.StartsWith("patient Mose:", r);
        Assert.Equal(Tools.MaxInspectLength, r.Length);
        Assert.StartsWith("error: kind must be", Run("inspect", "{\"kind\":\"weather\",\"query\":\"x\"}"));
        Assert.StartsWith("error: missing 'query'", Run("inspect", "{\"kind\":\"items\"}"));
        Assert.StartsWith("error: missing 'item'", Run("craft", "{}"));
        Assert.StartsWith("error: 'on' must be", Run("keep_gear", "{\"on\":\"maybe\"}"));
        Assert.StartsWith("error: 'item' and 'to'", Run("move_item", "{\"item\":\"x\"}"));
    }

    [Fact]
    public void GemmaStyleCallsForNewTools()
    {
        var calls = TextToolCallParser.Parse("treat{target:<|\"|>Mose Giorgio<|\"|>,item:<|\"|>bandage<|\"|>}<tool_call|>\nrefuel_reactor", Tools.All);
        Assert.Equal(new[] { "treat", "refuel_reactor" }, calls.Select(c => c.Name));
        Assert.Equal("bandage", Tools.Str(calls[0].Arguments, "item"));
    }
}

public class TaskReportsAndAuthorityTests
{
    readonly FakeClock clock = new();
    readonly FakeGame game = new FakeGame().AddBot(1, "Tyler Sexton", "medicaldoctor").AddBot(2, "Ann", "captain");

    [Fact]
    public async Task TaskReportWakesTheBotAndIsShownToIt()
    {
        var settings = Fixtures.Settings();
        int calls = 0;
        JsonNode? lastObs = null;
        var h = new FakeHandler(body =>
        {
            var obs = JsonNode.Parse(Fixtures.Obs(body["messages"]!.AsArray()[^1]!["content"]!.GetValue<string>()))!;
            if (obs["name"]!.GetValue<string>() == "Tyler Sexton") { calls++; lastObs = obs; }
            return (HttpStatusCode.OK, Fixtures.OpenAiToolResponse(("wait", "{}")));
        });
        var mgr = new AgentManager(settings, game, new LlmRouter(settings, h, clock.Utc), clock.Get);
        mgr.Update(); await mgr.WhenIdle();
        Assert.Equal(1, calls);

        clock.Advance(3);                                     // > min gap
        game.PendingReports.Add(new GameReport(1, "task 'treat Mose' ok: used Bandage x2"));
        mgr.Update(); await mgr.WhenIdle();                   // a success outside of a plan: noted, nobody needs to think
        clock.Advance(0.5); mgr.Update(); await mgr.WhenIdle();
        Assert.Equal(1, calls);

        clock.Advance(3);
        game.PendingReports.Add(new GameReport(1, "task 'treat Mose' failed: no bandages left"));
        mgr.Update(); await mgr.WhenIdle();                   // a failure wakes the bot
        clock.Advance(0.5); mgr.Update(); await mgr.WhenIdle();
        Assert.Equal(2, calls);
        var results = lastObs!["lastActionResults"]!.AsArray().Select(x => x!.GetValue<string>()).ToList();
        Assert.Contains(results, x => x.Contains("used Bandage x2"));
        Assert.Contains(results, x => x.Contains("no bandages left"));
    }

    [Fact]
    public async Task OnlyDecisionsForPlayerOrdersCarryAuthority()
    {
        var settings = Fixtures.Settings();
        var h = new FakeHandler(_ => (HttpStatusCode.OK, Fixtures.OpenAiToolResponse(("fix_leaks", "{}"))));
        var mgr = new AgentManager(settings, game, new LlmRouter(settings, h, clock.Utc), clock.Get);
        mgr.Update(); await mgr.WhenIdle();
        clock.Advance(0.5); mgr.Update();
        Assert.False(game.Authority[1]);                      // own initiative: must not override a player's menu order

        mgr.SubmitChat(new ChatCommand("Daniil", null, "Tyler, fix leaks", new[] { 1 }, true));
        for (int i = 0; i < 6; i++) { clock.Advance(0.5); mgr.Update(); await mgr.WhenIdle(); }
        Assert.True(game.Authority[1]);                       // carrying out a chat order: may replace it
    }

    [Fact]
    public void DangerWithoutSuitIsUrgent()
    {
        var o = new Observation { Gear = "no diving gear on; DANGER: gear locked automatically" };
        Assert.True(o.Urgent);
        o.Gear = "Водолазный костюм on (oxygen 90%); DANGER: gear locked automatically";
        Assert.False(o.Urgent);
        o.Gear = "no diving gear on";
        Assert.False(o.Urgent);
        Assert.DoesNotContain("dangerWithoutSuit", o.ToJson());
        Assert.Contains("\"gear\":\"no diving gear on\"", o.ToJson());
    }
}
