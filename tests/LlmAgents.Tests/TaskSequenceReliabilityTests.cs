using System.Net;
using System.Threading.Tasks;
using LlmAgents.Agents;
using LlmAgents.Llm;
using Xunit;

namespace LlmAgents.Tests;

public class TaskSequenceReliabilityTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CraftWaitsForTheOutputToBeClearedAndDoesNotRunAfterAFailedDrop(bool succeeds)
    {
        var clock = new FakeClock();
        var game = new FakeGame { DropResult = "ok, started: drop output" }.AddBot(1, "Ann", "assistant");
        var settings = Fixtures.Settings();
        int requests = 0;
        var handler = new FakeHandler(_ => (HttpStatusCode.OK, ++requests == 1
            ? Fixtures.OpenAiToolResponse(("drop", "{\"item\":\"#42\"}"), ("craft", "{\"item\":\"railgunshell\"}"))
            : Fixtures.OpenAiToolResponse(("carry_on", "{}"))));
        using var mgr = new AgentManager(settings, game, new LlmRouter(settings, handler, clock.Utc), clock.Get);
        mgr.Update(); await mgr.WhenIdle();
        clock.Advance(0.5); mgr.Update();
        Assert.Contains("1:drop #42", game.Actions);
        Assert.DoesNotContain(game.Actions, a => a.Contains(":craft "));
        game.PendingReports.Add(new GameReport(1, "task 'drop output' " + (succeeds ? "ok: dropped shell" : "failed: output inaccessible")));
        clock.Advance(0.5); mgr.Update();
        if (succeeds) Assert.Contains("1:craft railgunshell x1", game.Actions);
        else Assert.DoesNotContain(game.Actions, a => a.Contains(":craft "));
    }
}
