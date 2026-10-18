using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using LlmAgents.Agents;
using LlmAgents.Llm;
using LlmAgents.Memory;
using Xunit;

namespace LlmAgents.Tests;

public sealed class MemoryStoreTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "llmagents-mem-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(dir, recursive: true); } catch { }
    }

    [Fact]
    public void RoundTripsEverythingPerScope()
    {
        var a = new MemoryStore(dir);
        a.Open("campaign_Моя кампания");
        a.AddFact("Ольга", "engineer", "Реактор течёт на 30%", 5);
        a.AddJournal("Ольга", "engineer", "Спасла Макса от краулера.", 5, countRound: true);
        a.SetRelationship("Ольга", "engineer", "Max", "trust", "covered me in the fight", 5);
        a.AddPreference("Daniil", "short radio reports", 5);
        a.SetControl("Ольга", "engineer", true, "Daniil");
        a.Flush();
        Assert.False(a.IsDirty);
        Assert.True(File.Exists(a.FilePath));
        Assert.Contains("Реактор", File.ReadAllText(a.FilePath!)); // readable, not \u-escaped

        var b = new MemoryStore(dir);
        b.Open("campaign_Моя кампания");
        var m = b.Bot("ольга")!; // names are case-insensitive
        Assert.Equal(new[] { "Реактор течёт на 30%" }, m.Facts);
        Assert.Equal(1, m.Rounds);
        Assert.Single(m.Journal);
        Assert.Equal("trust", m.Relationships["MAX"].Feeling);
        Assert.True(m.AiControl);
        Assert.Equal("Daniil", m.AiControlBy);
        Assert.Equal(new[] { "short radio reports" }, b.Read(d => d.Players["daniil"]));

        // another campaign does not see it
        b.Open("campaign_other");
        Assert.Null(b.Bot("Ольга"));
    }

    [Fact]
    public void LimitsAndDeduplicates()
    {
        var s = new MemoryStore(null);
        s.Open("x");
        for (int i = 0; i < 5; i++) s.AddFact("Ann", "mechanic", $"fact {i}", 3);
        s.AddFact("Ann", "mechanic", "FACT 4", 3); // duplicate moves to the end, no copy
        Assert.Equal(new[] { "fact 2", "fact 3", "FACT 4" }, s.Bot("Ann")!.Facts);

        s.SetRelationship("Ann", "mechanic", "A", "neutral", "", 2);
        s.SetRelationship("Ann", "mechanic", "B", "dislike", "stole my wrench", 2);
        s.SetRelationship("Ann", "mechanic", "C", "like", "", 2); // over the limit: the neutral one goes first
        Assert.Equal(new[] { "B", "C" }, s.Bot("Ann")!.Relationships.Keys.OrderBy(k => k));

        Assert.Equal(1, s.ForgetFact("Ann", "fact 2"));
        Assert.Equal(0, s.ForgetFact("Ann", "nothing"));
    }

    [Fact]
    public void DamagedFileIsKeptAndMemoryStartsEmpty()
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "broken.json"), "{ this is not json");
        var s = new MemoryStore(dir);
        s.Open("broken");
        Assert.Null(s.Bot("Ann"));
        Assert.True(File.Exists(Path.Combine(dir, "broken.json.bad")));
    }

    [Fact]
    public void ScopeNamesAreSafeFileNames()
    {
        Assert.Equal("campaign_a_b_c", MemoryStore.SafeName("campaign_a/b\\c"));
        Assert.Equal("default", MemoryStore.SafeName("../"));
    }

    [Fact]
    public void OlderFilesWithNullNotesCanStillBeReadAndSaved()
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "old.json"),
            "{\"bots\":{\"Ann\":{\"name\":\"Ann\",\"lastRoundNotes\":null,\"journal\":[\"old round\"]},\"deleted\":null}}");
        var s = new MemoryStore(dir);
        s.Open("old");
        Assert.Empty(s.Bot("Ann")!.LastRoundNotes);
        Assert.Null(s.Bot("deleted"));
        s.AddJournal("Ann", "mechanic", "new round", 1, countRound: true, entryId: "new");
        s.Flush();
        s = new MemoryStore(dir);
        s.Open("old");
        Assert.Equal(new[] { "new round" }, s.Bot("Ann")!.Journal);
    }

    [Fact]
    public void FailedWritesSurviveSwitchingCampaignsAndLateUpdates()
    {
        // A regular file occupies the directory until the next flush can retry.
        File.WriteAllText(dir, "blocked");
        try
        {
            var s = new MemoryStore(dir);
            s.Open("a");
            s.AddFact("Ann", "mechanic", "kept before the switch", 10);
            s.Open("b");
            s.AddFact("Ann", "mechanic", "late lesson", 10, scope: "a");
            s.AddPreference("Daniil", "short reports", 10);
            Assert.True(s.IsDirty);
            File.Delete(dir);
            s.Flush();
            Assert.False(s.IsDirty);
            var reloaded = new MemoryStore(dir);
            reloaded.Open("a");
            Assert.Equal(new[] { "kept before the switch", "late lesson" }, reloaded.Bot("Ann")!.Facts);
            reloaded.Open("b");
            Assert.Equal(new[] { "short reports" }, reloaded.Read(d => d.Players["Daniil"]));
        }
        finally { if (File.Exists(dir)) File.Delete(dir); }
    }

    [Fact]
    public void LateSummaryReplacesItsOwnEntryAndCannotResurrectAPrunedRound()
    {
        var s = new MemoryStore(dir);
        s.Open("a");
        s.AddJournal("Ann", "mechanic", "first fallback", 2, true, entryId: "first");
        s.AddJournal("Ann", "mechanic", "second fallback", 2, true, entryId: "second");
        s.Flush();
        s = new MemoryStore(dir);
        s.Open("a");
        s.AddJournal("Ann", "mechanic", "first improved", 2, false, entryId: "first");
        Assert.Equal(new[] { "first improved", "second fallback" }, s.Bot("Ann")!.Journal);
        Assert.Equal(2, s.Bot("Ann")!.Rounds);
        s.AddJournal("Ann", "mechanic", "third fallback", 2, true, entryId: "third");
        s.AddJournal("Ann", "mechanic", "very late first", 2, false, entryId: "first");
        Assert.Equal(new[] { "second fallback", "third fallback" }, s.Bot("Ann")!.Journal);
        Assert.Equal(3, s.Bot("Ann")!.Rounds);
    }
}

