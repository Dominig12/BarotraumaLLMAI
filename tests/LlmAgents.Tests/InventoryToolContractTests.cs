using System.Linq;
using System.Text.Json.Nodes;
using LlmAgents.Agents;
using LlmAgents.Llm;
using Xunit;

namespace LlmAgents.Tests;

public sealed class InventoryToolContractTests
{
    [Theory]
    [InlineData("transfer_item", """{"item":"#42","target":"#80","count":2}""")]
    [InlineData("transfer_item", """{"item":"#42","target":"#80","container_index":-1}""")]
    [InlineData("transfer_item", """{"item":"#42","target":"#80","slot":"0"}""")]
    [InlineData("transfer_item", """{"item":"#42","target":"#80","slot":-1}""")]
    [InlineData("transfer_item", """{"item":"#42","target":"#80","count":0}""")]
    [InlineData("transfer_item", """{"item":"#42","target":"#80","count":33}""")]
    [InlineData("transfer_item", """{"item":"#42","target":"#80","replace":true}""")]
    [InlineData("transfer_item", """{"item":" ","target":"#80"}""")]
    [InlineData("transfer_item", """{"item":"#42","target":" "}""")]
    [InlineData("transfer_item", """{"item":"*","target":"#80"}""")]
    [InlineData("inspect_inventory", """{"depth":9}""")]
    [InlineData("inspect_inventory", """{"depth":-1}""")]
    [InlineData("inspect_inventory", """{"depth":"4"}""")]
    [InlineData("approach_object", """{"target":" "}""")]
    [InlineData("interact_object", """{"target":"#42","key":"fire"}""")]
    [InlineData("interact_object", """{"target":"#42","ignore_required_items":true}""")]
    public void InvalidInventoryActionsFailBeforeAnyGameMutation(string tool, string json)
    {
        var game = new FakeGame().AddBot(1, "Ann", "mechanic");
        using var manager = new AgentManager(Fixtures.Settings(), game, new LlmRouter(Fixtures.Settings()), () => 0);
        var result = Tools.Execute(new ToolCall(tool, JsonNode.Parse(json)!.AsObject()), new BotAgent(game.Bots[0]), game, manager);
        Assert.StartsWith("error:", result);
        Assert.Empty(game.Actions);
        Assert.Null(game.LastTransfer);
    }

    [Fact]
    public void ZeroBasedCompartmentAndSlotArePreservedIncludingZero()
    {
        var game = new FakeGame().AddBot(1, "Ann", "mechanic");
        using var manager = new AgentManager(Fixtures.Settings(), game, new LlmRouter(Fixtures.Settings()), () => 0);
        var bot = new BotAgent(game.Bots[0]);
        string Run(string json) => Tools.Execute(new ToolCall("transfer_item", JsonNode.Parse(json)!.AsObject()), bot, game, manager);
        Assert.StartsWith("ok, started", Run("""{"item":"#42","target":"#80","container_index":0,"slot":0}"""));
        Assert.Equal(("#42", "#80", 1, (int?)0, (int?)0), game.LastTransfer!.Value);
        Assert.StartsWith("ok, started", Run("""{"item":"steel","target":"self","count":3}"""));
        Assert.Equal(("steel", "self", 3, (int?)null, (int?)null), game.LastTransfer!.Value);
    }

    [Fact]
    public void InventoryLookupAndApproachKeepDifferentExecutionSemantics()
    {
        Assert.Equal(ToolKind.Lookup, Tools.Def("inspect_inventory")!.Kind);
        foreach (var name in new[] { "transfer_item", "approach_object", "interact_object" })
        {
            Assert.Equal(ToolKind.Task, Tools.Def(name)!.Kind);
            Assert.True(Tools.Def(name)!.MovesOn);
        }
        var game = new FakeGame().AddBot(1, "Ann", "mechanic");
        using var manager = new AgentManager(Fixtures.Settings(), game, new LlmRouter(Fixtures.Settings()), () => 0);
        var bot = new BotAgent(game.Bots[0]);
        Assert.Equal("inventory self depth=4", Tools.Execute(new ToolCall("inspect_inventory", new JsonObject()), bot, game, manager));
        Assert.Empty(game.Actions);
        Assert.StartsWith("ok, started", Tools.Execute(new ToolCall("approach_object", new JsonObject { ["target"] = "#80" }), bot, game, manager));
        Assert.Equal("1:approach #80", game.Actions.Single());
    }

    [Fact]
    public void PromptExplainsCompleteInventoryAndObjectHandleWorkflow()
    {
        var system = Prompts.System(new BotAgent(new BotHandle(1, "Ann", "mechanic")), 8);
        foreach (var text in new[] { "INVENTORY INTERACTION", "ZERO-BASED", "INNER item's #ID", "count=1", "accessible_now=false",
            "OBJECT HANDLES", "offset counts TOOLS", "NEXT request", "Handles expire", "game data", "inspect-only object" })
            Assert.Contains(text, system);
        var prompt = ToolPrompt.Build(system, Tools.Filtered(new[] { "inspect_inventory", "transfer_item", "approach_object", "object_tools" }));
        foreach (var text in new[] { "container_index", "slot", "minimum", "depth", "additionalProperties", "Never swaps" })
            Assert.Contains(text, prompt);
    }

    [Fact]
    public void NativeInteractionHandleFixesObjectAndKeyAndPreservesRemainingSchema()
    {
        var game = new FakeGame().AddBot(1, "Ann", "mechanic");
        game.Objects.Add(new GameObjectInfo("#42", "round_instance42", "Mod button", "mod_button", "item", new[] { "CustomController" },
            new[] { new ObjectToolBinding("interact_object", new JsonObject { ["target"] = "#42", ["key"] = "use" }) }));
        using var manager = new AgentManager(Fixtures.Settings(), game, new LlmRouter(Fixtures.Settings()), () => 0);
        var bot = new BotAgent(game.Bots[0]);
        manager.DiscoverObjectTools(bot, "#42", 0, 20);
        var spec = manager.ToolsFor(bot).Single(t => t.Name.StartsWith("obj_") && manager.CanonicalCall(bot, new ToolCall(t.Name, new JsonObject())).Name == "interact_object");
        Assert.Empty(spec.Parameters["properties"]!.AsObject());
        Assert.Contains("ONE native interaction-key press", spec.Description);
        Assert.StartsWith("error:", Tools.Execute(new ToolCall(spec.Name, new JsonObject { ["key"] = "select" }), bot, game, manager));
        Assert.Empty(game.Actions);
        Assert.StartsWith("ok, started", Tools.Execute(new ToolCall(spec.Name, new JsonObject()), bot, game, manager));
        Assert.Equal("1:interact #42 key=use", game.Actions.Single());
    }
}
