using System;
using System.Linq;
using System.Net;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using LlmAgents.Agents;
using LlmAgents.Llm;
using LlmAgents.SettingsUi;
using Xunit;

namespace LlmAgents.Tests;

public class MonitorPlanControlTests
{
    [Fact]
    public void UnauthorizedRequestNeverChangesThePlanOrPendingDecision()
    {
        using var h = new Harness();
        var plan = h.Start();
        h.Agent.Pending = true;
        int seq = h.Agent.RequestSeq;
        var response = h.Manager.HandleMonitorCommand(h.Request(plan), new object(), "Daniil", authorized: false);
        Assert.False(response.Success);
        Assert.Equal("forbidden", response.Code);
        Assert.Same(plan, h.Agent.Plan);
        Assert.True(h.Agent.Pending);
        Assert.Equal(seq, h.Agent.RequestSeq);
        Assert.DoesNotContain("1:cancel task", h.Game.Actions);
    }

    [Fact]
    public async Task LateModelAnswerCannotRecreateWorkCancelledThroughTheMonitor()
    {
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var h = new Harness(gate.Task, () => started.TrySetResult(true),
            Fixtures.OpenAiToolResponse(("craft", "{\"item\":\"bandage\"}")));
        try
        {
            var plan = h.Start();
            h.Agent.ReceivePlayerOrder("Daniil: reconsider the current work", from: "Daniil");
            h.Clock.Advance(0.5);
            h.Manager.Update();
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(h.Agent.Pending);
            Assert.True(h.Manager.HandleMonitorCommand(h.Request(plan), new object(), "Daniil", true).Success);

            gate.TrySetResult(true);
            await h.Manager.WhenIdle();
            h.Clock.Advance(0.5);
            h.Manager.Update();

            Assert.Null(h.Agent.Plan);
            Assert.Null(h.Game.CurrentTaskId(1));
            Assert.DoesNotContain(h.Game.Actions, action => action.StartsWith("1:craft "));
        }
        finally { gate.TrySetResult(true); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AuthorizedCancellationReleasesOnlyOwnedWorkAndInvalidatesItsDecision(bool clearGoal)
    {
        using var h = new Harness();
        var plan = h.Start();
        h.Game.RefuelReactor(2);
        long? otherTask = h.Game.CurrentTaskId(2);
        h.Game.Observations[1].CurrentOrder = "repairsystems ordered by player Daniil";
        h.Agent.SetGoal("prepare tools", h.Clock.Now, "player Daniil", "Daniil");
        h.Agent.Pending = true;
        h.Agent.RequestSeq = 7;
        h.Agent.AddEvent(EventKind.PlanReview, "review the previous plan", h.Clock.Now);
        h.Agent.AddEvent(EventKind.Retry, "retry the previous decision", h.Clock.Now);
        int failed = h.Agent.Stats.TasksFailed;
        var request = h.Request(plan);
        request.ClearGoal = clearGoal;

        var response = h.Manager.HandleMonitorCommand(request, new object(), "Daniil", authorized: true);

        Assert.True(response.Success);
        Assert.Equal("cancelled", response.Code);
        Assert.Null(h.Agent.Plan);
        Assert.Null(h.Game.CurrentTaskId(1));
        Assert.Equal(otherTask, h.Game.CurrentTaskId(2));
        Assert.Equal("repairsystems ordered by player Daniil", h.Game.Observations[1].CurrentOrder);
        Assert.DoesNotContain(h.Game.Actions, action => action.Contains("dismiss"));
        Assert.False(h.Agent.Pending);
        Assert.Equal(8, h.Agent.RequestSeq);
        Assert.Equal(failed, h.Agent.Stats.TasksFailed);
        Assert.Equal(clearGoal, h.Agent.CurrentGoal == null);
        Assert.DoesNotContain(h.Agent.Events, e => e.Kind is EventKind.PlanReview or EventKind.Retry or EventKind.TaskFailed);
        Assert.Contains(h.Agent.PastOrders, order => order.Text.Contains("Daniil") && order.Text.Contains("монитор"));
    }

    [Theory]
    [InlineData("missing_request_id")]
    [InlineData("unsafe_request_id")]
    [InlineData("missing_plan_id")]
    [InlineData("missing_revision")]
    [InlineData("zero_revision")]
    [InlineData("zero_bot")]
    [InlineData("unsupported_op")]
    public void IncompleteOrMalformedCancellationCannotFallBackToTheCurrentPlan(string invalid)
    {
        using var h = new Harness();
        var plan = h.Start();
        var request = h.Request(plan);
        switch (invalid)
        {
            case "missing_request_id": request.RequestId = null; break;
            case "unsafe_request_id": request.RequestId = "cancel\nnext"; break;
            case "missing_plan_id": request.ExpectedPlanId = null; break;
            case "missing_revision": request.ExpectedRevision = null; break;
            case "zero_revision": request.ExpectedRevision = 0; break;
            case "zero_bot": request.Bot = 0; break;
            default: request.Op = "cancel_everything"; break;
        }
        var response = h.Manager.HandleMonitorCommand(request, new object(), "Daniil", true);
        Assert.Equal("invalid_request", response.Code);
        Assert.False(response.Success);
        Assert.Same(plan, h.Agent.Plan);
        Assert.DoesNotContain("1:cancel task", h.Game.Actions);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void StalePlanOrRevisionCannotCancelTheCurrentTask(bool stalePlan)
    {
        using var h = new Harness();
        var plan = h.Start();
        var task = h.Game.CurrentTaskId(1);
        var request = h.Request(plan);
        if (stalePlan) request.ExpectedPlanId = Guid.NewGuid().ToString("N");
        else request.ExpectedRevision++;
        var response = h.Manager.HandleMonitorCommand(request, new object(), "Daniil", true);
        Assert.Equal(stalePlan ? "stale_plan" : "stale_revision", response.Code);
        Assert.Same(plan, h.Agent.Plan);
        Assert.Equal(task, h.Game.CurrentTaskId(1));
        Assert.DoesNotContain("1:cancel task", h.Game.Actions);
    }

    [Fact]
    public void NoActivePlanDoesNotCancelAnUnownedBackendTask()
    {
        using var h = new Harness();
        var plan = h.Start();
        var task = h.Game.CurrentTaskId(1);
        h.Agent.ClearPlan();
        var response = h.Manager.HandleMonitorCommand(h.Request(plan), new object(), "Daniil", true);
        Assert.Equal("no_active_plan", response.Code);
        Assert.Equal(task, h.Game.CurrentTaskId(1));
        Assert.DoesNotContain("1:cancel task", h.Game.Actions);
    }

    [Fact]
    public void UnknownBotCannotFallBackToAnotherBotsActivePlan()
    {
        using var h = new Harness();
        var plan = h.Start();
        var request = h.Request(plan);
        request.Bot = 12345;
        Assert.Equal("bot_not_found", h.Manager.HandleMonitorCommand(request, new object(), "Daniil", true).Code);
        Assert.Same(plan, h.Agent.Plan);
        Assert.DoesNotContain("1:cancel task", h.Game.Actions);
    }

    [Fact]
    public void ReplayReturnsAnImmutableReceiptWithoutTouchingAReplacementPlanOrItsDecision()
    {
        using var h = new Harness();
        var identity = new object();
        var oldPlan = h.Start();
        var request = h.Request(oldPlan);
        var first = h.Manager.HandleMonitorCommand(request, identity, "Daniil", true);
        Assert.True(first.Success);
        first.Success = false;
        first.Code = "client_mutated_receipt";
        var replacement = h.Start();
        h.Agent.Pending = true;
        h.Agent.RequestSeq = 20;

        var replay = h.Manager.HandleMonitorCommand(request, identity, "Daniil", true);

        Assert.True(replay.Success);
        Assert.Equal("cancelled", replay.Code);
        Assert.Same(replacement, h.Agent.Plan);
        Assert.True(h.Agent.Pending);
        Assert.Equal(20, h.Agent.RequestSeq);
        Assert.Single(h.Game.Actions, action => action == "1:cancel task");
    }

    [Fact]
    public void ReusingTheSameRequestIdWithDifferentCancellationParametersIsRejected()
    {
        using var h = new Harness();
        var identity = new object();
        var plan = h.Start();
        var request = h.Request(plan);
        Assert.True(h.Manager.HandleMonitorCommand(request, identity, "Daniil", true).Success);
        var replacement = h.Start();
        request.ExpectedPlanId = replacement.Id;
        request.ExpectedRevision = replacement.Tree.Revision;
        Assert.Equal("request_conflict", h.Manager.HandleMonitorCommand(request, identity, "Daniil", true).Code);
        Assert.Same(replacement, h.Agent.Plan);
        Assert.Single(h.Game.Actions, action => action == "1:cancel task");
    }

    [Fact]
    public void RevokedPermissionIsCheckedBeforeReturningACachedSuccess()
    {
        using var h = new Harness();
        var identity = new object();
        var request = h.Request(h.Start());
        Assert.True(h.Manager.HandleMonitorCommand(request, identity, "Daniil", true).Success);
        var replacement = h.Start();
        var denied = h.Manager.HandleMonitorCommand(request, identity, "Daniil", false);
        Assert.False(denied.Success);
        Assert.Equal("forbidden", denied.Code);
        Assert.Same(replacement, h.Agent.Plan);
    }

    [Fact]
    public void ConnectionIdentityUsesReferenceEqualityRatherThanAnActorNameOrValueEquality()
    {
        using var h = new Harness();
        var first = h.Request(h.Start(), "same-request");
        Assert.True(h.Manager.HandleMonitorCommand(first, new EqualIdentity(), "same name", true).Success);
        var second = h.Request(h.Start(), "same-request");
        Assert.True(h.Manager.HandleMonitorCommand(second, new EqualIdentity(), "same name", true).Success);
        Assert.Equal(2, h.Game.Actions.Count(action => action == "1:cancel task"));
    }

    [Fact]
    public void CancellationFailureRetainsThePlanAndDecisionAndTransportRetriesDoNotRepeatIt()
    {
        using var h = new Harness();
        var identity = new object();
        var plan = h.Start();
        var request = h.Request(plan);
        h.Agent.Pending = true;
        int seq = h.Agent.RequestSeq;
        h.Game.CancelTaskError = "error: native cleanup is not ready";
        Assert.Equal("cancel_failed", h.Manager.HandleMonitorCommand(request, identity, "Daniil", true).Code);
        h.Game.CancelTaskError = null;
        Assert.Equal("cancel_failed", h.Manager.HandleMonitorCommand(request, identity, "Daniil", true).Code);
        Assert.Same(plan, h.Agent.Plan);
        Assert.True(h.Agent.Pending);
        Assert.Equal(seq, h.Agent.RequestSeq);
        Assert.DoesNotContain("1:cancel task", h.Game.Actions);
        Assert.True(h.Manager.HandleMonitorCommand(h.Request(plan), identity, "Daniil", true).Success);
        Assert.Null(h.Agent.Plan);
    }

    [Fact]
    public void CancellingAnOldPlanDoesNotCancelANewerUnownedTask()
    {
        using var h = new Harness();
        var plan = h.Start();
        h.Game.Equip(1, "different tool");
        var newerTask = h.Game.CurrentTaskId(1);
        Assert.True(h.Manager.HandleMonitorCommand(h.Request(plan), new object(), "Daniil", true).Success);
        Assert.Null(h.Agent.Plan);
        Assert.Equal(newerTask, h.Game.CurrentTaskId(1));
        Assert.DoesNotContain("1:cancel task", h.Game.Actions);
    }

    [Fact]
    public void ExpiredReceiptCannotApplyAnOldCancellationToANewPlan()
    {
        using var h = new Harness();
        var identity = new object();
        var request = h.Request(h.Start());
        Assert.True(h.Manager.HandleMonitorCommand(request, identity, "Daniil", true).Success);
        var replacement = h.Start();
        h.Clock.Advance(301);
        Assert.Equal("stale_plan", h.Manager.HandleMonitorCommand(request, identity, "Daniil", true).Code);
        Assert.Same(replacement, h.Agent.Plan);
        Assert.Single(h.Game.Actions, action => action == "1:cancel task");
    }

    [Theory]
    [InlineData(true, 33)]
    [InlineData(false, 129)]
    public void CacheEvictionNeverLetsAnOldRequestCancelAReplacement(bool sameRequester, int commands)
    {
        using var h = new Harness();
        var identity = new object();
        MonitorRequest? first = null;
        for (int i = 0; i < commands; i++)
        {
            var request = h.Request(h.Start(), "request-" + i);
            first ??= request;
            Assert.True(h.Manager.HandleMonitorCommand(request, sameRequester || i == 0 ? identity : new object(), "Daniil", true).Success);
        }
        var replacement = h.Start();
        Assert.Equal("stale_plan", h.Manager.HandleMonitorCommand(first!, identity, "Daniil", true).Code);
        Assert.Same(replacement, h.Agent.Plan);
        Assert.Equal(commands, h.Game.Actions.Count(action => action == "1:cancel task"));
    }

    sealed class EqualIdentity
    {
        public override bool Equals(object? obj) => obj is EqualIdentity;
        public override int GetHashCode() => 1;
    }

    sealed class Harness : IDisposable
    {
        public readonly FakeClock Clock = new();
        public readonly FakeGame Game = new FakeGame().AddBot(1, "Ann", "mechanic").AddBot(2, "Bob", "engineer");
        public readonly AgentManager Manager;
        public BotAgent Agent => Manager.GetAgent(1)!;
        public Harness(Task? delay = null, Action? onRequest = null, string? reply = null)
        {
            var settings = Fixtures.Settings();
            settings.Bots.Autonomy = "order";
            settings.Bots.ThinkAtRoundStart = false;
            var handler = new FakeHandler(async (_, _) =>
            {
                onRequest?.Invoke();
                if (delay != null) await delay;
                return (HttpStatusCode.OK, reply ?? Fixtures.OpenAiToolResponse(("carry_on", "{}")));
            });
            Manager = new AgentManager(settings, Game, new LlmRouter(settings, handler, Clock.Utc), Clock.Get);
            Manager.Update();
        }
        public ActivePlan Start()
        {
            var tree = PlanTree.FromCalls(new[] { new ToolCall("equip", new JsonObject { ["item"] = "wrench" }) });
            tree.Next(new PlanConditions.ObservationContext(Game.Observe(1), 0, null, null, false));
            var plan = new ActivePlan(tree, false, Clock.Now);
            Game.Equip(1, "wrench");
            plan.WaitFor(PlanWait.Task, Clock.Now, deadline: Clock.Now + 180);
            plan.AwaitedTaskId = Game.CurrentTaskId(1);
            Agent.SetPlan(plan);
            return plan;
        }
        public MonitorRequest Request(ActivePlan plan, string? id = null) => new()
        {
            Op = "cancel_plan", Bot = 1, RequestId = id ?? Guid.NewGuid().ToString("N"),
            ExpectedPlanId = plan.Id, ExpectedRevision = plan.Tree.Revision,
        };
        public void Dispose() => Manager.Dispose();
    }
}
