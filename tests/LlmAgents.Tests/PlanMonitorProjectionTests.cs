using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using LlmAgents.Agents;
using LlmAgents.SettingsUi;
using Xunit;

namespace LlmAgents.Tests;

public sealed class PlanMonitorProjectionTests
{
    static readonly string[] Known = Tools.All.Select(t => t.Name).ToArray();
    sealed class Context : IConditionContext
    {
        public double Number(string name) => 0;
        public double Call(string name, string arg) => 0;
    }
    static readonly Context Ctx = new();
    static PlanTree Tree(string json) => PlanTree.Build(JsonNode.Parse(json), Known);
    static PlanTree.ActAction Next(PlanTree tree) => Assert.IsType<PlanTree.ActAction>(tree.Next(Ctx));
    static IEnumerable<string> SnapshotIds(JsonObject node)
    {
        yield return node["id"]!.GetValue<string>();
        foreach (var field in node)
        {
            if (field.Key == "steps" && field.Value is JsonArray sequence)
                foreach (var child in sequence.OfType<JsonObject>())
                    foreach (var id in SnapshotIds(child)) yield return id;
            else if (field.Key is "then" or "else" or "repeat" or "try" or "catch" && field.Value is JsonObject branch)
                foreach (var id in SnapshotIds(branch)) yield return id;
        }
    }

    [Fact]
    public void FullProjectionPreservesEveryNodeSequenceBranchAndConditionInPreorder()
    {
        var tree = Tree("""
            [{"if":"health < 50","then":[{"do":"equip","args":{"item":"#123"},"timeout":90,"ensure":"wearing('#123')","verify_timeout":15}],
              "else":[{"wait_until":"leaks == 0","timeout":23}]},
             {"repeat":[{"try":[{"do":"craft","args":{"item":"bandage"},"ensure":"count('bandage') >= 2"}],
                         "catch":[{"do":"say","args":{"text":"no ingredients"}}]}],"while":"health < 80","max":3},
             {"repeat":[{"do":"fix_leaks"}],"until":"leaks == 0","max":2}]
            """);
        var current = Next(tree);
        var plan = new ActivePlan(tree, true, 10) { ExecutedSteps = 1 };
        plan.WaitFor(PlanWait.Task, 12, deadline: 72);
        plan.AwaitedTaskId = 42;
        var progress = new GameTaskProgress(42, "blocked", "waiting at a cabinet", 3, 50, "source_inaccessible", "door locked", 2, 8);
        var result = PlanMonitorProjection.Build(plan, 15, progress);

        Assert.Equal(plan.Id, result.PlanId);
        Assert.Equal(tree.Revision, result.Revision);
        Assert.True(result.Active);
        Assert.Equal("running", result.Status);
        Assert.Equal("s0", result.RootNodeId);
        Assert.Equal(current.NodeId, result.CurrentNodeId);
        Assert.Equal(SnapshotIds(tree.Snapshot()["root"]!.AsObject()).ToArray(), result.Nodes.Select(n => n.Id).ToArray());
        Assert.Equal(result.Nodes.Count, result.Nodes.Select(n => n.Id).Distinct().Count());
        var root = result.Nodes[0];
        Assert.Equal(("s0", "root", "sequence", 0), (root.Id, root.Branch, root.Kind, root.Depth));
        Assert.Null(root.ParentId);
        Assert.Single(result.Nodes, n => n.IsCurrent && n.Id == current.NodeId);
        var action = result.Nodes.Single(n => n.Id == current.NodeId);
        Assert.Equal("equip", action.Tool);
        Assert.Equal("#123", JsonNode.Parse(action.ArgumentsJson!)!["item"]!.GetValue<string>());
        Assert.Equal("wearing('#123')", action.Ensure);
        Assert.Equal(90, action.TimeoutSeconds);
        Assert.Equal(15, action.VerifyTimeoutSeconds);

        var conditional = result.Nodes.Single(n => n.Kind == "if");
        Assert.Equal("health < 50", conditional.Condition);
        Assert.Equal("if", conditional.ConditionKind);
        var then = result.Nodes.Single(n => n.ParentId == conditional.Id && n.Branch == "then");
        var otherwise = result.Nodes.Single(n => n.ParentId == conditional.Id && n.Branch == "else");
        Assert.Equal(0, then.Index);
        Assert.Equal(1, otherwise.Index);
        Assert.Equal("skipped", otherwise.Status);
        Assert.Equal(then.Id, action.ParentId);
        Assert.Equal(3, action.Depth);
        var wait = result.Nodes.Single(n => n.Kind == "wait");
        Assert.Equal("leaks == 0", wait.Condition);
        Assert.Equal("wait_until", wait.ConditionKind);
        Assert.Equal(23, wait.TimeoutSeconds);
        Assert.Equal("skipped", wait.Status);

        var loop = result.Nodes.Single(n => n.ConditionKind == "while");
        Assert.Equal("health < 80", loop.Condition);
        Assert.Equal(3, loop.MaxIterations);
        Assert.Equal(0, loop.IterationsStarted);
        Assert.Contains(result.Nodes, n => n.ParentId == loop.Id && n.Branch == "repeat" && n.Kind == "sequence");
        var attempt = result.Nodes.Single(n => n.Kind == "try");
        Assert.Contains(result.Nodes, n => n.ParentId == attempt.Id && n.Branch == "try");
        Assert.Contains(result.Nodes, n => n.ParentId == attempt.Id && n.Branch == "catch");
        Assert.Contains(result.Nodes, n => n.ConditionKind == "until" && n.Condition == "leaks == 0" && n.MaxIterations == 2);

        Assert.Equal(5, result.ElapsedSeconds);
        Assert.Equal("task", result.Waiting);
        Assert.Equal(3, result.WaitingSeconds);
        Assert.Equal(57, result.WaitRemainingSeconds);
        Assert.Equal(60, result.WaitTimeoutSeconds);
        Assert.Equal(42, result.AwaitedTaskId);
        Assert.Equal(2, result.TaskProgress!.CompletedUnits);
        Assert.Equal(8, result.TaskProgress.TotalUnits);
        Assert.Equal("source_inaccessible", result.TaskProgress.BlockerCode);
        Assert.Equal(ActivePlan.MaximumExecutedSteps, result.ExecutionBudget);
    }

