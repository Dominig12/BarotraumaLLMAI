using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using LlmAgents.Agents;
using LlmAgents.Llm;
using Xunit;

namespace LlmAgents.Tests;

/// <summary>Problems seen in the in-game log of 2026-10-15 16:00 (orders ignored, chatter, "разберите все в шкафчике").</summary>
public class Log2FixesTests
{
    [Fact]
    public void OrderTextGivesSpeakerContextWithoutGuessingAnAction()
    {
        var cmd = new ChatCommand("Смертельное Рандеву", "Инженер", "ВСЕ ЗА МНОЙ!", new[] { 1 }, true, "Реакторный отсек [h163]");
        var me = new RosterEntry(1, "Ann", "mechanic", null, "none");
        var text = ChatDispatcher.FormatOrder(cmd, new[] { me }, me);
        Assert.Contains("in Реакторный отсек [h163]", text);
        Assert.Contains("('me' = Смертельное Рандеву)", text);
        Assert.Equal("Смертельное Рандеву (Инженер) on radio, in Реакторный отсек [h163]: \"ВСЕ ЗА МНОЙ!\" ('me' = Смертельное Рандеву)", text);
    }

    [Fact]
    public async Task OrdersArePutInFrontOfTheObservation()
    {
        var clock = new FakeClock();
        var game = new FakeGame().AddBot(1, "Ann", "mechanic");
        string? user = null;
        var handler = new FakeHandler(body =>
        {
            user = body["messages"]!.AsArray()[^1]!["content"]!.GetValue<string>();
            return (System.Net.HttpStatusCode.OK, Fixtures.OpenAiToolResponse(("wait", "{}")));
        });
        var settings = Fixtures.Settings();
        var mgr = new AgentManager(settings, game, new LlmRouter(settings, handler, clock.Utc), clock.Get);
        mgr.Update(); await mgr.WhenIdle(); clock.Advance(1);
        Assert.StartsWith("Why you are asked now:\n- [round start]", user); // no order: why, then the observation
        Assert.DoesNotContain("NEW ORDER", user);

        mgr.GetAgent(1)!.ReceivePlayerOrder("Dan on radio: \"follow me\"");
        mgr.Update(); await mgr.WhenIdle();
        Assert.StartsWith("Why you are asked now:\n- [order] new order", user);
        Assert.Contains("NEW ORDER - carry out now", user);
        Assert.Contains("- Dan on radio: \"follow me\"", user);
        var obs = JsonNode.Parse(Fixtures.Obs(user!))!.AsObject();
        Assert.Equal(new[] { "why", "playerOrders" }, obs.Take(2).Select(kv => kv.Key)); // also first inside the JSON
    }

    [Fact]
    public void EchoIsCappedCrewWide()
    {
        var game = new FakeGame().AddBot(1, "Ann", "mechanic").AddBot(2, "Bob", "engineer").AddBot(3, "Cid", "captain");
        var settings = Fixtures.Settings(", \"maxMessagesPerMinute\": 0, \"maxChatEchoPerMinute\": 2");
        var mgr = new AgentManager(settings, game, new LlmRouter(settings), () => 0);
        mgr.Update();
        for (int i = 0; i < 5; i++) Assert.Equal("ok", mgr.DeliverMessage(mgr.GetAgent(1)!, "Bob", $"m{i}"));
        Assert.Equal(2, game.Actions.Count(a => a.Contains("[→ Bob]")));
        Assert.Equal(5, mgr.GetAgent(2)!.DrainInbox().Count); // all delivered
    }

    [Fact]
    public void NoneMeansNoValueAndDeconstructTakesFrom()
    {
        var game = new FakeGame().AddBot(1, "Ann", "medicaldoctor");
        var mgr = new AgentManager(Fixtures.Settings(), game, new LlmRouter(Fixtures.Settings()), () => 0);
        var ann = new BotAgent(game.Bots[0]);
        Tools.Execute(new ToolCall("treat", new JsonObject { ["target"] = "Bob", ["item"] = "None" }), ann, game, mgr);
        Assert.Contains("1:treat Bob", game.Actions);
        Tools.Execute(new ToolCall("deconstruct", new JsonObject { ["item"] = "all", ["from"] = "Электрика [h140]" }), ann, game, mgr);
        Assert.Contains("1:deconstruct all from Электрика [h140]", game.Actions);
    }
}
