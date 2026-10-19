using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using LlmAgents.Agents;
using Xunit;

namespace LlmAgents.Tests;

public sealed class PlanNodeOutcomeTests
{
    static readonly string[] Known = Tools.All.Select(t => t.Name).ToArray();
    sealed class Context : IConditionContext
    {
        public double Health;
        public double Number(string name) => name == "health" ? Health : 0;
        public double Call(string name, string arg) => 0;
    }
    readonly Context ctx = new();
    static PlanTree Tree(string json) => PlanTree.Build(JsonNode.Parse(json), Known);
    PlanTree.ActAction Next(PlanTree tree) => Assert.IsType<PlanTree.ActAction>(tree.Next(ctx));
    static IEnumerable<JsonObject> Nodes(JsonObject node)
    {
        yield return node;
        foreach (var field in node)
        {
            if (field.Key == "steps" && field.Value is JsonArray children)
                foreach (var child in children.OfType<JsonObject>()) foreach (var next in Nodes(child)) yield return next;
            else if (field.Key is "then" or "else" or "repeat" or "try" or "catch" && field.Value is JsonObject branch)
                foreach (var next in Nodes(branch)) yield return next;
        }
    }
    static JsonObject Node(PlanTree tree, string id) => Nodes(tree.Snapshot()["root"]!.AsObject()).Single(n => n["id"]!.GetValue<string>() == id);
    static JsonArray Say(string text) => new(new JsonObject { ["do"] = "say", ["args"] = new JsonObject { ["text"] = text } });

    [Fact]
    public void ForkRetainsConfirmedOutcomesWithoutSharingMutableSnapshotData()
    {
        var tree = Tree("""[{"do":"say","args":{"text":"first"}},{"do":"fix_leaks"}]""");
        var first = Next(tree); tree.Report(true, "first confirmed");
        Next(tree);
        var copy = tree.Fork();
        Assert.Equal(tree.Snapshot().ToJsonString(), copy.Snapshot().ToJsonString());
        var inspected = Node(copy, first.NodeId);
        inspected["result"] = "UI mutation";
        Assert.Equal("first confirmed", Node(tree, first.NodeId)["result"]!.GetValue<string>());
        Assert.Equal("first confirmed", Node(copy, first.NodeId)["result"]!.GetValue<string>());
        copy.Report(false, "copy's current step failed");
        Assert.Null(Node(tree, tree.CurrentId!)["ok"]);
        Assert.True(Node(copy, first.NodeId)["ok"]!.GetValue<bool>());
    }

    [Fact]
    public void FutureEditPreservesOutcomesAndRejectedTransactionChangesNothing()
    {
        var tree = Tree("""[{"do":"say","args":{"text":"first"}},{"do":"equip","args":{"item":"#123"}},{"do":"say","args":{"text":"future"}}]""");
        var first = Next(tree); tree.Report(true, "completed effect");
        Next(tree);
        var futureId = tree.Root.Items[^1].Id;
        Assert.True(tree.Edit(new[] { new PlanEdit("replace", futureId, Say("edited future")) }, Known).Success);
        Assert.True(Node(tree, first.NodeId)["ok"]!.GetValue<bool>());
        Assert.Equal("completed effect", Node(tree, first.NodeId)["result"]!.GetValue<string>());
        var before = tree.Snapshot().ToJsonString();
        var failed = tree.Edit(new[] { new PlanEdit("append", "s0", Say("should roll back")), new PlanEdit("remove", "missing") }, Known);
        Assert.False(failed.Success);
        Assert.Equal(before, tree.Snapshot().ToJsonString());
    }

    [Fact]
    public void ReplacingRootDropsOldLeafOutcomesAndNewNodesBeginWithoutResults()
    {
        var tree = Tree("""[{"do":"say","args":{"text":"old"}},{"do":"fix_leaks"}]""");
        Next(tree); tree.Report(true, "old confirmed effect");
        Next(tree);
        Assert.True(tree.Edit(new[] { new PlanEdit("replace", "s0", Say("new")) }, Known).Success);
        Assert.All(Nodes(tree.Snapshot()["root"]!.AsObject()), n =>
        {
            Assert.False(n.ContainsKey("ok"));
            Assert.False(n.ContainsKey("result"));
        });
    }