    [Fact]
    public void ProjectionCopiesVerificationAndPriorOutcomeWithoutMarkingActionCompleted()
    {
        var tree = Tree("""[{"do":"say","args":{"text":"first"}},{"do":"equip","args":{"item":"#123"},"ensure":"wearing('#123')","verify_timeout":15}]""");
        Next(tree); tree.Report(true, "previous confirmed result");
        var current = Next(tree);
        tree.MarkVerifying();
        var plan = new ActivePlan(tree, false, 10) { ExecutedSteps = 2 };
        plan.WaitFor(PlanWait.Verification, 20, deadline: 35);
        plan.VerificationResult = "task 'equip' ok: native action completed";
        var result = PlanMonitorProjection.Build(plan, 25);

        Assert.Equal("verification", result.Waiting);
        Assert.Equal("wearing('#123')", result.VerificationCondition);
        Assert.Equal(plan.VerificationResult, result.VerificationNativeResult);
        Assert.Equal("previous confirmed result", result.LastResult);
        Assert.True(result.LastOk);
        Assert.Equal(5, result.WaitingSeconds);
        Assert.Equal(10, result.WaitRemainingSeconds);
        Assert.Equal(15, result.WaitTimeoutSeconds);
        Assert.Equal("verifying", result.Nodes.Single(n => n.Id == current.NodeId).Status);
        Assert.Equal(2, result.ExecutedSteps);
    }

