using System;
using System.Linq;
using System.Net;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using LlmAgents.Agents;
using LlmAgents.Llm;
using Xunit;

namespace LlmAgents.Tests;

public class WatchdogIntegrationTests
{
    [Fact]
    public async Task PersistentCorrelatedBlockerRequestsOneReviewAndNeverCancelsOrRepeatsTheAction()
    {
        using var h = new Harness();
        await h.Start();
        var plan = h.Agent.Plan!;
        var task = h.TaskId;
        var calls = h.Requests;
        h.Game.TaskProgressData[1] = Blocked(task);
        await h.Run(8);
        Assert.Equal(1, h.Reviews);
        Assert.Equal(calls + 1, h.Requests);
        Assert.Same(plan, h.Agent.Plan);
        Assert.Equal(task, h.TaskId);
        Assert.Equal(PlanWait.Task, plan.Waiting);
        Assert.Single(h.Game.Actions, action => action == "1:craft bandage x1");
        Assert.DoesNotContain("1:cancel task", h.Game.Actions);
        Assert.Equal(0, h.Agent.Stats.TasksFailed);
        Assert.Contains(h.Users, user => user.Contains("taskProgress") && user.Contains("no_power"));
    }

    [Fact]
    public async Task OnlyMatchingRealProgressRearmsAReviewAfterTheSameBlockerReturns()
    {
        using var h = new Harness();
        await h.Start();
        var task = h.TaskId;
        h.Game.TaskProgressData[1] = Blocked(task);
        await h.Run(3);
        Assert.Equal(1, h.Reviews);

        h.Game.TaskProgressData.Remove(1);
        await h.Run(2);
        h.Game.TaskProgressData[1] = Blocked(task + 100) with { NoProgressSeconds = 0 };
        await h.Run(2);
        h.Game.TaskProgressData[1] = Blocked(task);
        await h.Run(2);
        Assert.Equal(1, h.Reviews); // unavailable or stale snapshots cannot claim that this task recovered

        h.Game.TaskProgressData[1] = new GameTaskProgress(task, "running", "fabrication resumed", 100, 0, CompletedUnits: 1, TotalUnits: 2);
        await h.Run(2);
        Assert.Null(h.Agent.Plan!.ProgressReviewKey);
        h.Game.TaskProgressData[1] = Blocked(task) with { Step = "power lost again" };
        await h.Run(3);
        Assert.Equal(2, h.Reviews);
        Assert.Equal(task, h.TaskId);
        Assert.Single(h.Game.Actions, action => action == "1:craft bandage x1");
        Assert.DoesNotContain("1:cancel task", h.Game.Actions);
    }

    [Fact]
    public async Task OldTaskDiagnosticsCannotReviewOrCancelTheNewTask()
    {
        using var h = new Harness();
        await h.Start();
        var task = h.TaskId;
        var calls = h.Requests;
        h.Game.TaskProgressData[1] = Blocked(task + 10);
        await h.Run(8);
        Assert.Equal(0, h.Reviews);
        Assert.Equal(calls, h.Requests);
        Assert.Equal(task, h.TaskId);
        Assert.DoesNotContain("1:cancel task", h.Game.Actions);
    }

    [Theory]
    [InlineData("running", null)]
    [InlineData("waiting", null)]
    [InlineData("cleanup", "cleanup_failed")]
    public async Task StationaryUsefulWorkAndOwnedCleanupDoNotProduceOrdinaryStuckReviews(string phase, string? blocker)
    {
        using var h = new Harness();
        await h.Start();
        var task = h.TaskId;
        var calls = h.Requests;
        h.Game.TaskProgressData[1] = new GameTaskProgress(task, phase, "working without moving", 100, 90, blocker,
            blocker != null ? "owned objective cleanup is pending" : null, 0, 1);
        await h.Run(8);
        Assert.Equal(0, h.Reviews);
        Assert.Equal(calls, h.Requests);
        Assert.Equal(task, h.TaskId);
        Assert.Single(h.Game.Actions, action => action == "1:craft bandage x1");
        Assert.DoesNotContain("1:cancel task", h.Game.Actions);
    }

    static GameTaskProgress Blocked(long task) => new(task, "blocked", "waiting for the fabricator", 100, 45,
        "no_power", "Fabricator #123 has no power", 0, 1);

    sealed class Harness : IDisposable
    {
        public readonly FakeClock Clock = new();
        public readonly FakeGame Game = new();
        public readonly System.Collections.Concurrent.ConcurrentQueue<string> Users = new();
        public readonly AgentManager Manager;
        public int Requests;
        public BotAgent Agent => Manager.GetAgent(1)!;
        public long TaskId => Game.CurrentTaskId(1)!.Value;
        public int Reviews => Agent.Activity().Count(activity => activity.Kind == "task-blocked");

        public Harness()
        {
            Game.AddBot(1, "Larissa", "engineer");
            var settings = Fixtures.Settings(@", ""maxPlanSeconds"": 600, ""decisionIntervalSeconds"": 600");
            var handler = new FakeHandler(body =>
            {
                Users.Enqueue(body["messages"]!.AsArray()[^1]!["content"]!.GetValue<string>());
                return (HttpStatusCode.OK, Requests++ == 0 ? Fixtures.OpenAiToolResponse(("plan",
                    "{\"steps\":[{\"do\":\"craft\",\"args\":{\"item\":\"bandage\"}}]}")) : Fixtures.OpenAiToolResponse(("carry_on", "{}")));
            });
            Manager = new AgentManager(settings, Game, new LlmRouter(settings, handler, Clock.Utc), Clock.Get);
        }

        public async Task Start()
        {
            await Run(2);
            Assert.Equal(PlanWait.Task, Agent.Plan!.Waiting);
        }

        public async Task Run(double seconds)
        {
            for (double elapsed = 0; elapsed < seconds; elapsed += 0.5)
            {
                Manager.Update();
                await Manager.WhenIdle();
                Clock.Advance(0.5);
            }
        }

        public void Dispose() => Manager.Dispose();
    }
}
