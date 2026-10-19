using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using LlmAgents.Agents;
using LlmAgents.Llm;
using Xunit;

namespace LlmAgents.Tests;

/// <summary>Full LLM reply -> schema validation -> native task -> report -> plan management round trips.</summary>
public class PlanManagementIntegrationTests
{
    [Fact]
    public async Task InspectAppendAndFutureEditPreserveCurrentNativeTaskAndCompletedProgress()
    {
        using var h = new Harness();
        await h.StartPlan(Steps(Do("say", "text", "start"), Do("equip", "item", "weldingtool"), Do("say", "text", "old future")));
        var plan = h.Agent.Plan!;
        var taskId = h.TaskId;
        var currentId = plan.Tree.CurrentId;
        var futureId = plan.Tree.Root.Items[^1].Id;

        await h.Send(("inspect_plan", new JsonObject()));
        Assert.Same(plan, h.Agent.Plan);
        Assert.Equal(taskId, h.TaskId);
        Assert.Equal(currentId, plan.Tree.CurrentId);
        Assert.Contains(h.AllUserText, text => text.Contains("\"plan_id\"") && text.Contains("\"task_id\""));

        await h.Send(("append_plan", Version(plan, new JsonObject { ["steps"] = Steps(Do("say", "text", "appended")) })));
        Assert.Same(plan, h.Agent.Plan);
        Assert.Equal(taskId, h.TaskId);
        Assert.Equal(2, plan.Tree.Revision);
        Assert.Equal(currentId, plan.Tree.CurrentId);

        await h.Send(("edit_plan", Version(plan, new JsonObject
        {
            ["operations"] = new JsonArray(new JsonObject { ["op"] = "replace", ["node"] = futureId, ["steps"] = Steps(Do("say", "text", "edited future")) }),
        })));
        Assert.Equal(taskId, h.TaskId);
        Assert.Equal(3, plan.Tree.Revision);
        Assert.DoesNotContain("1:cancel task", h.Game.Actions);
        await h.Complete(taskId);
        Assert.Equal(new[] { "1:say start", "1:say edited future", "1:say appended" }, h.Game.Actions.Where(a => a.StartsWith("1:say ")).ToArray());
        Assert.False(h.Agent.Busy);
    }

    [Fact]
    public async Task CancelStopsOwnedNativeTaskWithoutClaimingFailureOrExecutingTheTail()
    {
        using var h = new Harness();
        await h.StartPlan(Steps(Do("equip", "item", "weldingtool"), Do("say", "text", "must not run")));
        var failures = h.Agent.Stats.TasksFailed;
        await h.Send(("cancel_plan", Version(h.Agent.Plan!, new JsonObject { ["reason"] = "new assignment" })));
        Assert.False(h.Agent.Busy);
        Assert.Null(h.Game.CurrentTaskId(1));
        Assert.Single(h.Game.Actions, action => action == "1:cancel task");
        Assert.DoesNotContain("1:say must not run", h.Game.Actions);
        Assert.Equal(failures, h.Agent.Stats.TasksFailed);
        Assert.DoesNotContain(h.AllUserText, text => text.Contains("plan stopped") || text.Contains("task 'equip' failed"));
        Assert.Contains(h.AllUserText, text => text.Contains("plan cancelled"));
    }

    [Fact]
    public async Task OldTaskCompletionCannotAdvanceReplacementPlanAwaitingANewTask()
    {
        using var h = new Harness();
        await h.StartPlan(Steps(Do("equip", "item", "weldingtool"), Do("say", "text", "old tail")));
        var oldTask = h.TaskId;
        var oldPlanId = h.Agent.Plan!.Id;
        await h.Send(("plan", new JsonObject { ["steps"] = Steps(Do("craft", "item", "bandage"), Do("say", "text", "new tail")) }));
        var newTask = h.TaskId;
        Assert.NotEqual(oldTask, newTask);
        Assert.NotEqual(oldPlanId, h.Agent.Plan!.Id);
        await h.Complete(oldTask);
        Assert.Equal(newTask, h.TaskId);
        Assert.Equal(PlanWait.Task, h.Agent.Plan!.Waiting);
        Assert.DoesNotContain("1:say old tail", h.Game.Actions);
        Assert.DoesNotContain("1:say new tail", h.Game.Actions);
        await h.Complete(newTask);
        Assert.Contains("1:say new tail", h.Game.Actions);
        Assert.False(h.Agent.Busy);
    }

