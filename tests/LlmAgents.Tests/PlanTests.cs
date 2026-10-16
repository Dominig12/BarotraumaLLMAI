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

/// <summary>Multi-step replies run as a plan, step by step; goals survive between turns.</summary>
public class PlanTests
{
    readonly FakeClock clock = new();
    readonly FakeGame game = new();
    readonly ConcurrentQueue<(JsonObject Obs, string User)> seen = new();
    Func<JsonObject, string> reply = _ => Fixtures.OpenAiToolResponse(("wait", "{}"));

    AgentManager Create(string global = "")
    {
        var settings = Fixtures.Settings(@", ""maxActionsPerDecision"": 6, ""maxPlanSeconds"": 120, ""planStepTimeoutSeconds"": 30" + global);
        var handler = new FakeHandler(body =>
        {
            var user = body["messages"]!.AsArray()[^1]!["content"]!.GetValue<string>();
            var obs = (JsonObject)JsonNode.Parse(Fixtures.Obs(user))!;
            seen.Enqueue((obs, user));
            return (HttpStatusCode.OK, reply(obs));
        });
        return new AgentManager(settings, game, new LlmRouter(settings, handler, clock.Utc), clock.Get);
    }

    async Task Step(AgentManager mgr, double seconds = 0.5)
    {
        mgr.Update();
        await mgr.WhenIdle();
        clock.Advance(seconds);
    }

    async Task Run(AgentManager mgr, double seconds)
    {
        for (double t = 0; t < seconds; t += 0.5) await Step(mgr);
    }

    List<string> Actions() => game.Actions.ToList();

    static string Plan(params (string, string)[] calls) => Fixtures.OpenAiToolResponse(calls);

    [Fact]
    public async Task StepsRunOneAfterAnotherWithoutAskingAgain()
    {
        game.AddBot(1, "Ann", "mechanic");
        var mgr = Create();
        int n = 0;
        reply = _ => n++ == 0
            ? Plan(("equip", "{\"item\":\"diving suit\"}"), ("go_to_room", "{\"room\":\"Command Room [h11]\"}"),
                   ("deconstruct", "{\"item\":\"all\",\"from\":\"locker\"}"), ("say", "{\"text\":\"Done\"}"))
            : Plan(("wait", "{}"));

        await Run(mgr, 2);
        Assert.Equal(new[] { "1:equip diving suit" }, Actions()); // the suit first, the rest waits
        await Run(mgr, 30);
        Assert.Single(seen);                                        // busy: no routine decisions meanwhile
        Assert.Single(Actions());
        Assert.Contains("waiting for the current task", mgr.Status(Array.Empty<FeatureStatus>()).Bots[0].Plan);

        game.PendingReports.Add(new GameReport(1, "task 'equip diving suit' ok: wearing it"));
        await Run(mgr, 1);
        Assert.Equal("1:goto Command Room [h11]", Actions()[^1]);  // walking: deconstruct waits for arrival
        await Run(mgr, 3);
        Assert.Equal(2, Actions().Count);

        game.Observations[1].Room = "Command Room [h11]";
        await Run(mgr, 2);
        // the room was only on the way: its "stay here" order goes, then the next step
        Assert.Equal(new[] { "1:end travel", "1:deconstruct all from locker" }, Actions().Skip(2));
        Assert.Single(seen);

        game.PendingReports.Add(new GameReport(1, "task 'deconstruct' ok: 5 items"));
        await Run(mgr, 2);
        Assert.Equal("1:say Done", Actions()[4]);
        // plan done: the bot thinks again and sees what happened along the way
        Assert.Equal(2, seen.Count);
        var results = seen.Last().Obs["lastActionResults"]!.AsArray().Select(x => x!.GetValue<string>()).ToList();
        Assert.Contains(results, r => r.Contains("equip diving suit"));
        Assert.Contains(results, r => r.Contains("deconstruct' ok"));
        Assert.Null(seen.Last().Obs["plan"]);
    }

