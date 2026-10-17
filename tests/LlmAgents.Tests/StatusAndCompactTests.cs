using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using LlmAgents.Agents;
using LlmAgents.Llm;
using LlmAgents.Personas;
using LlmAgents.SettingsUi;
using Xunit;

namespace LlmAgents.Tests;

public class CompactObservationTests
{
    static readonly JsonSerializerOptions Read = new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public void RealInGameObservationShrinksAndKeepsWhatMatters()
    {
        // an observation from a real round (5 bots, 23 rooms, one player)
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Data", "observation_ingame.json"));
        var obs = JsonSerializer.Deserialize<Observation>(json, Read)!;
        var full = obs.ToJson();
        var compact = obs.ToCompactJson();
        Assert.True(compact.Length < full.Length * 0.7, $"compact {compact.Length} vs full {full.Length}");

        var c = JsonNode.Parse(compact)!.AsObject();
        Assert.Equal("playerOrders", c.First().Key);
        Assert.Contains("ВСЕ ЗА МНОЙ", c["playerOrders"]![0]!.GetValue<string>());
        Assert.Equal(obs.Rooms.Count, c["rooms"]!.AsArray().Count);            // every room id still there for go_to_room
        Assert.Equal("Центральный проход [h1]", c["rooms"]![0]!.GetValue<string>());
        var player = c["crew"]!.AsArray().Select(x => x!.GetValue<string>()).First(x => x.StartsWith("Смертельное Рандеву"));
        Assert.Contains("HUMAN PLAYER", player);
        Assert.Contains("in Реакторный отсек [h163]", player);
        Assert.Null(c["threats"]);       // empty lists are dropped
        Assert.Null(c["roomFires"]);     // zeros too
        Assert.Null(c["roomAlerts"]);    // nothing flooded or burning
    }

