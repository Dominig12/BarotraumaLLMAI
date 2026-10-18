using System;
using System.Linq;
using System.Net;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using LlmAgents.Agents;
using LlmAgents.Llm;
using Xunit;

namespace LlmAgents.Tests;

public sealed class ObjectToolsTests
{
    static GameObjectInfo Item(int id, string lifetime = "round1_instance1", params ObjectToolBinding[] actions) =>
        new("#" + id, lifetime + "_" + id, "Модовый предмет " + id, "some_mod.prefab", "item", new[] { "ModdedComponent" }, actions);

    static ObjectToolBinding Take(int id) => new("take_item", new JsonObject { ["item"] = "#" + id });

    static string BoundName(AgentManager manager, BotAgent bot, string canonical) => manager.ToolsFor(bot)
        .Where(spec => spec.Name.StartsWith("obj_", StringComparison.Ordinal))
        .First(spec => manager.CanonicalCall(bot, new ToolCall(spec.Name, new JsonObject())).Name == canonical).Name;

    [Fact]
    public void EveryObjectIncludingUnknownModComponentGetsAnInspectHandle()
    {
        var game = new FakeGame().AddBot(1, "Ann", "assistant");
        game.Objects.Add(Item(10));
        game.Objects.Add(new GameObjectInfo("[e21]", "round1_structure21", "Custom wall", "modwall", "structure", Array.Empty<string>(), Array.Empty<ObjectToolBinding>()));
        var settings = Fixtures.Settings();
        using var manager = new AgentManager(settings, game, new LlmRouter(settings), () => 0);
        var bot = new BotAgent(game.Bots[0]);
        Assert.DoesNotContain(manager.ToolsFor(bot), spec => spec.Name.StartsWith("obj_"));
        var page = manager.DiscoverObjectTools(bot, null, 0, 20);
        Assert.Contains("2 matching tools", page);
        var generated = manager.ToolsFor(bot).Where(spec => spec.Name.StartsWith("obj_")).ToArray();
        Assert.Equal(2, generated.Length);
        Assert.All(generated, spec => Assert.True(spec.Name.Length <= 64));
        Assert.All(generated, spec => Assert.Empty(spec.Parameters["required"]!.AsArray()));
        Assert.All(generated, spec => Assert.StartsWith("object ", Tools.Execute(new ToolCall(spec.Name, new JsonObject()), bot, game, manager)));
    }

    [Fact]
    public void BoundFieldsCannotBeOverriddenAndCanonicalSchemaRemainsIntact()
    {
        var game = new FakeGame().AddBot(1, "Ann", "assistant");
        game.Objects.Add(Item(10, "r1_i1", Take(10)));
        var settings = Fixtures.Settings();
        using var manager = new AgentManager(settings, game, new LlmRouter(settings), () => 0);
        var bot = new BotAgent(game.Bots[0]);
        manager.DiscoverObjectTools(bot, "#10", 0, 20);
        var name = BoundName(manager, bot, "take_item");
        var schema = manager.ResolveTool(bot, name)!.Schema;
        Assert.Contains(name, manager.ToolHelp(bot, name));
        Assert.Contains("Fixed arguments", Tools.Execute(new ToolCall("inspect", new JsonObject { ["kind"] = "tools", ["query"] = name }), bot, game, manager));
        Assert.False(schema["properties"]!.AsObject().ContainsKey("item"));
        Assert.True(Tools.Def("take_item")!.Schema["properties"]!.AsObject().ContainsKey("item"));
        Assert.StartsWith("error: unknown argument 'item'", Tools.Execute(new ToolCall(name, new JsonObject { ["item"] = "#99" }), bot, game, manager));
        Assert.Empty(game.Actions);
        Assert.StartsWith("ok, started", Tools.Execute(new ToolCall(name, new JsonObject { ["count"] = 1 }), bot, game, manager));
        Assert.Contains("1:take #10 x1", game.Actions);
    }

