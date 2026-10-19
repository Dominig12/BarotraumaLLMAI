using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using LlmAgents.Agents;
using LlmAgents.Llm;
using LlmAgents.Memory;
using Xunit;

namespace LlmAgents.Tests;

public sealed class InventoryLoadoutToolsTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"slots\":null}")]
    [InlineData("{\"slots\":\"all\"}")]
    [InlineData("{\"slots\":[null]}")]
    [InlineData("{\"slots\":[{\"slot\":0}]}")]
    [InlineData("{\"slots\":[{\"slot\":0,\"prefab\":true}]}")]
    [InlineData("{\"slots\":[{\"slot\":0,\"prefab\":42}]}")]
    [InlineData("{\"slots\":[{\"slot\":\"0\",\"prefab\":\"wrench\"}]}")]
    [InlineData("{\"slots\":[{\"slot\":null,\"prefab\":\"wrench\"}]}")]
    [InlineData("{\"slots\":[{\"slot\":0.5,\"prefab\":\"wrench\"}]}")]
    [InlineData("{\"slots\":[{\"slot\":-1,\"prefab\":\"wrench\"}]}")]
    [InlineData("{\"slots\":[{\"slot\":256,\"prefab\":\"wrench\"}]}")]
    [InlineData("{\"slots\":[{\"slot\":0,\"prefab\":\"wrench\",\"count\":\"2\"}]}")]
    [InlineData("{\"slots\":[{\"slot\":0,\"prefab\":\"wrench\",\"count\":null}]}")]
    [InlineData("{\"slots\":[{\"slot\":0,\"prefab\":\"wrench\",\"count\":0}]}")]
    [InlineData("{\"slots\":[{\"slot\":0,\"prefab\":\"wrench\",\"count\":33}]}")]
    [InlineData("{\"slots\":[{\"slot\":0,\"prefab\":null,\"count\":2}]}")]
    [InlineData("{\"slots\":[{\"slot\":0,\"prefab\":\"#123\"}]}")]
    [InlineData("{\"slots\":[{\"slot\":0,\"prefab\":\"mod*\"}]}")]
    [InlineData("{\"slots\":[{\"slot\":0,\"prefab\":\" \"}]}")]
    [InlineData("{\"slots\":[],\"enabled\":\"true\"}")]
    [InlineData("{\"slots\":[],\"enabled\":null}")]
    [InlineData("{\"slots\":[],\"check_interval_seconds\":4}")]
    [InlineData("{\"slots\":[],\"check_interval_seconds\":301}")]
    [InlineData("{\"slots\":[],\"review_interval_seconds\":29}")]
    [InlineData("{\"slots\":[],\"review_interval_seconds\":3601}")]
    [InlineData("{\"slots\":[],\"review_interval_seconds\":\"120\"}")]
    [InlineData("{\"slots\":[],\"review_interval_seconds\":null}")]
    [InlineData("{\"slots\":[],\"fetch_missing\":true}")]
    [InlineData("{\"slots\":[{\"slot\":0,\"prefab\":\"wrench\",\"category\":\"tool\"}]}")]
    [InlineData("{\"slots\":[{\"slot\":0,\"prefab\":\"wrench\",\"path\":null}]}")]
    [InlineData("{\"slots\":[{\"slot\":0,\"prefab\":\"wrench\",\"path\":\"toolbelt\"}]}")]
    [InlineData("{\"slots\":[{\"slot\":0,\"prefab\":\"wrench\",\"path\":[null]}]}")]
    [InlineData("{\"slots\":[{\"slot\":0,\"prefab\":\"wrench\",\"path\":[{}]}]}")]
    [InlineData("{\"slots\":[{\"slot\":0,\"prefab\":\"wrench\",\"path\":[{\"slot\":\"6\"}]}]}")]
    [InlineData("{\"slots\":[{\"slot\":0,\"prefab\":\"wrench\",\"path\":[{\"slot\":6,\"container_index\":32}]}]}")]
    [InlineData("{\"slots\":[{\"slot\":0,\"prefab\":\"wrench\",\"path\":[{\"slot\":6,\"container_index\":null}]}]}")]
    [InlineData("{\"slots\":[{\"slot\":0,\"prefab\":\"wrench\",\"path\":[{\"slot\":6,\"item_id\":123}]}]}")]
    [InlineData("{\"slots\":[{\"slot\":0,\"prefab\":\"wrench\"},{\"slot\":0,\"prefab\":\"screwdriver\"}]}")]
    [InlineData("{\"slots\":[{\"slot\":6,\"prefab\":null},{\"slot\":0,\"prefab\":\"wrench\",\"path\":[{\"slot\":6}]}]}")]
    public void InvalidWireArgumentsFailBeforeTheBackendOrMemoryIsChanged(string json)
    {
        var game = new FakeGame().AddBot(1, "Ann", "mechanic");
        var settings = Fixtures.Settings();
        using var manager = new AgentManager(settings, game, new LlmRouter(settings), () => 0);
        manager.BeginRound("test");
        var agent = new BotAgent(game.Bots[0]);
        var result = Tools.Execute(new ToolCall("set_inventory_loadout", JsonNode.Parse(json)!.AsObject()), agent, game, manager);
        Assert.StartsWith("error:", result);
        Assert.Equal(0, game.InventoryLoadoutConfigureCalls);
        Assert.Empty(game.InventoryLoadouts);
        Assert.Empty(game.Actions);
        Assert.Null(agent.InventoryLoadout);
        Assert.Null(manager.Memory.Bot(agent.Name));
    }

    [Fact]
    public void OversizedRuleAndPathArraysAreRejectedBeforeMutation()
    {
        var game = new FakeGame().AddBot(1, "Ann", "mechanic");
        var settings = Fixtures.Settings();
        using var manager = new AgentManager(settings, game, new LlmRouter(settings), () => 0);
        var agent = new BotAgent(game.Bots[0]);
        var slots = new JsonArray(Enumerable.Range(0, 65).Select(slot => (JsonNode)new JsonObject { ["slot"] = slot, ["prefab"] = "wrench" }).ToArray());
        var path = new JsonArray(Enumerable.Range(0, 5).Select(slot => (JsonNode)new JsonObject { ["slot"] = slot }).ToArray());
        foreach (var args in new[]
        {
            new JsonObject { ["slots"] = slots },
            new JsonObject { ["slots"] = new JsonArray(new JsonObject { ["slot"] = 0, ["prefab"] = "wrench", ["path"] = path }) },
        })
            Assert.StartsWith("error:", Tools.Execute(new ToolCall("set_inventory_loadout", args), agent, game, manager));
        Assert.Equal(0, game.InventoryLoadoutConfigureCalls);
        Assert.Empty(game.Actions);
    }

    [Fact]
    public void ZeroBasedNestedSlotsNullAndExactModIdentifierCaseReachTheBackendUnchanged()
    {
        var game = new FakeGame().AddBot(1, "Ann", "mechanic");
        var settings = Fixtures.Settings();
        using var manager = new AgentManager(settings, game, new LlmRouter(settings), () => 10);
        manager.BeginRound("test");
        var agent = new BotAgent(game.Bots[0]);
        var arguments = JsonNode.Parse("""{"slots":[{"slot":0,"prefab":"Mod:Аптечка.v2","count":2,"path":[{"slot":0,"container_index":0}]},{"slot":2,"prefab":null}],"check_interval_seconds":5,"review_interval_seconds":30}""")!.AsObject();
        Assert.StartsWith("ok", Tools.Execute(new ToolCall("set_inventory_loadout", arguments), agent, game, manager));
        var backend = game.InventoryLoadouts[1];
        Assert.Equal("Mod:Аптечка.v2", backend.Slots[0].Prefab);
        Assert.Equal(0, backend.Slots[0].Slot);
        Assert.Equal(2, backend.Slots[0].Count);
        Assert.Equal(new InventoryContainerStep(0, 0), Assert.Single(backend.Slots[0].Path));
        Assert.Null(backend.Slots[1].Prefab);
        Assert.Equal(1, backend.Slots[1].Count);
        Assert.Equal(5, backend.CheckIntervalSeconds);
        Assert.Equal(30, backend.ReviewIntervalSeconds);
        Assert.Equal(40, agent.NextInventoryReviewAt);
        Assert.Equal("Mod:Аптечка.v2", manager.Memory.Bot("Ann")!.InventoryLoadout!.Slots[0].Prefab);
        arguments["slots"]![0]!["prefab"] = "changed after call";
        Assert.Equal("Mod:Аптечка.v2", backend.Slots[0].Prefab);
    }

    [Fact]
    public void BackendRejectionKeepsPreviousProfileMemoryReviewAndObservation()
    {
        var game = new FakeGame().AddBot(1, "Ann", "mechanic");
        var settings = Fixtures.Settings();
        using var manager = new AgentManager(settings, game, new LlmRouter(settings), () => 10);
        manager.BeginRound("test");
        var agent = new BotAgent(game.Bots[0]);
        Assert.StartsWith("ok", manager.SetInventoryLoadout(agent, Profile()));
        var oldProfile = agent.InventoryLoadout;
        var oldBackend = game.InventoryLoadouts[1];
        var oldMemory = JsonSerializer.Serialize(manager.Memory.Bot("Ann"));
        var observation = new Observation();
        agent.LastObservation = observation;
        agent.NextInventoryReviewAt = 25;
        agent.AddEvent(EventKind.InventoryReview, "pending review", 10);
        game.RejectInventoryLoadout = (_, _) => "error: incompatible native slot";
        Assert.Equal("error: incompatible native slot", Tools.Execute(new ToolCall("set_inventory_loadout",
            JsonNode.Parse("""{"slots":[{"slot":3,"prefab":"screwdriver"}]}""")!.AsObject()), agent, game, manager));
        Assert.Same(oldProfile, agent.InventoryLoadout);
        Assert.Same(oldBackend, game.InventoryLoadouts[1]);
        Assert.Equal(oldMemory, JsonSerializer.Serialize(manager.Memory.Bot("Ann")));
        Assert.Equal(25, agent.NextInventoryReviewAt);
        Assert.True(agent.HasEvent(EventKind.InventoryReview));
        Assert.Same(observation, agent.LastObservation);
    }

    [Fact]
    public void CallerBackendAgentAndMemoryHaveIndependentNestedProfileCopies()
    {
        var game = new FakeGame().AddBot(1, "Ann", "mechanic");
        var settings = Fixtures.Settings();
        using var manager = new AgentManager(settings, game, new LlmRouter(settings), () => 0);
        manager.BeginRound("test");
        var agent = new BotAgent(game.Bots[0]);
        var supplied = Profile();
        Assert.StartsWith("ok", manager.SetInventoryLoadout(agent, supplied));
        supplied.Slots[0].Prefab = "caller mutation";
        supplied.Slots[0].Path.Clear();
        game.InventoryLoadouts[1].Slots[0].Prefab = "backend mutation";
        game.InventoryLoadouts[1].Slots[0].Path[0] = new(9, 1);
        Assert.Equal("wrench", agent.InventoryLoadout!.Slots[0].Prefab);
        Assert.Equal(new InventoryContainerStep(6), agent.InventoryLoadout.Slots[0].Path[0]);
        agent.InventoryLoadout.Slots[0].Prefab = "agent mutation";
        agent.InventoryLoadout.Slots[0].Path.Clear();
        var saved = manager.Memory.Bot("Ann")!.InventoryLoadout!;
        Assert.Equal("wrench", saved.Slots[0].Prefab);
        Assert.Equal(new InventoryContainerStep(6), saved.Slots[0].Path[0]);
    }

    [Fact]
    public void EmptyReplacementClearsManagedRulesAndInspectionIsAReadOnlyLookup()
    {
        var game = new FakeGame().AddBot(1, "Ann", "mechanic");
        var settings = Fixtures.Settings();
        using var manager = new AgentManager(settings, game, new LlmRouter(settings), () => 0);
        var agent = new BotAgent(game.Bots[0]);
        manager.SetInventoryLoadout(agent, Profile());
        Assert.StartsWith("ok", Tools.Execute(new ToolCall("set_inventory_loadout", JsonNode.Parse("""{"slots":[]}""")!.AsObject()), agent, game, manager));
        Assert.Empty(game.InventoryLoadouts[1].Slots);
        Assert.Empty(agent.InventoryLoadout!.Slots);
        int before = game.Actions.Count;
        Assert.Equal(ToolKind.Lookup, Tools.Def("inspect_loadout")!.Kind);
        Assert.Equal("inventory loadout enabled=True slots=0", Tools.Execute(new ToolCall("inspect_loadout", new JsonObject()), agent, game, manager));
        Assert.Equal(before, game.Actions.Count);
    }

    static InventoryLoadout Profile() => new()
    {
        Slots = new() { new() { Slot = 0, Prefab = "wrench", Path = new() { new(6) } } },
    };
}
