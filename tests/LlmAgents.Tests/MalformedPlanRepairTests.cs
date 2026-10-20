using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using LlmAgents.Agents;
using LlmAgents.Llm;
using Xunit;

namespace LlmAgents.Tests;

public sealed class MalformedPlanRepairTests
{
    [Fact]
    public async Task MisplacedStepsGetCanonicalRepairFeedbackAndCorrectedTreeKeepsItsFollowUp()
    {
        var clock = new FakeClock();
        var game = new FakeGame().AddBot(1, "Tanika", "engineer");
        var users = new ConcurrentQueue<string>();
        int replies = 0;
        var handler = new FakeHandler(body =>
        {
            users.Enqueue(body["messages"]!.AsArray()[^1]!["content"]!.GetValue<string>());
            return (HttpStatusCode.OK, replies++ switch
            {
                0 => Fixtures.OpenAiTextResponse("""{"calls":[{"name":"plan","steps":[{"do":"craft","args":{"item":"railgunshell","amount":2}},{"do":"drop","args":{"item":"#6291"}}]}]}"""),
                1 => Fixtures.OpenAiTextResponse("""{"calls":[{"name":"plan","arguments":{"steps":[{"try":[{"do":"craft","args":{"item":"railgunshell","amount":2}},{"do":"drop","args":{"item":"#6291"}}],"catch":[{"do":"inspect","args":{"kind":"machines"}}]}]}}]}"""),
                _ => Fixtures.OpenAiToolResponse(("carry_on", "{}")),
            });
        });
        var settings = Fixtures.Settings(@", ""decisionIntervalSeconds"": 600");
        using var manager = new AgentManager(settings, game, new LlmRouter(settings, handler, clock.Utc), clock.Get);
        async Task Run(int ticks)
        {
            for (int i = 0; i < ticks; i++)
            {
                manager.Update();
                await manager.WhenIdle();
                clock.Advance(0.5);
            }
        }

        await Run(10);
        Assert.True(users.Count >= 2);
        var retry = users.ElementAt(1);
        Assert.Contains("arguments.steps", retry);
        Assert.Contains("complete intended workflow", retry);
        Assert.Equal(new[] { "1:craft railgunshell x2" }, game.Actions);
        var agent = manager.GetAgent(1)!;
        Assert.IsType<PlanTree.Try>(Assert.Single(agent.Plan!.Tree.Root.Items));
        long? task = game.CurrentTaskId(1);
        Assert.NotNull(task);
        game.PendingReports.Add(new GameReport(1, "task 'craft' ok: made 2", task, true));
        await Run(3);
        Assert.Equal(new[] { "1:craft railgunshell x2", "1:drop #6291" }, game.Actions);
        Assert.Null(agent.Plan);
    }
}