    [Fact]
    public void ProjectionIsDetachedAndNeverAdvancesTreeOrChangesWaitingState()
    {
        var tree = Tree("""[{"do":"say","args":{"text":"a \"quote\" and newline\n"}},{"do":"fix_leaks"}]""");
        var current = Next(tree);
        var plan = new ActivePlan(tree, true, 0);
        plan.WaitFor(PlanWait.Task, 1, deadline: 51);
        plan.AwaitedTaskId = 7;
        var progress = new GameTaskProgress(7, "running", "original native step", 5, 0);
        var before = tree.Snapshot().ToJsonString();
        var result = PlanMonitorProjection.Build(plan, 5, progress);
        Assert.Equal("a \"quote\" and newline\n", JsonNode.Parse(result.Nodes.Single(n => n.Id == current.NodeId).ArgumentsJson!)!["text"]!.GetValue<string>());
        result.TaskProgress!.Step = "external UI mutation";
        result.Nodes.Single(n => n.Id == current.NodeId).Status = "completed";
        result.Nodes.Clear();

        Assert.Equal(before, tree.Snapshot().ToJsonString());
        Assert.Equal("original native step", progress.Step);
        Assert.Equal(PlanWait.Task, plan.Waiting);
        Assert.Equal(51, plan.WaitDeadline);
        Assert.Equal(7, plan.AwaitedTaskId);
        Assert.Null(tree.LastOk);
        Assert.Equal(current.NodeId, tree.CurrentId);
        Assert.Equal(3, PlanMonitorProjection.Build(plan, 5).Nodes.Count);
    }

    [Fact]
    public void StaleOrUnrelatedNativeProgressIsNeverAttachedToAPlan()
    {
        var tree = Tree("""[{"do":"equip","args":{"item":"#123"},"ensure":"true"}]""");
        Next(tree);
        var plan = new ActivePlan(tree, false, 0);
        plan.WaitFor(PlanWait.Task, 1, deadline: 100);
        plan.AwaitedTaskId = 7;
        var progress = new GameTaskProgress(8, "blocked", "unrelated", 10, 60, "blocked");
        Assert.Null(PlanMonitorProjection.Build(plan, 2, progress).TaskProgress);
        Assert.NotNull(PlanMonitorProjection.Build(plan, 2, progress with { TaskId = 7 }).TaskProgress);
        tree.MarkVerifying();
        plan.WaitFor(PlanWait.Verification, 2, deadline: 12);
        plan.AwaitedTaskId = 7; // Even a stale matching ID must not turn a completed action into a live task.
        Assert.Null(PlanMonitorProjection.Build(plan, 3, progress with { TaskId = 7 }).TaskProgress);
    }

    [Fact]
    public void LoopCountersComeFromRealExecutionAndKeepTheBodySequenceIdentity()
    {
        var tree = Tree("""[{"repeat":[{"do":"fix_leaks"}],"while":"health < 50","max":3}]""");
        var first = Next(tree); tree.Report(true, "first iteration");
        var second = Next(tree);
        Assert.Equal(first.NodeId, second.NodeId);
        var plan = new ActivePlan(tree, false, 0);
        var result = PlanMonitorProjection.Build(plan, 3);
        var loop = result.Nodes.Single(n => n.Kind == "repeat");
        Assert.Equal(2, loop.IterationsStarted);
        Assert.Equal(3, loop.MaxIterations);
        Assert.Equal("while", loop.ConditionKind);
        Assert.Equal("health < 50", loop.Condition);
        Assert.Equal("repeat", result.Nodes.Single(n => n.Id == result.Nodes.Single(n => n.IsCurrent).ParentId).Branch);
    }