    [Fact]
    public void DespawnAndIdReuseInvalidateOldHandleAndNewRoundDropsBindings()
    {
        var game = new FakeGame().AddBot(1, "Ann", "assistant");
        game.Objects.Add(Item(10, "round1_first", Take(10)));
        var settings = Fixtures.Settings();
        using var manager = new AgentManager(settings, game, new LlmRouter(settings), () => 0);
        var bot = new BotAgent(game.Bots[0]);
        manager.DiscoverObjectTools(bot, "#10", 0, 20);
        var old = BoundName(manager, bot, "take_item");
        game.Objects.Clear();
        Assert.Contains("expired", Tools.Execute(new ToolCall(old, new JsonObject()), bot, game, manager));
        game.Objects.Add(Item(10, "round1_second", Take(10)));
        Assert.Contains("expired", Tools.Execute(new ToolCall(old, new JsonObject()), bot, game, manager));
        manager.DiscoverObjectTools(bot, "#10", 0, 20);
        var fresh = BoundName(manager, bot, "take_item");
        Assert.NotEqual(old, fresh);
        Assert.StartsWith("ok", Tools.Execute(new ToolCall(fresh, new JsonObject()), bot, game, manager));
        manager.Reset();
        Assert.StartsWith("error: unknown tool", Tools.Execute(new ToolCall(fresh, new JsonObject()), bot, game, manager));
    }

    [Fact]
    public void GeneratedAliasesNeverBypassCanonicalAllowedTools()
    {
        var game = new FakeGame().AddBot(1, "Ann", "assistant");
        game.Objects.Add(Item(10, "r1_i1", Take(10)));
        var settings = Fixtures.Settings();
        using var manager = new AgentManager(settings, game, new LlmRouter(settings), () => 0);
        var bot = new BotAgent(game.Bots[0]);
        manager.DiscoverObjectTools(bot, "#10", 0, 20);
        var take = BoundName(manager, bot, "take_item");
        settings.Bots.AllowedTools.AddRange(new[] { "object_tools", "inspect_object", take });
        Assert.StartsWith("error:", Tools.Execute(new ToolCall(take, new JsonObject()), bot, game, manager));
        Assert.DoesNotContain(manager.ToolsFor(bot), spec => spec.Name == take);
        Assert.DoesNotContain(" -> take_item", manager.DiscoverObjectTools(bot, "#10", 0, 20));
        Assert.Empty(game.Actions);
    }

    [Fact]
    public void SpawnedObjectsAppearImmediatelyAndBindingsArePrivateToEachBot()
    {
        var game = new FakeGame().AddBot(1, "Ann", "assistant").AddBot(2, "Bob", "mechanic");
        var settings = Fixtures.Settings();
        using var manager = new AgentManager(settings, game, new LlmRouter(settings), () => 0);
        var ann = new BotAgent(game.Bots[0]);
        var bob = new BotAgent(game.Bots[1]);
        Assert.Contains("0 matching tools", manager.DiscoverObjectTools(ann, null, 0, 20));
        game.Objects.Add(Item(10, "spawned", Take(10)));
        manager.DiscoverObjectTools(ann, "#10", 0, 20);
        var take = BoundName(manager, ann, "take_item");
        Assert.StartsWith("error: unknown tool", Tools.Execute(new ToolCall(take, new JsonObject()), bob, game, manager));
        manager.DiscoverObjectTools(bob, "#10", 0, 20);
        Assert.StartsWith("ok", Tools.Execute(new ToolCall(take, new JsonObject()), bob, game, manager));
        Assert.Contains("2:take #10 x1", game.Actions);
    }

    [Fact]
    public void CapabilityRemovalInvalidatesAHandleWithoutRemovingTheObject()
    {
        var game = new FakeGame().AddBot(1, "Ann", "assistant");
        game.Objects.Add(Item(10, "same_lifetime", Take(10)));
        var settings = Fixtures.Settings();
        using var manager = new AgentManager(settings, game, new LlmRouter(settings), () => 0);
        var bot = new BotAgent(game.Bots[0]);
        manager.DiscoverObjectTools(bot, "#10", 0, 20);
        var take = BoundName(manager, bot, "take_item");
        var inspect = BoundName(manager, bot, "inspect_object");
        game.Objects[0] = Item(10, "same_lifetime");
        Assert.Contains("expired", Tools.Execute(new ToolCall(take, new JsonObject()), bot, game, manager));
        Assert.Equal("object #10", Tools.Execute(new ToolCall(inspect, new JsonObject()), bot, game, manager));
        Assert.Empty(game.Actions);
    }