public sealed class MemoryAndControlTests
{
    readonly FakeClock clock = new();
    readonly FakeGame game = new();
    readonly MemoryStore memory = new(null);

    /// <summary>Records every request; agent decisions answered by Reply, summaries by Summary.</summary>
    sealed class Llm
    {
        public readonly ConcurrentQueue<(string System, string User, string[] Tools)> Requests = new();
        public Func<string, string> Reply = _ => Fixtures.OpenAiToolResponse(("wait", "{}"));
        public Func<string> Summary = () => Fixtures.OpenAiTextResponse("We sealed the breach together.");

        public FakeHandler Handler() => new(body =>
        {
            var msgs = body["messages"]!.AsArray();
            var system = msgs[0]!["content"]!.GetValue<string>();
            var user = msgs[^1]!["content"]!.GetValue<string>();
            var tools = body["tools"]?.AsArray().Select(t => t!["function"]!["name"]!.GetValue<string>()).ToArray() ?? Array.Empty<string>();
            Requests.Enqueue((system, user, tools));
            if (system.Contains("The round just ended")) return (HttpStatusCode.OK, Summary());
            var name = JsonNode.Parse(Fixtures.Obs(user))!["name"]!.GetValue<string>();
            return (HttpStatusCode.OK, Reply(name));
        });
    }

    (AgentManager Mgr, Llm Llm, ModSettings Settings) Create(Action<ModSettings>? tweak = null)
    {
        var settings = Fixtures.Settings();
        tweak?.Invoke(settings);
        var llm = new Llm();
        var mgr = new AgentManager(settings, game, new LlmRouter(settings, llm.Handler(), clock.Utc), clock.Get, memory);
        mgr.BeginRound("test");
        return (mgr, llm, settings);
    }

    async Task Step(AgentManager mgr, double seconds = 0.5)
    {
        mgr.Update();
        await mgr.WhenIdle();
        clock.Advance(seconds);
    }

    string Run(AgentManager mgr, int botId, string tool, string args = "{}") =>
        Tools.Execute(new ToolCall(tool, (JsonObject)JsonNode.Parse(args)!), mgr.GetAgent(botId)!, game, mgr);

