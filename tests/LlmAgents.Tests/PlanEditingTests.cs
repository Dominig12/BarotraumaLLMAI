using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using LlmAgents.Agents;
using LlmAgents.Llm;
using Xunit;

namespace LlmAgents.Tests;

public sealed class PlanEditingTests
{
    static readonly string[] Known = Tools.All.Select(t => t.Name).ToArray();
    sealed class Context : IConditionContext
    {
        public double Health;
        public double Number(string name) => name == "health" ? Health : 0;
        public double Call(string name, string arg) => 0;
    }
    readonly Context ctx = new();
    static JsonNode Steps(string json) => JsonNode.Parse(json)!;
    static JsonArray Say(params string[] texts) => new(texts.Select(t => (JsonNode)new JsonObject { ["do"] = "say", ["args"] = new JsonObject { ["text"] = t } }).ToArray());
    static PlanTree Tree(params string[] texts) => PlanTree.Build(Say(texts), Known);
    PlanTree.ActAction Next(PlanTree tree) => Assert.IsType<PlanTree.ActAction>(tree.Next(ctx));
    string NextText(PlanTree tree) => Next(tree).Call.Arguments["text"]!.GetValue<string>();
    static PlanEditResult Edit(PlanTree tree, string op, string id, JsonNode? steps = null) => tree.Edit(new[] { new PlanEdit(op, id, steps) }, Known);
    List<string> Finish(PlanTree tree)
    {
        var texts = new List<string>();
        for (int i = 0; i < 100; i++)
        {
            var action = tree.Next(ctx);
            if (action is PlanTree.DoneAction) return texts;
            var call = Assert.IsType<PlanTree.ActAction>(action);
            texts.Add(call.Call.Arguments["text"]!.GetValue<string>());
            Assert.Null(tree.Report(true, "ok"));
        }
        throw new InvalidOperationException("the test plan did not finish");
    }

    [Fact]
    public void ReplacingActiveActionRunsReplacementBeforeUntouchedFollowingStep()
    {
        var tree = Tree("old", "after");
        var active = Next(tree);
        var result = Edit(tree, "replace", active.NodeId, Say("new"));
        Assert.True(result.Success);
        Assert.True(result.InvalidatesCurrent);
        Assert.Null(tree.CurrentId);
        Assert.Equal(new[] { "new", "after" }, Finish(tree));
    }

    [Fact]
    public void RemovingActiveActionDoesNotSkipFollowingStep()
    {
        var tree = Tree("old", "after", "last");
        var active = Next(tree);
        var result = Edit(tree, "remove", active.NodeId);
        Assert.True(result.Success);
        Assert.True(result.InvalidatesCurrent);
        Assert.Equal(new[] { "after", "last" }, Finish(tree));
    }

    [Fact]
    public void RepeatedActionReplacementDoesNotGrowInvisibleSequenceNesting()
    {
        var tree = Tree("old", "after");
        for (int i = 0; i < 20; i++)
        {
            var active = Next(tree);
            var result = Edit(tree, "replace", active.NodeId, Say("replacement " + i));
            Assert.True(result.Success, result.Error);
            Assert.True(result.InvalidatesCurrent);
            Assert.Equal(2, tree.Root.Items.Count);
            Assert.All(tree.Root.Items, n => Assert.IsType<PlanTree.Do>(n));
        }
        Assert.Equal(new[] { "replacement 19", "after" }, Finish(tree));
    }

    [Fact]
    public void ReplacingActiveAncestorDiscardsOnlyThatBranch()
    {
        var tree = PlanTree.Build(Steps("[{\"if\":\"true\",\"then\":[{\"do\":\"say\",\"args\":{\"text\":\"old\"}}]},{\"do\":\"say\",\"args\":{\"text\":\"after\"}}]"), Known);
        Next(tree);
        var result = Edit(tree, "replace", tree.Root.Items[0].Id, Say("new"));
        Assert.True(result.Success);
        Assert.True(result.InvalidatesCurrent);
        Assert.Equal(new[] { "new", "after" }, Finish(tree));
    }