    [Fact]
    public void ReuseOfAnotherFixedTargetIdAlsoInvalidatesTheHandle()
    {
        var game = new FakeGame().AddBot(1, "Ann", "assistant");
        game.Objects.Add(Item(10, "source", new ObjectToolBinding("move_item", new JsonObject { ["item"] = "#10", ["to"] = "#11", ["destination"] = "container" })));
        game.Objects.Add(Item(11, "first_destination"));
        var settings = Fixtures.Settings();
        using var manager = new AgentManager(settings, game, new LlmRouter(settings), () => 0);
        var bot = new BotAgent(game.Bots[0]);
        manager.DiscoverObjectTools(bot, "#10", 0, 20);
        var move = BoundName(manager, bot, "move_item");
        game.Objects[1] = Item(11, "replacement_destination");
        Assert.Contains("expired", Tools.Execute(new ToolCall(move, new JsonObject()), bot, game, manager));
        Assert.Empty(game.Actions);
        manager.DiscoverObjectTools(bot, "#10", 0, 20);
        var fresh = BoundName(manager, bot, "move_item");
        Assert.NotEqual(move, fresh);
        Assert.StartsWith("ok", Tools.Execute(new ToolCall(fresh, new JsonObject()), bot, game, manager));
        Assert.Contains("1:move #10 -> #11", game.Actions);
    }

    [Fact]
    public void DiscoveryUsesExactSelectorsAndMultipleFixedBindingsHaveDistinctNames()
    {
        var game = new FakeGame().AddBot(1, "Ann", "assistant");
        game.Objects.Add(Item(10, "r1_i1", Take(10), new ObjectToolBinding("take_item", new JsonObject { ["item"] = "#10", ["count"] = 1 })));
        var settings = Fixtures.Settings();
        using var manager = new AgentManager(settings, game, new LlmRouter(settings), () => 0);
        var bot = new BotAgent(game.Bots[0]);
        Assert.Contains("0 matching tools", manager.DiscoverObjectTools(bot, "Модовый", 0, 20));
        Assert.Contains("3 matching tools", manager.DiscoverObjectTools(bot, "some_mod.prefab", 0, 20));
        Assert.Equal(3, manager.ToolsFor(bot).Count(spec => spec.Name.StartsWith("obj_")));
        Assert.StartsWith("error:", manager.DiscoverObjectTools(bot, null, -1, 1));
        Assert.StartsWith("error:", manager.DiscoverObjectTools(bot, null, 0, 21));
    }

    [Fact]
    public void LargeCatalogIsPagedAndRunningPlanBindingsSurviveEviction()
    {
        var game = new FakeGame().AddBot(1, "Ann", "assistant");
        for (var i = 0; i < 1000; i++) game.Objects.Add(Item(i));
        var settings = Fixtures.Settings();
        using var manager = new AgentManager(settings, game, new LlmRouter(settings), () => 0);
        var bot = new BotAgent(game.Bots[0]);
        manager.DiscoverObjectTools(bot, "#0", 0, 20);
        var pinned = BoundName(manager, bot, "inspect_object");
        bot.SetPlan(new ActivePlan(PlanTree.FromCalls(new[] { new ToolCall(pinned, new JsonObject()) }), false, 0));
        for (var offset = 0; offset < 100; offset += 20)
            Assert.Contains("1000 matching tools", manager.DiscoverObjectTools(bot, null, offset, 20));
        var offered = manager.ToolsFor(bot);
        Assert.True(offered.Count <= ObjectToolRegistry.NativeToolLimit);
        Assert.True(offered.Count(spec => spec.Name.StartsWith("obj_")) <= ObjectToolRegistry.MaxBoundTools);
        Assert.NotNull(manager.ResolveTool(bot, pinned));
        Assert.Equal("object #0", Tools.Execute(new ToolCall(pinned, new JsonObject()), bot, game, manager));
        Assert.Contains(pinned, manager.PlanToolNames(bot));
    }

