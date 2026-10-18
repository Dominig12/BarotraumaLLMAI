using System.Text.Json.Nodes;
using LlmAgents.Agents;
using LlmAgents.Llm;
using Xunit;

namespace LlmAgents.Tests;

public class CraftingToolsTests
{
    readonly FakeGame game = new FakeGame().AddBot(1, "Ann", "mechanic");
    readonly AgentManager mgr;
    readonly BotAgent ann;

    public CraftingToolsTests()
    {
        mgr = new AgentManager(Fixtures.Settings(), game, new LlmRouter(Fixtures.Settings()), () => 0);
        ann = new BotAgent(game.Bots[0]);
    }

    string Run(string name, JsonObject args) => Tools.Execute(new ToolCall(name, args), ann, game, mgr);

    [Fact]
    public void CraftPassesSalvageFlag()
    {
        Run("craft", new JsonObject { ["item"] = "fuel rod", ["amount"] = 2, ["salvage"] = true });
        Assert.Contains("1:craft fuel rod x2 salvage", game.Actions);
        Run("craft", new JsonObject { ["item"] = "bandage", ["salvage"] = false });
        Assert.Contains("1:craft bandage x1", game.Actions);
    }

    [Theory]
    [InlineData("recipes", null, "recipes :")]
    [InlineData("recipes", "medical", "recipes medical:")]
    [InlineData("salvage", null, "salvage :")]
    [InlineData("salvage", "steel", "salvage steel:")]
    [InlineData("recipe", null, "error: missing 'query'")]
    [InlineData("items", null, "items :")]
    [InlineData("blueprints", "x", "error: kind must be one of patient, items, recipes, recipe, salvage")]
    public void InspectKinds(string kind, string? query, string expectedStart)
    {
        var args = new JsonObject { ["kind"] = kind };
        if (query != null) args["query"] = query;
        Assert.StartsWith(expectedStart, Run("inspect", args));
    }

    [Fact]
    public void LongInspectAnswersAreClippedButKeepMore() =>
        Assert.Equal(Tools.MaxInspectLength, Run("inspect", new JsonObject { ["kind"] = "items", ["query"] = "x" }).Length);
}
