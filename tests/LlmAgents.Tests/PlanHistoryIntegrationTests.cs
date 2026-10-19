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

public sealed class PlanHistoryIntegrationTests
{
    [Fact]
    public async Task CompletedPlanAndExactNodeOutcomesRemainVisibleAfterTheBotBecomesIdle()
    {
        using var h = new Harness();
        await h.Start();
        var id = h.Agent.Plan!.Id;
        h.Game.PendingReports.Add(new GameReport(1, "task complete with a literal ; [then] quote", h.TaskId, true));
        await h.Tick();
        Assert.Null(h.Agent.Plan);
        var details = h.Manager.Monitor(1).Details!;
        Assert.Null(details.ActivePlanId);
        var snapshot = details.Plan!;
        Assert.False(snapshot.Active);
        Assert.Equal(id, snapshot.PlanId);
        Assert.Equal("completed", snapshot.Status);
        Assert.Null(snapshot.CurrentNodeId);
        var node = Assert.Single(snapshot.Nodes, node => node.Kind == "action");
        Assert.Equal("completed", node.Status);
        Assert.True(node.LastOk);
        Assert.Equal("task complete with a literal ; [then] quote", node.Result);
        Assert.Equal("#123", JsonNode.Parse(node.ArgumentsJson!)!["item"]!.GetValue<string>());
        Assert.Single(details.PlanHistory);
    }

    [Fact]
    public async Task FailedPlanRetainsItsFailedLeafAndSkippedRemainderAfterRoutineReplies()
    {
        using var h = new Harness();
        await h.Start(withTail: true);
        h.Game.PendingReports.Add(new GameReport(1, "native failure: destination inaccessible", h.TaskId, false));
        await h.Tick();
        await h.Run(2); // carry_on must not displace a useful failure with an empty plan
        var snapshot = h.Manager.Monitor(1).Details!.Plan!;
        Assert.Equal("failed", snapshot.Status);
        Assert.False(snapshot.Active);
        Assert.Contains("destination inaccessible", snapshot.EndReason);
        Assert.Contains(snapshot.Nodes, node => node.Tool == "equip" && node.Status == "failed"
            && node.LastOk == false && node.Result!.Contains("destination inaccessible"));
        Assert.Contains(snapshot.Nodes, node => node.Tool == "repair_systems" && node.Status == "skipped");
    }

    [Fact]
    public async Task FailedCancellationDoesNotArchiveOrModifyTheActivePlan()
    {
        using var h = new Harness();
        await h.Start();
        var plan = h.Agent.Plan!;
        h.Game.CancelTaskError = "error: cleanup unavailable";
        Assert.StartsWith("error:", h.Manager.CancelPlan(h.Agent, "manual"));
        var details = h.Manager.Monitor(1).Details!;
        Assert.Same(plan, h.Agent.Plan);
        Assert.True(details.Plan!.Active);
        Assert.Single(details.PlanHistory); // only live plan, no false cancelled entry
        Assert.Empty(h.Agent.PlanHistory());

        h.Game.CancelTaskError = null;
        Assert.StartsWith("ok", h.Manager.CancelPlan(h.Agent, "manual"));
        details = h.Manager.Monitor(1).Details!;
        Assert.False(details.Plan!.Active);
        Assert.Equal("cancelled", details.Plan.Status);
        Assert.Equal("manual", details.Plan.EndReason);
        Assert.Contains(details.Plan.Nodes, node => node.Tool == "equip" && node.Status == "cancelled");
        Assert.Single(h.Agent.PlanHistory());
    }

    [Fact]
    public async Task ReplacementRetainsDetachedHistoryWhileTheNewPlanIsTheDefaultSelection()
    {
        using var h = new Harness();
        await h.Start();
        var old = h.Agent.Plan!;
        await h.Start();
        var active = h.Agent.Plan!;
        Assert.NotEqual(old.Id, active.Id);
        var details = h.Manager.Monitor(1).Details!;
        Assert.Equal(active.Id, details.ActivePlanId);
        Assert.Equal(active.Id, details.Plan!.PlanId);
        Assert.Equal(new[] { active.Id, old.Id }, details.PlanHistory.Select(plan => plan.PlanId));
        var archived = h.Manager.Monitor(1, historyPlanId: old.Id).Details!.Plan!;
        Assert.Equal("replaced", archived.Status);
        Assert.False(archived.Active);
        var elapsed = archived.ElapsedSeconds;
        archived.Nodes.Clear(); // returned UI data must not mutate retained history
        h.Clock.Advance(20);
        var reread = h.Manager.Monitor(1, historyPlanId: old.Id).Details!.Plan!;
        Assert.NotEmpty(reread.Nodes);
        Assert.Equal(elapsed, reread.ElapsedSeconds);
        Assert.True(reread.EndedAgoSeconds >= 20);
        Assert.Equal(active.Id, h.Agent.Plan!.Id);
    }