    [Theory]
    [InlineData("cancelled")]
    [InlineData("replaced")]
    public void EndedCancellationFreezesDurationAndCancelsOnlyUnfinishedNodes(string status)
    {
        var tree = Tree("""[{"do":"say","args":{"text":"completed"}},{"do":"equip","args":{"item":"#123"},"ensure":"false"},{"do":"say","args":{"text":"pending"}}]""");
        var completed = Next(tree); tree.Report(true, "completed effect remains");
        Next(tree); tree.MarkVerifying();
        var plan = new ActivePlan(tree, true, 10);
        plan.WaitFor(PlanWait.Verification, 20, deadline: 40);
        plan.VerificationResult = "native success awaiting facts";
        var before = tree.Snapshot().ToJsonString();

        var result = PlanMonitorProjection.BuildEnded(plan, 50, 30, status, "assignment changed");
        Assert.False(result.Active);
        Assert.Equal(status, result.Status);
        Assert.Equal(20, result.ElapsedSeconds);
        Assert.Equal(20, result.EndedAgoSeconds);
        Assert.Equal("assignment changed", result.EndReason);
        Assert.Null(result.CurrentNodeId);
        Assert.Null(result.WaitRemainingSeconds);
        Assert.DoesNotContain(result.Nodes, n => n.IsCurrent);
        Assert.Equal("completed", result.Nodes.Single(n => n.Id == completed.NodeId).Status);
        Assert.All(result.Nodes.Where(n => n.Id != completed.NodeId), n => Assert.Equal("cancelled", n.Status));
        Assert.Equal(before, tree.Snapshot().ToJsonString());
        var summary = PlanMonitorProjection.Summarize(result);
        Assert.Equal(result.PlanId, summary.PlanId);
        Assert.Equal(status, summary.Status);
        Assert.Equal("assignment changed", summary.Reason);
        Assert.Equal(20, summary.ElapsedSeconds);
        Assert.Equal(20, summary.EndedAgoSeconds);
    }

    [Fact]
    public void CompletedAndFailedHistoricalViewsKeepTypedOutcomesAndSerializableSummaries()
    {
        var completedTree = Tree("""[{"do":"say","args":{"text":"done"}}]""");
        Next(completedTree); completedTree.Report(true, "confirmed completion");
        Assert.IsType<PlanTree.DoneAction>(completedTree.Next(Ctx));
        var completed = PlanMonitorProjection.BuildEnded(new ActivePlan(completedTree, false, 5), 50, 15, "completed");
        Assert.All(completed.Nodes, n => Assert.Equal("completed", n.Status));
        Assert.Equal("confirmed completion", completed.LastResult);
        Assert.True(completed.LastOk);
        Assert.Equal(10, completed.ElapsedSeconds);

        var failedTree = Tree("""[{"do":"equip","args":{"item":"#123"}},{"do":"say","args":{"text":"skipped"}}]""");
        Next(failedTree); failedTree.Report(false, "native task failed");
        var failed = PlanMonitorProjection.BuildEnded(new ActivePlan(failedTree, false, 5), 50, 16, "failed");
        Assert.Equal("native task failed", failed.Failure);
        Assert.False(failed.LastOk);
        Assert.Contains(failed.Nodes, n => n.Status == "failed");
        Assert.Contains(failed.Nodes, n => n.Status == "skipped");
        Assert.DoesNotContain(failed.Nodes, n => n.Status is "running" or "verifying");
        Assert.Equal("native task failed", PlanMonitorProjection.Summarize(failed).Reason);

        var restored = JsonSerializer.Deserialize<MonitorPlanSnapshot>(JsonSerializer.Serialize(completed))!;
        Assert.Equal(completed.PlanId, restored.PlanId);
        Assert.Equal(completed.Nodes.Count, restored.Nodes.Count);
        Assert.Equal("say", PlanMonitorProjection.Summarize(restored).Label);
        Assert.False(restored.Active);
    }

