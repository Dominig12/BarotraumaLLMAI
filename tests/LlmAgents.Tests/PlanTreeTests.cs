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

public class PlanConditionTests
{
    static readonly Observation O = new()
    {
        Name = "Sophie", HealthPercent = 40, Room = "Ballast [h5]", Equipped = { "Welding Tool [weldingtool]" },
        Threats = { new ThreatInfo("Crawler", 12, "Ballast [h5]") }, Leaks = { "Ballast [h5]" },
        Infestations = { "ballast flora: 34 branches - 30 in Ballast [h5] (root)" },
        Rooms = { new RoomInfo("Ballast [h5]", 70, 0), new RoomInfo("Engine [h2]", 0, 2) },
    };

    static bool Eval(string text, bool? lastOk = null) =>
        PlanConditions.Parse(text).IsTrue(new PlanConditions.ObservationContext(O, 30, lastOk, "task 'equip' ok: Welding Tool equipped", false));

    [Theory]
    [InlineData("health < 50", true)]
    [InlineData("health >= 50", false)]
    [InlineData("flora > 0 and has('welding tool')", true)]
    [InlineData("flora == 34", true)]
    [InlineData("not in_room('Ballast [h5]')", false)]
    [InlineData("in_room('Ballast')", true)]
    [InlineData("threats", true)]
    [InlineData("threat('crawler') or leaks == 0", true)]
    [InlineData("(health > 90 or wearing('welding')) and water('Ballast [h5]') > 50", true)]
    [InlineData("fires('Engine [h2]') == 2", true)]
    [InlineData("result('equipped')", true)]
    [InlineData("time > 60", false)]
    [InlineData("!leaks", false)]
    public void Evaluates(string text, bool expected) => Assert.Equal(expected, Eval(text));

    [Fact]
    public void LastStep()
    {
        Assert.True(Eval("last_failed", lastOk: false));
        Assert.False(Eval("last_ok", lastOk: false));
    }

    [Theory]
    [InlineData("helth < 50", "unknown name 'helth'")]
    [InlineData("hass('x')", "unknown function")]
    [InlineData("health <", "ends too early")]
    [InlineData("(health < 5", "missing ')'")]
    [InlineData("health < 5 )", "unexpected")]
    public void TellsWhatIsWrong(string text, string message) =>
        Assert.Contains(message, Assert.Throws<FormatException>(() => PlanConditions.Parse(text)).Message);
}

/// <summary>Plans as trees: conditions, waits, repeats, try/catch, partial updates.</summary>
public class PlanTreeTests
{
    readonly FakeClock clock = new();
    readonly FakeGame game = new();
    readonly ConcurrentQueue<string> users = new();
    Func<int, string> reply = _ => Fixtures.OpenAiToolResponse(("carry_on", "{}"));
    int calls;

