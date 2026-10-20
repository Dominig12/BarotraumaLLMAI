using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using LlmAgents.Agents;
using LlmAgents.Llm;
using Xunit;

namespace LlmAgents.Tests;

public class PlanReplyTransitionRegressionTests
{
    [Theory]
    [InlineData("speech_memory")]
    [InlineData("speech_carry_on")]
    [InlineData("lookup_carry_on")]
    public async Task AcknowledgementsDoNotCancelCraftOrLoseItsRemainingSteps(string reply)
    {
        using var h = new Harness();
        await h.StartCraft();
        var plan = h.Agent.Plan!;
        long task = h.TaskId;
        await h.Send(reply switch
        {
            "speech_memory" => new[] { Call("say", """{"text":"Leave the shells at the fabricator"}"""), Call("remember", """{"note":"Shells stay at the fabricator"}""") },
            "speech_carry_on" => new[] { Call("say", """{"text":"Still crafting"}"""), Call("carry_on") },
            _ => new[] { Call("inspect", """{"kind":"recipe","query":"railgunshell"}"""), Call("carry_on") },
        });
        Assert.Same(plan, h.Agent.Plan);
        Assert.Equal(task, h.TaskId);
        Assert.Equal(1, plan.Tree.Revision);
        Assert.DoesNotContain("1:cancel task", h.Game.Actions);
        Assert.Single(h.Game.Actions, action => action.StartsWith("1:craft "));
        await h.Complete(task);
        Assert.Contains("1:say original tail", h.Game.Actions);
    }

    [Fact]
    public async Task InvalidPassiveSiblingCannotSpeakOrCancelTheCurrentTask()
    {
        using var h = new Harness();
        await h.StartCraft();
        var plan = h.Agent.Plan;
        long task = h.TaskId;
        await h.Send(Call("say", """{"text":"must not run"}"""), Call("remember", """{"note":"invalid","unknown":true}"""));
        Assert.Same(plan, h.Agent.Plan);
        Assert.Equal(task, h.TaskId);
        Assert.DoesNotContain("1:say must not run", h.Game.Actions);
        Assert.DoesNotContain("1:cancel task", h.Game.Actions);
    }