    [Fact]
    public async Task FailedTaskCancelsTheRestAndWakesTheBot()
    {
        game.AddBot(1, "Ann", "mechanic");
        var mgr = Create();
        int n = 0;
        reply = _ => n++ == 0 ? Plan(("craft", "{\"item\":\"bandage\"}"), ("move_item", "{\"item\":\"bandage\",\"to\":\"Bob\"}")) : Plan(("wait", "{}"));

        await Run(mgr, 2);
        game.PendingReports.Add(new GameReport(1, "task 'craft bandage' failed: no cloth"));
        await Run(mgr, 3);

        Assert.DoesNotContain(Actions(), a => a.Contains("move"));
        Assert.Equal(2, seen.Count);
        var results = seen.Last().Obs["lastActionResults"]!.AsArray().Select(x => x!.GetValue<string>()).ToList();
        Assert.Contains(results, r => r.Contains("plan stopped") && r.Contains("move_item"));
    }

    [Fact]
    public async Task ErrorInAStepStopsThePlan()
    {
        game.AddBot(1, "Ann", "mechanic");
        var mgr = Create();
        int n = 0;
        reply = _ => n++ == 0 ? Plan(("go_to_room", "{\"room\":\"Nowhere\"}"), ("fix_leaks", "{}")) : Plan(("wait", "{}"));

        await Run(mgr, 3);
        Assert.DoesNotContain(Actions(), a => a.Contains("fix_leaks"));
        Assert.Equal(2, seen.Count);
    }

    [Fact]
    public async Task PlayerOrderInterruptsAndReplacesThePlan()
    {
        game.AddBot(1, "Ann", "mechanic");
        var mgr = Create();
        int n = 0;
        reply = obs => n++ switch
        {
            0 => Plan(("equip", "{\"item\":\"welding tool\"}"), ("fix_leaks", "{}")),
            _ => Plan(("follow", "{\"target\":\"Daniil\"}")),
        };
        await Run(mgr, 2);
        mgr.GetAgent(1)!.ReceivePlayerOrder("Daniil: follow me");
        await Run(mgr, 3);

        Assert.Equal(2, seen.Count);
        Assert.Contains("fix_leaks", seen.Last().Obs["plan"]!.GetValue<string>()); // it saw what it was about to do
        Assert.Contains("1:follow Daniil", Actions());

        // the old task's report no longer continues the replaced plan
        game.PendingReports.Add(new GameReport(1, "task 'equip welding tool' ok"));
        await Run(mgr, 3);
        Assert.DoesNotContain(Actions(), a => a.Contains("fix_leaks"));
    }

    [Fact]
    public async Task KeepPlanCarriesOnAfterAQuickWord()
    {
        game.AddBot(1, "Ann", "mechanic");
        var mgr = Create();
        int n = 0;
        reply = obs => n++ switch
        {
            0 => Plan(("equip", "{\"item\":\"welding tool\"}"), ("fix_leaks", "{}")),
            1 => Plan(("say", "{\"text\":\"After the leaks\"}"), ("keep_plan", "{}")),
            _ => Plan(("wait", "{}")),
        };
        await Run(mgr, 2);
        mgr.GetAgent(1)!.ReceivePlayerOrder("Daniil: what are you doing?");
        await Run(mgr, 3);
        Assert.Contains("1:say After the leaks", Actions());
        Assert.DoesNotContain(Actions(), a => a.Contains("fix_leaks"));

        game.PendingReports.Add(new GameReport(1, "task 'equip welding tool' ok"));
        await Run(mgr, 2);
        Assert.Contains("1:fix_leaks", Actions());
    }

    [Fact]
    public async Task OnlyNewUrgencyInterrupts()
    {
        game.AddBot(1, "Ann", "mechanic", o => o.Leaks.Add("Engine Room [h10]"));
        var mgr = Create();
        reply = _ => Plan(("equip", "{\"item\":\"welding tool\"}"), ("fix_leaks", "{}"));
        await Run(mgr, 20);
        Assert.Single(seen); // leaks were known when the plan was made

        game.Observations[1].Threats.Add(new ThreatInfo("crawler", 5, "Engine Room [h10]"));
        await Run(mgr, 6);
        Assert.Equal(2, seen.Count);
    }