    [Fact]
    public async Task ReplacingCurrentActionCancelsItBeforeStartingItsReplacement()
    {
        using var h = new Harness();
        await h.StartPlan(Steps(Do("equip", "item", "weldingtool"), Do("say", "text", "tail")));
        var oldTask = h.TaskId;
        await h.Send(("update_plan", Version(h.Agent.Plan!, new JsonObject
        {
            ["node"] = h.Agent.Plan!.Tree.CurrentId,
            ["steps"] = Steps(Do("craft", "item", "bandage")),
        })));
        var newTask = h.TaskId;
        Assert.NotEqual(oldTask, newTask);
        Assert.Equal(new[] { "1:equip weldingtool", "1:cancel task", "1:craft bandage x1" }, h.Game.Actions.ToArray());
        await h.Complete(oldTask);
        Assert.DoesNotContain("1:say tail", h.Game.Actions);
        await h.Complete(newTask);
        Assert.Contains("1:say tail", h.Game.Actions);
    }

    [Fact]
    public async Task InvalidSecondOperationRollsBackFirstOperationAndKeepsNativeWork()
    {
        using var h = new Harness();
        await h.StartPlan(Steps(Do("equip", "item", "weldingtool"), Do("say", "text", "future")));
        var plan = h.Agent.Plan!;
        var tree = plan.Tree;
        var before = tree.Snapshot().ToJsonString();
        var task = h.TaskId;
        await h.Send(("edit_plan", Version(plan, new JsonObject
        {
            ["operations"] = new JsonArray(
                new JsonObject { ["op"] = "replace", ["node"] = tree.CurrentId, ["steps"] = Steps(Do("craft", "item", "bandage")) },
                new JsonObject { ["op"] = "remove", ["node"] = "n99999" }),
        })));
        Assert.Same(plan, h.Agent.Plan);
        Assert.Same(tree, plan.Tree);
        Assert.Equal(before, plan.Tree.Snapshot().ToJsonString());
        Assert.Equal(task, h.TaskId);
        Assert.DoesNotContain("1:cancel task", h.Game.Actions);
        Assert.DoesNotContain(h.Game.Actions, action => action.StartsWith("1:craft "));
        await h.Complete(task);
        Assert.Contains("1:say future", h.Game.Actions);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StalePlanIdentityOrRevisionRejectsAllChanges(bool staleIdentity)
    {
        using var h = new Harness();
        await h.StartPlan(Steps(Do("equip", "item", "weldingtool"), Do("say", "text", "unchanged")));
        var plan = h.Agent.Plan!;
        var before = plan.Tree.Snapshot().ToJsonString();
        var task = h.TaskId;
        var args = Version(plan, new JsonObject { ["steps"] = Steps(Do("say", "text", "stale appended")) });
        if (staleIdentity) args["expected_plan_id"] = "a-plan-from-an-older-round";
        else args["expected_revision"] = plan.Tree.Revision + 1;
        await h.Send(("append_plan", args));
        Assert.Same(plan, h.Agent.Plan);
        Assert.Equal(before, plan.Tree.Snapshot().ToJsonString());
        Assert.Equal(task, h.TaskId);
        Assert.DoesNotContain("1:cancel task", h.Game.Actions);
        await h.Complete(task);
        Assert.Contains("1:say unchanged", h.Game.Actions);
        Assert.DoesNotContain("1:say stale appended", h.Game.Actions);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DirectToolCancellationAlsoRejectsAStalePlanIdentityOrRevision(bool staleIdentity)
    {
        using var h = new Harness();
        await h.StartPlan(Steps(Do("equip", "item", "weldingtool"), Do("say", "text", "retained")));
        var plan = h.Agent.Plan!;
        var before = plan.Tree.Snapshot().ToJsonString();
        var task = h.TaskId;
        var args = Version(plan, new JsonObject { ["reason"] = "stale command" });
        if (staleIdentity) args["expected_plan_id"] = "replaced-plan";
        else args["expected_revision"] = plan.Tree.Revision + 1;
        var result = Tools.Execute(new ToolCall("cancel_plan", args), h.Agent, h.Game, h.Manager);
        Assert.StartsWith("error:", result);
        Assert.Contains("stale", result);
        Assert.Same(plan, h.Agent.Plan);
        Assert.Equal(before, plan.Tree.Snapshot().ToJsonString());
        Assert.Equal(task, h.TaskId);
        Assert.DoesNotContain("1:cancel task", h.Game.Actions);
    }

    [Fact]
    public async Task PlanManagementCannotHideImmediateInventorySideActions()
    {
        using var h = new Harness();
        await h.StartPlan(Steps(Do("equip", "item", "weldingtool"), Do("say", "text", "unchanged")));
        var plan = h.Agent.Plan!;
        var before = plan.Tree.Snapshot().ToJsonString();
        var task = h.TaskId;
        await h.Send(
            ("append_plan", Version(plan, new JsonObject { ["steps"] = Steps(Do("say", "text", "hidden append")) })),
            ("equip", new JsonObject { ["item"] = "divingsuit" }));
        Assert.Equal(before, plan.Tree.Snapshot().ToJsonString());
        Assert.Equal(task, h.TaskId);
        Assert.DoesNotContain("1:equip divingsuit", h.Game.Actions);
        Assert.DoesNotContain("1:cancel task", h.Game.Actions);
        await h.Complete(task);
        Assert.DoesNotContain("1:say hidden append", h.Game.Actions);
    }

    [Fact]
    public async Task NativeCancellationFailureKeepsCurrentPlanAndRejectsReplacement()
    {
        using var h = new Harness();
        await h.StartPlan(Steps(Do("equip", "item", "weldingtool")));
        var plan = h.Agent.Plan!;
        var before = plan.Tree.Snapshot().ToJsonString();
        var task = h.TaskId;
        h.Game.CancelTaskError = "error: cannot cancel this actor now";
        await h.Send(("update_plan", Version(plan, new JsonObject
        {
            ["node"] = plan.Tree.CurrentId, ["steps"] = Steps(Do("craft", "item", "bandage")),
        })));
        Assert.Same(plan, h.Agent.Plan);
        Assert.Equal(before, plan.Tree.Snapshot().ToJsonString());
        Assert.Equal(task, h.TaskId);
        Assert.DoesNotContain(h.Game.Actions, action => action.StartsWith("1:craft "));
        h.Game.CancelTaskError = null;
    }

    [Fact]
    public async Task WhileUsesFreshTypedInventoryCountsAndStopsAtTheRequiredAmount()
    {
        using var h = new Harness(observation => observation.InventoryItems = new());
        await h.StartPlan(Steps(new JsonObject
        {
            ["repeat"] = Steps(Do("take_item", "item", "bandage")), ["while"] = "count('bandage') < 2", ["max"] = 3,
        }, new JsonObject
        {
            ["if"] = "count('bandage') >= 2", ["then"] = Steps(Do("say", "text", "stock ready")),
            ["else"] = Steps(Do("say", "text", "stock missing")),
        }));
        var firstTask = h.TaskId;
        h.Game.Observations[1].InventoryItems!.Add(new ObservedInventoryItem(100, "bandage", "Бинт", 100));
        await h.Complete(firstTask);
        var secondTask = h.TaskId;
        Assert.NotEqual(firstTask, secondTask);
        h.Game.Observations[1].InventoryItems!.Add(new ObservedInventoryItem(101, "bandage", "Бинт", 100));
        await h.Complete(secondTask);
        Assert.Equal(2, h.Game.Actions.Count(action => action.StartsWith("1:take bandage")));
        Assert.Contains("1:say stock ready", h.Game.Actions);
        Assert.DoesNotContain("1:say stock missing", h.Game.Actions);
        Assert.False(h.Agent.Busy);
    }

    [Fact]
    public async Task CompletionInvalidatesTheSameTickCachedInventoryBeforeChoosingTheNextBranch()
    {
        using var h = new Harness(observation => observation.InventoryItems = new());
        await h.StartPlan(Steps(Do("take_item", "item", "wrench"), new JsonObject
        {
            ["if"] = "count('wrench') > 0", ["then"] = Steps(Do("say", "text", "wrench acquired")),
            ["else"] = Steps(Do("say", "text", "stale empty inventory")),
        }));
        h.Agent.LastObservation = h.Game.Observe(1);
        h.Agent.LastObservationAt = h.Clock.Now;
        Assert.Empty(h.Agent.LastObservation.InventoryItems!);
        h.Game.Observations[1].InventoryItems!.Add(new ObservedInventoryItem(123, "wrench", "Гаечный ключ", 100));
        h.Game.PendingReports.Add(new GameReport(1, "task 'take wrench' ok: acquired", TaskId: h.TaskId, Success: true));
        var reportedAt = h.Clock.Now;
        await h.Tick(); // report and branch run now, before the regular situation check and without aging the cache
        Assert.True(h.Clock.Now - reportedAt < 1);
        Assert.Contains("1:say wrench acquired", h.Game.Actions);
        Assert.DoesNotContain("1:say stale empty inventory", h.Game.Actions);
        Assert.False(h.Agent.Busy);
    }

    [Fact]
    public async Task KeepingAndAppendingToAnAgedPlanDoNotRestartItsTimeConditions()
    {
        using var h = new Harness();
        await h.StartPlan(Steps(new JsonObject { ["wait_until"] = "time >= 20", ["timeout"] = 60 },
            Do("say", "text", "creation deadline reached")));
        var plan = h.Agent.Plan!;
        var startedAt = plan.StartedAt;
        h.Clock.Advance(5);
        await h.Send(("keep_plan", new JsonObject()));
        Assert.Same(plan, h.Agent.Plan);
        Assert.Equal(startedAt, plan.StartedAt);
        await h.Send(("append_plan", Version(plan, new JsonObject { ["steps"] = Steps(Do("say", "text", "appended tail")) })));
        Assert.Same(plan, h.Agent.Plan);
        Assert.Equal(startedAt, plan.StartedAt);
        Assert.DoesNotContain("1:say creation deadline reached", h.Game.Actions);
        await h.Run(9); // include the condition polling tick after the creation-age deadline
        Assert.True(h.Clock.Now - startedAt >= 20);
        Assert.Contains("1:say creation deadline reached", h.Game.Actions);
        Assert.Contains("1:say appended tail", h.Game.Actions);
        Assert.False(h.Agent.Busy);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PerStepTaskTimeoutCancelsNativeWorkAndRunsOnlyTheSelectedRecovery(bool caught)
    {
        using var h = new Harness();
        var action = Do("equip", "item", "weldingtool");
        action["timeout"] = 1;
        var steps = caught ? Steps(new JsonObject
        {
            ["try"] = Steps(action),
            ["catch"] = Steps(new JsonObject { ["if"] = "last_failed", ["then"] = Steps(Do("say", "text", "recovered")) }),
        }, Do("say", "text", "finished")) : Steps(action, Do("say", "text", "must not run"));
        h.Enqueue(("plan", new JsonObject { ["steps"] = steps }));
        await h.Tick();
        await h.Tick();
        var task = h.TaskId;
        await h.Run(4);
        Assert.Null(h.Game.CurrentTaskId(1));
        Assert.Single(h.Game.Actions, value => value == "1:cancel task");
        Assert.Equal(caught, h.Game.Actions.Contains("1:say recovered"));
        Assert.Equal(caught, h.Game.Actions.Contains("1:say finished"));
        Assert.DoesNotContain("1:say must not run", h.Game.Actions);
        Assert.False(h.Agent.Busy);
        // A late successful native report after the timeout cannot restart either branch.
        var actions = h.Game.Actions.ToArray();
        await h.Complete(task);
        Assert.Equal(actions, h.Game.Actions.ToArray());
    }

    [Fact]
    public async Task FailedTimeoutCancellationRetainsOwnedTaskUntilCancellationCanSucceedBeforeRecovery()
    {
        using var h = new Harness();
        var action = Do("equip", "item", "weldingtool");
        action["timeout"] = 1;
        h.Enqueue(("plan", new JsonObject
        {
            ["steps"] = Steps(new JsonObject
            {
                ["try"] = Steps(action),
                ["catch"] = Steps(Do("say", "text", "recovery after cancellation")),
            }, Do("say", "text", "recovery finished")),
        }));
        await h.Tick();
        await h.Tick();
        var plan = h.Agent.Plan!;
        var task = h.TaskId;
        h.Game.CancelTaskError = "error: cannot cancel this actor now";
        await h.Run(4);
        Assert.Same(plan, h.Agent.Plan);
        Assert.True(h.Agent.Busy);
        Assert.Equal(PlanWait.Task, plan.Waiting);
        Assert.Equal(task, plan.AwaitedTaskId);
        Assert.Equal(task, h.TaskId);
        Assert.DoesNotContain("1:cancel task", h.Game.Actions);
        Assert.DoesNotContain("1:say recovery after cancellation", h.Game.Actions);
        Assert.DoesNotContain("1:say recovery finished", h.Game.Actions);

        h.Game.CancelTaskError = null;
        await h.Run(6); // timeout cleanup retries after five seconds
        Assert.Null(h.Game.CurrentTaskId(1));
        Assert.Equal(new[] { "1:equip weldingtool", "1:cancel task", "1:say recovery after cancellation", "1:say recovery finished" }, h.Game.Actions.ToArray());
        Assert.False(h.Agent.Busy);
    }

    [Fact]
    public async Task ImmediateLoopsYieldAfterSixteenActionsAndStopAtTheLifetimeBudget()
    {
        using var h = new Harness();
        var loop = Steps(Do("say", "text", "tick"));
        for (int depth = 0; depth < 4; depth++) loop = Steps(new JsonObject { ["repeat"] = loop, ["max"] = 10 });
        h.Enqueue(("plan", new JsonObject { ["steps"] = loop }));
        await h.Tick();
        await h.Tick();
        Assert.Equal(ActivePlan.MaximumImmediateSteps, h.Game.Actions.Count(action => action == "1:say tick"));
        Assert.Equal(PlanWait.Yield, h.Agent.Plan!.Waiting);
        Assert.Equal(ActivePlan.MaximumImmediateSteps, h.Agent.Plan.ExecutedSteps);
        await h.Tick();
        Assert.Equal(2 * ActivePlan.MaximumImmediateSteps, h.Game.Actions.Count(action => action == "1:say tick"));
        await h.Run(80);
        Assert.Equal(ActivePlan.MaximumExecutedSteps, h.Game.Actions.Count(action => action == "1:say tick"));
        Assert.False(h.Agent.Busy);
        Assert.Contains(h.AllUserText, text => text.Contains("execution budget exhausted"));
    }

    static JsonArray Steps(params JsonObject[] steps) => new(steps.Select(step => (JsonNode?)step).ToArray());
    static JsonObject Do(string tool, string argument, string value) => new()
    {
        ["do"] = tool, ["args"] = new JsonObject { [argument] = value },
    };
    static JsonObject Version(ActivePlan plan, JsonObject args)
    {
        args["expected_plan_id"] = plan.Id;
        args["expected_revision"] = plan.Tree.Revision;
        return args;
    }

    sealed class Harness : IDisposable
    {
        public readonly FakeClock Clock = new();
        public readonly FakeGame Game = new();
        public readonly ConcurrentQueue<string> Users = new();
        readonly ConcurrentQueue<string> observedResults = new();
        readonly ConcurrentQueue<string> replies = new();
        public readonly AgentManager Manager;
        public BotAgent Agent => Manager.GetAgent(1)!;
        public long TaskId => Game.CurrentTaskId(1) ?? throw new InvalidOperationException("no native task is running");
        public IEnumerable<string> AllUserText => Users.Concat(observedResults).Concat(Agent.RecentResults(BotAgent.MaxResults));

        public Harness(Action<Observation>? setup = null)
        {
            Game.AddBot(1, "Sophie", "mechanic", setup);
            var settings = Fixtures.Settings(@", ""maxActionsPerDecision"": 6, ""maxPlanSeconds"": 600, ""planStepTimeoutSeconds"": 30, ""decisionIntervalSeconds"": 600");
            var handler = new FakeHandler(body =>
            {
                var user = body["messages"]!.AsArray()[^1]!["content"]!.GetValue<string>();
                Users.Enqueue(user);
                if (JsonNode.Parse(Fixtures.Obs(user))?["lastActionResults"] is JsonArray results)
                    foreach (var result in results) observedResults.Enqueue(result!.GetValue<string>());
                return (HttpStatusCode.OK, replies.TryDequeue(out var reply) ? reply : Fixtures.OpenAiToolResponse(("carry_on", "{}")));
            });
            Manager = new AgentManager(settings, Game, new LlmRouter(settings, handler, Clock.Utc), Clock.Get);
        }

        public void Enqueue(params (string Name, JsonObject Args)[] calls) =>
            replies.Enqueue(Fixtures.OpenAiToolResponse(calls.Select(call => (call.Name, call.Args.ToJsonString())).ToArray()));

        public async Task Tick()
        {
            Manager.Update();
            await Manager.WhenIdle();
            Clock.Advance(0.5);
        }

        public async Task Run(double seconds)
        {
            for (double elapsed = 0; elapsed < seconds; elapsed += 0.5) await Tick();
        }

        public async Task StartPlan(JsonArray steps)
        {
            Enqueue(("plan", new JsonObject { ["steps"] = steps }));
            await Run(2);
            Assert.True(Agent.Busy);
        }

        public async Task Send(params (string Name, JsonObject Args)[] calls)
        {
            Enqueue(calls);
            Agent.ReceivePlayerOrder("Daniil: update the current plan", from: "Daniil");
            await Run(3);
            Assert.Empty(replies);
        }

        public async Task Complete(long taskId)
        {
            Clock.Advance(1); // make the next condition observe current world facts rather than the previous cached snapshot
            Game.PendingReports.Add(new GameReport(1, "task 'fixture action' ok: completed", TaskId: taskId, Success: true));
            await Run(1.5);
        }

        public void Dispose() => Manager.Dispose();
    }
}
