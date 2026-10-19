using System;
using System.Linq;
using System.Text.Json.Nodes;
using LlmAgents.Agents;
using LlmAgents.Llm;
using Xunit;

namespace LlmAgents.Tests;

public sealed class StepVerificationContractTests
{
    static readonly string[] Known = Tools.All.Select(t => t.Name).ToArray();
    sealed class Context : IConditionContext
    {
        public double Number(string name) => 0;
        public double Call(string name, string arg) => 0;
    }
    readonly Context ctx = new();
    static JsonNode Steps(string json) => JsonNode.Parse(json)!;
    static JsonArray Say(string text) => new(new JsonObject { ["do"] = "say", ["args"] = new JsonObject { ["text"] = text } });
    static PlanTree Tree(string extra = "") => PlanTree.Build(Steps("[{\"do\":\"equip\",\"args\":{\"item\":\"#123\"},\"ensure\":\"wearing('#123')\"" + extra + "},{\"do\":\"say\",\"args\":{\"text\":\"after\"}}]"), Known);
    PlanTree.ActAction Next(PlanTree tree) => Assert.IsType<PlanTree.ActAction>(tree.Next(ctx));
    static JsonObject ActionSnapshot(PlanTree tree) => tree.Snapshot()["root"]!["steps"]![0]!.AsObject();

    [Fact]
    public void ActionCarriesParsedPostconditionAndIndependentExecutionAndVerificationTimeouts()
    {
        var tree = Tree(",\"timeout\":180,\"verify_timeout\":25.5");
        Assert.Null(tree.CurrentDo);
        var action = Next(tree);
        var node = Assert.IsType<PlanTree.Do>(tree.Root.Items[0]);
        Assert.Same(node, tree.CurrentDo);
        Assert.Same(node.Ensure, action.Ensure);
        Assert.Equal("wearing('#123')", action.Ensure!.Text);
        Assert.Equal(180, action.TimeoutSeconds);
        Assert.Equal(25.5, action.VerifyTimeoutSeconds);
        Assert.Equal(25.5, node.VerifyTimeoutSeconds);
    }

    [Fact]
    public void VerificationTimeoutDefaultsToTenSecondsAndIsVisibleInInspection()
    {
        var tree = Tree();
        var action = Next(tree);
        Assert.Equal(10, action.VerifyTimeoutSeconds);
        Assert.Equal(10, tree.CurrentDo!.VerifyTimeoutSeconds);
        Assert.Equal(10, ActionSnapshot(tree)["verify_timeout"]!.GetValue<double>());
    }

    [Fact]
    public void PlainReplyActionsKeepOptionalVerificationAbsent()
    {
        var tree = PlanTree.FromCalls(new[] { new ToolCall("fix_leaks", new JsonObject()) });
        var action = Next(tree);
        Assert.Null(action.Ensure);
        Assert.Null(tree.CurrentDo!.Ensure);
        Assert.Null(ActionSnapshot(tree)["ensure"]);
        Assert.Null(ActionSnapshot(tree)["verify_timeout"]);
    }

    [Fact]
    public void MarkVerifyingKeepsCurrentFrameAndDoesNotPrematurelyReportSuccess()
    {
        var tree = Tree();
        var action = Next(tree);
        tree.MarkVerifying();
        Assert.Equal(action.NodeId, tree.CurrentId);
        Assert.Equal(action.NodeId, tree.CurrentDo!.Id);
        Assert.Null(tree.LastOk);
        Assert.Null(tree.LastResult);
        Assert.Equal("verifying", ActionSnapshot(tree)["status"]!.GetValue<string>());
        Assert.Contains("<VERIFYING>", tree.Describe());
        Assert.Contains("ensure wearing('#123')", tree.Describe());
        Assert.Contains("verify max 10s", tree.Describe());

        tree.Report(true, "verified equipment slot");
        Assert.Null(tree.CurrentDo);
        Assert.True(tree.LastOk);
        Assert.Equal("completed", ActionSnapshot(tree)["status"]!.GetValue<string>());
        Assert.Equal("say", Next(tree).Call.Name);
    }