    [Fact]
    public async Task LongTermMemoryToolsWriteTheStoreAndShowUpInTheObservation()
    {
        game.AddBot(1, "Ann", "mechanic", o => o.Crew.Add(new CrewInfo("Daniil", "captain", null, 100, true, "none")));
        game.AddBot(2, "Bob", "engineer");
        var (mgr, llm, _) = Create();
        await Step(mgr);

        Assert.Equal("ok", Run(mgr, 1, "remember", "{\"note\":\"short-lived\"}"));
        Assert.Equal("ok, kept for future rounds", Run(mgr, 1, "remember", "{\"note\":\"pump 2 is broken\",\"long_term\":true}"));
        Assert.Equal("ok", Run(mgr, 1, "note_relationship", "{\"person\":\"Bob\",\"feeling\":\"trust\",\"note\":\"fixed my suit\"}"));
        Assert.StartsWith("error: 'feeling'", Run(mgr, 1, "note_relationship", "{\"person\":\"Bob\",\"feeling\":\"love\",\"note\":\"x\"}"));
        Assert.StartsWith("error: that is you", Run(mgr, 1, "note_relationship", "{\"person\":\"Ann\",\"feeling\":\"like\",\"note\":\"x\"}"));
        Assert.StartsWith("ok", Run(mgr, 1, "note_preference", "{\"player\":\"Daniil\",\"preference\":\"wants short reports\"}"));
        Assert.StartsWith("ok", Run(mgr, 2, "note_preference", "{\"player\":\"Eve\",\"preference\":\"likes music\"}"));

        var ann = memory.Bot("Ann")!;
        Assert.Equal(new[] { "pump 2 is broken" }, ann.Facts);
        Assert.Equal("fixed my suit", ann.Relationships["Bob"].Note);

        clock.Advance(20);
        while (llm.Requests.TryDequeue(out _)) { }
        mgr.GetAgent(1)!.ReceivePlayerOrder("Daniil: report", from: "Daniil");   // bots think on events: give Ann a reason
        await Step(mgr);
        var obs = JsonNode.Parse(Fixtures.Obs(llm.Requests.First(r => r.User.Contains("\"name\":\"Ann\"")).User))!;
        var mem = obs["memory"]!;
        Assert.Equal("pump 2 is broken", mem["facts"]![0]!.GetValue<string>());
        Assert.Equal("Bob: trust - fixed my suit", mem["relationships"]![0]!.GetValue<string>());
        // preferences only for players aboard
        Assert.Equal(new[] { "Daniil: wants short reports" }, mem["playerPreferences"]!.AsArray().Select(x => x!.GetValue<string>()));

        Assert.Equal("ok", Run(mgr, 1, "forget", "{\"text\":\"pump 2\"}").Replace(", forgot 1", ""));
        Assert.Empty(memory.Bot("Ann")!.Facts);
    }

    [Fact]
    public void MemoryToolsAreHiddenWhenMemoryIsDisabled()
    {
        var names = Tools.For(Array.Empty<string>(), aiControl: false, memory: false).Select(t => t.Name).ToList();
        Assert.DoesNotContain("note_relationship", names);
        Assert.DoesNotContain("order_crew", names);
        Assert.Contains("remember", names);
        Assert.Contains("order_crew", Tools.For(Array.Empty<string>(), aiControl: true).Select(t => t.Name));
    }

    [Fact]
    public async Task BotToBotMessagesAreEchoedToPlayers()
    {
        game.AddBot(1, "Ann", "mechanic").AddBot(2, "Bob", "engineer").AddBot(3, "Cid", "engineer");
        var (mgr, _, settings) = Create();
        await Step(mgr);

        Assert.Equal("ok", Run(mgr, 1, "message_crew", "{\"to\":\"bob\",\"text\":\"Reactor is yours\"}"));
        Assert.Equal("ok (sent to 2)", Run(mgr, 1, "message_crew", "{\"to\":\"engineer\",\"text\":\"Both to the reactor\"}"));
        Assert.Contains("1:say [→ Bob] Reactor is yours", game.Actions);
        Assert.Contains("1:say [→ engineer] Both to the reactor", game.Actions);
        Assert.True(mgr.GetAgent(2)!.HasMail);

        settings.Global.ShowBotMessagesInChat = false;
        int before = game.Actions.Count;
        Assert.Equal("ok", Run(mgr, 1, "message_crew", "{\"to\":\"Cid\",\"text\":\"quietly\"}"));
        Assert.Equal(before, game.Actions.Count);
    }