    [Fact]
    public void EachCompletedLeafKeepsItsOwnTypedOutcomeAfterRecoveryAndSuccessfulWait()
    {
        var tree = Tree("""[{"try":[{"do":"equip","args":{"item":"#123"}}],"catch":[{"do":"say","args":{"text":"recovered"}}]},{"wait_until":"true"}]""");
        var failed = Next(tree);
        Assert.Null(tree.Report(false, "native equip failed"));
        var recovery = Next(tree);
        tree.Report(true, "recovery spoke");
        Assert.IsType<PlanTree.DoneAction>(tree.Next(Ctx));
        var plan = PlanMonitorProjection.BuildEnded(new ActivePlan(tree, false, 0), 10, 5, "completed");
        var failedNode = plan.Nodes.Single(n => n.Id == failed.NodeId);
        var recoveredNode = plan.Nodes.Single(n => n.Id == recovery.NodeId);
        var wait = plan.Nodes.Single(n => n.Kind == "wait");
        Assert.False(failedNode.LastOk);
        Assert.Equal("native equip failed", failedNode.Result);
        Assert.True(recoveredNode.LastOk);
        Assert.Equal("recovery spoke", recoveredNode.Result);
        Assert.True(wait.LastOk);
        Assert.Equal("condition satisfied: true", wait.Result);
        Assert.True(plan.LastOk);
        Assert.Equal("completed", plan.Status);
    }

    [Fact]
    public void SummaryUsesStructuredToolNamesAndWorksForControlOnlyOrEmptyPlans()
    {
        var plan = new ActivePlan(Tree("""[{"do":"say","args":{"text":"not the label"}},{"do":"fix_leaks"},{"do":"repair_systems"},{"do":"wait"}]"""), false, 0);
        var summary = PlanMonitorProjection.Summarize(PlanMonitorProjection.Build(plan, 5));
        Assert.Equal("say → fix_leaks → repair_systems …", summary.Label);
        Assert.DoesNotContain("not the label", summary.Label);
        Assert.Equal("wait", PlanMonitorProjection.Summarize(PlanMonitorProjection.Build(new ActivePlan(Tree("""[{"wait_until":"false"}]"""), false, 0), 1)).Label);
        Assert.Equal("sequence", PlanMonitorProjection.Summarize(PlanMonitorProjection.Build(new ActivePlan(Tree("[]"), false, 0), 1)).Label);
    }

    [Fact]
    public void TimersStayNonnegativeAndInvalidDiagnosticNumbersRemainJsonSerializable()
    {
        var tree = Tree("""[{"do":"equip","args":{"item":"#123"}}]""");
        Next(tree);
        var plan = new ActivePlan(tree, false, double.NaN);
        plan.WaitFor(PlanWait.Task, double.NaN, deadline: double.PositiveInfinity);
        plan.AwaitedTaskId = 7;
        var result = PlanMonitorProjection.Build(plan, double.PositiveInfinity,
            new GameTaskProgress(7, "running", "work", double.NaN, double.PositiveInfinity));
        Assert.Equal(0, result.ElapsedSeconds);
        Assert.Equal(0, result.WaitingSeconds);
        Assert.Null(result.WaitRemainingSeconds);
        Assert.Null(result.WaitTimeoutSeconds);
        Assert.Equal(0, result.TaskProgress!.ElapsedSeconds);
        Assert.Equal(0, result.TaskProgress.NoProgressSeconds);
        Assert.NotEmpty(JsonSerializer.Serialize(result));

        plan.WaitFor(PlanWait.Condition, 10, deadline: 20);
        result = PlanMonitorProjection.Build(plan, 25);
        Assert.Equal(0, result.WaitRemainingSeconds);
        Assert.Equal(15, result.WaitingSeconds);
        Assert.Equal(10, result.WaitTimeoutSeconds);
        plan.YieldUntilNextTick(30);
        result = PlanMonitorProjection.Build(plan, 35);
        Assert.Equal("yield", result.Waiting);
        Assert.Equal(0, result.WaitingSeconds);
        Assert.Null(result.WaitRemainingSeconds);
    }
}