    [Fact]
    public void AppendKeepsActiveActionAndCompletedPrefix()
    {
        var tree = Tree("done", "active");
        Assert.Equal("done", NextText(tree));
        tree.Report(true, "ok");
        var active = Next(tree);
        var result = Edit(tree, "append", "s0", Say("added"));
        Assert.True(result.Success);
        Assert.False(result.InvalidatesCurrent);
        Assert.Equal(active.NodeId, tree.CurrentId);
        tree.Report(true, "ok");
        Assert.Equal(new[] { "added" }, Finish(tree));
    }

    [Fact]
    public void AppendingToCurrentBranchPreservesItsCursor()
    {
        var tree = PlanTree.Build(Steps("[{\"if\":\"true\",\"then\":[{\"do\":\"say\",\"args\":{\"text\":\"first\"}},{\"do\":\"say\",\"args\":{\"text\":\"active\"}}]}]"), Known);
        Next(tree); tree.Report(true, "ok");
        Next(tree);
        var branch = Assert.IsType<PlanTree.If>(tree.Root.Items[0]).Then;
        var result = Edit(tree, "append", branch.Id, Say("last"));
        Assert.True(result.Success);
        Assert.False(result.InvalidatesCurrent);
        tree.Report(true, "ok");
        Assert.Equal(new[] { "last" }, Finish(tree));
    }

    [Fact]
    public void InsertBeforeActiveLeafCancelsAndRetriesItAfterInsertion()
    {
        var tree = Tree("first", "active", "after");
        Next(tree); tree.Report(true, "ok");
        var active = Next(tree);
        var result = Edit(tree, "insert_before", active.NodeId, Say("inserted"));
        Assert.True(result.Success);
        Assert.True(result.InvalidatesCurrent);
        Assert.Equal(new[] { "inserted", "active", "after" }, Finish(tree));
    }

    [Fact]
    public void InsertAfterActiveLeafKeepsTaskAndRunsAddedStepsNext()
    {
        var tree = Tree("active", "after");
        var active = Next(tree);
        var result = Edit(tree, "insert_after", active.NodeId, Say("inserted"));
        Assert.True(result.Success);
        Assert.False(result.InvalidatesCurrent);
        tree.Report(true, "ok");
        Assert.Equal(new[] { "inserted", "after" }, Finish(tree));
    }

    [Theory]
    [InlineData("insert_before", "n1")]
    [InlineData("insert_after", "n1")]
    [InlineData("prepend", "s0")]
    [InlineData("append", "s0")]
    public void EmptyInsertionIsRejectedWithoutInvalidatingCurrentTask(string op, string id)
    {
        var tree = Tree("active", "after");
        Next(tree);
        var before = tree.Snapshot().ToJsonString();
        var result = Edit(tree, op, id, new JsonArray());
        Assert.False(result.Success);
        Assert.False(result.InvalidatesCurrent);
        Assert.Contains("at least one", result.Error);
        Assert.Equal(before, tree.Snapshot().ToJsonString());
        tree.Report(true, "ok");
        Assert.Equal(new[] { "after" }, Finish(tree));
    }

    [Fact]
    public void PrependUsesCurrentSequenceCursorWithoutReplayingDoneActions()
    {
        var tree = Tree("done", "active", "after");
        Next(tree); tree.Report(true, "ok");
        Next(tree);
        var result = Edit(tree, "prepend", "s0", Say("inserted"));
        Assert.True(result.Success);
        Assert.True(result.InvalidatesCurrent);
        Assert.Equal(new[] { "inserted", "active", "after" }, Finish(tree));
    }

