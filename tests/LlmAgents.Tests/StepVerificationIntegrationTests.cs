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

/// <summary>LLM contracts through real manager execution, correlated native reports and fresh observed outcomes.</summary>
public sealed class StepVerificationIntegrationTests
{
    [Theory]
    [InlineData(0, 100, true)]
    [InlineData(100, 0, false)]
    public async Task ImmediateActionVerifiesFreshFactsInsteadOfItsPreActionObservation(int cachedHealth, int actualHealth, bool passes)
    {
        using var h = new Harness();
        await h.Request(("plan", Plan(Do("keep_gear", new JsonObject { ["on"] = true }, "health > 50"), Say("verified tail"))));
        h.Agent.LastObservation = h.Game.Observe(1);
        h.Agent.LastObservation.HealthPercent = cachedHealth;
        h.Agent.LastObservationAt = h.Clock.Now;
        h.Game.Observations[1].HealthPercent = actualHealth;

        await h.Tick();

        Assert.Single(h.Game.Actions, action => action == "1:keepgear True");
        if (passes)
        {
            Assert.Contains("1:say verified tail", h.Game.Actions);
            Assert.Null(h.Agent.Plan);
            Assert.Contains(h.Results, result => result.Contains("verified actual result: health > 50"));
        }
        else
        {
            Assert.Equal(PlanWait.Verification, h.Agent.Plan!.Waiting);
            Assert.DoesNotContain("1:say verified tail", h.Game.Actions);
        }
    }

    [Fact]
    public async Task TypedSuccessWaitsForEquipmentAndPollsNeverRepeatTheAction()
    {
        using var h = new Harness();
        await h.StartPlan(Equip("wearing('#123')"), Say("equipped"));
        var task = h.TaskId;
        await h.Complete(task);
        var plan = h.Agent.Plan!;
        Assert.Equal(PlanWait.Verification, plan.Waiting);
        Assert.Equal("verifying", CurrentStatus(plan));
        Assert.Null(plan.Tree.LastOk);
        Assert.DoesNotContain("1:say equipped", h.Game.Actions);

        await h.Run(4);
        Assert.Same(plan, h.Agent.Plan);
        Assert.Single(h.Game.Actions, action => action == "1:equip #123");
        Assert.DoesNotContain("1:cancel task", h.Game.Actions);

        h.Game.Observations[1].InventoryItems!.Add(new ObservedInventoryItem(123, "wrench", "Wrench", 100, Equipped: true));
        await h.Run(1.5);
        Assert.Contains("1:say equipped", h.Game.Actions);
        Assert.Null(h.Agent.Plan);
        Assert.Single(h.Game.Actions, action => action == "1:equip #123");
    }

    [Fact]
    public async Task VerificationTimeoutEntersNearestCatchWithoutRepeatingNativeEffects()
    {
        using var h = new Harness();
        var inner = new JsonObject
        {
            ["try"] = Steps(Equip("false", 2)), ["catch"] = Steps(Say("inner recovery")),
        };
        await h.StartPlan(new JsonObject
        {
            ["try"] = Steps(inner), ["catch"] = Steps(Say("outer recovery must not run")),
        }, Say("after recovery"));
        await h.Complete(h.TaskId);
        await h.Run(3);
        Assert.Contains("1:say inner recovery", h.Game.Actions);
        Assert.Contains("1:say after recovery", h.Game.Actions);
        Assert.DoesNotContain("1:say outer recovery must not run", h.Game.Actions);
        Assert.Single(h.Game.Actions, action => action == "1:equip #123");
        Assert.Contains(h.Results, result => result.Contains("postcondition failed after 2s") && result.Contains("action was not repeated"));
        Assert.Null(h.Agent.Plan);
    }