    [Fact]
    public void NextLoopIterationClearsPriorResultsInsteadOfDisplayingAStaleSuccess()
    {
        var tree = Tree("""[{"repeat":[{"do":"fix_leaks"}],"max":2}]""");
        var first = Next(tree); tree.Report(true, "first round confirmed");
        Assert.True(Node(tree, first.NodeId)["ok"]!.GetValue<bool>());
        var second = Next(tree);
        Assert.Equal(first.NodeId, second.NodeId);
        Assert.False(Node(tree, second.NodeId).ContainsKey("ok"));
        Assert.False(Node(tree, second.NodeId).ContainsKey("result"));
        tree.Report(false, "second round failed");
        Assert.False(Node(tree, second.NodeId)["ok"]!.GetValue<bool>());
        Assert.Equal("second round failed", Node(tree, second.NodeId)["result"]!.GetValue<string>());
    }

    [Fact]
    public void ActionAndImmediateWaitOutcomesAreBoundedWithoutTruncatingTheActualLastResult()
    {
        var tree = Tree("""[{"do":"say","args":{"text":"long outcome"}}]""");
        var action = Next(tree);
        string report = new('x', 5000);
        tree.Report(true, report);
        Assert.Equal(1000, Node(tree, action.NodeId)["result"]!.GetValue<string>().Length);
        Assert.Equal(report, tree.LastResult);

        string condition = "true or result('" + new string('y', 1400) + "')";
        var waitTree = PlanTree.Build(new JsonArray(new JsonObject { ["wait_until"] = condition }), Known);
        string waitId = waitTree.Root.Items[0].Id;
        Assert.IsType<PlanTree.DoneAction>(waitTree.Next(ctx));
        Assert.True(Node(waitTree, waitId)["ok"]!.GetValue<bool>());
        Assert.Equal(1000, Node(waitTree, waitId)["result"]!.GetValue<string>().Length);
        Assert.Equal("condition satisfied: " + condition, waitTree.LastResult);
    }

    [Fact]
    public void ResumedAndTimedOutWaitsHaveTheirOwnOutcomes()
    {
        var tree = Tree("""[{"wait_until":"health > 50"}]""");
        var wait = Assert.IsType<PlanTree.WaitAction>(tree.Next(ctx));
        Assert.False(Node(tree, wait.Node.Id).ContainsKey("ok"));
        ctx.Health = 100;
        Assert.True(tree.TryResume(ctx));
        Assert.True(Node(tree, wait.Node.Id)["ok"]!.GetValue<bool>());
        Assert.Equal("condition satisfied: health > 50", Node(tree, wait.Node.Id)["result"]!.GetValue<string>());

        ctx.Health = 0;
        var timedOut = Tree("""[{"try":[{"wait_until":"health > 50"}],"catch":[{"do":"say","args":{"text":"recover"}}]}]""");
        var pending = Assert.IsType<PlanTree.WaitAction>(timedOut.Next(ctx));
        Assert.Null(timedOut.Report(false, "wait timed out"));
        Assert.False(Node(timedOut, pending.Node.Id)["ok"]!.GetValue<bool>());
        Assert.Equal("wait timed out", Node(timedOut, pending.Node.Id)["result"]!.GetValue<string>());
        Assert.Equal("say", Next(timedOut).Call.Name);
    }

    [Fact]
    public void VerificationDoesNotCreateASuccessOutcomeUntilThePostconditionIsReported()
    {
        var tree = Tree("""[{"do":"equip","args":{"item":"#123"},"ensure":"false"}]""");
        var action = Next(tree);
        tree.MarkVerifying();
        Assert.False(Node(tree, action.NodeId).ContainsKey("ok"));
        Assert.False(Node(tree, action.NodeId).ContainsKey("result"));
        tree.Report(false, "postcondition failed");
        Assert.False(Node(tree, action.NodeId)["ok"]!.GetValue<bool>());
        Assert.Equal("postcondition failed", Node(tree, action.NodeId)["result"]!.GetValue<string>());
    }
}
