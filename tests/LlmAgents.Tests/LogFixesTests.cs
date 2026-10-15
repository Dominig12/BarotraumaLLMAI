using System.Linq;
using System.Text.Json.Nodes;
using LlmAgents.Agents;
using LlmAgents.Llm;
using Xunit;

namespace LlmAgents.Tests;

/// <summary>Problems seen in the in-game log of 2026-10-15 (Gemma-like local model).</summary>
public class LogFixesTests
{
    static string P(string text) =>
        string.Join(" | ", TextToolCallParser.Parse(text, Tools.All).Select(c => $"{c.Name}({Tools.ArgsToString(c)})"));

    [Fact]
    public void BrokenJsonKeepsEveryCall()
    {
        // an extra brace after the first call used to lose message_crew and keep only wait
        var raw = "{\"calls\": [{\"name\": \"message_crew\", \"arguments\": {\"to\": \"Glenda Pickett\", \"text\": \"Meet us near Engineer Bay\"}}}, {\"name\": \"wait\", \"arguments\": {}}]}";
        Assert.Equal("message_crew(to=\"Glenda Pickett\", text=\"Meet us near Engineer Bay\") | wait()", P(raw));
    }

    [Fact]
    public void ValidJsonIsStillReadOnce() =>
        Assert.Equal("fix_leaks() | say(text=\"ok\")", P("{\"calls\": [{\"name\": \"fix_leaks\", \"arguments\": {}}, {\"name\": \"say\", \"arguments\": {\"text\": \"ok\"}}]}"));

    [Theory]
    [InlineData("inspect(kind='recipe', query='fuel rod')", "inspect(kind=\"recipe\", query=\"fuel rod\")")]
    [InlineData("craft(\"medical kit\", amount=1)", "craft(amount=\"1\", item=\"medical kit\")")]
    [InlineData("follow(\"Bettyann Mcneil\")", "follow(target=\"Bettyann Mcneil\")")]
    [InlineData("say(\"Everyone seems green\") wait()", "say(text=\"Everyone seems green\") | wait()")]
    [InlineData("say(\"Everyone seems green\")\nwait()", "say(text=\"Everyone seems green\") | wait()")]
    public void LooseCallsWithQuotesAndPositionalArguments(string raw, string expected) => Assert.Equal(expected, P(raw));

    [Fact]
    public void ApostrophesInsideTextSurvive() =>
        Assert.Equal("say(text=\"I'm on it, don't worry\")", P("say(text=\"I'm on it, don't worry\")"));

    [Fact]
    public void QuotedValuesAreUnwrappedByTheExecutor()
    {
        var game = new FakeGame().AddBot(1, "Ann", "mechanic");
        var mgr = new AgentManager(Fixtures.Settings(), game, new LlmRouter(Fixtures.Settings()), () => 0);
        var ann = new BotAgent(game.Bots[0]);
        var r = Tools.Execute(new ToolCall("inspect", new JsonObject { ["kind"] = "'recipe'", ["query"] = "'fuel rod'" }), ann, game, mgr);
        Assert.StartsWith("recipe fuel rod:", r);
        // patient without a query = everyone who is hurt
        Assert.StartsWith("patient :", Tools.Execute(new ToolCall("inspect", new JsonObject { ["kind"] = "patient" }), ann, game, mgr));
        Assert.StartsWith("error: missing 'query'", Tools.Execute(new ToolCall("inspect", new JsonObject { ["kind"] = "items" }), ann, game, mgr));
    }

    [Fact]
    public void MessageToAHumanPlayerIsSaidOverTheRadio()
    {
        var game = new FakeGame()
            .AddBot(1, "Milton Gumbs", "mechanic", o => o.Crew.Add(new CrewInfo("Смертельное Рандеву", "engineer", null, 100, true, "none")))
            .AddBot(2, "Elliana Shah", "captain");
        var mgr = new AgentManager(Fixtures.Settings(), game, new LlmRouter(Fixtures.Settings()), () => 0);
        mgr.Update();
        var milton = mgr.GetAgent(1)!;

        var r = mgr.DeliverMessage(milton, "Смертельное Рандеву", "Надо пополнить топливо реактора");
        Assert.StartsWith("ok (said to player Смертельное Рандеву", r);
        Assert.Contains("1:say Смертельное Рандеву, Надо пополнить топливо реактора", game.Actions);

        Assert.StartsWith("ok (said to player", mgr.DeliverMessage(milton, "Smertelnoe Randevu", "translit works")); // typed in latin
        Assert.StartsWith("error: no AI crew member", mgr.DeliverMessage(milton, "Nobody", "x"));
    }
}