    [Fact]
    public void RejectsInsertionBeforeRunningControlBranchWithoutReplayingItsPrefix()
    {
        var tree = PlanTree.Build(Steps("[{\"if\":\"true\",\"then\":[{\"do\":\"say\",\"args\":{\"text\":\"done\"}},{\"do\":\"say\",\"args\":{\"text\":\"active\"}}]}]"), Known);
        Next(tree); tree.Report(true, "ok");
        Next(tree);
        var before = tree.Snapshot().ToJsonString();
        var result = Edit(tree, "prepend", "s0", Say("inserted"));
        Assert.False(result.Success);
        Assert.Contains("running control branch", result.Error);
        Assert.Equal(before, tree.Snapshot().ToJsonString());
        tree.Report(true, "ok");
        Assert.Empty(Finish(tree));
    }

    [Theory]
    [InlineData("replace")]
    [InlineData("remove")]
    [InlineData("insert_before")]
    [InlineData("insert_after")]
    public void RejectsEditingAlreadyExecutedLeaf(string op)
    {
        var tree = Tree("done", "pending");
        var done = Next(tree);
        tree.Report(true, "ok");
        var before = tree.Snapshot().ToJsonString();
        var result = Edit(tree, op, done.NodeId, op == "remove" ? null : Say("changed"));
        Assert.False(result.Success);
        Assert.Equal(before, tree.Snapshot().ToJsonString());
        Assert.Equal(new[] { "pending" }, Finish(tree));
    }

    [Fact]
    public void RemovesFutureNodeWhileKeepingCurrentTask()
    {
        var tree = Tree("active", "remove", "after");
        var active = Next(tree);
        var result = Edit(tree, "remove", tree.Root.Items[1].Id);
        Assert.True(result.Success);
        Assert.False(result.InvalidatesCurrent);
        Assert.Equal(active.NodeId, tree.CurrentId);
        tree.Report(true, "ok");
        Assert.Equal(new[] { "after" }, Finish(tree));
    }

    [Fact]
    public void EntireTransactionRollsBackIfLaterOperationFails()
    {
        var tree = Tree("active", "pending");
        Next(tree);
        var before = tree.Snapshot().ToJsonString();
        var result = tree.Edit(new[] { new PlanEdit("append", "s0", Say("added")), new PlanEdit("remove", "missing") }, Known);
        Assert.False(result.Success);
        Assert.False(result.InvalidatesCurrent);
        Assert.Equal(before, tree.Snapshot().ToJsonString());
        Assert.Equal(1, tree.Revision);
    }

    [Fact]
    public void OversizedReplacementCannotMutateTreeOrIdCounters()
    {
        var tree = Tree("active");
        var reference = tree.Fork();
        Next(tree);
        Next(reference);
        var before = tree.Snapshot().ToJsonString();
        var result = Edit(tree, "replace", tree.CurrentId!, Say(Enumerable.Range(0, 40).Select(i => i.ToString()).ToArray()));
        Assert.False(result.Success);
        Assert.Contains("too big", result.Error);
        Assert.Equal(before, tree.Snapshot().ToJsonString());
        Assert.True(Edit(tree, "append", "s0", Say("valid")).Success);
        Assert.True(Edit(reference, "append", "s0", Say("valid")).Success);
        Assert.Equal(reference.Snapshot().ToJsonString(), tree.Snapshot().ToJsonString());
    }

    [Fact]
    public void EditChecksTotalDepthIncludingExistingAncestors()
    {
        JsonNode nested = Say("deep");
        for (int i = 0; i < 4; i++) nested = new JsonArray(new JsonObject { ["steps"] = nested });
        var tree = PlanTree.Build(nested, Known);
        Next(tree);
        var before = tree.Snapshot().ToJsonString();
        JsonNode replacement = new JsonArray(new JsonObject { ["steps"] = new JsonArray(new JsonObject { ["steps"] = Say("too deep") }) });
        var result = Edit(tree, "replace", tree.CurrentId!, replacement);
        Assert.False(result.Success);
        Assert.Contains("deep", result.Error);
        Assert.Equal(before, tree.Snapshot().ToJsonString());
    }