    [Fact]
    public async Task MissingTypedInventoryFactsCannotVerifyZeroItemCount()
    {
        using var h = new Harness(observation => observation.InventoryItems = null);
        await h.StartPlan(Do("keep_gear", new JsonObject { ["on"] = true }, "count('bandage') == 0", 10), Say("must not assume empty"));
        await h.Run(3);
        Assert.Equal(PlanWait.Verification, h.Agent.Plan!.Waiting);
        Assert.DoesNotContain("1:say must not assume empty", h.Game.Actions);
        Assert.Single(h.Game.Actions, action => action == "1:keepgear True");

        h.Game.Observations[1].InventoryItems = new();
        await h.Run(1.5);
        Assert.Contains("1:say must not assume empty", h.Game.Actions);
        Assert.Null(h.Agent.Plan);
    }

    [Fact]
    public async Task NativeFailureWinsEvenWhenPostconditionIsAlreadyTrue()
    {
        using var h = new Harness();
        await h.StartPlan(new JsonObject
        {
            ["try"] = Steps(Equip("true"), Say("incorrect success")),
            ["catch"] = Steps(Say("native failure recovered")),
        });
        await h.Complete(h.TaskId, success: false, text: "task 'equip' ok: misleading text with typed failure");
        Assert.Contains("1:say native failure recovered", h.Game.Actions);
        Assert.DoesNotContain("1:say incorrect success", h.Game.Actions);
        Assert.DoesNotContain(h.Results, result => result.Contains("verified actual result"));
        Assert.Single(h.Game.Actions, action => action == "1:equip #123");
    }

    [Fact]
    public async Task VerificationTimeoutStartsAfterNativeCompletionRatherThanActionDispatch()
    {
        using var h = new Harness();
        await h.StartPlan(new JsonObject
        {
            ["try"] = Steps(Equip("false", 3)), ["catch"] = Steps(Say("verified timeout recovery")),
        });
        var task = h.TaskId;
        await h.Run(10);
        Assert.Equal(PlanWait.Task, h.Agent.Plan!.Waiting);
        var completedAt = h.Clock.Now;
        await h.Complete(task);
        var plan = h.Agent.Plan!;
        Assert.Equal(PlanWait.Verification, plan.Waiting);
        Assert.Equal(completedAt, plan.WaitingSince);
        Assert.Equal(completedAt + 3, plan.WaitDeadline);
        await h.Run(1);
        Assert.Same(plan, h.Agent.Plan);
        Assert.DoesNotContain("1:say verified timeout recovery", h.Game.Actions);
        await h.Run(3);
        Assert.Contains("1:say verified timeout recovery", h.Game.Actions);
    }

    [Fact]
    public async Task AppendingAndEditingFutureStepsPreserveVerificationDeadlineAndNativeResult()
    {
        using var h = new Harness();
        await h.StartPlan(Equip("health > 50", 30), Say("old future"));
        await h.Complete(h.TaskId);
        var plan = h.Agent.Plan!;
        var currentId = plan.Tree.CurrentId;
        var futureId = plan.Tree.Root.Items[1].Id;
        var deadline = plan.WaitDeadline;
        var result = plan.VerificationResult;

        await h.Send(("append_plan", Version(plan, new JsonObject { ["steps"] = Steps(Say("appended")) })));
        await h.Send(("edit_plan", Version(plan, new JsonObject
        {
            ["operations"] = new JsonArray(new JsonObject { ["op"] = "replace", ["node"] = futureId, ["steps"] = Steps(Say("edited future")) }),
        })));

        Assert.Same(plan, h.Agent.Plan);
        Assert.Equal(PlanWait.Verification, plan.Waiting);
        Assert.Equal(deadline, plan.WaitDeadline);
        Assert.Equal(result, plan.VerificationResult);
        Assert.Equal(currentId, plan.Tree.CurrentId);
        Assert.Equal("verifying", CurrentStatus(plan));
        Assert.Equal(3, plan.Tree.Revision);
        Assert.DoesNotContain("1:cancel task", h.Game.Actions);
        Assert.Single(h.Game.Actions, action => action == "1:equip #123");

        h.Game.Observations[1].HealthPercent = 100;
        await h.Run(1.5);
        Assert.Equal(new[] { "1:say edited future", "1:say appended" }, h.Game.Actions.Where(action => action.StartsWith("1:say ")).ToArray());
    }

