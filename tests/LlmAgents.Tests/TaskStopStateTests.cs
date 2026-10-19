using System;
using System.Collections.Generic;
using LlmAgents.Agents;
using Xunit;

namespace LlmAgents.Tests;

public sealed class TaskStopStateTests
{
    [Fact]
    public void CommitsTerminalReportBeforeHooksAndReleasesOwnershipBeforeNativeHandoff()
    {
        var state = new TaskStopState();
        var seen = new List<(string Hook, bool Started, bool Pending, string? Report)>();
        Assert.False(state.Started);
        Assert.False(state.CleanupPending);
        Assert.Null(state.Report);

        string result = state.Finish("ok: delivered",
            () => seen.Add(("stopping", state.Started, state.CleanupPending, state.Report)),
            () => seen.Add(("clear", state.Started, state.CleanupPending, state.Report)),
            () => seen.Add(("finish", state.Started, state.CleanupPending, state.Report)));

        Assert.Equal("ok: delivered", result);
        Assert.Equal(new[]
        {
            ("stopping", true, true, (string?)"ok: delivered"),
            ("clear", true, true, (string?)"ok: delivered"),
            ("finish", true, false, (string?)"ok: delivered"),
        }, seen);
        Assert.True(state.Started);
        Assert.False(state.CleanupPending);
    }

    [Fact]
    public void TransientClearFailureRetriesOnlyCleanupAndKeepsOriginalReport()
    {
        var state = new TaskStopState();
        int stopping = 0, clearing = 0, finishing = 0;
        bool pendingDuringFinish = false;
        void Stop() => stopping++;
        void Clear()
        {
            clearing++;
            if (clearing == 1) throw new InvalidOperationException("native handoff interrupted");
        }
        void Finish() { finishing++; pendingDuringFinish = state.CleanupPending; }

        var failure = Assert.Throws<InvalidOperationException>(() => state.Finish("cancelled", Stop, Clear, Finish));
        Assert.Equal("native handoff interrupted", failure.Message);
        Assert.True(state.Started);
        Assert.True(state.CleanupPending);
        Assert.True(pendingDuringFinish);
        Assert.Equal("cancelled", state.Report);
        Assert.Equal((1, 1, 1), (stopping, clearing, finishing));

        Assert.Equal("cancelled", state.Finish("failed: retry must not change report", Stop, Clear, Finish));
        Assert.False(state.CleanupPending);
        Assert.Equal((1, 2, 1), (stopping, clearing, finishing));
        Assert.Equal("cancelled", state.Finish("ok: replacement work", Stop, Clear, Finish));
        Assert.Equal((1, 2, 1), (stopping, clearing, finishing));
    }

    [Fact]
    public void RepeatedClearFailuresKeepCleanupPendingUntilOneClearSucceeds()
    {
        var state = new TaskStopState();
        int stopping = 0, clearing = 0, finishing = 0;
        void Stop() => stopping++;
        void Clear() { if (++clearing < 3) throw new InvalidOperationException("character temporarily unavailable"); }
        void Finish() => finishing++;

        Assert.Throws<InvalidOperationException>(() => state.Finish("failed: original", Stop, Clear, Finish));
        Assert.True(state.CleanupPending);
        Assert.Throws<InvalidOperationException>(() => state.Finish("cancelled", Stop, Clear, Finish));
        Assert.True(state.CleanupPending);
        Assert.Equal("failed: original", state.Report);
        Assert.Equal((1, 2, 1), (stopping, clearing, finishing));

        Assert.Equal("failed: original", state.Finish("different", Stop, Clear, Finish));
        Assert.False(state.CleanupPending);
        Assert.Equal((1, 3, 1), (stopping, clearing, finishing));
    }

    [Fact]
    public void StoppingFailureStillClearsNativeObjectiveAndFinishesOnce()
    {
        var state = new TaskStopState();
        var hooks = new List<string>();
        void Stop() { hooks.Add("stopping"); throw new InvalidOperationException("grip release failed"); }
        void Clear() => hooks.Add("clear");
        void Finish() => hooks.Add("finish");

        var failure = Assert.Throws<InvalidOperationException>(() => state.Finish("cancelled", Stop, Clear, Finish));
        Assert.Equal("grip release failed", failure.Message);
        Assert.Equal(new[] { "stopping", "clear", "finish" }, hooks);
        Assert.False(state.CleanupPending);
        Assert.Equal("cancelled", state.Finish("retry", Stop, Clear, Finish));
        Assert.Equal(new[] { "stopping", "clear", "finish" }, hooks);
    }

