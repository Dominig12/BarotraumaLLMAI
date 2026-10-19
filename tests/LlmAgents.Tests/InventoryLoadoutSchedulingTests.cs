using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using LlmAgents.Agents;
using LlmAgents.Llm;
using LlmAgents.Memory;
using Xunit;

namespace LlmAgents.Tests;

public sealed class InventoryLoadoutSchedulingTests
{
    sealed class Harness : IDisposable
    {
        public readonly FakeClock Clock = new();
        public readonly FakeGame Game = new FakeGame().AddBot(1, "Ann", "mechanic");
        public readonly ConcurrentQueue<JsonObject> Seen = new();
        public readonly AgentManager Manager;
        public readonly LlmRouter Router;
        public TaskCompletionSource<bool>? ResponseGate;
        public readonly TaskCompletionSource<bool> RequestStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public BotAgent Agent => Manager.GetAgent(1)!;

        public Harness(string autonomy = "events", Action<ModSettings>? configure = null)
        {
            var settings = Fixtures.Settings();
            settings.Bots.Autonomy = autonomy;
            settings.Bots.ThinkAtRoundStart = false;
            settings.Global.MaxPlanSeconds = 100000;
            configure?.Invoke(settings);
            var handler = new FakeHandler(async (_, body, token) =>
            {
                var user = body["messages"]!.AsArray()[^1]!["content"]!.GetValue<string>();
                Seen.Enqueue(JsonNode.Parse(Fixtures.Obs(user))!.AsObject());
                RequestStarted.TrySetResult(true);
                if (ResponseGate is { } gate) await gate.Task.WaitAsync(token);
                return (HttpStatusCode.OK, Fixtures.OpenAiToolResponse(("carry_on", "{}")));
            });
            Router = new LlmRouter(settings, handler, Clock.Utc);
            Manager = new AgentManager(settings, Game, Router, Clock.Get);
            Manager.BeginRound("test");
            Manager.Update();
        }

        public async Task At(double now)
        {
            Clock.Now = now;
            Manager.Update();
            await Manager.WhenIdle();
        }

        public void Configure(int reviewSeconds = 120, bool enabled = true) =>
            Assert.StartsWith("ok", Manager.SetInventoryLoadout(Agent, Profile(enabled, reviewSeconds)));

        public void Dispose()
        {
            ResponseGate?.TrySetResult(true);
            Manager.Dispose();
        }
    }

    static InventoryLoadout Profile(bool enabled = true, int reviewSeconds = 120) => new()
    {
        Enabled = enabled,
        ReviewIntervalSeconds = reviewSeconds,
        Slots = new() { new() { Slot = 0, Prefab = "wrench", Path = new() { new(6) } } },
    };

    [Theory]
    [InlineData("events")]
    [InlineData("order")]
    public async Task ExplicitProfilePermitsReviewsAt120SecondsEvenWithoutGeneralAutonomy(string autonomy)
    {
        using var h = new Harness(autonomy);
        h.Configure();
        await h.At(119.5);
        Assert.Empty(h.Seen);
        await h.At(120);
        var observation = Assert.Single(h.Seen);
        Assert.StartsWith("[inventory review]", observation["why"]!.AsArray()[0]!.GetValue<string>());
        var layout = observation["inventoryLoadout"]!.AsObject();
        Assert.True(layout["enabled"]!.GetValue<bool>());
        Assert.Equal(120, layout["review_interval_seconds"]!.GetValue<int>());
        Assert.Equal("wrench", layout["slots"]![0]!["prefab"]!.GetValue<string>());
        Assert.Equal(6, layout["slots"]![0]!["path"]![0]!["slot"]!.GetValue<int>());
        Assert.Contains("enabled=True", observation["inventoryLoadoutStatus"]!.GetValue<string>());
        Assert.Equal(240, h.Agent.NextInventoryReviewAt);
        await h.At(120.5);
        await h.At(239.5);
        Assert.Single(h.Seen);
        await h.At(240);
        Assert.Equal(2, h.Seen.Count);
    }