    [Fact]
    public void RevisionCheckRejectsStaleEditsWithoutMutation()
    {
        var tree = Tree("first");
        var result = tree.Edit(new[] { new PlanEdit("append", "s0", Say("second")) }, Known, expectedRevision: 1);
        Assert.True(result.Success);
        Assert.Equal(2, tree.Revision);
        var before = tree.Snapshot().ToJsonString();
        result = tree.Edit(new[] { new PlanEdit("remove", "n1") }, Known, expectedRevision: 1);
        Assert.False(result.Success);
        Assert.Contains("revision", result.Error);
        Assert.Equal(before, tree.Snapshot().ToJsonString());
    }

    [Fact]
    public void ReplaceActiveWaitRunsReplacementAndDoesNotRetainWait()
    {
        var tree = PlanTree.Build(Steps("[{\"wait_until\":\"health > 50\",\"timeout\":30},{\"do\":\"say\",\"args\":{\"text\":\"after\"}}]"), Known);
        var wait = Assert.IsType<PlanTree.WaitAction>(tree.Next(ctx));
        var result = Edit(tree, "replace", wait.Node.Id, Say("replacement"));
        Assert.True(result.Success);
        Assert.True(result.InvalidatesCurrent);
        Assert.Null(tree.CurrentWait);
        Assert.Equal(new[] { "replacement", "after" }, Finish(tree));
    }

    [Fact]
    public void SuccessfulImmediateWaitUpdatesResultBeforeFollowingBranchInSameNextCall()
    {
        var tree = PlanTree.Build(Steps("[{\"try\":[{\"do\":\"say\",\"args\":{\"text\":\"fail\"}}],\"catch\":[]},{\"wait_until\":\"true\"},{\"if\":\"last_ok and result('condition satisfied')\",\"then\":[{\"do\":\"say\",\"args\":{\"text\":\"success\"}}],\"else\":[{\"do\":\"say\",\"args\":{\"text\":\"stale failure\"}}]}]"), Known);
        Assert.Equal("fail", NextText(tree));
        tree.Report(false, "failed before wait");
        Assert.False(tree.LastOk);
        Assert.Equal("success", NextText(tree));
        Assert.True(tree.LastOk);
        Assert.Equal("condition satisfied: true", tree.LastResult);
    }

    [Fact]
    public void ResumedWaitReportsSuccessLikeAnAction()
    {
        var tree = PlanTree.Build(Steps("[{\"wait_until\":\"health > 50\"},{\"if\":\"last_ok\",\"then\":[{\"do\":\"say\",\"args\":{\"text\":\"success\"}}]}]"), Known);
        Assert.IsType<PlanTree.WaitAction>(tree.Next(ctx));
        ctx.Health = 100;
        Assert.True(tree.TryResume(ctx));
        Assert.True(tree.LastOk);
        Assert.Equal("condition satisfied: health > 50", tree.LastResult);
        Assert.Equal("success", NextText(tree));
    }

    [Fact]
    public void RemovingRequiredBranchSequenceLeavesValidEmptyBranch()
    {
        var tree = PlanTree.Build(Steps("[{\"if\":\"true\",\"then\":[{\"do\":\"say\",\"args\":{\"text\":\"old\"}}]},{\"do\":\"say\",\"args\":{\"text\":\"after\"}}]"), Known);
        Next(tree);
        var branch = Assert.IsType<PlanTree.If>(tree.Root.Items[0]).Then;
        var result = Edit(tree, "remove", branch.Id);
        Assert.True(result.Success);
        Assert.True(result.InvalidatesCurrent);
        Assert.Equal(new[] { "after" }, Finish(tree));
        Assert.Empty(Assert.IsType<PlanTree.If>(tree.Root.Items[0]).Then.Items);
    }

