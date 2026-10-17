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

/// <summary>One plan engine for every reply, and faults that stay where they happen.</summary>
public class ReliabilityTests
{
    readonly FakeClock clock = new();
    readonly FakeGame game = new();
    readonly ConcurrentQueue<string> asked = new();
    Func<int, string> reply = _ => Fixtures.OpenAiToolResponse(("carry_on", "{}"));

    AgentManager Create(string global = "")
    {
        var settings = Fixtures.Settings(@", ""planStepTimeoutSeconds"": 30" + global);
        int n = 0;
        var handler = new FakeHandler(body =>
        {
            asked.Enqueue(body["messages"]!.AsArray()[^1]!["content"]!.GetValue<string>());
            return (HttpStatusCode.OK, reply(n++));
        });
        return new AgentManager(settings, game, new LlmRouter(settings, handler, clock.Utc), clock.Get);
    }

    async Task Run(AgentManager mgr, double seconds)
    {
        for (double t = 0; t < seconds; t += 0.5)
        {
            mgr.Update();
            await mgr.WhenIdle();
            clock.Advance(0.5);
        }
    }

    [Fact]
    public async Task A_walk_as_the_last_step_is_waited_for_and_then_reported()
    {
        game.AddBot(1, "Ann", "mechanic");
        var mgr = Create();
        await Run(mgr, 2);
        reply = _ => Fixtures.OpenAiToolResponse(("go_to_room", "{\"room\":\"Command Room [h11]\"}"));
        mgr.GetAgent(1)!.ReceivePlayerOrder("Daniil (Captain) on radio: \"Анна, иди в рубку\"", from: "Daniil");
        await Run(mgr, 3);
        var ann = mgr.GetAgent(1)!;
        Assert.Equal(PlanWait.Arrival, ann.Plan?.Waiting);
        Assert.DoesNotContain(game.Notices, x => x.Text.Contains("выполнено"));

        game.Observations[1].Room = "Command Room [h11]";
        await Run(mgr, 3);
        Assert.Null(ann.Plan);
        Assert.Contains(game.Notices, x => x.Text.StartsWith("[ИИ] Ann: выполнено"));
        Assert.DoesNotContain("1:end travel", game.Actions);   // nothing came after: the bot stays there
    }

    [Fact]
    public async Task A_plain_plan_can_be_changed_with_update_plan()
    {
        game.AddBot(1, "Ann", "mechanic");
        reply = n => n switch
        {
            0 => Fixtures.OpenAiToolResponse(("equip", "{\"item\":\"welding tool\"}"), ("fix_leaks", "{}")),
            1 => Fixtures.OpenAiToolResponse(("update_plan", "{\"node\":\"n2\",\"steps\":[{\"do\":\"repair_systems\"}]}")),
            _ => Fixtures.OpenAiToolResponse(("carry_on", "{}")),
        };
        var mgr = Create();
        await Run(mgr, 2);
        var ann = mgr.GetAgent(1)!;
        Assert.Contains("n2 fix_leaks", ann.PlanText());
        ann.ReceivePlayerOrder("Daniil: Ann, repair instead of welding");
        await Run(mgr, 2);
        game.PendingReports.Add(new GameReport(1, "task 'equip welding tool' ok"));
        await Run(mgr, 2);
        Assert.Contains("1:repair_systems", game.Actions);
        Assert.DoesNotContain("1:fix_leaks", game.Actions);
    }

    [Fact]
    public async Task A_reply_that_keeps_failing_is_asked_again_only_once()
    {
        game.AddBot(1, "Ann", "mechanic");
        reply = _ => Fixtures.OpenAiToolResponse(("go_to_room", "{\"room\":\"Nowhere [h99]\"}"));
        var mgr = Create();
        await Run(mgr, 20);
        Assert.Equal(2, asked.Count);   // the first look, and one more try with the error in view
        Assert.Contains("your last reply had an error", asked.Last());
    }

    [Fact]
    public async Task A_broken_game_call_does_not_stop_the_other_phases()
    {
        game.AddBot(1, "Ann", "mechanic");
        game.BreakReports = true;
        var mgr = Create();
        await Run(mgr, 3);
        Assert.NotEmpty(asked);   // the bots still think although reading the task reports fails every tick
    }

    [Fact]
    public async Task A_late_answer_to_an_abandoned_request_is_dropped()
    {
        game.AddBot(1, "Ann", "mechanic");
        var gate = new TaskCompletionSource();
        var settings = Fixtures.Settings(extraRoot: "");
        settings.Providers[0].TimeoutSeconds = 1;
        int calls = 0;
        var handler = new FakeHandler(async (_, _) =>
        {
            if (System.Threading.Interlocked.Increment(ref calls) == 1) await gate.Task;   // the first request hangs
            return (HttpStatusCode.OK, Fixtures.OpenAiToolResponse(("say", "{\"text\":\"late\"}")));
        });
        var mgr = new AgentManager(settings, game, new LlmRouter(settings, handler, clock.Utc), clock.Get);
        mgr.Update();
        var ann = mgr.GetAgent(1)!;
        Assert.True(ann.Pending);
        // far past every timeout: the watchdog frees the bot
        clock.Advance(200);
        mgr.Update();
        Assert.False(ann.Pending);
        gate.SetResult();
        await mgr.WhenIdle();
        clock.Advance(0.5);
        mgr.Update();
        Assert.DoesNotContain("1:say late", game.Actions);   // the old answer did nothing
        Assert.Equal(0, ann.Decisions);
    }
}