    [Theory]
    [InlineData("fix_leaks", "{}", "1:fix_leaks")]
    [InlineData("fix_leaks", "{\"priority\":2}", "1:fix_leaks p2")]
    [InlineData("fix_leaks", "{\"priority\":1}", "1:fix_leaks p1")]
    [InlineData("fix_leaks", "{\"priority\":3}", "1:fix_leaks")]
    [InlineData("operate", "{\"role\":\"reactor\",\"priority\":2}", "1:operate reactor p2")]
    [InlineData("go_to_room", "{\"room\":\"Engine Room [h10]\",\"priority\":1}", "1:goto Engine Room [h10] p1")]
    [InlineData("set_order_priority", "{\"order\":\"fix_leaks\",\"priority\":0}", "1:priority fix_leaks 0")]
    public async Task OrderToolsPassThePriority(string tool, string args, string expected)
    {
        game.AddBot(1, "Ann", "mechanic");
        var (mgr, _, _) = Create();
        await Step(mgr);
        Run(mgr, 1, tool, args);
        Assert.Equal(expected, game.Actions.Last());
    }

    [Fact]
    public async Task SetOrderPriorityValidatesItsArguments()
    {
        game.AddBot(1, "Ann", "mechanic");
        var (mgr, _, _) = Create();
        await Step(mgr);
        Assert.StartsWith("error: 'priority' must be between", Run(mgr, 1, "set_order_priority", "{\"order\":\"fix_leaks\",\"priority\":7}"));
        Assert.StartsWith("error: missing 'priority'", Run(mgr, 1, "set_order_priority", "{\"order\":\"fix_leaks\"}"));
        Assert.StartsWith("error: missing", Run(mgr, 1, "set_order_priority", "{\"priority\":1}"));
    }

    [Theory]
    [InlineData("{\"priority\":\"low\"}")]
    [InlineData("{\"priority\":\"2\"}")]
    [InlineData("{\"priority\":9}")]
    public async Task InvalidPrioritiesDoNotBecomeDefaultsOrGetClamped(string args)
    {
        game.AddBot(1, "Ann", "mechanic");
        var (mgr, _, _) = Create();
        await Step(mgr);
        int before = game.Actions.Count;
        Assert.StartsWith("error:", Run(mgr, 1, "fix_leaks", args));
        Assert.Equal(before, game.Actions.Count);
    }

    [Fact]
    public async Task ControlOrderGrantsCommandAndIsRemembered()
    {
        game.AddBot(1, "Ann", "captain").AddBot(2, "Bob", "mechanic");
        var (mgr, llm, _) = Create();
        await Step(mgr);
        Assert.StartsWith("error: order_crew is only available", Run(mgr, 1, "order_crew", "{\"to\":\"Bob\",\"order\":\"weld\"}"));

        game.PendingControl.Add(new ControlEvent(1, true, "Daniil"));
        llm.Reply = name => name == "Ann"
            ? Fixtures.OpenAiToolResponse(("say", "{\"text\":\"Taking command\"}"), ("order_crew", "{\"to\":\"mechanic\",\"order\":\"fix the leaks, priority 3\"}"))
            : Fixtures.OpenAiToolResponse(("fix_leaks", "{}"));
        clock.Advance(5);
        await Step(mgr);   // control event -> Ann thinks right away (a player order)
        await Step(mgr);   // Ann's calls run; Bob receives the order
        await Step(mgr);   // Bob thinks
        await Step(mgr);   // Bob's calls run

        var ann = mgr.GetAgent(1)!;
        Assert.True(ann.AiControl);
        Assert.True(game.Control[1]);
        Assert.True(memory.Bot("Ann")!.AiControl);

        var annReq = llm.Requests.First(r => r.User.Contains("\"name\":\"Ann\"") && r.User.Contains("FULL CONTROL"));
        Assert.Contains("order_crew", annReq.Tools);
        Assert.Contains("FULL AI CONTROL", annReq.System);
        Assert.Contains("\"control\":\"FULL AI CONTROL (given by Daniil)\"", annReq.User);

        var bobReq = llm.Requests.First(r => r.User.Contains("\"name\":\"Bob\"") && r.User.Contains("Order from Ann"));
        Assert.DoesNotContain("order_crew", bobReq.Tools);
        Assert.DoesNotContain("FULL AI CONTROL", bobReq.System);
        Assert.Contains("1:say [→ Bob] fix the leaks, priority 3", game.Actions); // the only mechanic: shown by name
        Assert.Contains("2:fix_leaks", game.Actions);
        // an AI commander's order is binding but carries no player authority over command-menu orders
        Assert.False(game.Authority[2]);

        // next round: control comes back from memory
        mgr.Reset();
        game.Control.Clear();
        await Step(mgr);
        Assert.True(mgr.GetAgent(1)!.AiControl);
        Assert.True(game.Control[1]);

        // taken back
        game.PendingControl.Add(new ControlEvent(1, false, "Daniil"));
        await Step(mgr);
        Assert.False(mgr.GetAgent(1)!.AiControl);
        Assert.False(game.Control[1]);
        Assert.False(memory.Bot("Ann")!.AiControl);
    }

