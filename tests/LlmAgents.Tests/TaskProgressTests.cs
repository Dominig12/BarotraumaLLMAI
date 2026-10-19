using System;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using LlmAgents.Agents;
using LlmAgents.Llm;
using LlmAgents.SettingsUi;
using Xunit;

namespace LlmAgents.Tests;

public class TaskProgressTests
{
    static GameTaskProgress Blocked(long id = 7, double noProgress = 45) =>
        new(id, "blocked", "waiting for machine power", 90, noProgress, "no_power", "Fabricator #123 has no power", 2, 5);

    [Theory]
    [InlineData("running", true)]
    [InlineData("waiting", true)]
    [InlineData("blocked", true)]
    [InlineData("cleanup", false)]
    [InlineData("unknown", false)]
    public void PersistentExplicitBlockerNeedsReviewOnlyForAnOrdinaryTaskPhase(string phase, bool expected) =>
        Assert.Equal(expected, TaskProgressRules.NeedsReview(Blocked() with { Phase = phase }, 7));

    [Theory]
    [InlineData(0, false)]
    [InlineData(44.999, false)]
    [InlineData(45, true)]
    [InlineData(90, true)]
    [InlineData(-1, false)]
    [InlineData(double.NaN, false)]
    [InlineData(double.PositiveInfinity, false)]
    [InlineData(double.NegativeInfinity, false)]
    public void ReviewUsesTheMeaningfulProgressClockAndAFiniteThreshold(double seconds, bool expected) =>
        Assert.Equal(expected, TaskProgressRules.NeedsReview(Blocked(noProgress: seconds), 7));

