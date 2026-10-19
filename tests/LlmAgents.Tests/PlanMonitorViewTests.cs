using System;
using System.Collections.Generic;
using System.Linq;
using LlmAgents.SettingsUi;
using Xunit;

namespace LlmAgents.Tests;

public sealed class PlanMonitorViewTests
{
    static MonitorPlanSnapshot Tree() => new()
    {
        PlanId = "p1", Revision = 2, Status = "running", CurrentNodeId = "n5", RootNodeId = "s0",
        Nodes = new()
        {
            Node("s0", null, "sequence"), Node("n1", "s0", "if"), Node("s1", "n1", "sequence"), Node("n2", "s1", "action"),
            Node("s2", "n1", "sequence"), Node("n3", "s2", "wait"), Node("n4", "s0", "repeat"),
            Node("s3", "n4", "sequence"), Node("n5", "s3", "action"),
        },
    };
    static MonitorPlanNode Node(string id, string? parent, string kind) => new() { Id = id, ParentId = parent, Kind = kind, Status = "pending" };

    [Fact]
    public void CollapsingAControlNodeHidesItsWholeSubtreeAndPreservesSiblingOrder()
    {
        var tree = Tree();
        var visible = PlanMonitorView.VisibleNodes(tree, new HashSet<string> { "n1" });
        Assert.Equal(new[] { "s0", "n1", "n4", "s3", "n5" }, visible.Select(n => n.Id).ToArray());
        Assert.True(PlanMonitorView.HasChildren(tree, "n1"));
        Assert.True(PlanMonitorView.HasChildren(tree, "s1"));
        Assert.False(PlanMonitorView.HasChildren(tree, "n2"));
        Assert.False(PlanMonitorView.HasChildren(tree, "missing"));
    }

    [Fact]
    public void RevealExpandsOnlyTheSelectedNodesAncestors()
    {
        var tree = Tree();
        var collapsed = new HashSet<string> { "s0", "n1", "s1", "n4" };
        PlanMonitorView.Reveal(tree, "n2", collapsed);
        Assert.Equal(new[] { "n4" }, collapsed.ToArray());
        var visible = PlanMonitorView.VisibleNodes(tree, collapsed);
        Assert.Contains(visible, n => n.Id == "n2");
        Assert.DoesNotContain(visible, n => n.Id == "n5");
        PlanMonitorView.Reveal(tree, "missing", collapsed);
        Assert.Equal(new[] { "n4" }, collapsed.ToArray());
    }

    [Fact]
    public void HiddenSelectionMovesToNearestVisibleAncestorAndMissingSelectionFollowsCurrent()
    {
        var tree = Tree();
        var visible = PlanMonitorView.VisibleNodes(tree, new HashSet<string> { "n1" });
        Assert.Equal("n1", PlanMonitorView.SelectVisible(tree, visible, "n2"));
        Assert.Equal("n5", PlanMonitorView.SelectVisible(tree, visible, "missing"));
        Assert.Equal("n4", PlanMonitorView.SelectVisible(tree, visible, "n4"));
        visible = PlanMonitorView.VisibleNodes(tree, new HashSet<string> { "s0" });
        Assert.Equal("s0", PlanMonitorView.SelectVisible(tree, visible, "n5"));
        Assert.Null(PlanMonitorView.SelectVisible(tree, Array.Empty<MonitorPlanNode>(), "n5"));
    }

    [Fact]
    public void CorruptParentCyclesDuplicatesAndMissingParentsCannotTrapTreeNavigation()
    {
        var tree = new MonitorPlanSnapshot
        {
            Nodes = new() { Node("a", "b", "sequence"), Node("b", "a", "sequence"), Node("c", "missing", "action"), Node("a", "b", "sequence") },
        };
        var collapsed = new HashSet<string>();
        Assert.Equal(4, PlanMonitorView.VisibleNodes(tree, collapsed).Count);
        collapsed.Add("a"); collapsed.Add("b");
        PlanMonitorView.Reveal(tree, "b", collapsed);
        Assert.DoesNotContain("a", collapsed);
        Assert.Contains("b", collapsed);
        var visible = PlanMonitorView.VisibleNodes(tree, collapsed);
        Assert.Contains(visible, n => n.Id == "c");
        Assert.NotNull(PlanMonitorView.SelectVisible(tree, visible, "a"));
    }

    [Theory]
    [InlineData(true, false, false, true)]
    [InlineData(false, false, false, false)]
    [InlineData(true, true, false, false)]
    [InlineData(true, false, true, false)]
    public void CancellationRequiresPermissionLiveDataAndAnUnpausedMonitor(bool permitted, bool paused, bool stale, bool expected) =>
        Assert.Equal(expected, PlanMonitorView.CanCancel(permitted, paused, stale, "p1", Tree()));

    [Fact]
    public void HistoryOtherPlanIdsMissingRevisionAndTerminalPlansCannotBeCancelled()
    {
        var tree = Tree();
        Assert.False(PlanMonitorView.CanCancel(true, false, false, "other", tree));
        Assert.False(PlanMonitorView.CanCancel(true, false, false, null, tree));
        Assert.False(PlanMonitorView.CanCancel(true, false, false, "p1", null));
        tree.Active = false;
        Assert.False(PlanMonitorView.CanCancel(true, false, false, "p1", tree));
        tree.Active = true; tree.Revision = 0;
        Assert.False(PlanMonitorView.CanCancel(true, false, false, "p1", tree));
        tree.Revision = 2; tree.Status = "failed";
        Assert.False(PlanMonitorView.CanCancel(true, false, false, "p1", tree));
    }

    [Theory]
    [InlineData(0.5f, 100, 200, 1000, 0.6f)]
    [InlineData(0.9f, 0, 500, 100, 1)]
    [InlineData(0.1f, 500, 0, 100, 0)]
    [InlineData(0.5f, 100, 200, 0, 0)]
    [InlineData(0.5f, 100, 200, -1, 0)]
    public void RefreshPreservesTheAnchorAndClampsScrollToItsRealRange(float original, float oldOffset, float newOffset, float overflow, float expected) =>
        Assert.InRange(Math.Abs(PlanMonitorView.RestoreScroll(original, oldOffset, newOffset, overflow) - expected), 0, 0.00001f);
}