    AgentManager Create()
    {
        var settings = Fixtures.Settings(@", ""maxActionsPerDecision"": 6, ""maxPlanSeconds"": 600, ""planStepTimeoutSeconds"": 30");
        var handler = new FakeHandler(body =>
        {
            users.Enqueue(body["messages"]!.AsArray()[^1]!["content"]!.GetValue<string>());
            return (HttpStatusCode.OK, reply(calls++));
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

    static string PlanReply(string steps) => Fixtures.OpenAiToolResponse(("plan", "{\"steps\":" + steps + "}"));

    List<string> Actions() => game.Actions.ToList();

    [Fact]
    public async Task IfChoosesByTheSituation()
    {
        game.AddBot(1, "Sophie", "mechanic", o => o.HealthPercent = 30);
        var mgr = Create();
        reply = n => n == 0 ? PlanReply("[{\"if\":\"health < 50\",\"then\":[{\"do\":\"say\",\"args\":{\"text\":\"hurt\"}}],\"else\":[{\"do\":\"say\",\"args\":{\"text\":\"fine\"}}]},{\"do\":\"fix_leaks\"}]")
                            : Fixtures.OpenAiToolResponse(("carry_on", "{}"));
        await Run(mgr, 3);
        Assert.Contains("1:say hurt", Actions());
        Assert.DoesNotContain("1:say fine", Actions());
        Assert.Contains(Actions(), a => a.StartsWith("1:fix_leaks"));
        Assert.False(mgr.GetAgent(1)!.Busy);
    }

    [Fact]
    public async Task WaitUntilGoesOnWhenTheConditionComesTrue()
    {
        game.AddBot(1, "Sophie", "mechanic");
        var mgr = Create();
        reply = n => n == 0 ? PlanReply("[{\"wait_until\":\"in_room('Command Room [h11]')\",\"timeout\":60},{\"do\":\"say\",\"args\":{\"text\":\"here\"}}]")
                            : Fixtures.OpenAiToolResponse(("carry_on", "{}"));
        await Run(mgr, 3);
        Assert.DoesNotContain("1:say here", Actions());
        Assert.Contains("waiting for a condition", mgr.GetAgent(1)!.PlanText());
        int asked = calls;
        game.Observations[1].Room = "Command Room [h11]";
        await Run(mgr, 2);
        Assert.Contains("1:say here", Actions());
        Assert.Equal(asked, calls);                    // the model was not asked for that
    }

    [Fact]
    public async Task AWaitThatTimesOutFailsThePlanAndTheModelIsAsked()
    {
        game.AddBot(1, "Sophie", "mechanic");
        var mgr = Create();
        reply = n => n == 0 ? PlanReply("[{\"wait_until\":\"leaks > 0\",\"timeout\":5},{\"do\":\"fix_leaks\"}]")
                            : Fixtures.OpenAiToolResponse(("carry_on", "{}"));
        await Run(mgr, 10);
        Assert.DoesNotContain(Actions(), a => a.StartsWith("1:fix_leaks"));
        Assert.Contains(users, u => u.Contains("did not come true"));
    }

    [Fact]
    public async Task RepeatUntilWithTasks()
    {
        game.AddBot(1, "Sophie", "mechanic", o => o.Infestations.Add("ballast flora: 30 branches - 30 in Ballast [h5] (root)"));
        var mgr = Create();
        reply = n => n == 0 ? PlanReply("[{\"repeat\":[{\"do\":\"attack\",\"args\":{\"target\":\"ballast flora\"}}],\"until\":\"flora == 0\",\"max\":3},{\"do\":\"say\",\"args\":{\"text\":\"clean\"}}]")
                            : Fixtures.OpenAiToolResponse(("carry_on", "{}"));
        await Run(mgr, 2);
        Assert.Single(Actions(), a => a.StartsWith("1:attack"));
        game.PendingReports.Add(new GameReport(1, "task 'burn the ballast flora' ok: burnt 20 of 30 branches, 10 left"));
        await Run(mgr, 1);
        Assert.Equal(2, Actions().Count(a => a.StartsWith("1:attack")));   // still flora: again
        game.Observations[1].Infestations.Clear();
        game.PendingReports.Add(new GameReport(1, "task 'burn the ballast flora' ok: the ballast flora is gone"));
        await Run(mgr, 1);
        Assert.Equal(2, Actions().Count(a => a.StartsWith("1:attack")));
        Assert.Contains("1:say clean", Actions());
    }

    [Fact]
    public async Task TryCatchesAFailedStep()
    {
        game.AddBot(1, "Sophie", "mechanic");
        var mgr = Create();
        reply = n => n == 0 ? PlanReply("[{\"try\":[{\"do\":\"go_to_room\",\"args\":{\"room\":\"Nowhere [h99]\"}},{\"do\":\"say\",\"args\":{\"text\":\"arrived\"}}],\"catch\":[{\"if\":\"last_failed\",\"then\":[{\"do\":\"say\",\"args\":{\"text\":\"no way\"}}]}]},{\"do\":\"fix_leaks\"}]")
                            : Fixtures.OpenAiToolResponse(("carry_on", "{}"));
        await Run(mgr, 3);
        Assert.Contains("1:say no way", Actions());
        Assert.DoesNotContain("1:say arrived", Actions());
        Assert.Contains(Actions(), a => a.StartsWith("1:fix_leaks"));
    }

    [Fact]
    public async Task AFailedStepWithoutTryStopsThePlan()
    {
        game.AddBot(1, "Sophie", "mechanic");
        var mgr = Create();
        reply = n => n == 0 ? PlanReply("[{\"do\":\"go_to_room\",\"args\":{\"room\":\"Nowhere [h99]\"}},{\"do\":\"fix_leaks\"}]")
                            : Fixtures.OpenAiToolResponse(("carry_on", "{}"));
        await Run(mgr, 3);
        Assert.DoesNotContain(Actions(), a => a.StartsWith("1:fix_leaks"));
        Assert.Contains(users, u => u.Contains("plan stopped"));
    }

    [Fact]
    public async Task UpdatePlanReplacesOneNode()
    {
        game.AddBot(1, "Sophie", "mechanic");
        var mgr = Create();
        reply = n => n switch
        {
            0 => PlanReply("[{\"wait_until\":\"leaks > 0\",\"timeout\":300},{\"do\":\"fix_leaks\"}]"),
            _ => Fixtures.OpenAiToolResponse(("carry_on", "{}")),
        };
        await Run(mgr, 2);
        var agent = mgr.GetAgent(1)!;
        Assert.Contains("n1 wait until leaks > 0", agent.PlanText());
        Assert.Contains("n2 fix_leaks", agent.PlanText());

        // a player's order arrives; the model swaps the waiting step for a say and keeps the rest
        reply = _ => Fixtures.OpenAiToolResponse(("update_plan", "{\"node\":\"n1\",\"steps\":[{\"do\":\"say\",\"args\":{\"text\":\"on it\"}}]}"));
        agent.ReceivePlayerOrder("Daniil: Sophie, stop waiting", from: "Daniil");
        await Run(mgr, 2);
        Assert.Contains("1:say on it", Actions());
        Assert.Contains(Actions(), a => a.StartsWith("1:fix_leaks"));
    }

    [Fact]
    public async Task ABrokenPlanIsReportedAndNothingRuns()
    {
        game.AddBot(1, "Sophie", "mechanic");
        var mgr = Create();
        reply = n => n == 0 ? PlanReply("[{\"if\":\"helth < 5\",\"then\":[{\"do\":\"fix_leaks\"}]}]") : Fixtures.OpenAiToolResponse(("carry_on", "{}"));
        await Run(mgr, 3);
        Assert.DoesNotContain(Actions(), a => a.StartsWith("1:fix_leaks"));
        Assert.Contains(users, u => u.Contains("could not be read") && u.Contains("helth"));
    }

    [Fact]
    public async Task OtherCallsOfTheReplyBecomeSteps()
    {
        game.AddBot(1, "Sophie", "mechanic");
        var mgr = Create();
        reply = n => n == 0
            ? Fixtures.OpenAiToolResponse(("say", "{\"text\":\"first\"}"), ("plan", "{\"steps\":[{\"do\":\"say\",\"args\":{\"text\":\"second\"}}]}"), ("say", "{\"text\":\"third\"}"))
            : Fixtures.OpenAiToolResponse(("carry_on", "{}"));
        await Run(mgr, 2);
        var says = Actions().Where(a => a.StartsWith("1:say")).ToList();
        Assert.Equal(new[] { "1:say first", "1:say second", "1:say third" }, says);
    }

    [Fact]
    public void Describe()
    {
        var tree = PlanTree.Build(JsonNode.Parse("[{\"do\":\"equip\",\"args\":{\"item\":\"welding tool\"}},{\"repeat\":[{\"do\":\"fix_leaks\"}],\"until\":\"leaks == 0\",\"max\":2},{\"try\":[{\"do\":\"craft\",\"args\":{\"item\":\"bandage\"}}],\"catch\":[{\"do\":\"say\",\"args\":{\"text\":\"no\"}}]}]"),
            Tools.All.Select(t => t.Name).ToList());
        var d = tree.Describe();
        Assert.Contains("n1 equip(item=\"welding tool\")", d);
        Assert.Contains("repeat until leaks == 0 (max 2)", d);
        Assert.Contains("try", d);
        Assert.Contains("catch", d);
    }

    [Theory]
    [InlineData("[{\"do\":\"plan\",\"args\":{}}]", "cannot be a step")]
    [InlineData("[{\"do\":\"fly\"}]", "unknown tool 'fly'")]
    [InlineData("[{\"if\":\"health\",\"then\":[[[[[[[{\"do\":\"say\"}]]]]]]]}]", "every step must be an object")]
    public void RefusesBrokenTrees(string json, string message) =>
        Assert.Contains(message, Assert.Throws<FormatException>(() => PlanTree.Build(JsonNode.Parse(json), Tools.All.Select(t => t.Name).ToList())).Message);

    [Fact]
    public void ThePromptShowsAValidExample() =>
        Assert.Contains("{\"do\":\"equip\",\"args\":{\"category\":\"diving_suit\"}}", Prompts.System(new BotAgent(new BotHandle(1, "Sophie", "mechanic")), 6));
}