    [Theory]
    [InlineData("cancel_plan")]
    [InlineData("plan")]
    [InlineData("update_plan")]
    public async Task ManagementDuringVerificationDoesNotCancelUnrelatedNativeWorkOrReplayOldAction(string control)
    {
        using var h = new Harness();
        await h.StartPlan(Equip("false", 30), Say("obsolete tail"));
        var completedTask = h.TaskId;
        await h.Complete(completedTask);
        var plan = h.Agent.Plan!;
        Assert.Equal(PlanWait.Verification, plan.Waiting);
        h.Game.Equip(1, "crowbar");
        var unrelatedTask = h.TaskId;
        JsonObject args = control switch
        {
            "cancel_plan" => Version(plan, new JsonObject { ["reason"] = "changed assignment" }),
            "plan" => Plan(Say("replacement")),
            _ => Version(plan, new JsonObject { ["node"] = plan.Tree.CurrentId, ["steps"] = Steps(Say("replacement")) }),
        };
        await h.Send((control, args));

        Assert.Equal(unrelatedTask, h.Game.CurrentTaskId(1));
        Assert.DoesNotContain("1:cancel task", h.Game.Actions);
        Assert.Single(h.Game.Actions, action => action == "1:equip #123");
        if (control == "update_plan") Assert.Contains("1:say obsolete tail", h.Game.Actions);
        else Assert.DoesNotContain("1:say obsolete tail", h.Game.Actions);
        if (control != "cancel_plan") Assert.Contains("1:say replacement", h.Game.Actions);

        await h.Complete(completedTask);
        Assert.Equal(unrelatedTask, h.Game.CurrentTaskId(1));
        Assert.Single(h.Game.Actions, action => action == "1:equip #123");
    }

    [Fact]
    public async Task DuplicateOldCompletionCannotBypassPendingVerification()
    {
        using var h = new Harness();
        await h.StartPlan(Equip("false", 30), Say("must still wait"));
        var completedTask = h.TaskId;
        await h.Complete(completedTask);
        var plan = h.Agent.Plan!;
        var deadline = plan.WaitDeadline;
        await h.Complete(completedTask, text: "task 'equip' ok: duplicate old completion");
        await h.Run(2);
        Assert.Same(plan, h.Agent.Plan);
        Assert.Equal(PlanWait.Verification, plan.Waiting);
        Assert.Equal(deadline, plan.WaitDeadline);
        Assert.Equal("verifying", CurrentStatus(plan));
        Assert.DoesNotContain("1:say must still wait", h.Game.Actions);
        Assert.Single(h.Game.Actions, action => action == "1:equip #123");
    }

    [Fact]
    public async Task ExplicitEnsureTurnsLookupErrorIntoCaughtFailureRatherThanVerifiedSuccess()
    {
        using var h = new Harness();
        await h.StartPlan(new JsonObject
        {
            ["try"] = Steps(Do("inspect_object", new JsonObject { ["target"] = "#999" }, "true"), Say("incorrect lookup success")),
            ["catch"] = Steps(Say("lookup error recovered")),
        });
        Assert.Contains("1:say lookup error recovered", h.Game.Actions);
        Assert.DoesNotContain("1:say incorrect lookup success", h.Game.Actions);
        Assert.DoesNotContain(h.Results, result => result.Contains("verified actual result"));
    }

    [Fact]
    public async Task InspectPlanReportsPendingConditionAndOriginalNativeResultWithoutResettingVerification()
    {
        using var h = new Harness();
        await h.StartPlan(Equip("wearing('#123')", 30), Say("after"));
        const string nativeResult = "task 'equip' ok: native completion awaiting equipment confirmation";
        await h.Complete(h.TaskId, text: nativeResult);
        var plan = h.Agent.Plan!;
        var deadline = plan.WaitDeadline;
        await h.Send(("inspect_plan", new JsonObject()));

        Assert.Same(plan, h.Agent.Plan);
        Assert.Equal(deadline, plan.WaitDeadline);
        var snapshot = JsonNode.Parse(h.Manager.InspectPlan(h.Agent))!;
        Assert.Equal("verification", snapshot["waiting"]!.GetValue<string>());
        Assert.Equal("wearing('#123')", snapshot["verification_condition"]!.GetValue<string>());
        Assert.Equal(nativeResult, snapshot["verification_native_result"]!.GetValue<string>());
        Assert.Equal("verifying", snapshot["root"]!["steps"]![0]!["status"]!.GetValue<string>());
        Assert.Contains(h.Results, result => result.Contains("inspect_plan() ->") && result.Contains("verification_condition") && result.Contains("native completion awaiting equipment confirmation"));
        Assert.DoesNotContain("1:say after", h.Game.Actions);
    }