    [Fact]
    public void MarkVerifyingRequiresAnIssuedAction()
    {
        var tree = Tree();
        Assert.Throws<InvalidOperationException>(() => tree.MarkVerifying());
        Next(tree);
        tree.MarkVerifying();
        tree.Report(true, "verified");
        Assert.Throws<InvalidOperationException>(() => tree.MarkVerifying());
    }

    [Fact]
    public void ForkPreservesVerificationMetadataAndPendingStageWithoutSharedArguments()
    {
        var tree = Tree(",\"verify_timeout\":30");
        Next(tree);
        tree.MarkVerifying();
        var fork = tree.Fork();
        Assert.Equal(tree.Snapshot().ToJsonString(), fork.Snapshot().ToJsonString());
        Assert.NotSame(tree.CurrentDo, fork.CurrentDo);
        Assert.Equal("wearing('#123')", fork.CurrentDo!.Ensure!.Text);
        Assert.Equal(30, fork.CurrentDo.VerifyTimeoutSeconds);
        fork.CurrentDo.Call.Arguments["item"] = "#456";
        Assert.Equal("#123", tree.CurrentDo!.Call.Arguments["item"]!.GetValue<string>());
        fork.Report(false, "verification timeout");
        Assert.Equal("verifying", ActionSnapshot(tree)["status"]!.GetValue<string>());
        Assert.Null(tree.LastOk);
    }

    [Fact]
    public void FailedVerificationUsesExistingCatchWithoutReissuingAction()
    {
        var tree = PlanTree.Build(Steps("[{\"try\":[{\"do\":\"equip\",\"args\":{\"item\":\"#123\"},\"ensure\":\"wearing('#123')\"}],\"catch\":[{\"if\":\"last_failed\",\"then\":[{\"do\":\"say\",\"args\":{\"text\":\"verification failed\"}}]}]}]"), Known);
        Assert.Equal("equip", Next(tree).Call.Name);
        tree.MarkVerifying();
        Assert.Null(tree.Report(false, "ensure did not become true"));
        Assert.False(tree.LastOk);
        var recovery = Next(tree);
        Assert.Equal("say", recovery.Call.Name);
        Assert.Equal("verification failed", recovery.Call.Arguments["text"]!.GetValue<string>());
        tree.Report(true, "ok");
        Assert.IsType<PlanTree.DoneAction>(tree.Next(ctx));
    }

    [Theory]
    [InlineData("insert_before", "n1")]
    [InlineData("prepend", "s0")]
    public void InsertingBeforeVerifyingActionCannotReplayAppliedEffects(string op, string node)
    {
        var tree = Tree();
        Next(tree);
        tree.MarkVerifying();
        var before = tree.Snapshot().ToJsonString();
        var result = tree.Edit(new[] { new PlanEdit(op, node, Say("inserted")) }, Known);
        Assert.False(result.Success);
        Assert.False(result.InvalidatesCurrent);
        Assert.Contains("being verified", result.Error);
        Assert.Equal(before, tree.Snapshot().ToJsonString());
    }

    [Theory]
    [InlineData("append", "s0")]
    [InlineData("insert_after", "n1")]
    public void AddingFutureStepsPreservesVerificationAndItsCurrentAction(string op, string node)
    {
        var tree = Tree();
        var action = Next(tree);
        tree.MarkVerifying();
        var result = tree.Edit(new[] { new PlanEdit(op, node, Say("added")) }, Known);
        Assert.True(result.Success, result.Error);
        Assert.False(result.InvalidatesCurrent);
        Assert.Equal(action.NodeId, tree.CurrentId);
        Assert.Equal("verifying", ActionSnapshot(tree)["status"]!.GetValue<string>());
        Assert.Equal("wearing('#123')", tree.CurrentDo!.Ensure!.Text);
    }

