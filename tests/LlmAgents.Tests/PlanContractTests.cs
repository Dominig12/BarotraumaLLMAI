using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using LlmAgents.Agents;
using LlmAgents.Llm;
using Xunit;

namespace LlmAgents.Tests;

public class PlanContractTests
{
    [Theory]
    [InlineData("""{"tree":[{"do":"fix_leaks"}]}""", "steps")]
    [InlineData("""{"steps":[{"do":"fix_leaks"}],"extra":true}""", "extra")]
    [InlineData("""{"steps":{"do":"fix_leaks"}}""", "steps")]
    [InlineData("""{"steps":[{"wait_until":"leaks > 0","timeout":"5"}]}""", "timeout")]
    [InlineData("""{"steps":[{"repeat":[{"do":"fix_leaks"}],"max":99}]}""", "max")]
    public async Task InvalidPlanArgumentsRejectTheWholeReplyBeforeAnyAction(string arguments, string field)
    {
        var clock = new FakeClock();
        var game = new FakeGame().AddBot(1, "Ann", "mechanic");
        var users = new ConcurrentQueue<string>();
        int replies = 0;
        var handler = new FakeHandler(body =>
        {
            users.Enqueue(body["messages"]!.AsArray()[^1]!["content"]!.GetValue<string>());
            return (HttpStatusCode.OK, replies++ == 0
                ? Fixtures.OpenAiToolResponse(("say", """{"text":"before"}"""), ("plan", arguments), ("say", """{"text":"after"}"""))
                : Fixtures.OpenAiToolResponse(("carry_on", "{}")));
        });
        using var mgr = new AgentManager(Fixtures.Settings(), game, new LlmRouter(Fixtures.Settings(), handler, clock.Utc), clock.Get);
        for (var i = 0; i < 8; i++)
        {
            mgr.Update();
            await mgr.WhenIdle();
            clock.Advance(0.5);
        }
        Assert.Empty(game.Actions);
        Assert.Contains(users, u => u.Contains("could not be read") && u.Contains(field));
    }

    [Fact]
    public async Task InvalidUpdateCannotRunSiblingCallsOrChangeTheExistingPlan()
    {
        var clock = new FakeClock();
        var game = new FakeGame().AddBot(1, "Ann", "mechanic");
        int replies = 0;
        var handler = new FakeHandler(_ => (HttpStatusCode.OK, replies++ == 0
            ? Fixtures.OpenAiToolResponse(("plan", """{"steps":[{"wait_until":"leaks > 0","timeout":300},{"do":"fix_leaks"}]}"""))
            : Fixtures.OpenAiToolResponse(("update_plan", """{"node":"n1","steps":[{"wait_until":"leaks > 0","timeout":301}]}"""), ("say", """{"text":"must not run"}"""))));
        using var mgr = new AgentManager(Fixtures.Settings(), game, new LlmRouter(Fixtures.Settings(), handler, clock.Utc), clock.Get);
        for (var i = 0; i < 6; i++)
        {
            mgr.Update();
            await mgr.WhenIdle();
            clock.Advance(0.5);
        }
        var agent = mgr.GetAgent(1)!;
        var original = agent.PlanText();
        Assert.Contains("n1 wait until leaks > 0", original);
        agent.ReceivePlayerOrder("Daniil: Ann, revise your plan", from: "Daniil");
        for (var i = 0; i < 6; i++)
        {
            mgr.Update();
            await mgr.WhenIdle();
            clock.Advance(0.5);
        }
        Assert.True(replies > 1);
        Assert.Empty(game.Actions);
        Assert.Equal(original, agent.PlanText());
    }

    [Fact]
    public void TypedRequiredArgumentsArePresentWithoutStringCoercion()
    {
        Assert.False(Tools.MissingAllRequired(new ToolCall("plan", System.Text.Json.Nodes.JsonNode.Parse("""{"steps":[{"do":"fix_leaks"}]}""")!.AsObject())));
        Assert.False(Tools.MissingAllRequired(new ToolCall("keep_gear", System.Text.Json.Nodes.JsonNode.Parse("""{"on":false}""")!.AsObject())));
        Assert.True(Tools.MissingAllRequired(new ToolCall("say", new System.Text.Json.Nodes.JsonObject())));
    }
}