    static string CurrentStatus(ActivePlan plan) => plan.Tree.Snapshot()["root"]!["steps"]![0]!["status"]!.GetValue<string>();
    static JsonArray Steps(params JsonNode[] nodes) => new(nodes);
    static JsonObject Plan(params JsonNode[] nodes) => new() { ["steps"] = Steps(nodes) };
    static JsonObject Say(string text) => Do("say", new JsonObject { ["text"] = text });
    static JsonObject Equip(string ensure, double timeout = 10) => Do("equip", new JsonObject { ["item"] = "#123" }, ensure, timeout);
    static JsonObject Do(string tool, JsonObject args, string? ensure = null, double timeout = 10)
    {
        var step = new JsonObject { ["do"] = tool, ["args"] = args };
        if (ensure != null) { step["ensure"] = ensure; step["verify_timeout"] = timeout; }
        return step;
    }
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
        public readonly AgentManager Manager;
        readonly ConcurrentQueue<string> replies = new();
        readonly ConcurrentQueue<string> recordedResults = new();
        public BotAgent Agent => Manager.GetAgent(1)!;
        public long TaskId => Game.CurrentTaskId(1) ?? throw new InvalidOperationException("no native task");
        public IEnumerable<string> Results => recordedResults.Concat(Agent.RecentResults(BotAgent.MaxResults));

        public Harness(Action<Observation>? setup = null)
        {
            Game.AddBot(1, "Sophie", "mechanic", observation =>
            {
                observation.HealthPercent = 0;
                observation.InventoryItems = new();
                setup?.Invoke(observation);
            });
            var settings = Fixtures.Settings(@", ""maxActionsPerDecision"": 6, ""maxPlanSeconds"": 600, ""planStepTimeoutSeconds"": 30, ""decisionIntervalSeconds"": 600");
            settings.Bots.Autonomy = "order";
            settings.Bots.ThinkAtRoundStart = false;
            settings.Bots.ReactToMenuOrders = false;
            settings.Global.MinSecondsBetweenCallsPerBot = 0;
            var handler = new FakeHandler(body =>
            {
                var user = body["messages"]!.AsArray()[^1]!["content"]!.GetValue<string>();
                if (JsonNode.Parse(Fixtures.Obs(user))?["lastActionResults"] is JsonArray results)
                    foreach (var result in results) recordedResults.Enqueue(result!.GetValue<string>());
                return (HttpStatusCode.OK, replies.TryDequeue(out var reply) ? reply : Fixtures.OpenAiToolResponse(("carry_on", "{}")));
            });
            Manager = new AgentManager(settings, Game, new LlmRouter(settings, handler, Clock.Utc), Clock.Get);
            Manager.Update();
            Clock.Advance(0.5);
        }

        public async Task Request(params (string Name, JsonObject Args)[] calls)
        {
            replies.Enqueue(Fixtures.OpenAiToolResponse(calls.Select(call => (call.Name, call.Args.ToJsonString())).ToArray()));
            Agent.ReceivePlayerOrder("Daniil: apply the requested plan change", from: "Daniil");
            await Tick();
        }
        public async Task Send(params (string Name, JsonObject Args)[] calls)
        {
            await Request(calls);
            await Tick();
            Assert.Empty(replies);
        }
        public Task StartPlan(params JsonNode[] steps) => Send(("plan", Plan(steps)));
        public async Task Complete(long id, bool success = true, string text = "task 'fixture action' ok: completed")
        {
            Game.PendingReports.Add(new GameReport(1, text, TaskId: id, Success: success));
            await Tick();
        }
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
        public void Dispose() => Manager.Dispose();
    }
}