    [Fact]
    public void SnapshotContainsEverySequenceAndExactArgumentsAndReturnsDetachedJson()
    {
        var tree = PlanTree.Build(Steps("[{\"if\":\"health > 50\",\"then\":[{\"do\":\"say\",\"args\":{\"text\":\"a \\\"quote\\\" and newline\\n\"}}],\"else\":[{\"wait_until\":\"true\"}]}]"), Known);
        var snapshot = tree.Snapshot();
        var branch = Assert.IsType<PlanTree.If>(tree.Root.Items[0]);
        Assert.Equal("s0", snapshot["root"]!["id"]!.GetValue<string>());
        Assert.Equal(branch.Then.Id, snapshot["root"]!["steps"]![0]!["then"]!["id"]!.GetValue<string>());
        Assert.Equal(branch.Else!.Id, snapshot["root"]!["steps"]![0]!["else"]!["id"]!.GetValue<string>());
        var inspectedArgs = snapshot["root"]!["steps"]![0]!["then"]!["steps"]![0]!["args"]!;
        Assert.Equal("a \"quote\" and newline\n", inspectedArgs["text"]!.GetValue<string>());
        inspectedArgs["text"] = "changed outside";
        ctx.Health = 100;
        Assert.Equal("a \"quote\" and newline\n", NextText(tree));
    }

    [Fact]
    public void ForkCopiesCurrentActionAndLoopCountersWithoutSharing()
    {
        var tree = PlanTree.Build(Steps("[{\"repeat\":[{\"do\":\"say\",\"args\":{\"text\":\"again\"}}],\"max\":3}]"), Known);
        Next(tree); tree.Report(true, "first");
        Next(tree);
        var copy = tree.Fork();
        Assert.Equal(tree.Snapshot().ToJsonString(), copy.Snapshot().ToJsonString());
        tree.Report(true, "second");
        Assert.Equal(new[] { "again" }, Finish(tree));
        Assert.Equal("first", copy.LastResult);
        Assert.Equal("again", NextText(copy));
        copy.Report(true, "second independently");
        Assert.Equal(new[] { "again" }, Finish(copy));
    }

    [Fact]
    public void WhileAndUntilAreCheckedBeforeFirstAndEveryFollowingIteration()
    {
        var tree = PlanTree.Build(Steps("[{\"repeat\":[{\"do\":\"say\",\"args\":{\"text\":\"again\"}}],\"while\":\"health < 50\",\"max\":10}]"), Known);
        Next(tree); tree.Report(true, "ok");
        ctx.Health = 100;
        Assert.IsType<PlanTree.DoneAction>(tree.Next(ctx));
        Assert.Equal(1, tree.Snapshot()["root"]!["steps"]![0]!["iterations_started"]!.GetValue<int>());
        var until = PlanTree.Build(Steps("[{\"repeat\":[{\"do\":\"say\",\"args\":{\"text\":\"never\"}}],\"until\":\"health > 50\",\"max\":10}]"), Known);
        Assert.IsType<PlanTree.DoneAction>(until.Next(ctx));
        Assert.Equal(0, until.Snapshot()["root"]!["steps"]![0]!["iterations_started"]!.GetValue<int>());
    }

    [Fact]
    public void FailureInCatchUnwindsToOuterCatchAndSkippedStepsAreVisible()
    {
        var tree = PlanTree.Build(Steps("[{\"try\":[{\"try\":[{\"do\":\"say\",\"args\":{\"text\":\"fail\"}},{\"do\":\"say\",\"args\":{\"text\":\"skip\"}}],\"catch\":[{\"do\":\"say\",\"args\":{\"text\":\"inner catch\"}}]}],\"catch\":[{\"do\":\"say\",\"args\":{\"text\":\"outer catch\"}}]}]"), Known);
        Assert.Equal("fail", NextText(tree));
        Assert.Null(tree.Report(false, "failed"));
        Assert.Equal("inner catch", NextText(tree));
        Assert.Null(tree.Report(false, "catch failed"));
        Assert.Equal(new[] { "outer catch" }, Finish(tree));
        Assert.Contains("\"status\":\"skipped\"", tree.Snapshot().ToJsonString());
    }