    [Fact]
    public async Task HistoryIsBoundedAndANoLongerRetainedSelectionDoesNotSilentlySwitchToAnotherPlan()
    {
        using var h = new Harness();
        string? first = null;
        for (int i = 0; i < BotAgent.MaxPlanHistory + 2; i++)
        {
            await h.Start();
            first ??= h.Agent.Plan!.Id;
            Assert.StartsWith("ok", h.Manager.CancelPlan(h.Agent, "cancel " + i));
        }
        var details = h.Manager.Monitor(1).Details!;
        Assert.Equal(BotAgent.MaxPlanHistory, details.PlanHistory.Count);
        Assert.Equal(BotAgent.MaxPlanHistory, h.Agent.PlanHistory().Count);
        var missing = h.Manager.Monitor(1, historyPlanId: first).Details!;
        Assert.Null(missing.Plan);
        Assert.NotNull(missing.PlanSelectionMessage);
    }

    [Fact]
    public async Task MenuCancellationIsRetainedAndReadingTheMonitorNeverAdvancesOrCancelsWork()
    {
        using var h = new Harness();
        await h.Start(withTail: true);
        var plan = h.Agent.Plan!;
        var before = plan.Tree.Snapshot().ToJsonString();
        var deadline = plan.WaitDeadline;
        var actions = h.Game.Actions.ToArray();
        for (int i = 0; i < 4; i++) h.Manager.Monitor(1);
        Assert.Equal(before, plan.Tree.Snapshot().ToJsonString());
        Assert.Equal(deadline, plan.WaitDeadline);
        Assert.Equal(actions, h.Game.Actions.ToArray());
        h.Game.PendingMenuOrders.Add(new MenuOrder(1, "Daniil", "repairsystems"));
        await h.Tick();
        var archived = h.Manager.Monitor(1).Details!.Plan!;
        Assert.False(archived.Active);
        Assert.Equal("cancelled", archived.Status);
        Assert.Contains("Daniil", archived.EndReason);
        Assert.DoesNotContain("1:simple repair_systems", h.Game.Actions);
    }

    [Fact]
    public async Task ResetDropsRoundHistoryAndOldEntityReferences()
    {
        using var h = new Harness();
        await h.Start();
        h.Manager.CancelPlan(h.Agent);
        Assert.NotEmpty(h.Agent.PlanHistory());
        h.Manager.Reset();
        h.Clock.Advance(1);
        await h.Tick();
        Assert.Empty(h.Agent.PlanHistory());
        Assert.Null(h.Manager.Monitor(1).Details!.Plan);
    }

    sealed class Harness : IDisposable
    {
        public readonly FakeClock Clock = new();
        public readonly FakeGame Game = new FakeGame().AddBot(1, "Sophie", "mechanic");
        readonly ConcurrentQueue<string> replies = new();
        public readonly AgentManager Manager;
        public BotAgent Agent => Manager.GetAgent(1)!;
        public long TaskId => Game.CurrentTaskId(1)!.Value;
        public Harness()
        {
            var settings = Fixtures.Settings();
            settings.Bots.Autonomy = "order";
            settings.Bots.ThinkAtRoundStart = false;
            settings.Bots.ReactToMenuOrders = false;
            settings.Global.MinSecondsBetweenCallsPerBot = 0;
            Manager = new AgentManager(settings, Game, new LlmRouter(settings, new FakeHandler(_ =>
                (HttpStatusCode.OK, replies.TryDequeue(out var reply) ? reply : Fixtures.OpenAiToolResponse(("carry_on", "{}")))), Clock.Utc), Clock.Get);
            Manager.Update();
            Clock.Advance(0.5);
        }
        public async Task Start(bool withTail = false)
        {
            string steps = "{\"steps\":[{\"do\":\"equip\",\"args\":{\"item\":\"#123\"}}"
                + (withTail ? ",{\"do\":\"repair_systems\",\"args\":{}}" : "") + "]}";
            replies.Enqueue(Fixtures.OpenAiToolResponse(("plan", steps)));
            Agent.ReceivePlayerOrder("Daniil: prepare tools", from: "Daniil");
            await Run(2);
            Assert.Equal(PlanWait.Task, Agent.Plan!.Waiting);
        }
        public async Task Tick() { Manager.Update(); await Manager.WhenIdle(); Clock.Advance(0.5); }
        public async Task Run(double seconds) { for (double elapsed = 0; elapsed < seconds; elapsed += 0.5) await Tick(); }
        public void Dispose() => Manager.Dispose();
    }
}
