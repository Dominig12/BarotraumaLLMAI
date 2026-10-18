using System.Linq;
using System.Net;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using LlmAgents.Agents;
using LlmAgents.Llm;
using Xunit;

namespace LlmAgents.Tests;

public class BodyToolsTests
{
    [Theory]
    [InlineData("[c42]", 42, 4)]
    [InlineData("Crawler [c42]", 42, 4)]
    [InlineData("Crawler [c43]", 42, 0)]
    [InlineData("[c999999999999999999999]", 42, 0)]
    [InlineData("nearest", 42, 1)]
    [InlineData("crawler", 42, 3)]
    [InlineData("Bob", 42, 0)]
    public void BodyReferencesDoNotConfuseCorpsesOfTheSameSpecies(string query, int id, int expected) =>
        Assert.Equal(expected, BodyRules.MatchScore(query, id, "Crawler", "crawler"));

    [Fact]
    public void BodyToolsValidateAndForwardArguments()
    {
        var settings = Fixtures.Settings();
        var game = new FakeGame().AddBot(1, "Ann", "assistant");
        using var mgr = new AgentManager(settings, game, new LlmRouter(settings), () => 0);
        var bot = new BotAgent(game.Bots[0]);
        string Run(string name, string args) => Tools.Execute(new ToolCall(name, JsonNode.Parse(args)!.AsObject()), bot, game, mgr);
        Assert.StartsWith("error:", Run("drag_body", "{\"target\":\"[c42]\"}"));
        Assert.StartsWith("error:", Run("loot_body", "{}"));
        Assert.Empty(game.Actions);
        Assert.StartsWith("ok", Run("drag_body", "{\"target\":\"[c42]\",\"to\":\"Medbay [h7]\"}"));
        Assert.Contains("1:drag [c42] -> Medbay [h7]", game.Actions);
        Assert.StartsWith("ok", Run("loot_body", "{\"target\":\"[c42]\",\"item\":\"medical\",\"count\":999}"));
        Assert.Contains("1:loot [c42] item medical x32", game.Actions);
        Assert.Contains("bodies", Tools.InspectKinds);
        Assert.All(new[] { "drag_body", "loot_body", "drop" }, t => Assert.Contains(t, Tools.TaskTools));
    }

    [Theory]
    [InlineData("Ларисса, оттащи тело в медотсек")]
    [InlineData("обыщи труп монстра")]
    [InlineData("loot the crawler corpse")]
    public void BodyOrdersOfferTheTools(string order)
    {
        var bot = new BotAgent(new BotHandle(1, "Ann", "assistant"));
        var groups = ToolSelection.For(bot, new Observation(), new[] { order }, 0, false);
        var tools = Tools.For(new string[0], false, false, groups);
        Assert.Contains(tools, t => t.Name == "drag_body");
        Assert.Contains(tools, t => t.Name == "loot_body");
    }

    [Fact]
    public async Task ABodyTaskFinishesBeforeTheNextStep()
    {
        var clock = new FakeClock();
        var game = new FakeGame().AddBot(1, "Ann", "assistant");
        var settings = Fixtures.Settings();
        var handler = new FakeHandler(_ => (HttpStatusCode.OK, Fixtures.OpenAiToolResponse(
            ("loot_body", "{\"target\":\"[c42]\"}"),
            ("drag_body", "{\"target\":\"[c42]\",\"to\":\"Engine Room [h10]\"}"))));
        using var mgr = new AgentManager(settings, game, new LlmRouter(settings, handler, clock.Utc), clock.Get);
        mgr.Update(); await mgr.WhenIdle();
        clock.Advance(0.5); mgr.Update();
        Assert.Contains(game.Actions, a => a.StartsWith("1:loot "));
        Assert.DoesNotContain(game.Actions, a => a.StartsWith("1:drag "));
        game.PendingReports.Add(new GameReport(1, "task 'loot Crawler [c42]' ok: looted alien blood"));
        clock.Advance(0.5); mgr.Update();
        Assert.Contains(game.Actions, a => a.StartsWith("1:drag "));
    }
}