    [Fact]
    public void AppendToCompletedRootRunsOnlyNewWork()
    {
        var tree = Tree("old");
        Assert.Equal(new[] { "old" }, Finish(tree));
        Assert.True(Edit(tree, "append", "s0", Say("new")).Success);
        Assert.Equal(new[] { "new" }, Finish(tree));
    }

    [Fact]
    public void BuiltinArgumentsAreValidatedBeforeBuildOrEdit()
    {
        Assert.Throws<FormatException>(() => PlanTree.Build(Steps("[{\"do\":\"say\",\"args\":{\"message\":\"wrong field\"}}]"), Known));
        var tree = Tree("active");
        Next(tree);
        var before = tree.Snapshot().ToJsonString();
        var result = Edit(tree, "append", "s0", Steps("[{\"do\":\"move_item\",\"args\":{\"item\":\"#1\",\"to\":\"self\",\"count\":0}}]"));
        Assert.False(result.Success);
        Assert.Equal(before, tree.Snapshot().ToJsonString());
    }

    [Fact]
    public void PlainRepliesKeepPerStepValidationSoFailedSpeechCanRemainSoft()
    {
        var tree = PlanTree.FromCalls(new[]
        {
            new ToolCall("say", new JsonObject { ["message"] = "invalid" }),
            new ToolCall("fix_leaks", new JsonObject()),
        });
        Assert.Equal("say", Next(tree).Call.Name);
        tree.Report(true, "error: missing text (soft speech failure)");
        Assert.Equal("fix_leaks", Next(tree).Call.Name);
    }

    [Theory]
    [InlineData("[{\"say\":{\"text\":\"no alias\"}}]")]
    [InlineData("[{\"tool\":\"say\",\"args\":{\"text\":\"no alias\"}}]")]
    [InlineData("[{\"if\":\"true\",\"then\":[],\"do\":\"fix_leaks\"}]")]
    [InlineData("[{\"repeat\":[],\"while\":\"true\",\"until\":\"false\"}]")]
    [InlineData("[{\"repeat\":[],\"max\":0}]")]
    [InlineData("[{\"repeat\":[],\"max\":11}]")]
    [InlineData("[{\"repeat\":[],\"max\":2.5}]")]
    [InlineData("[{\"repeat\":[],\"max\":\"3\"}]")]
    [InlineData("[{\"wait_until\":\"true\",\"timeout\":0}]")]
    [InlineData("[{\"wait_until\":\"true\",\"timeout\":301}]")]
    [InlineData("[{\"wait_until\":\"true\",\"timeout\":null}]")]
    [InlineData("[{\"do\":\"say\",\"args\":{\"text\":\"x\"},\"timeout\":7201}]")]
    [InlineData("[{\"do\":\"say\",\"args\":{\"text\":\"x\"},\"timeout\":\"30\"}]")]
    public void RejectsNoncanonicalOrOutOfRangeNodes(string json) => Assert.Throws<FormatException>(() => PlanTree.Build(Steps(json), Known));

    [Fact]
    public void ActionTimeoutSurvivesExecutionForkAndSnapshot()
    {
        var tree = PlanTree.Build(Steps("[{\"do\":\"say\",\"args\":{\"text\":\"x\"},\"timeout\":25.5}]"), Known);
        Assert.Equal(25.5, Next(tree).TimeoutSeconds);
        var copy = tree.Fork();
        Assert.Equal(25.5, Next(copy).TimeoutSeconds);
        Assert.Equal(25.5, copy.Snapshot()["root"]!["steps"]![0]!["timeout"]!.GetValue<double>());
    }

    [Fact]
    public void GeneratedToolNamesRequireCurrentKnownCapability()
    {
        var steps = Steps("[{\"do\":\"object_123_use\",\"args\":{\"custom\":42}}]");
        Assert.Throws<FormatException>(() => PlanTree.Build(steps, Known));
        var tree = PlanTree.Build(steps, Known.Concat(new[] { "object_123_use" }).ToArray());
        Assert.Equal("object_123_use", Next(tree).Call.Name);
    }
}
