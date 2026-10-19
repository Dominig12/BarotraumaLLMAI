using LlmAgents.Agents;
using Xunit;

namespace LlmAgents.Tests;

public class TaskReportCorrelationTests
{
    [Fact]
    public void EveryStartAndReplacementGetsANewTaskIdentity()
    {
        var game = new FakeGame().AddBot(1, "Ann", "assistant").AddBot(2, "Bob", "engineer");
        Assert.Null(game.CurrentTaskId(1));
        game.Equip(1, "wrench");
        long first = game.CurrentTaskId(1)!.Value;
        game.Craft(1, "bandage", 1);
        long replacement = game.CurrentTaskId(1)!.Value;
        game.RefuelReactor(2);
        long otherBot = game.CurrentTaskId(2)!.Value;
        game.CancelCurrentTask(1);
        game.TakeItem(1, "bandage", 1);
        long afterCancellation = game.CurrentTaskId(1)!.Value;

        Assert.True(first < replacement && replacement < otherBot && otherBot < afterCancellation);
    }

    [Fact]
    public void StaleCompletionDoesNotRemoveTheReplacementTask()
    {
        var game = new FakeGame().AddBot(1, "Ann", "assistant");
        game.Equip(1, "wrench");
        long oldId = game.CurrentTaskId(1)!.Value;
        game.Craft(1, "bandage", 1);
        long replacement = game.CurrentTaskId(1)!.Value;
        game.PendingReports.Add(new GameReport(1, "task 'equip wrench' ok", oldId, true));

        var report = Assert.Single(game.DrainReports());

        Assert.Equal(oldId, report.TaskId);
        Assert.True(report.Success);
        Assert.Equal(replacement, game.CurrentTaskId(1));
    }

    [Fact]
    public void LegacyFixtureIsCorrelatedWithoutParsingFailureWordsInTheItemName()
    {
        var game = new FakeGame().AddBot(1, "Ann", "assistant");
        game.Equip(1, "failed relay");
        long taskId = game.CurrentTaskId(1)!.Value;
        game.PendingReports.Add(new GameReport(1, "task 'equip failed relay' ok: taken cancelled circuit"));

        var report = Assert.Single(game.DrainReports());

        Assert.Equal(taskId, report.TaskId);
        Assert.True(report.Success);
        Assert.Null(game.CurrentTaskId(1));
    }

    [Fact]
    public void ExplicitTypedOutcomeIsIndependentOfTheHumanReadableText()
    {
        var game = new FakeGame().AddBot(1, "Ann", "assistant");
        game.Equip(1, "wrench");
        long taskId = game.CurrentTaskId(1)!.Value;
        game.PendingReports.Add(new GameReport(1, "ok-looking diagnostic text", taskId, false));

        Assert.False(Assert.Single(game.DrainReports()).Success);
        Assert.Null(game.CurrentTaskId(1));
    }

    [Fact]
    public void CancellationIsQuietAndLeavesOtherBotsTasksAlone()
    {
        var game = new FakeGame().AddBot(1, "Ann", "assistant").AddBot(2, "Bob", "engineer");
        game.Equip(1, "wrench");
        game.RefuelReactor(2);
        long otherBot = game.CurrentTaskId(2)!.Value;

        Assert.StartsWith("ok", game.CancelCurrentTask(1));
        Assert.Null(game.CurrentTaskId(1));
        Assert.Equal(otherBot, game.CurrentTaskId(2));
        Assert.Empty(game.DrainReports());
        Assert.DoesNotContain(game.Actions, action => action.Contains("dismiss") || action.Contains("end travel"));
    }

    [Fact]
    public void RejectedCancellationKeepsTheCurrentTaskIdentity()
    {
        var game = new FakeGame { CancelTaskError = "error: cancellation is unavailable" }.AddBot(1, "Ann", "assistant");
        game.Equip(1, "wrench");
        long taskId = game.CurrentTaskId(1)!.Value;

        Assert.StartsWith("error:", game.CancelCurrentTask(1));
        Assert.Equal(taskId, game.CurrentTaskId(1));
        Assert.Empty(game.DrainReports());
    }
}
