using System.Linq;
using System.Net;
using System.Threading.Tasks;
using LlmAgents.Agents;
using LlmAgents.Llm;
using LlmAgents.SettingsUi;
using Xunit;

namespace LlmAgents.Tests;

public class MonitorTests
{
    readonly FakeClock clock = new();
    readonly FakeGame game = new();

    AgentManager Create(string reply)
    {
        var settings = Fixtures.Settings();
        var handler = new FakeHandler(_ => (HttpStatusCode.OK, reply));
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
    public async Task Snapshot_shows_the_decision_the_calls_and_the_counters()
    {
        game.AddBot(1, "Ann", "mechanic");
        game.AddBot(2, "Bob", "engineer");
        var mgr = Create(Fixtures.OpenAiToolResponse(("say", "{\"text\":\"Иду\"}"), ("set_goal", "{\"goal\":\"починить насос\"}")));
        mgr.Update();
        mgr.GetAgent(1)!.ReceivePlayerOrder("Daniil (Captain) on radio: \"Ann, fix the pump\"", from: "Daniil");
        await Run(mgr, 6);

        var s = mgr.Monitor(1);
        Assert.Equal(2, s.Bots.Count);
        var ann = s.Bots.Single(b => b.Name == "Ann");
        Assert.True(ann.Alive);
        Assert.True(ann.Decisions >= 1);
        Assert.Equal("починить насос", ann.Goal);

        var d = s.Details!;
        Assert.Equal(1, d.Id);
        Assert.True(d.Requests >= 1);
        Assert.Equal(0, d.FailedRequests);
        Assert.True(d.ToolCalls >= 2);
        Assert.Equal(1, d.OrdersReceived);
        Assert.Contains(d.TopTools, t => t.StartsWith("say "));
        Assert.Contains(d.Activity, a => a.Kind == "order" && a.Text.Contains("fix the pump"));
        Assert.Contains(d.Activity, a => a.Kind == "think" && a.Text.Contains("say("));
        Assert.Contains(d.Activity, a => a.Kind == "do" && a.Text.StartsWith("say("));
        Assert.Equal("think", d.Activity.First(a => a.Kind is "think" or "order").Kind);   // newest first
        Assert.True(s.TokensIn > 0 || s.TokensOut >= 0);
        Assert.Contains(s.Conversation, c => c.Contains("Иду"));

        Assert.Null(mgr.Monitor(0).Details);
        Assert.Empty(mgr.Monitor(1, activity: false).Details!.Activity);
    }

    [Fact]
    public async Task Failed_requests_are_counted_and_shown()
    {
        game.AddBot(1, "Ann", "mechanic");
        var settings = Fixtures.Settings();
        var mgr = new AgentManager(settings, game,
            new LlmRouter(settings, new FakeHandler(_ => (HttpStatusCode.InternalServerError, "{\"error\":\"down\"}")), clock.Utc), clock.Get);
        mgr.Update();
        mgr.GetAgent(1)!.ReceivePlayerOrder("Daniil: \"Ann, come\"", from: "Daniil");
        await Run(mgr, 4);

        var d = mgr.Monitor(1).Details!;
        Assert.True(d.FailedRequests >= 1);
        Assert.Contains(d.Activity, a => a.Kind == "error" && a.Text.Contains("request failed"));
        Assert.True(mgr.Monitor(1).Bots[0].Errors >= 1);
    }

    [Fact]
    public void Snapshot_survives_the_wire()
    {
        var s = new MonitorSnapshot
        {
            Version = "1",
            Bots = { new MonitorBot { Id = 3, Name = "Олег", State = "plan", Goal = "насос" } },
            Details = new MonitorBotDetails { Id = 3, PlanSteps = { "go_to_room(room=\"Engine\")" }, Activity = { new MonitorActivity { Ago = 2, Kind = "do", Text = "x" } } },
        };
        var back = SettingsNet.Deserialize<MonitorSnapshot>(SettingsNet.Serialize(s))!;
        Assert.Equal("Олег", back.Bots[0].Name);
        Assert.Equal("go_to_room(room=\"Engine\")", back.Details!.PlanSteps[0]);
        Assert.Equal("do", back.Details.Activity[0].Kind);
        Assert.True(SettingsNet.Deserialize<MonitorRequest>("{\"bot\":5,\"activity\":true}") is { Bot: 5, Activity: true });
    }
}