    [Theory]
    [InlineData("events")]
    [InlineData("order")]
    public async Task BotsWithoutAnExplicitProfileDoNotGetInventoryPollingRequests(string autonomy)
    {
        using var h = new Harness(autonomy);
        await h.At(3600);
        Assert.Empty(h.Seen);
        Assert.Null(h.Agent.InventoryLoadout);
    }

    [Fact]
    public async Task MinimumReviewIntervalAndEmptyManagedLayoutAreRespected()
    {
        using var h = new Harness();
        h.Configure(reviewSeconds: 30);
        await h.At(29.5);
        Assert.Empty(h.Seen);
        await h.At(30);
        Assert.Single(h.Seen);
        await h.At(30.5);
        Assert.StartsWith("ok", h.Manager.SetInventoryLoadout(h.Agent, new InventoryLoadout { ReviewIntervalSeconds = 30 }));
        await h.At(90);
        Assert.Single(h.Seen);
    }

    [Fact]
    public async Task InventoryReviewWaitsForThePlanAndNativeTaskToFinish()
    {
        using var h = new Harness();
        h.Configure(reviewSeconds: 30);
        var plan = new ActivePlan(PlanTree.FromCalls(new[] { new ToolCall("craft", new JsonObject { ["item"] = "bandage" }) }), false, 0);
        plan.WaitFor(PlanWait.Task, 0);
        h.Agent.SetPlan(plan);
        await h.At(30);
        Assert.Empty(h.Seen);
        Assert.Same(plan, h.Agent.Plan);
        Assert.DoesNotContain(h.Agent.Events, e => e.Kind == EventKind.InventoryReview);
        h.Agent.ClearPlan();
        h.Game.Observations[1].CurrentTask = "crafting bandages";
        await h.At(30.5);
        Assert.Empty(h.Seen);
        h.Game.Observations[1].CurrentTask = null;
        await h.At(45.5);
        Assert.Single(h.Seen);
        Assert.False(EventPolicy.InterruptsPlan(EventKind.InventoryReview));
        Assert.True(EventPolicy.Tier(EventKind.InventoryReview) > EventPolicy.Tier(EventKind.Order));
    }

    [Theory]
    [InlineData("fire")]
    [InlineData("flood")]
    [InlineData("threat")]
    [InlineData("breach")]
    [InlineData("hurt")]
    [InlineData("air")]
    public async Task OrderAutonomyStillDefersInventoryReviewDuringActualDanger(string danger)
    {
        using var h = new Harness("order");
        h.Configure(reviewSeconds: 30);
        var observation = h.Game.Observations[1];
        switch (danger)
        {
            case "fire": observation.RoomFires = 1; break;
            case "flood": observation.Rooms[1] = new("Command Room [h11]", 80, 0); break;
            case "threat": observation.Threats.Add(new("Crawler", 5, observation.Room)); break;
            case "breach": observation.Leaks.Add("Ballast [h12]"); break;
            case "hurt": observation.HealthPercent = 50; break;
            case "air": observation.OxygenPercent = 50; break;
        }
        Assert.False(h.Agent.InDanger); // Order autonomy does not continuously collect danger-triggered decisions.
        await h.At(30);
        Assert.Empty(h.Seen);
        Assert.DoesNotContain(h.Agent.Events, e => e.Kind == EventKind.InventoryReview);
        observation.RoomFires = 0;
        observation.Rooms[1] = new("Command Room [h11]", 0, 0);
        observation.Threats.Clear();
        observation.Leaks.Clear();
        observation.HealthPercent = observation.OxygenPercent = 100;
        await h.At(45);
        Assert.Single(h.Seen);
    }

    [Fact]
    public async Task NoAvailableModelSlotQueuesOnlyOneReviewAndDisableRemovesIt()
    {
        using var h = new Harness(configure: s => s.Global.MaxConcurrentRequests = 1);
        h.Configure(reviewSeconds: 30);
        Assert.True(h.Router.TryReserve("a", out var held, out _));
        using (held)
        {
            for (int second = 30; second <= 120; second += 5) await h.At(second);
            Assert.Empty(h.Seen);
            Assert.Single(h.Agent.Events, e => e.Kind == EventKind.InventoryReview);
            h.Configure(reviewSeconds: 30, enabled: false);
            Assert.DoesNotContain(h.Agent.Events, e => e.Kind == EventKind.InventoryReview);
        }
        await h.At(121);
        await h.At(1000);
        Assert.Empty(h.Seen);
        Assert.False(h.Game.InventoryLoadouts[1].Enabled);
        Assert.False(h.Manager.Memory.Bot("Ann")!.InventoryLoadout!.Enabled);
    }