    [Fact]
    public async Task ExplicitNewActionsStillReplaceWorkEvenWhenCarryOnIsAlsoPresent()
    {
        using var h = new Harness();
        await h.StartCraft();
        var old = h.Agent.Plan;
        await h.Send(Call("treat", """{"target":"Sophie"}"""), Call("carry_on"));
        Assert.NotSame(old, h.Agent.Plan);
        Assert.Equal(new[] { "1:cancel task", "1:treat Sophie" }, h.Game.Actions.Skip(1));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelAndReplaceStartsNewWorkAndIgnoresLateOldCompletion(bool structured)
    {
        using var h = new Harness();
        await h.StartCraft();
        var oldPlan = h.Agent.Plan!;
        long oldTask = h.TaskId;
        h.Agent.SetGoal("old goal", h.Clock.Now);
        var cancellation = h.Cancel(clearGoal: true);
        if (structured)
            await h.Send(cancellation, Call("set_goal", """{"goal":"stabilize Sophie"}"""), Call("plan", """
                {"steps":[{"if":"health >= 50","then":[{"do":"treat","args":{"target":"Sophie"}}],"else":[{"do":"say","args":{"text":"wrong branch"}}]},{"do":"say","args":{"text":"replacement tail"}}]}
                """));
        else
            await h.Send(cancellation, Call("set_goal", """{"goal":"stabilize Sophie"}"""), Call("treat", """{"target":"Sophie"}"""), Call("say", """{"text":"replacement tail"}"""));
        long replacementTask = h.TaskId;
        Assert.NotEqual(oldTask, replacementTask);
        Assert.NotEqual(oldPlan.Id, h.Agent.Plan!.Id);
        Assert.Equal("stabilize Sophie", h.Agent.Goal);
        Assert.Equal(new[] { "1:cancel task", "1:treat Sophie" }, h.Game.Actions.Skip(1));
        Assert.Equal(0, h.Agent.Stats.TasksFailed);
        await h.Complete(oldTask);
        Assert.Equal(replacementTask, h.TaskId);
        Assert.DoesNotContain("1:say original tail", h.Game.Actions);
        Assert.DoesNotContain("1:say replacement tail", h.Game.Actions);
        await h.Complete(replacementTask);
        Assert.Contains("1:say replacement tail", h.Game.Actions);
        Assert.DoesNotContain("1:say wrong branch", h.Game.Actions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidReplacementIsRejectedBeforeCancellationEvenInAnUnchosenBranch(bool structured)
    {
        using var h = new Harness();
        await h.StartCraft();
        var oldPlan = h.Agent.Plan!;
        long oldTask = h.TaskId;
        h.Agent.SetGoal("old goal", h.Clock.Now);
        await h.Send(h.Cancel(clearGoal: true), Call("say", """{"text":"must not run"}"""), structured
            ? Call("plan", """{"steps":[{"if":"health >= 50","then":[{"do":"treat","args":{"target":"Sophie"}}],"else":[{"do":"craft","args":{"item":"bandage","amount":-1}}]}]}""")
            : Call("craft", """{"item":"bandage","amount":-1}"""));
        Assert.Same(oldPlan, h.Agent.Plan);
        Assert.Equal(oldTask, h.TaskId);
        Assert.Equal("old goal", h.Agent.Goal);
        Assert.Single(h.Game.Actions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StaleCancellationGuardCannotExecuteReplacementSiblings(bool staleIdentity)
    {
        using var h = new Harness();
        await h.StartCraft();
        var oldPlan = h.Agent.Plan!;
        long oldTask = h.TaskId;
        var cancellation = h.Cancel();
        cancellation.Args[staleIdentity ? "expected_plan_id" : "expected_revision"] = staleIdentity ? JsonValue.Create("old-id") : JsonValue.Create(99);
        await h.Send(cancellation, Call("say", """{"text":"must not run"}"""), Call("treat", """{"target":"Sophie"}"""));
        Assert.Same(oldPlan, h.Agent.Plan);
        Assert.Equal(oldTask, h.TaskId);
        Assert.Single(h.Game.Actions);
    }

    [Fact]
    public async Task FailedNativeCancellationLeavesGoalAndReplacementUntouched()
    {
        using var h = new Harness();
        await h.StartCraft();
        var oldPlan = h.Agent.Plan!;
        long oldTask = h.TaskId;
        h.Agent.SetGoal("old goal", h.Clock.Now);
        h.Game.CancelTaskError = "error: cleanup is pending";
        await h.Send(h.Cancel(clearGoal: true), Call("set_goal", """{"goal":"new goal"}"""), Call("treat", """{"target":"Sophie"}"""));
        Assert.Same(oldPlan, h.Agent.Plan);
        Assert.Equal(oldTask, h.TaskId);
        Assert.Equal("old goal", h.Agent.Goal);
        Assert.Single(h.Game.Actions);
    }

    [Theory]
    [InlineData("keep_plan")]
    [InlineData("inspect_plan")]
    public async Task RetainingControlCannotHideUnscheduledPhysicalActions(string control)
    {
        using var h = new Harness();
        await h.StartCraft();
        var oldPlan = h.Agent.Plan!;
        long oldTask = h.TaskId;
        await h.Send(Call(control), Call("treat", """{"target":"Sophie"}"""));
        Assert.Same(oldPlan, h.Agent.Plan);
        Assert.Equal(oldTask, h.TaskId);
        Assert.Single(h.Game.Actions);
    }

    [Fact]
    public async Task CancellationWithoutReplacementDoesNotRestartTheCancelledGoal()
    {
        using var h = new Harness();
        await h.StartCraft();
        h.Agent.SetGoal("old goal", h.Clock.Now);
        await h.Send(h.Cancel());
        await h.Run(10);
        Assert.Null(h.Agent.Plan);
        Assert.Null(h.Game.CurrentTaskId(1));
        Assert.Equal("old goal", h.Agent.Goal);
        Assert.Equal(new[] { "1:craft railgunshell x2 salvage", "1:cancel task" }, h.Game.Actions);
        Assert.Equal(0, h.Agent.Stats.TasksFailed);
    }

    static (string Name, JsonObject Args) Call(string name, string args = "{}") => (name, JsonNode.Parse(args)!.AsObject());

    sealed class Harness : IDisposable
    {
        public readonly FakeClock Clock = new();
        public readonly FakeGame Game = new FakeGame().AddBot(1, "Sophie", "mechanic");
        readonly ConcurrentQueue<string> replies = new();
        public readonly AgentManager Manager;
        public BotAgent Agent => Manager.GetAgent(1)!;
        public long TaskId => Game.CurrentTaskId(1) ?? throw new InvalidOperationException("no current task");

        public Harness()
        {
            var settings = Fixtures.Settings(@", ""maxActionsPerDecision"": 6, ""maxPlanSeconds"": 600, ""decisionIntervalSeconds"": 600");
            var handler = new FakeHandler(_ => (HttpStatusCode.OK, replies.TryDequeue(out var reply) ? reply : Fixtures.OpenAiToolResponse(("carry_on", "{}"))));
            Manager = new AgentManager(settings, Game, new LlmRouter(settings, handler, Clock.Utc), Clock.Get);
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

        public async Task StartCraft()
        {
            replies.Enqueue(Fixtures.OpenAiToolResponse(("plan", """{"steps":[{"do":"craft","args":{"item":"railgunshell","amount":2,"salvage":true}},{"do":"say","args":{"text":"original tail"}}]}""")));
            await Run(2);
            Assert.Equal(PlanWait.Task, Agent.Plan!.Waiting);
        }

        public (string Name, JsonObject Args) Cancel(bool clearGoal = false) => Call("cancel_plan", new JsonObject
        {
            ["reason"] = "different assignment", ["clear_goal"] = clearGoal,
            ["expected_plan_id"] = Agent.Plan!.Id, ["expected_revision"] = Agent.Plan.Tree.Revision,
        }.ToJsonString());

        public async Task Send(params (string Name, JsonObject Args)[] calls)
        {
            replies.Enqueue(Fixtures.OpenAiToolResponse(calls.Select(call => (call.Name, call.Args.ToJsonString())).ToArray()));
            Agent.ReceivePlayerOrder("Daniil: follow up", from: "Daniil");
            await Run(3);
            Assert.Empty(replies);
        }

        public async Task Complete(long task)
        {
            Game.PendingReports.Add(new GameReport(1, "task 'fixture' ok: completed", TaskId: task, Success: true));
            await Run(1.5);
        }

        public void Dispose() => Manager.Dispose();
    }
}