    [Fact]
    public void ProblemsAreListedSeparately()
    {
        var o = new Observation
        {
            Name = "Ann", Rooms = { new RoomInfo("Ballast [h5]", 40, 0), new RoomInfo("Engine [h2]", 0, 2), new RoomInfo("Bridge [h1]", 0, 0) },
            Threats = { new ThreatInfo("Crawler", 12, "Ballast [h5]") }, RoomFires = 1,
            Crew = { new CrewInfo("Bob", "mechanic", null, 60, false, "fixleaks [p3]", "burn 20") },
        };
        var c = JsonNode.Parse(o.ToCompactJson())!;
        Assert.Equal(new[] { "Ballast [h5]: water 40%", "Engine [h2]: fire x2" }, c["roomAlerts"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Equal("Crawler 12m in Ballast [h5]", c["threats"]![0]!.GetValue<string>());
        Assert.Equal("Bob (mechanic) outside, health 60%, order: fixleaks [p3], injuries: burn 20", c["crew"]![0]!.GetValue<string>());
        Assert.Equal(1, c["roomFires"]!.GetValue<int>());
    }

    [Fact]
    public async Task CompactIsWhatTheModelGetsEvenWithTheOldSwitchOff()
    {
        foreach (var compact in new[] { true, false })
        {
            var clock = new FakeClock();
            var game = new FakeGame().AddBot(1, "Ann", "mechanic");
            string? user = null;
            var handler = new FakeHandler(body =>
            {
                user = body["messages"]!.AsArray()[^1]!["content"]!.GetValue<string>();
                return (System.Net.HttpStatusCode.OK, Fixtures.OpenAiToolResponse(("wait", "{}")));
            });
            var settings = Fixtures.Settings($", \"compactObservation\": {(compact ? "true" : "false")}");
            var mgr = new AgentManager(settings, game, new LlmRouter(settings, handler, clock.Utc), clock.Get);
            mgr.Update(); await mgr.WhenIdle();
            var rooms = JsonNode.Parse(Fixtures.Obs(user!))!["rooms"]!.AsArray();
            Assert.True(rooms[0] is JsonValue);   // "compactObservation" is gone: always the short form
        }
    }
}

public class StatusTests
{
    [Fact]
    public async Task StatusShowsBotsProvidersAndProblems()
    {
        var clock = new FakeClock();
        var game = new FakeGame().AddBot(1, "Ann", "mechanic", o => o.CurrentOrder = "fixleaks [p3]").AddBot(2, "Bob", "engineer");
        var settings = Fixtures.Settings();
        var handler = new FakeHandler(_ => (System.Net.HttpStatusCode.OK, Fixtures.OpenAiToolResponse(("fix_leaks", "{}"))));
        var mgr = new AgentManager(settings, game, new LlmRouter(settings, handler, clock.Utc), clock.Get);
        mgr.Update(); await mgr.WhenIdle(); clock.Advance(0.5); mgr.Update();   // t=0.5: Ann's first look applied (answers apply on the next update)
        clock.Advance(1.0); mgr.Update(); await mgr.WhenIdle(); clock.Advance(0.5); mgr.Update();   // Bob's (staggered by 1.5 s)
        clock.Advance(1.5);                                                      // t=3.5
        game.Observations[2].CurrentOrder = "x";
        var st = mgr.Status(new[] { new FeatureStatus("Chat orders", true), new FeatureStatus("Gear lock", false, "patch failed") });

        Assert.Equal(ModInfo.Version, st.Version);
        var ann = st.Bots.Single(b => b.Name == "Ann");
        Assert.Equal("fixleaks [p3]", ann.Orders);
        Assert.Equal("fix_leaks()", ann.LastDecision);
        Assert.True(Math.Abs(ann.SecondsSinceDecision!.Value - 3) < 0.1, $"since {ann.SecondsSinceDecision} why {ann.Why} decisions {ann.Decisions}");
        Assert.Equal(2, st.Providers.Single(p => p.Name == "a").Ok);
        Assert.Equal(2, st.RequestsToday);

        var text = st.ToText();
        Assert.Contains("PROBLEMS: Gear lock (patch failed)", text);
        Assert.Contains("Ann (mechanic): fixleaks [p3]; last decision 3s ago (why: round start", text);
        Assert.Contains("): fix_leaks()", text);
        Assert.Contains("Requests: 2 in the last minute, 2 today; bots think: on events", text);
        Assert.Contains("a (m-a): ok 2, errors 0", text);
    }

    [Fact]
    public void ErrorsAreKeptPerBot()
    {
        var game = new FakeGame().AddBot(1, "Ann", "mechanic");
        var mgr = new AgentManager(Fixtures.Settings(), game, new LlmRouter(Fixtures.Settings()), () => 0);
        mgr.Update();
        var ann = mgr.GetAgent(1)!;
        for (int i = 0; i < 5; i++) ann.AddError($"e{i}");
        Assert.Equal(new[] { "e2", "e3", "e4" }, ann.RecentErrors());
    }

    [Fact]
    public void StatusCommandAndTabAndPerBotSwitch()
    {
        var game = new FakeGame().AddBot(1, "Ann", "mechanic").AddBot(2, "Bob", "engineer");
        var editor = new ConfigEditor(Fixtures.Config());
        AgentManager? mgr = null;
        var svc = new SettingsService(editor, new PersonaStore(null), (s, _) => mgr!.ApplySettings(s, null),
            () => new GameInfo(game.Bots.Select(b => new CrewEntry(b.Name, b.Job, null, false)).ToList(), new[] { "mechanic", "engineer" }),
            () => mgr!.Status(Array.Empty<FeatureStatus>()));
        mgr = new AgentManager(editor.Settings, game, new LlmRouter(editor.Settings), () => 0);
        mgr.Update();

        Assert.StartsWith($"LlmAgents {ModInfo.Version}", svc.HandleCommand("!llm status", true));
        var tab = svc.Fields().Where(f => f.Category == SettingsSchema.StatusTab).ToList();
        Assert.Contains(tab, f => f.Group!.StartsWith("Ann") && f.Label == "Приказы");
        Assert.Contains(tab, f => f.Path.SequenceEqual(new[] { "bots", "disabledCharacters", "Ann" }) && f.Type == FieldType.Bool);

        Assert.Equal("Ann: только ванильный ИИ", svc.HandleCommand("!llm ai ann off", true));
        mgr.Update();
        Assert.Null(mgr.GetAgent(1));                   // left to the vanilla AI
        Assert.NotNull(mgr.GetAgent(2));
        Assert.Contains(svc.Fields(), f => f.Group == "Ann — ванильный ИИ"); // can be switched back from the window
        Assert.Equal("Ann: управляет LLM", svc.HandleCommand("!llm ai Ann on", true));
        mgr.Update();
        Assert.NotNull(mgr.GetAgent(1));
    }
}

public class ConfigDefaultsTests
{
    [Fact]
    public void OldConfigGetsNewSettingsWithoutLosingValues()
    {
        // a config from an older version: no memory section, no new global keys, a provider without toolMode
        var old = @"{
          ""defaultProvider"": ""local"",
          ""global"": { ""decisionIntervalSeconds"": 7, ""maxNotes"": 3 },
          ""providers"": [ { ""name"": ""local"", ""type"": ""openai"", ""baseUrl"": ""http://127.0.0.1:11434/v1"", ""model"": ""gemma3"", ""temperature"": 0.3 } ],
          ""bots"": { ""providerByJob"": { ""captain"": ""local"" } },
          ""myOwnKey"": true
        }";
        var e = new ConfigEditor(old);
        var added = e.AddMissingDefaults();
        Assert.Contains("memory", added);
        Assert.Contains("global.maxChatEchoPerMinute", added);
        Assert.Contains("global.planStepTimeoutSeconds", added);
        Assert.Contains("bots.autonomy", added);
        Assert.Contains("providers.local.toolMode", added);
        Assert.DoesNotContain(added, a => a.Contains("apiKey\"") || a == "providers.local.apiKey");
        Assert.DoesNotContain(added, a => a.StartsWith("bots.providerByJob."));

        Assert.Equal(7, e.Settings.Global.DecisionIntervalSeconds);   // kept
        Assert.Equal(0.3, e.Settings.Providers[0].Temperature);
        Assert.Equal("local", e.Settings.ProviderFor("captain"));
        Assert.Contains("\"myOwnKey\": true", e.ToJson());
        Assert.True(e.Settings.Memory.Enabled);

        Assert.Empty(e.AddMissingDefaults()); // second time: nothing to add
    }

    [Fact]
    public void ShippedConfigIsComplete()
    {
        var e = ConfigEditor.Load(Path.Combine(AppContext.BaseDirectory, "llm_config.json"));
        Assert.Empty(e.AddMissingDefaults().Where(k => !k.EndsWith("apiKey")));
    }
}

public class CompactHistoryTests
{
    [Fact]
    public async Task OldTurnsAreOneLineEach()
    {
        var clock = new FakeClock();
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Data", "observation_ingame.json"));
        var real = JsonSerializer.Deserialize<Observation>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        real.PlayerOrders.Clear(); real.Inbox.Clear(); real.Notes.Clear(); real.LastActionResults.Clear();
        var game = new FakeGame();
        game.Bots.Add(new BotHandle(1, real.Name, real.Job));
        game.Observations[1] = real;
        int lastSize = 0;
        JsonArray? lastMessages = null;
        var handler = new FakeHandler(body =>
        {
            lastMessages = body["messages"]!.AsArray();
            lastSize = body.ToJsonString().Length;
            return (System.Net.HttpStatusCode.OK, Fixtures.OpenAiToolResponse(("wait", "{}")));
        });
        int Run()
        {
            var settings = Fixtures.Settings(", \"historyTurns\": 6");
            settings.Bots.Autonomy = "always";   // a heartbeat every 10 s: 7 turns
            var mgr = new AgentManager(settings, game, new LlmRouter(settings, handler, clock.Utc), clock.Get);
            for (int i = 0; i < 7; i++) { mgr.Update(); mgr.WhenIdle().Wait(); clock.Advance(11); }
            return lastSize;
        }
        Run();
        var old = lastMessages![1]!["content"]!.GetValue<string>();
        Assert.Matches(@"^\(\d+ ?(s|min) ago\) why: ", old);   // each old turn: its age, why the bot was asked, where it was
        Assert.Contains("you were in Реакторный отсек [h163]", old);
        // the old turns are short lines, not the whole old observation
        var now = lastMessages[^1]!["content"]!.GetValue<string>();
        Assert.True(old.Length < now.Length / 4, $"old turn {old.Length} chars vs the new message {now.Length}");
        await Task.CompletedTask;
    }
}
