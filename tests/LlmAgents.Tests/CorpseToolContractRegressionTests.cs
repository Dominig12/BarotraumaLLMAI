using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using LlmAgents.Agents;
using LlmAgents.Llm;
using Xunit;

namespace LlmAgents.Tests;

public sealed class CorpseToolContractRegressionTests
{
    [Theory]
    [InlineData("[c6551]", "deconstruct", true)]
    [InlineData("[c6551]", "attention", true)]
    [InlineData("[c6551]", "deconstruct", false)]
    [InlineData("#690", "deconstruct", true)]
    public void ScopedMarkingPassesTheExactSourceWithoutBroadeningIt(string source, string mark, bool on)
    {
        var game = new FakeGame().AddBot(1, "Kizzie", "medicaldoctor");
        var settings = Fixtures.Settings();
        using var manager = new AgentManager(settings, game, new LlmRouter(settings), () => 0);
        var call = new ToolCall("mark_item", new JsonObject { ["item"] = "*", ["mark"] = mark, ["from"] = source, ["on"] = on });
        Assert.Null(Tools.ValidateArguments(call));
        Assert.StartsWith("ok", Tools.Execute(call, new BotAgent(game.Bots[0]), game, manager));
        Assert.Equal(source, game.LastMarkFrom);
        Assert.Equal(("*", mark, on, (string?)null), game.LastMark!.Value);
        Assert.Single(game.Actions);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("self")]
    [InlineData("Melany Larsen")]
    [InlineData("[h4]")]
    [InlineData("[c-1]")]
    [InlineData("[c99999999999999]")]
    [InlineData("#690 take everything")]
    public void BulkMarkerCannotTreatMixedPocketsOrNaturalLanguageAsCorpseScope(string? source)
    {
        var game = new FakeGame().AddBot(1, "Kizzie", "medicaldoctor");
        var settings = Fixtures.Settings();
        using var manager = new AgentManager(settings, game, new LlmRouter(settings), () => 0);
        var args = new JsonObject { ["item"] = "*", ["mark"] = "deconstruct" };
        if (source != null) args["from"] = source;
        Assert.StartsWith("error:", Tools.Execute(new ToolCall("mark_item", args), new BotAgent(game.Bots[0]), game, manager));
        Assert.Null(game.LastMark);
        Assert.Empty(game.Actions);
    }

    [Theory]
    [InlineData("plan")]
    [InlineData("plan,craft,inspect")]
    [InlineData("plan,craft")]
    public void PromptPlanExampleSurvivesWireParsingAndFullNestedSchemaValidation(string offered)
    {
        var tools = Tools.Filtered(offered.Split(','));
        var prompt = ToolPrompt.Build("SYS", tools);
        var example = prompt.Split('\n').Single(line => line.StartsWith("{\"calls\":") && line.Contains("\"name\":\"plan\""));
        var call = Assert.Single(TextToolCallParser.Parse(example, tools));
        Assert.Equal("plan", call.Name);
        Assert.Null(Tools.ValidateArguments(call));
        var tree = PlanTree.Build(call.Arguments["steps"], tools.Select(t => t.Name).ToArray());
        foreach (var action in Walk(tree.Root).OfType<PlanTree.Do>())
        {
            Assert.Contains(tools, tool => tool.Name == action.Call.Name);
            Assert.Null(Tools.ValidateArguments(action.Call));
        }
        if (offered.Contains("inspect"))
            Assert.IsType<PlanTree.Try>(Assert.Single(tree.Root.Items));
        else
            Assert.IsType<PlanTree.If>(Assert.Single(tree.Root.Items));
    }

    [Fact]
    public void PromptDoesNotTeachTheLoggedMalformedPlanEnvelope()
    {
        var tools = Tools.Filtered(new[] { "plan", "craft", "inspect" });
        var malformed = """{"calls":[{"name":"plan","steps":[{"do":"craft","args":{"item":"railgunshell"}}]}]}""";
        Assert.Empty(TextToolCallParser.Parse(malformed, tools));
        Assert.Contains("steps INSIDE arguments", ToolPrompt.Build("SYS", tools));
    }

    static IEnumerable<PlanTree.Node> Walk(PlanTree.Node node) => new[] { node }.Concat(node.Children.SelectMany(Walk));
}
