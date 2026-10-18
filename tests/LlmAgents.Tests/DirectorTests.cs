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

public class DirectorTests
{
    readonly FakeClock clock = new();
    readonly FakeGame game = new();
    readonly ConcurrentQueue<string> directorPrompts = new();
    readonly ConcurrentQueue<string> botRequests = new();
    string botReply = Fixtures.OpenAiToolResponse(("say", "{\"text\":\"Есть!\"}"), ("come_to", "{\"target\":\"Daniil\"}"));
    Func<string, (HttpStatusCode, string)> director = _ => (HttpStatusCode.OK, Fixtures.OpenAiToolResponse(("carry_on", "{}")));

    const string On = "\"director\": { \"enabled\": true, \"minSecondsBetweenCalls\": 3, \"idleCheckSeconds\": 0, \"fallbackAfterFailures\": 2, \"fallbackSeconds\": 30 },";

    AgentManager Create(string extraRoot = On)
    {
        var settings = Fixtures.Settings(extraRoot: extraRoot);
        var handler = new FakeHandler(body =>
        {
            var msgs = body["messages"]!.AsArray();
            var tools = body["tools"]!.AsArray().Select(t => t!["function"]!["name"]!.GetValue<string>()).ToList();
            var last = msgs[^1]!["content"]!.GetValue<string>();
            if (tools.Contains("assign"))
            {
                directorPrompts.Enqueue(last);
                return director(last);
            }
            botRequests.Enqueue(last);
            return (HttpStatusCode.OK, botReply);
        });
        return new AgentManager(settings, game, new LlmRouter(settings, handler, clock.Utc), clock.Get);
    }

    async Task Run(AgentManager mgr, double seconds, double step = 0.5)
    {
        for (double t = 0; t < seconds; t += step)
        {
            mgr.Update();
            await mgr.WhenIdle();
            clock.Advance(step);
        }
    }

    static string Assign(string bot, string goal, string stepsJson) =>
        Fixtures.OpenAiToolResponse(("assign", new JsonObject { ["bot"] = bot, ["goal"] = goal, ["steps"] = JsonNode.Parse(stepsJson) }.ToJsonString()));

    [Fact]
    public async Task Danger_goes_to_the_director_and_its_plan_runs()
    {
        game.AddBot(1, "Ann", "mechanic", o => o.Leaks.Add("Engine Room [h10]"));
        game.AddBot(2, "Bob", "medicaldoctor");
        director = _ => (HttpStatusCode.OK, Assign("Ann", "weld the engine room", "[{\"do\":\"fix_leaks\"}]"));
        var mgr = Create();

        await Run(mgr, 6);

        Assert.Contains("1:fix_leaks", game.Actions);
        Assert.Empty(botRequests);                       // the bots did not think on their own
        var prompt = directorPrompts.First();
        Assert.Contains("[danger] Ann", prompt);
        Assert.Contains("Leaks: Engine Room [h10]", prompt);
        Assert.Contains("Bob (medicaldoctor)", prompt);
        Assert.Equal("director", mgr.GetAgent(1)!.LastDecision![..8]);
    }

    [Fact]
    public async Task Player_order_stays_with_the_bot_and_the_director_leaves_it_alone()
    {
        game.AddBot(1, "Ann", "mechanic");
        game.AddBot(2, "Bob", "engineer", o => o.Leaks.Add("Ballast [h5]"));
        director = _ => (HttpStatusCode.OK, Assign("Ann", "weld", "[{\"do\":\"fix_leaks\"}]"));
        var mgr = Create();
        mgr.GetAgent(1);   // agents appear on the first update
        mgr.Update();
        mgr.GetAgent(1)!.ReceivePlayerOrder("Daniil (Captain) on radio: \"Ann, come to me\"", from: "Daniil");

        await Run(mgr, 8);

        Assert.Single(botRequests);                       // Ann answered the player herself
        Assert.Contains("1:say Есть!", game.Actions);
        Assert.DoesNotContain("1:fix_leaks", game.Actions); // the director could not take her
        Assert.Contains(directorPrompts, p => p.Contains("Ann") && p.Contains("NOT ASSIGNABLE"));
    }