    [Fact]
    public async Task OrderAutonomyOnlyThinksWhenAskedUnlessUnderControl()
    {
        game.AddBot(1, "Ann", "mechanic", o => o.Leaks.Add("Engine Room [h10]")).AddBot(2, "Bob", "engineer");
        var (mgr, llm, _) = Create(s => { s.Bots.Autonomy = "order"; s.Bots.ControlDecisionIntervalSeconds = 6; });
        for (int i = 0; i < 60; i++) await Step(mgr);
        Assert.Empty(llm.Requests); // urgent leak, but the vanilla AI handles it

        mgr.GetAgent(2)!.ReceivePlayerOrder("Daniil: Bob, go to the reactor");
        await Step(mgr);
        Assert.Single(llm.Requests);
        Assert.Contains("\"name\":\"Bob\"", llm.Requests.Single().User);

        game.Observations[1].Leaks.Clear(); // calm: only the decision interval makes her think
        game.PendingControl.Add(new ControlEvent(1, true, "Daniil"));
        for (int i = 0; i < 40; i++) await Step(mgr); // 20 s
        int annCalls = llm.Requests.Count(r => r.User.Contains("\"name\":\"Ann\""));
        Assert.InRange(annCalls, 3, 5); // the immediate reaction, then every 6 s (control interval) instead of 10 s
    }

    [Fact]
    public async Task EndRoundWritesSummariesToTheJournal()
    {
        game.AddBot(1, "Ann", "mechanic").AddBot(2, "Bob", "engineer");
        var (mgr, llm, _) = Create();
        llm.Reply = _ => Fixtures.OpenAiToolResponse(("fix_leaks", "{}"));
        await Step(mgr);
        await Step(mgr); // calls executed -> round log has events

        mgr.EndRound();
        await mgr.WhenIdle();
        var ann = memory.Bot("Ann")!;
        Assert.Equal(1, ann.Rounds);
        Assert.Equal("We sealed the breach together.", ann.Journal.Single());
        var req = llm.Requests.Last(r => r.System.Contains("The round just ended"));
        Assert.Empty(req.Tools);
        Assert.Contains("fix_leaks", req.User);

        // the summary is shown in later rounds
        mgr.Reset();
        clock.Advance(30);
        while (llm.Requests.TryDequeue(out _)) { }
        await Step(mgr);
        Assert.Contains("We sealed the breach together.", llm.Requests.First(r => r.User.Contains("\"name\":\"Ann\"")).User);
    }

    [Fact]
    public async Task EndRoundFallsBackToAPlainSummaryWhenTheModelFails()
    {
        game.AddBot(1, "Ann", "mechanic");
        var (mgr, llm, _) = Create();
        llm.Reply = _ => Fixtures.OpenAiToolResponse(("fix_leaks", "{}"));
        llm.Summary = () => Fixtures.OpenAiTextResponse("");
        await Step(mgr);
        await Step(mgr);
        mgr.EndRound();
        await mgr.WhenIdle();
        var entry = memory.Bot("Ann")!.Journal.Single();
        Assert.StartsWith("Last round:", entry);
        Assert.Contains("fix_leaks", entry);
    }