    [Fact]
    public async Task LongPlanIsReviewedAfterMaxPlanSeconds()
    {
        game.AddBot(1, "Ann", "mechanic");
        var mgr = Create();
        reply = _ => Plan(("craft", "{\"item\":\"bandage\",\"amount\":5}"), ("say", "{\"text\":\"ok\"}"));
        await Run(mgr, 60);
        Assert.Single(seen);
        await Run(mgr, 70);
        Assert.Equal(2, seen.Count);
        Assert.NotNull(seen.Last().Obs["plan"]);
    }

    [Fact]
    public async Task WalkingThatNeverArrivesTimesOut()
    {
        game.AddBot(1, "Ann", "mechanic");
        var mgr = Create();
        int n = 0;
        reply = _ => n++ == 0 ? Plan(("go_to_room", "{\"room\":\"Command Room [h11]\"}"), ("repair_systems", "{}")) : Plan(("wait", "{}"));
        await Run(mgr, 40);
        Assert.DoesNotContain(Actions(), a => a.Contains("repair_systems"));
        Assert.Equal(2, seen.Count);
        Assert.Contains(seen.Last().Obs["lastActionResults"]!.AsArray(), r => r!.GetValue<string>().Contains("did not reach"));
    }

    [Fact]
    public async Task WithoutSequentialActionsEverythingRunsAtOnce()
    {
        game.AddBot(1, "Ann", "mechanic");
        var mgr = Create(@", ""sequentialActions"": false");
        reply = _ => Plan(("equip", "{\"item\":\"diving suit\"}"), ("fix_leaks", "{}"));
        await Run(mgr, 2);
        Assert.Equal(new[] { "1:equip diving suit", "1:fix_leaks" }, Actions().Take(2));
    }

    [Fact]
    public async Task PlayerOrderBecomesTheGoalUntilDone()
    {
        game.AddBot(1, "Ann", "mechanic");
        var mgr = Create();
        await Run(mgr, 1);
        var ann = mgr.GetAgent(1)!;

        // a greeting answered with say only does not become a goal
        ann.ReceivePlayerOrder("Daniil: hi Ann");
        reply = _ => Plan(("say", "{\"text\":\"Hi!\"}"));
        await Run(mgr, 3);
        Assert.Null(ann.Goal);

        ann.ReceivePlayerOrder("Daniil: make 3 bandages");
        reply = _ => Plan(("craft", "{\"item\":\"bandage\",\"amount\":3}"));
        await Run(mgr, 3);
        Assert.Contains("make 3 bandages", ann.Goal);

        game.PendingReports.Add(new GameReport(1, "task 'craft bandage x3' ok"));
        reply = _ => Plan(("set_goal", "{\"done\":true}"));
        await Run(mgr, 3);
        Assert.Contains("make 3 bandages", seen.Last().Obs["goal"]!.GetValue<string>()); // shown while deciding
        Assert.Null(ann.Goal);
    }

    [Fact]
    public void SetGoalTool()
    {
        game.AddBot(1, "Ann", "mechanic");
        var mgr = Create();
        mgr.Update();
        var a = mgr.GetAgent(1)!;
        Assert.StartsWith("ok, goal", Tools.Execute(new ToolCall("set_goal", new JsonObject { ["goal"] = "keep the reactor fuelled" }), a, game, mgr));
        Assert.Equal("keep the reactor fuelled", a.Goal);
        Assert.Contains("finished", Tools.Execute(new ToolCall("set_goal", new JsonObject { ["done"] = "true" }), a, game, mgr));
        Assert.Null(a.Goal);
    }

    [Fact]
    public void GoalTextDropsRoutingNotes() =>
        Assert.Equal("Daniil (Captain) on radio: \"make bandages\"",
            AgentManager.GoalText("Daniil (Captain) on radio: \"make bandages\" [also sent to: Max] ('me' = Daniil) Hint: probably craft(item=\"bandage\")."));

    [Theory]
    [InlineData("Engine Room [h57]", "Engine Room [h57]", true)]
    [InlineData("Engine Room [h57]", "engine room [h57]", true)]
    [InlineData("Engine Room [h57]", "Reactor [h57]", true)]
    [InlineData("Engine Room [h57]", "Engine Room [h58]", false)]
    [InlineData("Engine Room [h57]", "Engine Room", true)]
    [InlineData(null, "Engine Room", false)]
    public void RoomMatching(string? current, string target, bool expected) =>
        Assert.Equal(expected, AgentManager.InRoom(current, target));
}
