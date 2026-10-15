using System.Linq;
using System.Text.Json.Nodes;
using LlmAgents.Agents;
using LlmAgents.Llm;
using Xunit;

namespace LlmAgents.Tests;

public class ToolsTests
{
    readonly FakeGame game = new FakeGame().AddBot(1, "Ann", "mechanic").AddBot(2, "Bob", "engineer");
    readonly AgentManager mgr;
    readonly BotAgent ann;

    public ToolsTests()
    {
        mgr = new AgentManager(Fixtures.Settings(), game, new LlmRouter(Fixtures.Settings()), () => 0);
        ann = new BotAgent(game.Bots[0]);
    }

    string Run(string name, string args = "{}") =>
        Tools.Execute(new ToolCall(name, (JsonObject)JsonNode.Parse(args)!), ann, game, mgr);

    [Fact]
    public void AllToolSchemasAreValidObjects()
    {
        Assert.Equal(Tools.All.Count, Tools.All.Select(t => t.Name).Distinct().Count());
        foreach (var t in Tools.All)
        {
            Assert.Equal("object", t.Parameters["type"]!.GetValue<string>());
            Assert.Matches("^[a-z_]+$", t.Name);
            var required = t.Parameters["required"]?.AsArray().Select(r => r!.GetValue<string>()) ?? Enumerable.Empty<string>();
            foreach (var r in required) Assert.NotNull(t.Parameters["properties"]![r]);
        }
        foreach (var key in Tools.SimpleOrders.Keys) Assert.Contains(Tools.All, t => t.Name == key);
    }

    [Theory]
    [InlineData("fix_leaks", "{}", "1:fix_leaks")]
    [InlineData("rescue_injured", "{}", "1:rescue_injured")]
    [InlineData("wait", "{}", "1:wait")]
    [InlineData("go_to_room", "{\"room\":\"Command Room [h11]\"}", "1:goto Command Room [h11]")]
    [InlineData("follow", "{\"target\":\"Bob\"}", "1:follow Bob")]
    [InlineData("operate", "{\"role\":\"Reactor\",\"option\":\"PowerUp\"}", "1:operate reactor powerup")]
    [InlineData("operate", "{\"role\":\"pumps\"}", "1:operate pumps")]
    [InlineData("return_to_default", "{}", "1:dismiss")]
    [InlineData("say", "{\"text\":\"Пробоина!\"}", "1:say Пробоина!")]
    public void MapsToGameActions(string name, string args, string expected)
    {
        Assert.StartsWith("ok", Run(name, args));
        Assert.Equal(expected, game.Actions.Single());
    }

    [Theory]
    [InlineData("go_to_room", "{}", "missing 'room'")]
    [InlineData("go_to_room", "{\"room\":\"Moon\"}", "unknown room")]
    [InlineData("operate", "{\"role\":\"toilet\"}", "role must be one of")]
    [InlineData("say", "{\"text\":\"\"}", "missing 'text'")]
    [InlineData("message_crew", "{\"to\":\"Bob\"}", "required")]
    [InlineData("launch_nukes", "{}", "unknown tool")]
    public void RejectsBadCalls(string name, string args, string error)
    {
        var r = Run(name, args);
        Assert.StartsWith("error:", r);
        Assert.Contains(error, r);
        Assert.Empty(game.Actions);
    }

    [Fact]
    public void ToleratesNonStringArguments()
    {
        Assert.StartsWith("ok", Run("say", "{\"text\": 42}"));
        Assert.Equal("1:say 42", game.Actions.Single());
    }

    [Fact]
    public void SayIsClipped()
    {
        Run("say", "{\"text\":\"" + new string('a', 500) + "\"}");
        Assert.Equal(Tools.MaxSayLength, game.Actions.Single().Length - "1:say ".Length);
    }

    [Fact]
    public void RememberKeepsLastNotes()
    {
        Run("remember", "{\"note\":\"one\"}");
        Run("remember", "{\"note\":\"two\"}");
        Run("remember", "{\"note\":\"three\"}");
        Assert.Equal(new[] { "two", "three" }, ann.Notes()); // maxNotes = 2 in fixture
    }

    [Fact]
    public void FilteredToolList()
    {
        var f = Tools.Filtered(new[] { "say", "FIX_LEAKS" });
        Assert.Equal(new[] { "fix_leaks", "say" }, f.Select(t => t.Name).OrderBy(x => x));
        Assert.Equal(Tools.All.Count, Tools.Filtered(System.Array.Empty<string>()).Count);
    }
}