    [Fact]
    public async Task GeneratedCallsAreOfferedNextTurnAndPlansWaitForActualTaskReports()
    {
        var game = new FakeGame().AddBot(1, "Ann", "assistant");
        game.Objects.Add(Item(10, "r1_i1", Take(10)));
        game.Objects.Add(new GameObjectInfo("[h11]", "r1_room11", "Command Room", "command", "room", Array.Empty<string>(),
            new[] { new ObjectToolBinding("go_to_room", new JsonObject { ["room"] = "Command Room [h11]" }) }));
        var settings = Fixtures.Settings();
        var clock = new FakeClock();
        var replies = 0;
        var handler = new FakeHandler(body =>
        {
            replies++;
            if (replies == 1) return (HttpStatusCode.OK, Fixtures.OpenAiToolResponse(("object_tools", "{}")));
            if (replies != 2) return (HttpStatusCode.OK, Fixtures.OpenAiToolResponse(("carry_on", "{}")));
            var names = body["tools"]!.AsArray().Select(tool => tool!["function"]!["name"]!.GetValue<string>()).ToArray();
            var take = Assert.Single(names, name => name.Contains("_take_item_"));
            var go = Assert.Single(names, name => name.Contains("_go_to_room_"));
            return (HttpStatusCode.OK, Fixtures.OpenAiToolResponse(("plan", new JsonObject
            {
                ["steps"] = new JsonArray
                {
                    new JsonObject { ["do"] = go, ["args"] = new JsonObject() },
                    new JsonObject { ["do"] = take, ["args"] = new JsonObject { ["count"] = 1 } },
                    new JsonObject { ["do"] = "say", ["args"] = new JsonObject { ["text"] = "done" } },
                },
            }.ToJsonString())));
        });
        using var manager = new AgentManager(settings, game, new LlmRouter(settings, handler, clock.Utc), clock.Get);
        for (var i = 0; i < 10; i++) { manager.Update(); await manager.WhenIdle(); clock.Advance(0.5); }
        Assert.Contains("1:goto Command Room [h11]", game.Actions);
        Assert.Equal(PlanWait.Arrival, manager.GetAgent(1)!.Plan!.Waiting);
        Assert.DoesNotContain("1:take #10 x1", game.Actions);
        game.Observations[1].Room = "Command Room [h11]";
        clock.Advance(1);
        manager.Update();
        Assert.Contains("1:end travel", game.Actions);
        Assert.Contains("1:take #10 x1", game.Actions);
        Assert.DoesNotContain("1:say done", game.Actions);
        Assert.Equal(PlanWait.Task, manager.GetAgent(1)!.Plan!.Waiting);
        Assert.Equal("take_item", manager.GetAgent(1)!.TaskTool);
        game.PendingReports.Add(new GameReport(1, "task 'take #10' ok: taken"));
        clock.Advance(0.5);
        manager.Update();
        Assert.Contains("1:say done", game.Actions);
    }

    [Fact]
    public void PromptParserAcceptsGeneratedNamesWithTheSameFullSchema()
    {
        var game = new FakeGame().AddBot(1, "Ann", "assistant");
        game.Objects.Add(Item(10, "r1_i1", Take(10)));
        var settings = Fixtures.Settings();
        using var manager = new AgentManager(settings, game, new LlmRouter(settings), () => 0);
        var bot = new BotAgent(game.Bots[0]);
        manager.DiscoverObjectTools(bot, "#10", 0, 20);
        var take = BoundName(manager, bot, "take_item");
        var calls = TextToolCallParser.Parse(new JsonObject { ["calls"] = new JsonArray
            { new JsonObject { ["name"] = take, ["arguments"] = new JsonObject { ["count"] = 1 } } } }.ToJsonString(), manager.ToolsFor(bot));
        Assert.Equal(take, Assert.Single(calls).Name);
        Assert.StartsWith("ok", Tools.Execute(calls[0], bot, game, manager));
    }
}