    [Fact]
    public async Task Player_order_through_vanilla_orders_keeps_the_bot_from_the_director()
    {
        game.AddBot(1, "Ann", "mechanic");
        botReply = Fixtures.OpenAiToolResponse(("operate", "{\"role\":\"pumps\"}"));
        director = _ => (HttpStatusCode.OK, Assign("Ann", "weld", "[{\"do\":\"fix_leaks\"}]"));
        var mgr = Create();
        mgr.Update();
        mgr.GetAgent(1)!.ReceivePlayerOrder("Daniil (Captain) on radio: \"Ann, run the pumps\"", from: "Daniil");
        await Run(mgr, 4);
        Assert.Contains(game.Actions, a => a.StartsWith("1:operate pumps"));
        Assert.Equal("Daniil", mgr.GetAgent(1)!.CurrentGoal?.Player);   // the plan is over, the order goes on

        game.Observations[1].Leaks.Add("Engine Room [h10]");
        await Run(mgr, 8);
        Assert.DoesNotContain("1:fix_leaks", game.Actions);
    }

    [Fact]
    public async Task Failing_director_hands_the_thinking_back_to_the_bots()
    {
        game.AddBot(1, "Ann", "mechanic", o => o.Leaks.Add("Engine Room [h10]"));
        director = _ => (HttpStatusCode.InternalServerError, "{\"error\":\"down\"}");
        var mgr = Create();

        await Run(mgr, 30);

        Assert.True(directorPrompts.Count >= 2);
        Assert.False(mgr.DirectorActive);
        Assert.NotEmpty(botRequests);                     // Ann got her danger back and decided herself
        Assert.Contains(mgr.Status(Array.Empty<FeatureStatus>()).Features, f => f.Name == "Директор" && !f.Ok);
    }

    [Fact]
    public async Task Bot_tool_called_directly_is_explained_next_time()
    {
        game.AddBot(1, "Ann", "mechanic", o => o.Leaks.Add("Engine Room [h10]"));
        int n = 0;
        director = _ => (HttpStatusCode.OK, n++ == 0
            ? Fixtures.OpenAiToolResponse(("fix_leaks", "{}"), ("assign", "{\"bot\":\"Nobody\",\"goal\":\"x\",\"steps\":[{\"do\":\"wait\"}]}"))
            : Fixtures.OpenAiToolResponse(("carry_on", "{}")));
        var mgr = Create();
        await Run(mgr, 3);
        game.Observations[1].RoomFires = 1;   // something new, so the director is asked again
        game.Observations[1].Rooms[0] = new RoomInfo("Engine Room [h10]", 0, 1);
        await Run(mgr, 10);

        var second = directorPrompts.Skip(1).FirstOrDefault() ?? "";
        Assert.Contains("is a bot tool", second);
        Assert.Contains("no AI bot named 'Nobody'", second);
        Assert.DoesNotContain("1:fix_leaks", game.Actions);
    }

    [Fact]
    public async Task Director_off_changes_nothing()
    {
        game.AddBot(1, "Ann", "mechanic", o => o.Leaks.Add("Engine Room [h10]"));
        var mgr = Create(extraRoot: "");
        await Run(mgr, 5);
        Assert.Empty(directorPrompts);
        Assert.NotEmpty(botRequests);
    }

