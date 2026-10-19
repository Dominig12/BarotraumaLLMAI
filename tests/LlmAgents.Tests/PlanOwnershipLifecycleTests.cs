using System;
using System.Net;
using System.Threading.Tasks;
using LlmAgents.Agents;
using LlmAgents.Llm;
using Xunit;

namespace LlmAgents.Tests;

public class PlanOwnershipLifecycleTests
{
    static string EquipReply => Fixtures.OpenAiToolResponse(("equip", "{\"item\":\"wrench\"}"),
        ("say", "{\"text\":\"obsolete tail\"}"));

    [Fact]
    public async Task NonMovementMenuOrderReleasesTheInterruptedPlansTravelOrder()
    {
        using var h = new Harness(Fixtures.OpenAiToolResponse(
            ("go_to_room", "{\"room\":\"Command Room [h11]\"}"),
            ("say", "{\"text\":\"obsolete tail\"}")));
        await h.Request();
        await h.Tick();
        Assert.Equal(PlanWait.Arrival, h.Agent.Plan!.Waiting);
        h.Game.Observations[1].CurrentOrder = "repairsystems ordered by Daniil";

        h.Game.PendingMenuOrders.Add(new MenuOrder(1, "Daniil", "repairsystems"));
        await h.Tick();

        Assert.Null(h.Agent.Plan);
        Assert.Single(h.Game.Actions, action => action == "1:end travel");
        Assert.DoesNotContain("1:say obsolete tail", h.Game.Actions);
        Assert.DoesNotContain("1:dismiss", h.Game.Actions);
        Assert.Equal("repairsystems ordered by Daniil", h.Game.Observations[1].CurrentOrder);
    }

    [Fact]
    public async Task MenuOrderReleasesTheTaskStillOwnedByThePlanBeingCleared()
    {
        using var h = new Harness(EquipReply);
        await h.Request();
        await h.Tick();
        Assert.Equal(PlanWait.Task, h.Agent.Plan!.Waiting);
        Assert.NotNull(h.Game.CurrentTaskId(1));

        h.Game.PendingMenuOrders.Add(new MenuOrder(1, "Daniil", "repairsystems"));
        await h.Tick();

        Assert.Null(h.Agent.Plan);
        Assert.Null(h.Game.CurrentTaskId(1));
        Assert.Single(h.Game.Actions, action => action == "1:cancel task");
        Assert.DoesNotContain("1:say obsolete tail", h.Game.Actions);
        Assert.DoesNotContain("1:dismiss", h.Game.Actions);
    }

    [Fact]
    public async Task MenuOrderTakesPriorityOverAnAlreadyQueuedAnswer()
    {
        using var h = new Harness(EquipReply);
        await h.Request(); // the answer is queued, but the answers phase has not applied it yet
        Assert.Empty(h.Game.Actions);
        h.Game.PendingMenuOrders.Add(new MenuOrder(1, "Daniil", "repairsystems"));

        await h.Tick();

        Assert.Empty(h.Game.Actions);
        Assert.Null(h.Agent.Plan);
        Assert.Null(h.Game.CurrentTaskId(1));
    }

    [Fact]
    public async Task AnswerThatArrivesAfterTheMenuOrderIsDropped()
    {
        using var h = new Harness(EquipReply, delayed: true);
        await h.Request(waitForReply: false);
        await h.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(h.Agent.Pending);
        h.Game.PendingMenuOrders.Add(new MenuOrder(1, "Daniil", "repairsystems"));
        h.TickWithoutWaiting();
        Assert.False(h.Agent.Pending);

        h.ReleaseReply();
        await h.Manager.WhenIdle();
        await h.Tick();

        Assert.Empty(h.Game.Actions);
        Assert.Null(h.Agent.Plan);
        Assert.Null(h.Game.CurrentTaskId(1));
        Assert.Equal(0, h.Agent.Decisions);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReplyForARemovedAgentCannotActAfterTheSameBotIsEnabledAgain(bool disableJob)
    {
        using var h = new Harness(EquipReply, delayed: true);
        var removedAgent = h.Agent;
        await h.Request(waitForReply: false);
        await h.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (disableJob) h.Settings.Bots.DisabledJobs.Add("mechanic");
        else h.Settings.Bots.DisabledCharacters.Add("Ann");
        h.TickWithoutWaiting();
        Assert.Null(h.Manager.GetAgent(1));

        h.Settings.Bots.DisabledJobs.Clear();
        h.Settings.Bots.DisabledCharacters.Clear();
        h.TickWithoutWaiting();
        var replacementAgent = h.Agent;
        Assert.NotSame(removedAgent, replacementAgent);

        h.ReleaseReply();
        await h.Manager.WhenIdle();
        await h.Tick();

        Assert.Empty(h.Game.Actions);
        Assert.Null(h.Game.CurrentTaskId(1));
        Assert.Null(removedAgent.Plan);
        Assert.Null(replacementAgent.Plan);
        Assert.Equal(0, removedAgent.Decisions);
        Assert.Equal(0, replacementAgent.Decisions);
    }

    sealed class Harness : IDisposable
    {
        readonly TaskCompletionSource<bool> release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<bool> Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly FakeClock Clock = new();
        public readonly FakeGame Game = new FakeGame().AddBot(1, "Ann", "mechanic");
        public readonly ModSettings Settings = Fixtures.Settings();
        public readonly AgentManager Manager;
        public BotAgent Agent => Manager.GetAgent(1)!;

        public Harness(string reply, bool delayed = false)
        {
            Settings.Bots.Autonomy = "order";
            Settings.Bots.ThinkAtRoundStart = false;
            Settings.Bots.ReactToMenuOrders = false;
            Settings.Global.MinSecondsBetweenCallsPerBot = 0;
            var handler = new FakeHandler(async (_, _) =>
            {
                Started.TrySetResult(true);
                if (delayed) await release.Task;
                return (HttpStatusCode.OK, reply);
            });
            Manager = new AgentManager(Settings, Game, new LlmRouter(Settings, handler, Clock.Utc), Clock.Get);
            Manager.Update();
            Clock.Advance(0.5);
        }

        public async Task Request(bool waitForReply = true)
        {
            Agent.ReceivePlayerOrder("Daniil: prepare the requested tools", from: "Daniil");
            Manager.Update();
            if (waitForReply) await Manager.WhenIdle();
            Clock.Advance(0.5);
        }

        public async Task Tick()
        {
            Manager.Update();
            await Manager.WhenIdle();
            Clock.Advance(0.5);
        }

        public void TickWithoutWaiting()
        {
            Manager.Update();
            Clock.Advance(0.5);
        }

        public void ReleaseReply() => release.TrySetResult(true);
        public void Dispose() { ReleaseReply(); Manager.Dispose(); }
    }
}