    [Fact]
    public async Task ASlowModelDoesNotAccumulateMoreInventoryReviewEvents()
    {
        using var h = new Harness();
        h.Configure(reviewSeconds: 30);
        h.ResponseGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Clock.Now = 30;
        h.Manager.Update();
        await h.RequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(h.Agent.Pending);
        for (int second = 31; second <= 90; second++)
        {
            h.Clock.Now = second;
            h.Manager.Update();
            Assert.DoesNotContain(h.Agent.Events, e => e.Kind == EventKind.InventoryReview);
        }
        Assert.Single(h.Seen);
        h.ResponseGate.TrySetResult(true);
        await h.Manager.WhenIdle();
        await h.At(90.5);
        Assert.InRange(h.Seen.Count, 1, 2); // At most the one newly due review after applying the delayed answer.
        Assert.DoesNotContain(h.Agent.Events, e => e.Kind == EventKind.InventoryReview);
    }

    [Fact]
    public async Task AWaitingHumanOrderGetsTheFirstPriorityWhenAReviewWasAlreadyQueued()
    {
        using var h = new Harness(configure: s => s.Global.MaxConcurrentRequests = 1);
        h.Configure(reviewSeconds: 30);
        Assert.True(h.Router.TryReserve("a", out var held, out _));
        await h.At(30);
        Assert.True(h.Agent.HasEvent(EventKind.InventoryReview));
        h.Agent.ReceivePlayerOrder("Daniil: repair the engine", from: "Daniil");
        held!.Dispose();
        await h.At(30.5);
        var observation = Assert.Single(h.Seen);
        Assert.StartsWith("[order]", observation["why"]!.AsArray()[0]!.GetValue<string>());
        Assert.Contains("repair the engine", observation["playerOrders"]!.AsArray()[0]!.GetValue<string>());
    }

    [Fact]
    public async Task InventoryReviewStaysWithItsBotWhenTheDirectorIsEnabled()
    {
        using var h = new Harness(configure: s => { s.Director.Enabled = true; s.Director.IdleCheckSeconds = 0; });
        h.Configure(reviewSeconds: 30);
        await h.At(30);
        Assert.Equal("Ann", Assert.Single(h.Seen)["name"]!.GetValue<string>());
        Assert.Equal(0, h.Manager.DirectorDecisions);
        Assert.StartsWith("inventory review:", h.Agent.LastWhy);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NewRoundRestoresProfileToANewCharacterIdIncludingDisabledState(bool enabled)
    {
        using var h = new Harness();
        h.Configure(enabled: enabled);
        var previous = h.Agent.InventoryLoadout!;
        h.Manager.Reset();
        h.Game.Bots.Clear();
        h.Game.InventoryLoadouts.Clear(); // The game adapter clears round-specific runtime state too.
        h.Game.AddBot(42, "ANN", "mechanic");
        h.Clock.Now = 10;
        h.Manager.BeginRound("test");
        h.Manager.Update();
        var restored = h.Manager.GetAgent(42)!;
        Assert.NotNull(restored);
        Assert.Null(h.Manager.GetAgent(1));
        Assert.Equal(enabled, restored.InventoryLoadout!.Enabled);
        Assert.Equal(enabled, h.Game.InventoryLoadouts[42].Enabled);
        Assert.Equal("wrench", restored.InventoryLoadout.Slots[0].Prefab);
        Assert.Equal(new InventoryContainerStep(6), restored.InventoryLoadout.Slots[0].Path[0]);
        Assert.Equal(130, restored.NextInventoryReviewAt);
        Assert.NotSame(previous, restored.InventoryLoadout);
        previous.Slots[0].Path.Clear();
        Assert.Single(restored.InventoryLoadout.Slots[0].Path);
    }
}