    [Fact]
    public void Summary_counts_a_monster_once_and_marks_who_is_free()
    {
        var ann = new BotAgent(new BotHandle(1, "Ann", "mechanic"));
        var bob = new BotAgent(new BotHandle(2, "Bob", "securityofficer"));
        Observation O(string name) => new()
        {
            Name = name, Room = "Engine [h1]", Doing = "idle",
            Threats = { new ThreatInfo("crawler", 10, "Ballast [h5]"), new ThreatInfo("crawler", 12, "Ballast [h5]") },
            Rooms = { new RoomInfo("Engine [h1]", 0, 0), new RoomInfo("Ballast [h5]", 40, 0) },
            Submarine = new SubInfo(80, 1200, 1, ReactorFuelPercent: 40),
        };
        var text = Director.Summary(new[] { new DirectorEvent(EventKind.Danger, "Ann, Bob", "hostile crawler", 0) },
            new[] { new DirectedBot(ann, O("Ann"), null), new DirectedBot(bob, O("Bob"), "under full AI control, decides itself") },
            new[] { "Bob guards the reactor" }, new string[0], 10);

        Assert.Contains("crawler x2 in Ballast [h5]", text);
        Assert.DoesNotContain("x4", text);
        Assert.Contains("Ballast [h5]: water 40%", text);
        Assert.Contains("reactor fuel 40%", text);
        Assert.Contains("Ann (mechanic) in Engine [h1]; doing: idle; NO WORK", text);
        Assert.Contains("NOT ASSIGNABLE: under full AI control", text);
        Assert.Contains("Bob guards the reactor", text);
    }

    [Fact]
    public void Bot_tool_list_has_the_work_tools_but_not_the_plans_or_memory()
    {
        var list = Director.BotToolList();
        Assert.Contains("- fix_leaks(", list);
        Assert.Contains("- go_to_room(room", list);
        Assert.DoesNotContain("- plan(", list);
        Assert.DoesNotContain("- remember(", list);
        Assert.DoesNotContain("- order_crew(", list);
        Assert.Contains("Arguments JSON Schema:", Director.SystemPrompt("Russian"));
    }

    [Fact]
    public async Task DirectorDiscoversPerBotObjectSchemasAndAssignsGeneratedTaskThatWaitsForItsReport()
    {
        game.AddBot(1, "Ann", "mechanic", observation => observation.Leaks.Add("Engine Room [h10]"));
        game.Objects.Add(new GameObjectInfo("#42", "round1_item42", "Modded salvage", "mod.salvage", "item", new[] { "ModComponent" },
            new[] { new ObjectToolBinding("take_item", new JsonObject { ["item"] = "#42" }) }));
        var requests = 0;
        director = prompt =>
        {
            requests++;
            if (requests == 1) return (HttpStatusCode.OK, Fixtures.OpenAiToolResponse(("object_tools", "{\"bot\":\"Ann\",\"target\":\"#42\"}")));
            if (requests > 2) return (HttpStatusCode.OK, Fixtures.OpenAiToolResponse(("carry_on", "{}")));
            Assert.Contains("Arguments JSON Schema:", prompt);
            Assert.Contains("Bound object: #42", prompt);
            Assert.Contains("Modded salvage", prompt);
            Assert.Contains("\"maximum\":8", prompt); // full schema survives summary, rather than a 200-character result excerpt
            var match = System.Text.RegularExpressions.Regex.Match(prompt, @"obj_[A-Za-z0-9_]+_take_item_[a-f0-9]+\b");
            Assert.True(match.Success);
            var steps = new JsonArray
            {
                new JsonObject { ["do"] = match.Value, ["args"] = new JsonObject { ["count"] = 1 } },
                new JsonObject { ["do"] = "say", ["args"] = new JsonObject { ["text"] = "salvage secured" } },
            };
            return (HttpStatusCode.OK, Assign("Ann", "collect modded salvage", steps.ToJsonString()));
        };
        using var manager = Create();
        await Run(manager, 10);
        Assert.Equal(2, requests);
        Assert.Empty(botRequests);
        Assert.Contains("1:take #42 x1", game.Actions);
        Assert.DoesNotContain("1:say salvage secured", game.Actions);
        Assert.Equal(PlanWait.Task, manager.GetAgent(1)!.Plan!.Waiting);
        Assert.Equal("director", manager.GetAgent(1)!.CurrentGoal!.Source);
        game.PendingReports.Add(new GameReport(1, "task 'take Modded salvage' ok: taken #42"));
        await Run(manager, 5);
        Assert.Contains("1:say salvage secured", game.Actions);
        Assert.Null(manager.GetAgent(1)!.Plan);
        Assert.Null(manager.GetAgent(1)!.CurrentGoal);
        Assert.True(requests >= 3);
    }