    [Fact]
    public void MissingMismatchedOrUncorrelatedProgressCannotReviewAnotherTask()
    {
        Assert.False(TaskProgressRules.NeedsReview(null, 7));
        Assert.False(TaskProgressRules.NeedsReview(Blocked(), null));
        Assert.False(TaskProgressRules.NeedsReview(Blocked(), 8));
        Assert.False(TaskProgressRules.NeedsReview(Blocked(id: 0), 0));
        Assert.False(TaskProgressRules.NeedsReview(Blocked(id: -1), -1));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void StationaryWorkAndHumanDescriptionsNeverInventAnExplicitBlocker(string? code)
    {
        var progress = Blocked(noProgress: 3600) with { Phase = "running", BlockerCode = code, Step = "failed, blocked, no power", Blocker = "looks stuck" };
        Assert.False(TaskProgressRules.NeedsReview(progress, 7));
    }

    [Fact]
    public void ChangingDisplayTextDoesNotResetTheProgressClockOrChangeTheReviewDecision()
    {
        var progress = Blocked();
        var renamed = progress with { Step = "an item called 'ok'", Blocker = "a different explanation" };
        Assert.True(TaskProgressRules.NeedsReview(progress, 7));
        Assert.True(TaskProgressRules.NeedsReview(renamed, 7));
        Assert.Equal(45, renamed.NoProgressSeconds);
        Assert.Equal(progress, progress with { });
    }

    [Fact]
    public void ConfirmedCountsCannotPretendTheEntireTaskFinishedOrSuppressABlocker()
    {
        var progress = Blocked() with { CompletedUnits = 5, TotalUnits = 5, BlockerCode = "output_full", Blocker = "products await collection" };
        Assert.True(TaskProgressRules.NeedsReview(progress, 7));
        Assert.Contains("confirmed 5/5", TaskProgressRules.Describe(progress));
    }

    [Fact]
    public void TypedObservationRetainsTaskIdentityCountsAndBlockerInFullCompactAndHistoryForms()
    {
        var observation = new Observation { Name = "Larissa", CurrentTask = "craft", TaskProgress = Blocked() };
        foreach (var json in new[] { observation.ToJson(), observation.ToCompactJson() })
        {
            var progress = JsonNode.Parse(json)!["taskProgress"]!;
            Assert.Equal(7, progress["taskId"]!.GetValue<long>());
            Assert.Equal("blocked", progress["phase"]!.GetValue<string>());
            Assert.Equal("no_power", progress["blockerCode"]!.GetValue<string>());
            Assert.Equal(2, progress["completedUnits"]!.GetValue<int>());
            Assert.Equal(5, progress["totalUnits"]!.GetValue<int>());
        }
        var back = JsonSerializer.Deserialize<Observation>(observation.ToJson(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Assert.Equal(observation.TaskProgress, back.TaskProgress);
        Assert.Contains("task #7 blocked", observation.HistorySummary());
        Assert.Contains("confirmed 2/5", observation.HistorySummary());
        Assert.Contains("no_power", observation.HistorySummary());
    }

    [Fact]
    public void OptionalProgressPreservesUnknownCountsAndZeroElapsedTimeInsteadOfInventingValues()
    {
        var observation = new Observation { TaskProgress = new GameTaskProgress(1, "running", "starting", 0, 0) };
        var progress = JsonNode.Parse(observation.ToCompactJson())!["taskProgress"]!;
        Assert.Equal(0, progress["elapsedSeconds"]!.GetValue<double>());
        Assert.Equal(0, progress["noProgressSeconds"]!.GetValue<double>());
        Assert.Null(progress["completedUnits"]);
        Assert.Null(progress["totalUnits"]);
        Assert.Null(progress["blockerCode"]);
        Assert.Null(JsonNode.Parse(new Observation().ToCompactJson())!["taskProgress"]);
    }

    [Fact]
    public void FakeBackendSnapshotsAreImmutableAndAnOldReportDoesNotEraseCurrentProgress()
    {
        var game = new FakeGame().AddBot(1, "Sophie", "mechanic");
        game.Equip(1, "wrench");
        var current = game.CurrentTaskId(1)!.Value;
        var progress = Blocked(current);
        game.TaskProgressData[1] = progress;
        var observation = game.Observe(1);
        Assert.Equal(progress, observation.TaskProgress);
        game.TaskProgressData[1] = progress with { CompletedUnits = 3 };
        Assert.Equal(2, observation.TaskProgress!.CompletedUnits);
        game.PendingReports.Add(new GameReport(1, "old report", current + 1, true));
        game.DrainReports();
        Assert.Equal(current, game.CurrentTaskId(1));
        Assert.Equal(3, game.TaskProgress(1)!.CompletedUnits);
        game.CancelCurrentTask(1);
        Assert.Null(game.TaskProgress(1));
    }

    [Fact]
    public async Task MonitorMapsLiveTypedProgressAndPreservesItAcrossTheIndependentClientWire()
    {
        var clock = new FakeClock();
        var game = new FakeGame().AddBot(1, "Sophie", "mechanic");
        var settings = Fixtures.Settings();
        using var manager = new AgentManager(settings, game, new LlmRouter(settings,
            new FakeHandler(_ => (HttpStatusCode.OK, Fixtures.OpenAiToolResponse(("carry_on", "{}")))), clock.Utc), clock.Get);
        manager.Update();
        await manager.WhenIdle();
        game.Equip(1, "wrench");
        var id = game.CurrentTaskId(1)!.Value;
        var progress = Blocked(id);
        game.TaskProgressData[1] = progress;
        var actions = game.Actions.ToArray();
        var snapshot = manager.Monitor(1);
        var details = snapshot.Details!.TaskProgress!;
        Assert.Equal(id, details.TaskId);
        Assert.Equal("no_power", details.BlockerCode);
        Assert.Equal(2, details.CompletedUnits);
        Assert.Equal(5, details.TotalUnits);
        Assert.Equal(45, details.NoProgressSeconds);
        Assert.Equal(details.Blocker, snapshot.Bots.Single().TaskProgress!.Blocker);
        var copy = SettingsNet.Deserialize<MonitorSnapshot>(SettingsNet.Serialize(snapshot))!;
        Assert.Equal(id, copy.Details!.TaskProgress!.TaskId);
        Assert.Equal("blocked", copy.Details.TaskProgress.Phase);
        Assert.Equal("no_power", copy.Bots.Single().TaskProgress!.BlockerCode);
        Assert.Equal(progress, game.TaskProgress(1));
        Assert.Equal(actions, game.Actions.ToArray());
    }

    [Fact]
    public async Task MonitorDistinguishesNativeSuccessFromAnUnverifiedActualResult()
    {
        var clock = new FakeClock();
        var game = new FakeGame().AddBot(1, "Sophie", "mechanic", observation => observation.InventoryItems = new());
        var settings = Fixtures.Settings();
        const string steps = "{\"steps\":[{\"do\":\"take_item\",\"args\":{\"item\":\"wrench\"},\"ensure\":\"count('wrench') > 0\",\"verify_timeout\":30}]}";
        using var manager = new AgentManager(settings, game, new LlmRouter(settings,
            new FakeHandler(_ => (HttpStatusCode.OK, Fixtures.OpenAiToolResponse(("plan", steps)))), clock.Utc), clock.Get);
        for (int tick = 0; tick < 4; tick++)
        {
            manager.Update();
            await manager.WhenIdle();
            clock.Advance(0.5);
        }
        game.PendingReports.Add(new GameReport(1, "task 'take wrench' ok: backend accepted", game.CurrentTaskId(1), true));
        manager.Update();
        await manager.WhenIdle();
        var snapshot = manager.Monitor(1);
        Assert.Equal("verifying", snapshot.Bots.Single().State);
        Assert.Equal("count('wrench') > 0", snapshot.Details!.VerificationCondition);
        Assert.Contains("backend accepted", snapshot.Details.VerificationNativeResult);
        Assert.Contains("verifying actual result", snapshot.Details.PlanNow);
        Assert.Equal(30, snapshot.Details.VerificationRemainingSeconds);
        var copy = SettingsNet.Deserialize<MonitorSnapshot>(SettingsNet.Serialize(snapshot))!;
        Assert.Equal(snapshot.Details.VerificationCondition, copy.Details!.VerificationCondition);
        Assert.Equal(snapshot.Details.VerificationNativeResult, copy.Details.VerificationNativeResult);
        Assert.Equal(snapshot.Details.VerificationRemainingSeconds, copy.Details.VerificationRemainingSeconds);
        Assert.Equal(PlanWait.Verification, manager.GetAgent(1)!.Plan!.Waiting);
    }
}