    [Fact]
    public void FinalHookFailureIsBestEffortAndDoesNotReplaceSuccessfulReport()
    {
        var state = new TaskStopState();
        int finishing = 0;
        void Finish() { finishing++; throw new InvalidOperationException("removed reservation owner"); }

        Assert.Equal("ok", state.Finish("ok", () => { }, () => { }, Finish));
        Assert.False(state.CleanupPending);
        Assert.Equal("ok", state.Finish("cancelled", () => { }, () => { }, Finish));
        Assert.Equal(1, finishing);
    }

    [Fact]
    public void FinalHookFailureCannotHideNativeCleanupFailureOrPreventRetry()
    {
        var state = new TaskStopState();
        int clearing = 0, finishing = 0;
        void Clear() { if (++clearing == 1) throw new InvalidOperationException("clear failed"); }
        void Finish() { finishing++; throw new ArgumentException("finish also failed"); }

        var failure = Assert.Throws<InvalidOperationException>(() => state.Finish("cancelled", () => { }, Clear, Finish));
        Assert.Equal("clear failed", failure.Message);
        Assert.True(state.CleanupPending);
        Assert.Equal("cancelled", state.Finish("another report", () => { }, Clear, Finish));
        Assert.False(state.CleanupPending);
        Assert.Equal(1, finishing);
        Assert.Equal(2, clearing);
    }

    [Fact]
    public void RepeatedFinishAfterSuccessNeverTouchesReplacementObjective()
    {
        var state = new TaskStopState();
        Assert.Equal("ok", state.Finish("ok", () => { }, () => { }, () => { }));
        void MustNotRun() => throw new InvalidOperationException("would touch replacement objective");
        for (int i = 0; i < 10; i++)
            Assert.Equal("ok", state.Finish("cancelled", MustNotRun, MustNotRun, MustNotRun));
        Assert.False(state.CleanupPending);
    }

    [Fact]
    public void ParentAndChildCancellationRetryOnlyPendingNativeCleanup()
    {
        var parent = new TaskStopState();
        var child = new TaskStopState();
        int parentStopping = 0, parentClearing = 0, parentFinishing = 0;
        int childStopping = 0, childClearing = 0, childFinishing = 0;
        void StopChild() => childStopping++;
        void ClearChild()
        {
            if (++childClearing == 1) throw new InvalidOperationException("child native handoff interrupted");
        }
        void FinishChild() => childFinishing++;
        void StopParent() => parentStopping++;
        void ClearOwnedWork()
        {
            parentClearing++;
            child.Finish("cancelled", StopChild, ClearChild, FinishChild);
        }
        void FinishParent() => parentFinishing++;

        var failure = Assert.Throws<InvalidOperationException>(() =>
            parent.Finish("failed: parent operation stopped", StopParent, ClearOwnedWork, FinishParent));
        Assert.Equal("child native handoff interrupted", failure.Message);
        Assert.True(parent.Started);
        Assert.True(child.Started);
        Assert.True(parent.CleanupPending);
        Assert.True(child.CleanupPending);
        Assert.Equal((1, 1, 1), (parentStopping, parentClearing, parentFinishing));
        Assert.Equal((1, 1, 1), (childStopping, childClearing, childFinishing));

        Assert.Equal("failed: parent operation stopped", parent.Finish("cancelled", StopParent, ClearOwnedWork, FinishParent));
        Assert.False(parent.CleanupPending);
        Assert.False(child.CleanupPending);
        Assert.Equal("failed: parent operation stopped", parent.Report);
        Assert.Equal("cancelled", child.Report);
        Assert.Equal((1, 2, 1), (parentStopping, parentClearing, parentFinishing));
        Assert.Equal((1, 2, 1), (childStopping, childClearing, childFinishing));

        Assert.Equal("failed: parent operation stopped", parent.Finish("ok: unrelated replacement", StopParent, ClearOwnedWork, FinishParent));
        Assert.Equal((1, 2, 1), (parentStopping, parentClearing, parentFinishing));
        Assert.Equal((1, 2, 1), (childStopping, childClearing, childFinishing));
    }
}