    [Theory]
    [InlineData("replace")]
    [InlineData("remove")]
    public void ExplicitReplacementOrRemovalInvalidatesVerificationWithoutRetainingOldAction(string op)
    {
        var tree = Tree();
        var action = Next(tree);
        tree.MarkVerifying();
        var result = tree.Edit(new[] { new PlanEdit(op, action.NodeId, op == "replace" ? Say("replacement") : null) }, Known);
        Assert.True(result.Success, result.Error);
        Assert.True(result.InvalidatesCurrent);
        Assert.Null(tree.CurrentDo);
        Assert.Null(tree.CurrentId);
        var next = Next(tree);
        Assert.Equal("say", next.Call.Name);
        Assert.Equal(op == "replace" ? "replacement" : "after", next.Call.Arguments["text"]!.GetValue<string>());
    }

    [Fact]
    public void RejectedEditRetainsOriginalVerificationFrameAndMetadata()
    {
        var tree = Tree();
        Next(tree);
        tree.MarkVerifying();
        var before = tree.Snapshot().ToJsonString();
        var result = tree.Edit(new[] { new PlanEdit("append", "s0", Steps("[{\"do\":\"fix_leaks\",\"ensure\":\"helth > 0\"}]")) }, Known);
        Assert.False(result.Success);
        Assert.Equal(before, tree.Snapshot().ToJsonString());
    }

    [Theory]
    [InlineData("{\"do\":\"fix_leaks\",\"verify_timeout\":10}")]
    [InlineData("{\"do\":\"fix_leaks\",\"ensure\":\"\"}")]
    [InlineData("{\"do\":\"fix_leaks\",\"ensure\":null}")]
    [InlineData("{\"do\":\"fix_leaks\",\"ensure\":true}")]
    [InlineData("{\"do\":\"fix_leaks\",\"ensure\":\"unknown_fact > 0\"}")]
    [InlineData("{\"do\":\"fix_leaks\",\"ensure\":\"true\",\"verify_timeout\":0}")]
    [InlineData("{\"do\":\"fix_leaks\",\"ensure\":\"true\",\"verify_timeout\":301}")]
    [InlineData("{\"do\":\"fix_leaks\",\"ensure\":\"true\",\"verify_timeout\":null}")]
    [InlineData("{\"do\":\"fix_leaks\",\"ensure\":\"true\",\"verify_timeout\":\"10\"}")]
    public void TreeRejectsMalformedVerificationContractsBeforeExecution(string node) =>
        Assert.Throws<FormatException>(() => PlanTree.Build(Steps("[" + node + "]"), Known));

    [Theory]
    [InlineData("plan", "{\"steps\":[{\"do\":\"fix_leaks\",\"ensure\":\"leaks == 0\",\"verify_timeout\":30}]}")]
    [InlineData("update_plan", "{\"node\":\"n1\",\"steps\":[{\"do\":\"fix_leaks\",\"ensure\":\"leaks == 0\"}]}")]
    [InlineData("append_plan", "{\"steps\":[{\"do\":\"fix_leaks\",\"ensure\":\"leaks == 0\"}]}")]
    [InlineData("edit_plan", "{\"operations\":[{\"op\":\"append\",\"node\":\"s0\",\"steps\":[{\"do\":\"fix_leaks\",\"ensure\":\"leaks == 0\"}]}]}")]
    public void EveryPlanManagementSchemaAdvertisesVerificationOnActions(string tool, string args) =>
        Assert.Null(Tools.ValidateArguments(new ToolCall(tool, Steps(args).AsObject())));

    [Theory]
    [InlineData("{\"steps\":[{\"do\":\"fix_leaks\",\"verify_timeout\":10}]}")]
    [InlineData("{\"steps\":[{\"do\":\"fix_leaks\",\"ensure\":1}]}")]
    [InlineData("{\"steps\":[{\"do\":\"fix_leaks\",\"ensure\":\"true\",\"verify_timeout\":0}]}")]
    [InlineData("{\"steps\":[{\"do\":\"fix_leaks\",\"ensure\":\"true\",\"verify_timeout\":301}]}")]
    [InlineData("{\"steps\":[{\"do\":\"fix_leaks\",\"ensure\":\"true\",\"verify_timeout\":\"10\"}]}")]
    public void WireSchemaRejectsInvalidVerificationTypesRangesAndDependency(string args) =>
        Assert.NotNull(Tools.ValidateArguments(new ToolCall("plan", Steps(args).AsObject())));
}
