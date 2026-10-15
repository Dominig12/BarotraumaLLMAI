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
    [InlineData("craft", "{\"item\":\"bandage\",\"amount\":50}", "1:craft bandage x10")]
    [InlineData("craft", "{\"item\":\"bandage\",\"amount\":\"3\"}", "1:craft bandage x3")]
    [InlineData("deconstruct", "{\"item\":\"old wrench\"}", "1:deconstruct old wrench")]
    [InlineData("treat", "{\"target\":\"Mose Giorgio\"}", "1:treat Mose Giorgio")]
    [InlineData("treat", "{\"target\":\"me\",\"item\":\"bandage\"}", "1:treat Tyler Sexton with bandage")]
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
            var obs = JsonNode.Parse(body["messages"]!.AsArray()[^1]!["content"]!.GetValue<string>())!;
            if (obs["name"]!.GetValue<string>() == "Tyler Sexton") { calls++; lastObs = obs; }
            return (HttpStatusCode.OK, Fixtures.OpenAiToolResponse(("wait", "{}")));
        });
        var mgr = new AgentManager(settings, game, new LlmRouter(settings, h, clock.Utc), clock.Get);
        mgr.Update(); await mgr.WhenIdle();
        Assert.Equal(1, calls);

        clock.Advance(3);                                     // > min gap, < decision interval
        game.PendingReports.Add(new GameReport(1, "task 'treat Mose' ok: used Bandage x2"));
        mgr.Update(); await mgr.WhenIdle();                   // report delivered, bot woken
        clock.Advance(0.5); mgr.Update(); await mgr.WhenIdle();
        Assert.Equal(2, calls);
        Assert.Contains(lastObs!["lastActionResults"]!.AsArray(), x => x!.GetValue<string>().Contains("used Bandage x2"));
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
}