    [Fact]
    public async Task RoundBaselineIsSavedBeforeASummaryAndSurvivesImmediateShutdown()
    {
        var dir = Path.Combine(Path.GetTempPath(), "llmagents-round-" + Guid.NewGuid().ToString("N"));
        try
        {
            game.AddBot(1, "Ann", "mechanic");
            var settings = Fixtures.Settings();
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var handler = new FakeHandler(async (_, body, ct) =>
            {
                var system = body["messages"]![0]!["content"]!.GetValue<string>();
                if (system.Contains("The round just ended"))
                {
                    started.TrySetResult();
                    await Task.Delay(-1, ct);
                }
                return (HttpStatusCode.OK, Fixtures.OpenAiToolResponse(("wait", "{}")));
            });
            var store = new MemoryStore(dir);
            var mgr = new AgentManager(settings, game, new LlmRouter(settings, handler, clock.Utc), clock.Get, store);
            mgr.BeginRound("campaign_a");
            await Step(mgr);
            mgr.GetAgent(1)!.LogEvent("task craft shells completed");
            mgr.Remember(mgr.GetAgent(1)!, "C4 is in the armory cabinet", false);
            mgr.EndRound();
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var reloaded = new MemoryStore(dir);
            reloaded.Open("campaign_a");
            Assert.Equal(1, reloaded.Bot("Ann")!.Rounds);
            Assert.Contains("craft shells", reloaded.Bot("Ann")!.Journal.Single());
            Assert.Contains("C4 is in the armory cabinet", reloaded.Bot("Ann")!.LastRoundNotes);

            mgr.EndRound(); // repeated lifecycle callbacks must not count the round twice
            mgr.Dispose();
            await mgr.WhenIdle();
            Assert.Equal(1, store.Bot("Ann")!.Rounds);
            Assert.Single(store.Bot("Ann")!.Journal);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public async Task CheckpointedNotesAreRecoveredWithoutARoundEndHook()
    {
        var dir = Path.Combine(Path.GetTempPath(), "llmagents-notes-" + Guid.NewGuid().ToString("N"));
        try
        {
            game.AddBot(1, "Ann", "mechanic");
            var settings = Fixtures.Settings();
            var handler = new FakeHandler(_ => (HttpStatusCode.OK, Fixtures.OpenAiToolResponse(("wait", "{}"))));
            var store = new MemoryStore(dir);
            using var old = new AgentManager(settings, game, new LlmRouter(settings, handler, clock.Utc), clock.Get, store);
            old.BeginRound("campaign_a");
            await Step(old);
            old.Remember(old.GetAgent(1)!, "The spare tanks are in the reactor cabinet", false);
            store.Flush(); // the normal periodic save, before EndRound or Dispose
            Assert.Empty(store.Bot("Ann")!.LastRoundNotes); // not mislabelled as last round while still playing

            var reloaded = new MemoryStore(dir);
            using var next = new AgentManager(settings, game, new LlmRouter(settings, handler, clock.Utc), clock.Get, reloaded);
            next.BeginRound("campaign_a");
            await Step(next);
            Assert.Contains("spare tanks", next.Recall(next.GetAgent(1)!, "tanks"));
            Assert.Empty(reloaded.Bot("Ann")!.PendingNotes);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public async Task DepartedBotsAndMissingEndHooksKeepTheirNotesAndJournal()
    {
        game.AddBot(1, "Ann", "mechanic");
        var (mgr, _, _) = Create(s => s.Memory.RoundSummary = false);
        await Step(mgr);
        mgr.Remember(mgr.GetAgent(1)!, "Shells are stored beside the railgun", false);
        mgr.GetAgent(1)!.LogEvent("task loaded railgun");
        game.Dead.Add(1);
        await Step(mgr);
        Assert.Null(mgr.GetAgent(1));
        mgr.Reset(); // a new round without the normal EndRound event
        var ann = memory.Bot("Ann")!;
        Assert.Equal(1, ann.Rounds);
        Assert.Contains("loaded railgun", ann.Journal.Single());
        Assert.Contains("Shells are stored beside the railgun", ann.LastRoundNotes);
    }

    [Fact]
    public async Task CommanderOrdersDoNotGrantAuthority()
    {
        game.AddBot(1, "Ann", "mechanic");
        var (mgr, llm, _) = Create();
        llm.Reply = _ => Fixtures.OpenAiToolResponse(("fix_leaks", "{}"));
        await Step(mgr);
        await Step(mgr);
        Assert.False(game.Authority[1]);

        clock.Advance(10);
        mgr.GetAgent(1)!.ReceivePlayerOrder("from AI commander", authority: false);
        await Step(mgr);
        await Step(mgr);
        Assert.False(game.Authority[1]);

        clock.Advance(10);
        mgr.GetAgent(1)!.ReceivePlayerOrder("from a human");
        await Step(mgr);
        await Step(mgr);
        Assert.True(game.Authority[1]);
    }
}