    [Fact]
    public void DirectorKeepsFullDiscoverySchemasButClipsRoutineResults()
    {
        var schemaResult = "object_tools(bot=\"Ann\") -> " + new string('x', 500) + "Arguments JSON Schema: {\"type\":\"object\"}";
        var text = Director.Summary(Array.Empty<DirectorEvent>(), Array.Empty<DirectedBot>(), Array.Empty<string>(),
            new[] { schemaResult, "assign(bot=\"Ann\") -> " + new string('z', 500) }, 0);
        Assert.Contains(schemaResult, text);
        Assert.DoesNotContain(new string('z', 500), text);
        var spec = Assert.Single(Director.ToolSpecs, tool => tool.Name == "object_tools");
        Assert.Contains("bot", spec.Parameters["required"]!.AsArray().Select(node => node!.GetValue<string>()));
        Assert.NotNull(Tools.ValidateAgainstSchema(new JsonObject { ["bot"] = "Ann", ["limit"] = 21 }, spec.Parameters));
        Assert.NotNull(Tools.ValidateAgainstSchema(new JsonObject { ["bot"] = "Ann", ["offset"] = -1 }, spec.Parameters));
    }

    [Fact]
    public async Task DirectorDiscoverySchemasSurviveATransientRequestFailure()
    {
        game.AddBot(1, "Ann", "mechanic", observation => observation.Leaks.Add("Engine Room [h10]"));
        game.Objects.Add(new GameObjectInfo("#42", "round1_item42", "Modded salvage", "mod.salvage", "item", Array.Empty<string>(),
            new[] { new ObjectToolBinding("take_item", new JsonObject { ["item"] = "#42" }) }));
        var discovered = false;
        var recover = false;
        var failures = 0;
        director = prompt =>
        {
            if (!discovered)
            {
                discovered = true;
                return (HttpStatusCode.OK, Fixtures.OpenAiToolResponse(("object_tools", "{\"bot\":\"Ann\",\"target\":\"#42\"}")));
            }
            if (!recover) { failures++; return (HttpStatusCode.InternalServerError, "{\"error\":\"temporary outage\"}"); }
            var match = System.Text.RegularExpressions.Regex.Match(prompt, @"obj_[A-Za-z0-9_]+_take_item_[a-f0-9]+\b");
            if (!match.Success || !prompt.Contains("Arguments JSON Schema:", StringComparison.Ordinal))
                return (HttpStatusCode.OK, Fixtures.OpenAiToolResponse(("carry_on", "{}")));
            return (HttpStatusCode.OK, Assign("Ann", "collect salvage", new JsonArray
                { new JsonObject { ["do"] = match.Value, ["args"] = new JsonObject { ["count"] = 1 } } }.ToJsonString()));
        };
        using var manager = Create();
        await Run(manager, 6);
        Assert.True(failures >= 1);
        Assert.DoesNotContain("1:take #42 x1", game.Actions);
        recover = true;
        await Run(manager, 12);
        Assert.Contains("1:take #42 x1", game.Actions);
        Assert.True(manager.DirectorActive);
        Assert.Empty(botRequests);
    }

    [Theory]
    [InlineData("Ann", null)]
    [InlineData("ann lee", "Ann Lee")]
    [InlineData("Ли", null)]
    [InlineData("Олег", null)]
    [InlineData("Олег Сидоров", "Олег Сидоров")]
    public void Finds_bots_by_name(string query, string? expected)
    {
        var bots = new[] { new BotAgent(new BotHandle(1, "Ann Lee", "mechanic")), new BotAgent(new BotHandle(2, "Олег Сидоров", "engineer")) };
        Assert.Equal(expected, Director.FindBot(bots, query)?.Name);
    }
}
