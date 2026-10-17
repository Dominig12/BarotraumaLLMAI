using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using LlmAgents.Agents;
using LlmAgents.Llm;
using LlmAgents.SettingsUi;
using Xunit;

namespace LlmAgents.Tests;

/// <summary>Bots think on events, not on a timer; every request says why it was made.</summary>
public class EventSchedulingTests
{
    readonly FakeClock clock = new();
    readonly FakeGame game = new();
    readonly ConcurrentQueue<(string Bot, JsonObject Obs, string User, JsonArray Messages)> seen = new();
    Func<string, JsonObject, string> reply = (_, _) => Fixtures.OpenAiToolResponse(("carry_on", "{}"));
    HttpStatusCode status = HttpStatusCode.OK;

    AgentManager Create(Action<ModSettings>? tweak = null, string global = "", string extraRoot = "")
    {
        var settings = Fixtures.Settings(global, extraRoot: extraRoot);
        tweak?.Invoke(settings);
        var handler = new FakeHandler(body =>
        {
            var msgs = body["messages"]!.AsArray();
            var user = msgs[^1]!["content"]!.GetValue<string>();
            if (body["tools"]!.AsArray().Any(t => t!["function"]!["name"]!.GetValue<string>() == ChatDispatcher.ToolName))
                return (HttpStatusCode.OK, Fixtures.DispatchByName(user));
            var obs = (JsonObject)JsonNode.Parse(Fixtures.Obs(user))!;
            var name = obs["name"]!.GetValue<string>();
            seen.Enqueue((name, obs, user, msgs));
            return status != HttpStatusCode.OK ? (status, "down") : (HttpStatusCode.OK, reply(name, obs));
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

    List<string> WhyOf(int index) => seen.ElementAt(index).Obs["why"]!.AsArray().Select(x => x!.GetValue<string>()).ToList();
    int Count(string bot) => seen.Count(s => s.Bot == bot);

    [Fact]
    public async Task NothingHappeningMeansNoRequests()
    {
        game.AddBot(1, "Ann", "mechanic").AddBot(2, "Bob", "engineer");
        var mgr = Create();
        await Run(mgr, 300);
        // one look around at round start each, then nothing: the vanilla AI does the routine work
        Assert.Equal(1, Count("Ann"));
        Assert.Equal(1, Count("Bob"));
        Assert.Contains("[round start]", WhyOf(0)[0]);
    }

    [Fact]
    public async Task NewDangerWakesTheRightJobsOnce()
    {
        game.AddBot(1, "Ann", "mechanic").AddBot(2, "Doc", "medicaldoctor");
        var mgr = Create(s => s.Bots.ThinkAtRoundStart = false);
        await Run(mgr, 5);
        Assert.Empty(seen);

        foreach (var o in game.Observations.Values) o.Leaks.Add("Ballast [h12]");
        await Run(mgr, 30);
        // a breach is the mechanic's business, once, even though it lasts
        Assert.Equal(1, Count("Ann"));
        Assert.Equal(0, Count("Doc"));
        Assert.Contains(seen.Single().Obs["why"]!.AsArray(), w => w!.GetValue<string>().StartsWith("[danger] hull breach: Ballast [h12]"));

        // a badly hurt crewmate is the medic's business
        game.Observations[2].Crew.Add(new CrewInfo("Ann", "mechanic", "Ballast [h12]", 30, false, "none"));
        await Run(mgr, 3);
        Assert.Equal(1, Count("Doc"));
        Assert.Contains(seen.Last().Obs["why"]!.AsArray(), w => w!.GetValue<string>().Contains("Ann is badly hurt (health 30%)"));
    }

    [Fact]
    public async Task EndOfDangerIsAnEvent()
    {
        game.AddBot(1, "Ann", "mechanic", o => o.Threats.Add(new ThreatInfo("Crawler", 5, "Engine Room [h10]")));
        var mgr = Create(s => s.Bots.ThinkAtRoundStart = false);
        await Run(mgr, 5);
        Assert.Equal(1, Count("Ann"));
        game.Observations[1].Threats.Clear();
        await Run(mgr, 8);
        Assert.Equal(1, Count("Ann"));      // not at once: the crawler may come back
        await Run(mgr, 10);
        Assert.Equal(2, Count("Ann"));
        Assert.StartsWith("[calm] the danger is over", WhyOf(1)[0]);
    }

    [Fact]
    public async Task OrderAutonomyIgnoresDangerButHearsOrders()
    {
        game.AddBot(1, "Ann", "mechanic", o => o.Leaks.Add("Ballast [h12]"));
        var mgr = Create(s => s.Bots.Autonomy = "order");
        await Run(mgr, 20);
        Assert.Empty(seen);
        mgr.GetAgent(1)!.ReceivePlayerOrder("Daniil: weld", from: "Daniil");
        await Run(mgr, 1);
        Assert.Single(seen);
    }

    [Fact]
    public async Task LookupAtTheEndOfAPlanIsReadRightAway()
    {
        game.AddBot(1, "Ann", "mechanic");
        int n = 0;
        reply = (_, _) => n++ == 0
            ? Fixtures.OpenAiToolResponse(("say", "{\"text\":\"Смотрю\"}"), ("inspect", "{\"kind\":\"recipes\"}"))
            : Fixtures.OpenAiToolResponse(("carry_on", "{}"));
        var mgr = Create();
        await Run(mgr, 5);
        Assert.Equal(2, seen.Count);
        Assert.StartsWith("[lookup]", WhyOf(1)[0]);
        Assert.Contains(seen.Last().Obs["lastActionResults"]!.AsArray(), r => r!.GetValue<string>().StartsWith("inspect("));
    }

    [Fact]
    public async Task FinishedTaskIsLookedAtOnce()
    {
        game.AddBot(1, "Ann", "mechanic");
        int n = 0;
        reply = (_, _) => n++ == 0 ? Fixtures.OpenAiToolResponse(("craft", "{\"item\":\"bandage\"}")) : Fixtures.OpenAiToolResponse(("carry_on", "{}"));
        var mgr = Create();
        await Run(mgr, 5);
        Assert.Single(seen);
        game.PendingReports.Add(new GameReport(1, "task 'craft bandage' ok: made 1x bandage"));
        await Run(mgr, 3);
        Assert.Equal(2, seen.Count);
        Assert.StartsWith("[done] your plan is done: task 'craft bandage' ok", WhyOf(1)[0]);
        await Run(mgr, 60);
        Assert.Equal(2, seen.Count);   // carry_on: nothing more to do
    }

    [Fact]
    public async Task MessagesWaitForTheEndOfAPlanButDangerDoesNot()
    {
        game.AddBot(1, "Ann", "mechanic").AddBot(2, "Bob", "engineer");
        int n = 0;
        reply = (bot, _) => bot == "Ann" && n++ == 0
            ? Fixtures.OpenAiToolResponse(("craft", "{\"item\":\"bandage\"}"), ("say", "{\"text\":\"Готово\"}"))
            : Fixtures.OpenAiToolResponse(("carry_on", "{}"));
        var mgr = Create();
        await Run(mgr, 4);
        Assert.True(mgr.GetAgent(1)!.Busy);
        mgr.DeliverMessage(mgr.GetAgent(2)!, "Ann", "need help in ballast");
        await Run(mgr, 15);
        Assert.Equal(1, Count("Ann"));          // the message waits, the plan goes on

        game.Observations[1].RoomFires = 1;     // new danger interrupts
        await Run(mgr, 3);
        Assert.Equal(2, Count("Ann"));
        var why = seen.Last(s => s.Bot == "Ann").Obs["why"]!.AsArray().Select(x => x!.GetValue<string>()).ToList();
        Assert.StartsWith("[danger] fire in your room", why[0]);
        Assert.Contains(why, w => w.StartsWith("[message] message from Bob"));   // goes along
        Assert.NotNull(seen.Last(s => s.Bot == "Ann").Obs["plan"]);
    }

    [Fact]
    public async Task MenuOrderCancelsThePlanAndBecomesTheGoal()
    {
        game.AddBot(1, "Ann", "mechanic");
        int n = 0;
        reply = (_, _) => n++ == 0
            ? Fixtures.OpenAiToolResponse(("craft", "{\"item\":\"bandage\"}"), ("repair_systems", "{}"))
            : Fixtures.OpenAiToolResponse(("carry_on", "{}"));
        var mgr = Create();
        await Run(mgr, 3);
        var ann = mgr.GetAgent(1)!;
        Assert.True(ann.Busy);

        game.PendingMenuOrders.Add(new MenuOrder(1, "Daniil", "fixleaks (your task 'craft bandage' was cancelled)"));
        await Run(mgr, 2);
        Assert.False(ann.Busy);
        Assert.DoesNotContain(game.Actions, a => a.Contains("repair_systems"));
        Assert.Contains("fixleaks", ann.Goal);
        Assert.Equal("player Daniil", ann.CurrentGoal!.Source);
        Assert.StartsWith("[menu order] Daniil gave you an order through the command menu: fixleaks", WhyOf(1)[0]);
        Assert.Contains("from player Daniil", seen.Last().Obs["goal"]!.GetValue<string>());

        // moving an order up or down in the crew list is noted, but changes no plan and asks nothing
        int before = seen.Count;
        game.PendingMenuOrders.Add(new MenuOrder(1, "Daniil", "priority change: fixleaks is now p2", PriorityOnly: true));
        await Run(mgr, 3);
        Assert.Equal(before, seen.Count);
        Assert.Contains("fixleaks", ann.Goal);
        Assert.Contains(ann.PastOrders, o => o.Text.Contains("priority change"));

        game.PendingMenuOrders.Add(new MenuOrder(1, "Daniil", "dismiss all orders", Dismissal: true));
        await Run(mgr, 3);
        Assert.Null(ann.Goal);
        Assert.Contains("do not give them to yourself again", WhyOf(2)[0]);
    }

    [Fact]
    public async Task OrdersSurviveOutagesAndTheSenderIsToldWhenTheyCannotBeDone()
    {
        game.AddBot(1, "Ann", "mechanic");
        var mgr = Create(s => s.Bots.ThinkAtRoundStart = false);
        status = HttpStatusCode.ServiceUnavailable;
        mgr.SubmitChat(new ChatCommand("Daniil", null, "Ann, weld the breach", new[] { 1 }, true));
        await Run(mgr, 2);
        Assert.Contains(game.Notices, n => n.Player == "Daniil" && n.Text.Contains("Приказ получили: Ann"));
        Assert.True(mgr.GetAgent(1)!.HasPlayerOrder);   // put back after the failed request

        await Run(mgr, 120);
        Assert.False(mgr.GetAgent(1)!.HasPlayerOrder);
        Assert.Contains(game.Notices, n => n.Player == "Daniil" && n.Text.Contains("Ann не может выполнить приказ"));
        Assert.Contains(mgr.GetAgent(1)!.RecentErrors(), e => e.Contains("no decision"));
    }

    [Fact]
    public async Task AnUnusableReplyIsAskedOnceMore()
    {
        game.AddBot(1, "Ann", "mechanic");
        int n = 0;
        reply = (_, _) => n++ == 0 ? Fixtures.OpenAiTextResponse("Hmm, let me think about it.") : Fixtures.OpenAiToolResponse(("fix_leaks", "{}"));
        var mgr = Create(s => s.Bots.ThinkAtRoundStart = false);
        mgr.Update();
        mgr.GetAgent(1)!.ReceivePlayerOrder("Daniil: fix the leak", from: "Daniil");
        await Run(mgr, 4);
        Assert.Equal(2, seen.Count);
        Assert.Contains("1:fix_leaks", game.Actions);
        Assert.Contains(WhyOf(1), w => w.StartsWith("[retry]"));
        Assert.Contains("NEW ORDER", seen.Last().User);   // the order is shown again
    }

    [Fact]
    public async Task FullControlGetsAHeartbeatThatSlowsDownWhenNothingChanges()
    {
        game.AddBot(1, "Ann", "captain");
        var mgr = Create(s => { s.Bots.ThinkAtRoundStart = false; s.Bots.ControlDecisionIntervalSeconds = 10; s.Global.MaxIdleIntervalSeconds = 40; });
        game.PendingControl.Add(new ControlEvent(1, true, "Daniil"));
        await Run(mgr, 120);
        var annTimes = seen.Count;
        Assert.StartsWith("[control] Daniil handed you FULL CONTROL", WhyOf(0)[0]);
        Assert.Contains(WhyOf(1), w => w.StartsWith("[heartbeat]"));
        // 10, 20, 40, 40... instead of every 10 s
        Assert.InRange(annTimes, 4, 6);
    }

    [Fact]
    public async Task HistoryLinesCarryTheirAgeAndReason()
    {
        game.AddBot(1, "Ann", "mechanic");
        var mgr = Create();
        await Run(mgr, 2);
        clock.Advance(120);
        mgr.GetAgent(1)!.ReceivePlayerOrder("Daniil: report", from: "Daniil");
        await Run(mgr, 2);
        var history = seen.Last().Messages[1]!["content"]!.GetValue<string>();
        Assert.StartsWith("(2 min ago) why: [round start]", history);
    }

    [Fact]
    public async Task APlanForAnOrderClosesTheGoalAndTellsThePlayer()
    {
        game.AddBot(1, "Olga", "engineer");
        int n = 0;
        reply = (_, _) => n++ switch
        {
            1 => Fixtures.OpenAiToolResponse(("say", "{\"text\":\"Есть\"}"), ("refuel_reactor", "{}")),
            2 => Fixtures.OpenAiToolResponse(("treat", "{\"target\":\"Ivan\"}")),
            _ => Fixtures.OpenAiToolResponse(("carry_on", "{}")),
        };
        var mgr = Create();
        await Run(mgr, 2);
        var olga = mgr.GetAgent(1)!;
        mgr.SubmitChat(new ChatCommand("Daniil", null, "Ольга, перезаряди реактор", new[] { 1 }, true));
        await Run(mgr, 2);
        Assert.Contains("перезаряди реактор", olga.Goal);
        Assert.Equal("Daniil", olga.CurrentGoal!.Player);

        game.PendingReports.Add(new GameReport(1, "task 'refuel reactor' ok: fuel rod loaded"));
        await Run(mgr, 3);
        Assert.Null(olga.Goal);                 // done: no need to ask the model again just to say so
        Assert.Equal(2, seen.Count);
        Assert.Contains(game.Notices, x => x.Text.StartsWith("[ИИ] Olga: выполнено (") && x.Text.Contains("перезаряди реактор"));

        mgr.SubmitChat(new ChatCommand("Daniil", null, "Ольга, вылечи Ивана", new[] { 1 }, true));
        await Run(mgr, 2);
        game.PendingReports.Add(new GameReport(1, "task 'treat Ivan' failed: no medicine aboard"));
        await Run(mgr, 3);
        Assert.Contains(game.Notices, x => x.Text.StartsWith("[ИИ] Olga: не получилось (") && x.Text.Contains("no medicine aboard"));
        Assert.StartsWith("[failed] task 'treat Ivan' failed", WhyOf(3)[0]);   // and the model is asked to adapt
    }

    [Fact]
    public async Task RoutineThinkingLeavesPartOfTheBudgetForOrders()
    {
        for (int i = 1; i <= 5; i++) game.AddBot(i, "Bot" + i, "mechanic");
        // 10 requests per minute: routine work may use 8, the last 2 are kept for orders
        var mgr = Create(s => { s.Bots.Autonomy = "always"; s.Global.DecisionIntervalSeconds = 5; s.Global.IdleBackoff = false; },
            global: @", ""requestsPerMinute"": 10");
        await Run(mgr, 30);
        Assert.Equal(8, seen.Count);
        mgr.GetAgent(3)!.ReceivePlayerOrder("Daniil: weld", from: "Daniil");
        await Run(mgr, 2);
        Assert.Equal(9, seen.Count);
        Assert.Contains("NEW ORDER", seen.Last().User);
    }

    [Fact]
    public async Task AnOrderThatWaitsForTheLimitIsReportedToTheSender()
    {
        for (int i = 1; i <= 5; i++) game.AddBot(i, "Bot" + i, "mechanic");
        var mgr = Create(s => { s.Bots.Autonomy = "always"; s.Global.DecisionIntervalSeconds = 5; s.Global.IdleBackoff = false; },
            global: @", ""requestsPerMinute"": 3");
        await Run(mgr, 20);      // the whole minute is used up
        int before = seen.Count;
        mgr.GetAgent(3)!.ReceivePlayerOrder("Daniil: weld", from: "Daniil");
        await Run(mgr, 5);
        Assert.Equal(before, seen.Count);
        var notes = game.Notices.Where(x => x.Text.Contains("приказ ждёт")).ToList();
        Assert.Single(notes);    // once, not every tick
        Assert.Equal("Daniil", notes[0].Player);
        await Run(mgr, 60);      // the limit frees up: the order goes through
        Assert.Contains(seen, r => r.User.Contains("NEW ORDER"));
    }

    [Fact]
    public void RequestsAreCounted()
    {
        game.AddBot(1, "Ann", "mechanic");
        var mgr = Create();
        mgr.Update();
        mgr.WhenIdle().Wait();
        Assert.Equal(1, mgr.RequestsLastMinute);
        clock.Advance(61);
        Assert.Equal(0, mgr.RequestsLastMinute);
    }
}

public class ChatFeedbackTests
{
    [Fact]
    public async Task SenderIsToldWhoTookTheOrderOrThatNobodyHeardIt()
    {
        var clock = new FakeClock();
        var game = new FakeGame().AddBot(1, "Ann", "mechanic").AddBot(2, "Bob", "mechanic");
        var settings = Fixtures.Settings();
        int dispatches = 0;
        var handler = new FakeHandler(body =>
        {
            if (body["tools"]!.AsArray().Any(t => t!["function"]!["name"]!.GetValue<string>() == ChatDispatcher.ToolName))
            {
                dispatches++;
                return (HttpStatusCode.OK, Fixtures.OpenAiToolResponse((ChatDispatcher.ToolName, "{\"recipients\":[\"Ann\",\"Bob\"]}")));
            }
            return (HttpStatusCode.OK, Fixtures.OpenAiToolResponse(("carry_on", "{}")));
        });
        var mgr = new AgentManager(settings, game, new LlmRouter(settings, handler, clock.Utc), clock.Get);
        mgr.Update();
        mgr.SubmitChat(new ChatCommand("Daniil", null, "Механики, откачайте воду насосами", new[] { 1, 2 }, true));
        mgr.SubmitChat(new ChatCommand("Daniil", null, "Все ко мне", Array.Empty<int>(), true));
        mgr.SubmitChat(new ChatCommand("Daniil", null, "ну и денёк", Array.Empty<int>(), true));
        for (int i = 0; i < 4; i++) { mgr.Update(); await mgr.WhenIdle(); clock.Advance(0.5); }

        Assert.Equal(1, dispatches);   // the model decides; nobody heard the other two
        var notes = game.Notices.Select(n => n.Text).ToList();
        Assert.Contains("[ИИ] Приказ получили: Ann, Bob", notes);
        Assert.Single(notes, n => n.Contains("никто из ИИ-экипажа не услышал"));   // the order, not the chatter
        Assert.All(game.Notices, n => Assert.Equal("Daniil", n.Player));

        settings.Chat.ConfirmToSender = false;
        while (game.Notices.TryDequeue(out _)) { }
        mgr.SubmitChat(new ChatCommand("Daniil", null, "Ann, за мной", new[] { 1, 2 }, true));
        for (int i = 0; i < 2; i++) { mgr.Update(); await mgr.WhenIdle(); clock.Advance(0.5); }
        Assert.Empty(game.Notices);
    }
}

public class ConfigMigrationTests
{
    const string OldConfig = @"{
  ""enabled"": true,
  ""defaultProvider"": ""local"",
  ""global"": { ""decisionIntervalSeconds"": 10, ""maxActionsPerDecision"": 3, ""historyTurns"": 8, ""logFile"": """" },
  ""providers"": [
    { ""name"": ""local"", ""type"": ""openai"", ""baseUrl"": ""http://x.test/v1"", ""model"": ""m"", ""maxOutputTokens"": 300 },
    { ""name"": ""cloud"", ""type"": ""openai"", ""baseUrl"": ""http://y.test/v1"", ""model"": ""m"", ""maxOutputTokens"": 1200 }
  ],
  ""bots"": { ""autonomy"": ""always"", ""controlDecisionIntervalSeconds"": 6 }
}";

    [Fact]
    public void OldDefaultsAreUpgradedAndUserValuesKept()
    {
        var editor = new ConfigEditor(OldConfig);
        var changed = editor.MigrateDefaults();
        var s = editor.Settings;
        Assert.Equal(60, s.Global.DecisionIntervalSeconds);
        Assert.Equal(6, s.Global.MaxActionsPerDecision);
        Assert.Equal(8, s.Global.HistoryTurns);             // set by the user: kept
        Assert.Equal("events", s.Bots.Autonomy);
        Assert.Equal(20, s.Bots.ControlDecisionIntervalSeconds);
        Assert.Equal(800, s.Providers[0].MaxOutputTokens);
        Assert.Equal(1200, s.Providers[1].MaxOutputTokens); // kept
        Assert.Equal(ModSettings.CurrentConfigVersion, s.ConfigVersion);
        Assert.Contains("bots.autonomy: \"always\" -> \"events\"", changed);
        Assert.Contains("providers.local.maxOutputTokens: 300 -> 800", changed);

        // the second time nothing changes
        var again = new ConfigEditor(editor.ToJson());
        Assert.Empty(again.MigrateDefaults());
        Assert.Contains($"\"configVersion\": {ModSettings.CurrentConfigVersion}", editor.ToJson());
    }

    [Fact]
    public void ACurrentConfigIsNotTouched()
    {
        var json = OldConfig.Replace("\"enabled\": true,", $"\"configVersion\": {ModSettings.CurrentConfigVersion}, \"enabled\": true,");
        var editor = new ConfigEditor(json);
        Assert.Empty(editor.MigrateDefaults());
        Assert.Equal("always", editor.Settings.Bots.Autonomy);
    }

    [Fact]
    public void Version2GetsOnlyTheVersion3Changes()
    {
        var json = OldConfig.Replace("\"enabled\": true,", "\"configVersion\": 2, \"enabled\": true,")
            .Replace("\"global\": {", "\"chat\": { \"dispatchWaitSeconds\": 6 }, \"global\": { \"maxNotes\": 5,");
        var editor = new ConfigEditor(json);
        var changed = editor.MigrateDefaults();
        Assert.Equal("always", editor.Settings.Bots.Autonomy);          // a version 2 change: not applied again
        Assert.Equal(12, editor.Settings.Chat.DispatchWaitSeconds);
        Assert.Equal(12, editor.Settings.Global.MaxNotes);
        Assert.Contains("configVersion: 2 -> 4", changed);
    }

    [Fact]
    public void Version3LosesTheSettingsThatAreGone()
    {
        var json = OldConfig.Replace("\"enabled\": true,", "\"configVersion\": 3, \"enabled\": true,")
            .Replace("\"global\": {", "\"global\": { \"sequentialActions\": false, \"compactObservation\": true, \"speechChannel\": \"radio\",");
        var editor = new ConfigEditor(json);
        var changed = editor.MigrateDefaults();
        Assert.Contains("global.sequentialActions: removed (no longer a setting)", changed);
        Assert.Contains("global.compactObservation: removed (no longer a setting)", changed);
        Assert.DoesNotContain("sequentialActions", editor.ToJson());
        Assert.Equal("auto", editor.Settings.Global.SpeechChannel);      // "radio" was the same as "auto"
        Assert.Equal("always", editor.Settings.Bots.Autonomy);           // older changes are not applied again
    }
}
