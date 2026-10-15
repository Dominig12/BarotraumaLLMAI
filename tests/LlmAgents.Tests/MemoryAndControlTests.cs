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
            var name = JsonNode.Parse(user)!["name"]!.GetValue<string>();
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
        Assert.StartsWith("error: feeling", Run(mgr, 1, "note_relationship", "{\"person\":\"Bob\",\"feeling\":\"love\",\"note\":\"x\"}"));
        Assert.StartsWith("error: that is you", Run(mgr, 1, "note_relationship", "{\"person\":\"Ann\",\"feeling\":\"like\",\"note\":\"x\"}"));
        Assert.StartsWith("ok", Run(mgr, 1, "note_preference", "{\"player\":\"Daniil\",\"preference\":\"wants short reports\"}"));
        Assert.StartsWith("ok", Run(mgr, 2, "note_preference", "{\"player\":\"Eve\",\"preference\":\"likes music\"}"));

        var ann = memory.Bot("Ann")!;
        Assert.Equal(new[] { "pump 2 is broken" }, ann.Facts);
        Assert.Equal("fixed my suit", ann.Relationships["Bob"].Note);

        clock.Advance(20);
        while (llm.Requests.TryDequeue(out _)) { }
        await Step(mgr);
        var obs = JsonNode.Parse(llm.Requests.First(r => r.User.Contains("\"name\":\"Ann\"")).User)!;
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
    [InlineData("fix_leaks", "{\"priority\":\"low\"}", "1:fix_leaks p1")]
    [InlineData("fix_leaks", "{\"priority\":9}", "1:fix_leaks")]
    [InlineData("operate", "{\"role\":\"reactor\",\"priority\":\"2\"}", "1:operate reactor p2")]
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
        Assert.StartsWith("error: priority", Run(mgr, 1, "set_order_priority", "{\"order\":\"fix_leaks\",\"priority\":7}"));
        Assert.StartsWith("error: priority", Run(mgr, 1, "set_order_priority", "{\"order\":\"fix_leaks\"}"));
        Assert.StartsWith("error: missing", Run(mgr, 1, "set_order_priority", "{\"priority\":1}"));
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
        Assert.True(game.Authority[1]);

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
        var (mgr, llm, _) = Create(s => s.Bots.Autonomy = "order");
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
