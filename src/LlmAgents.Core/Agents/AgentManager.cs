using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LlmAgents.Llm;
using LlmAgents.Memory;
using LlmAgents.Personas;

namespace LlmAgents.Agents;

/// <summary>The tool calls of one reply.</summary>
/// <param name="PlayerAuthority">The decision carried out a human player's chat order.</param>
/// <param name="AutoGoal">null = leave the goal; "" = clear it; text = the player's order becomes the goal.</param>
public sealed record AgentBatch(int BotId, List<ToolCall> Calls, bool PlayerAuthority = false, string? AutoGoal = null);

/// <summary>
/// Owns all agents. <see cref="Update"/> must be called every frame on the game thread; it is cheap
/// (it only does work every TickSeconds). LLM calls run on the thread pool and only enqueue
/// commands, which are executed back on the game thread in the next Update.
///
/// Scheduling is fair: every tick the bots that want to think are sorted by priority
/// (player order &gt; urgent / has mail &gt; routine) and then by how long they have waited,
/// and only then get LLM slots. With few slots and many bots nobody starves.
/// </summary>
public sealed class AgentManager : IDisposable
{
    public ModSettings Settings { get; private set; }
    readonly IGameApi game;
    LlmRouter router;
    readonly PersonaStore personas;
    readonly Func<double> clock;
    readonly MemoryStore memory;
    readonly Dictionary<int, BotAgent> agents = new();
    readonly ConcurrentQueue<AgentBatch> batches = new();
    readonly ConcurrentQueue<(int Generation, ChatCommand Cmd, List<RosterEntry> Recipients)> routedChats = new();
    readonly List<(double At, ChatCommand Cmd)> pendingChats = new();
    readonly ConcurrentDictionary<Task, byte> inFlight = new();
    readonly CancellationTokenSource cts = new();
    double nextTick = double.NegativeInfinity;
    double nextFlush;
    int generation; // bumped on Reset, so late answers from a previous round are dropped

    public const int MaxBatchesPerUpdate = 16;
    /// <summary>Tools that start a multi-step task; with sequentialActions the next step waits for its result.</summary>
    public static readonly IReadOnlyCollection<string> TaskTools = new[] { "equip", "refuel_reactor", "craft", "deconstruct", "treat", "move_item", "come_to" };
    /// <summary>Calls that only talk or note things down: a reply made only of these doesn't turn an order into a goal.</summary>
    static readonly IReadOnlyCollection<string> TalkTools = new[] { "say", "message_crew", "remember", "forget", "note_relationship", "note_preference", "inspect", "keep_plan" };
    /// <summary>Echoed bot-to-bot messages are clipped to this (the game's chat limit is about 200).</summary>
    public const int MaxChatEcho = 190;
    const double FlushSeconds = 15;
    /// <summary>How long a round summary waits for a free LLM slot before the plain summary is used.</summary>
    public const double SummaryWaitSeconds = 45;

    enum Tier { PlayerOrder = 0, Urgent = 1, Routine = 2 }

    sealed record Candidate(BotAgent Agent, Observation Obs, Tier Tier, bool Due);

    public AgentManager(ModSettings settings, IGameApi game, LlmRouter router, Func<double>? clock = null, MemoryStore? memory = null,
        PersonaStore? personas = null)
    {
        Settings = settings;
        this.personas = personas ?? new PersonaStore(null);
        this.memory = memory ?? new MemoryStore(null);
        this.game = game;
        this.router = router;
        if (clock == null)
        {
            var sw = Stopwatch.StartNew();
            clock = () => sw.Elapsed.TotalSeconds;
        }
        this.clock = clock;
    }

    public IReadOnlyCollection<BotAgent> Agents => agents.Values;
    public double Now => clock();
    public BotAgent? GetAgent(int id) => agents.TryGetValue(id, out var a) ? a : null;
    public int PendingChatCount => pendingChats.Count;
    public MemoryStore Memory => memory;
    public PersonaStore Personas => personas;

    /// <summary>
    /// Applies changed settings right away (in-game settings window, !llm set). Agents keep their state. A new router is
    /// passed only when providers or limits changed; requests already running finish on the old one.
    /// </summary>
    public void ApplySettings(ModSettings settings, LlmRouter? newRouter)
    {
        Settings = settings;
        if (newRouter != null)
        {
            var old = router;
            router = newRouter;
            // give in-flight requests time to finish before their HTTP clients go away
            Task.Delay(TimeSpan.FromSeconds(180)).ContinueWith(_ => { try { old.Dispose(); } catch { } }, TaskScheduler.Default);
        }
        nextTick = double.NegativeInfinity;
    }

    /// <summary>Snapshot for !llm status and the settings window (game thread).</summary>
    public ModStatus Status(IReadOnlyList<FeatureStatus> features)
    {
        double now = clock();
        var bots = agents.Values.OrderBy(a => a.Name).Select(a =>
        {
            Observation? o = null;
            try { if (game.IsAlive(a.Id)) o = game.Observe(a.Id); } catch { /* status must never fail */ }
            return new BotStatus(a.Id, a.Name, a.Job, ProviderFor(a), a.AiControl, personas.PromptFor(a.Name) != null,
                o?.CurrentOrder ?? "?", o?.CurrentTask, a.LastDecision,
                double.IsNaN(a.LastDecisionAt) ? null : now - a.LastDecisionAt, a.RecentErrors(), a.Pending, a.Decisions,
                a.Goal, a.PlanText());
        }).ToList();
        return new ModStatus(ModInfo.Version, Settings.Enabled, features, router.Status(), router.GlobalGate.UsedToday,
            Settings.Global.DailyRequestBudget, bots, Settings.Bots.DisabledCharacters.ToList(), MemoryOn ? memory.Scope : null);
    }

    /// <summary>Provider for this bot: its persona's provider if set and known, else by job, else the default.</summary>
    public string ProviderFor(BotAgent agent)
    {
        var p = personas.ProviderFor(agent.Name);
        return p != null && Settings.Providers.Any(x => x.Name.Equals(p, StringComparison.OrdinalIgnoreCase)) ? p : Settings.ProviderFor(agent.Job);
    }
    bool MemoryOn => Settings.Memory.Enabled;

    /// <summary>Selects the long-term memory for this round (one scope per campaign save). Call at round start.</summary>
    public void BeginRound(string scope)
    {
        if (MemoryOn) memory.Open(scope);
    }

    /// <summary>For tests and diagnostics: completes when all in-flight LLM requests have finished.</summary>
    public async Task WhenIdle()
    {
        while (!inFlight.IsEmpty) await Task.WhenAll(inFlight.Keys.ToArray()).ConfigureAwait(false);
    }

    public void Reset()
    {
        Interlocked.Increment(ref generation);
        agents.Clear();
        pendingChats.Clear();
        while (batches.TryDequeue(out _)) { }
        while (routedChats.TryDequeue(out _)) { }
        nextTick = double.NegativeInfinity;
    }

    // ---------------------------------------------------------------- game thread

    public void Update()
    {
        if (!Settings.Enabled) return;
        double now = clock();
        if (now < nextTick) return;
        nextTick = now + Settings.Global.TickSeconds;

        DrainCommands(now);
        SyncAgents();
        DrainReports(now);
        CheckArrivals(now);
        DrainControl();
        DeliverRoutedChats();
        DispatchChats(now);   // chat routing gets LLM slots before routine thinking
        ScheduleDecisions(now);

        if (now >= nextFlush)
        {
            nextFlush = now + FlushSeconds;
            if (MemoryOn) memory.Flush();
        }
    }

    void SyncAgents()
    {
        var seen = new HashSet<int>();
        foreach (var h in game.GetBots())
        {
            if (!Settings.IsJobEnabled(h.Job) || !Settings.IsCharacterEnabled(h.Name)) continue;
            seen.Add(h.Id);
            if (!agents.TryGetValue(h.Id, out var agent))
            {
                agent = new BotAgent(h);
                agents[h.Id] = agent;
                Log.Info($"agent created: {h.Name} ({h.Job}) -> provider '{ProviderFor(agent)}'" + (personas.PromptFor(h.Name) != null ? " (persona)" : ""));
                // full AI control given in an earlier round stays until a player takes it back
                if (MemoryOn && memory.Bot(h.Name) is { AiControl: true } m)
                {
                    agent.AiControl = true;
                    agent.ControlBy = m.AiControlBy;
                    game.SetAiControl(h.Id, true);
                    Log.Info($"[{h.Name}] full AI control restored (given by {m.AiControlBy})");
                }
            }
            else
            {
                agent.Name = h.Name;
                agent.Job = h.Job;
                agent.Trait = h.Trait;
            }
        }
        foreach (var id in agents.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            Log.Info($"agent removed: {agents[id].Name}");
            agents.Remove(id);
        }
    }

    void DrainCommands(double now)
    {
        int budget = MaxBatchesPerUpdate;
        while (budget-- > 0 && batches.TryDequeue(out var b))
        {
            if (!agents.TryGetValue(b.BotId, out var agent) || !game.IsAlive(b.BotId)) continue;
            if (b.AutoGoal != null) agent.SetGoal(b.AutoGoal, now);
            bool keep = b.Calls.Any(c => c.Name == "keep_plan");
            var steps = b.Calls.Select(c => new PlanStep(c, b.PlayerAuthority)).ToList();

            if (!Settings.Global.SequentialActions)
            {
                agent.ClearPlan();
                foreach (var step in steps) Run(agent, step);
                continue;
            }

            agent.PlanStartedAt = now;
            agent.PlanUrgency = agent.DecisionUrgency;
            if (keep && agent.Busy)
            {
                // a word in between (say, message...), then the old plan goes on
                foreach (var step in steps) Run(agent, step);
                continue;
            }
            if (agent.Plan.Count > 0) Log.Info($"[{agent.Name}] new decision replaces planned steps: {agent.PlanText()}");
            // the new plan starts now (a task still running goes on until a new task replaces it; its report just wakes the bot)
            agent.ClearPlan();
            agent.Plan.AddRange(steps);
            Advance(agent, now);
        }
    }

    /// <summary>Runs the plan's next steps until one has to be waited for (a task or walking to a room).</summary>
    void Advance(BotAgent agent, double now)
    {
        while (agent.Waiting == PlanWait.None && agent.Plan.Count > 0)
        {
            var step = agent.Plan[0];
            agent.Plan.RemoveAt(0);
            var result = Run(agent, step);
            if (result.StartsWith("error"))
            {
                if (agent.Plan.Count > 0) StopPlan(agent, $"step {step.Call.Name} failed");
                return;
            }
            if (TaskTools.Contains(step.Call.Name) && result.StartsWith("ok, started"))
            {
                agent.Waiting = PlanWait.Task;
                agent.WaitingSince = now;
            }
            else if (step.Call.Name == "go_to_room" && agent.Plan.Count > 0)
            {
                agent.Waiting = PlanWait.Arrival;
                agent.WaitRoom = ArrivalRoom(result) ?? Tools.Str(step.Call.Arguments, "room");
                agent.WaitingSince = now;
                agent.NextArrivalCheckAt = now;
            }
        }
    }

    void StopPlan(BotAgent agent, string why)
    {
        var rest = agent.PlanText();
        agent.ClearPlan();
        var line = $"plan stopped ({why})" + (rest != null ? "; not done: " + rest : "");
        agent.RecordResult(line);
        agent.LogEvent(line);
        agent.WakeUp = true;
        Log.Info($"[{agent.Name}] {line}");
    }

    string Run(BotAgent agent, PlanStep step)
    {
        game.BeginCommand(agent.Id, step.Authority);
        var result = Tools.Execute(step.Call, agent, game, this);
        var line = $"{step.Call.Name}({Tools.ArgsToString(step.Call)}) -> {result}";
        if (step.Call.Name != "keep_plan") agent.RecordResult(line);
        if (!TalkTools.Contains(step.Call.Name) && step.Call.Name != "set_goal" && result.StartsWith("ok") && !result.Contains("already"))
            agent.ActedSinceDecision = true;
        if (result.StartsWith("error")) agent.AddError(line);
        // the round log feeds the end-of-round summary: keep what matters, skip routine noise
        if (step.Call.Name is not ("inspect" or "remember" or "return_to_default" or "wait" or "keep_gear" or "forget" or "keep_plan" or "set_goal")
            && !result.Contains("already")) agent.LogEvent(line);
        Log.Info($"[{agent.Name}] {line}");
        return result;
    }

    static readonly System.Text.RegularExpressions.Regex HeadingTo = new(@"heading to (.+?\[[^\]]+\])");
    static readonly System.Text.RegularExpressions.Regex RoomTag = new(@"\[[^\]]+\]");

    /// <summary>The room id the game resolved ("ok, heading to Engine Room [h57]").</summary>
    static string? ArrivalRoom(string result)
    {
        var m = HeadingTo.Match(result);
        return m.Success ? m.Groups[1].Value : null;
    }

    public static bool InRoom(string? current, string? target)
    {
        if (current == null || string.IsNullOrWhiteSpace(target)) return false;
        if (string.Equals(current, target, StringComparison.OrdinalIgnoreCase)) return true;
        var tag = RoomTag.Match(target);
        return tag.Success ? current.Contains(tag.Value, StringComparison.OrdinalIgnoreCase)
                           : current.StartsWith(target.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>go_to_room steps: go on with the plan once the bot is there.</summary>
    void CheckArrivals(double now)
    {
        foreach (var agent in agents.Values)
        {
            if (agent.Waiting != PlanWait.Arrival || now < agent.NextArrivalCheckAt) continue;
            agent.NextArrivalCheckAt = now + 1.0;
            if (!game.IsAlive(agent.Id)) { agent.ClearPlan(); continue; }
            if (InRoom(game.Observe(agent.Id).Room, agent.WaitRoom))
            {
                agent.Waiting = PlanWait.None;
                agent.WaitRoom = null;
                Advance(agent, now);
            }
            else if (now - agent.WaitingSince > Settings.Global.PlanStepTimeoutSeconds)
            {
                StopPlan(agent, $"did not reach {agent.WaitRoom} in {Settings.Global.PlanStepTimeoutSeconds:0}s");
            }
        }
    }

    /// <summary>A player gave or took back full AI control through the command menu.</summary>
    void DrainControl()
    {
        foreach (var e in game.DrainControlEvents())
        {
            if (!agents.TryGetValue(e.BotId, out var a))
            {
                game.SetAiControl(e.BotId, false); // not an AI agent (disabled job): nothing would act on it
                continue;
            }
            a.AiControl = e.Granted;
            a.ControlBy = e.Granted ? e.By : null;
            game.SetAiControl(a.Id, e.Granted);
            if (MemoryOn) memory.SetControl(a.Name, a.Job, e.Granted, e.By);
            a.ReceivePlayerOrder(e.Granted
                ? $"{e.By} handed you FULL CONTROL: from now on you decide everything yourself (see 'control'). Confirm briefly with say."
                : $"{e.By} took back full control: you are a normal crew member again. Confirm briefly with say.");
            a.LogEvent(e.Granted ? $"{e.By} gave me full control" : $"{e.By} took back full control");
            nextTick = double.NegativeInfinity;
            Log.Info($"[{a.Name}] full AI control {(e.Granted ? "given" : "taken back")} by {e.By}");
        }
    }

    /// <summary>Results of multi-step tasks (equip, craft, treat...) go back to the bot, which then thinks again.</summary>
    void DrainReports(double now)
    {
        foreach (var r in game.DrainReports())
        {
            if (!agents.TryGetValue(r.BotId, out var agent)) continue;
            agent.RecordResult(r.Text);
            agent.LogEvent(r.Text);
            bool failed = r.Text.Contains("failed") || r.Text.Contains("cancelled");
            if (failed) agent.AddError(r.Text);
            Log.Info($"[{agent.Name}] {r.Text}");
            if (agent.Waiting == PlanWait.Task)
            {
                agent.Waiting = PlanWait.None;
                if (failed && agent.Plan.Count > 0) { StopPlan(agent, "a task failed"); continue; }
                if (agent.Plan.Count > 0)
                {
                    // next step right away; the model sees all results when the plan is done
                    Advance(agent, now);
                    if (!agent.Busy) agent.WakeUp = true;
                    continue;
                }
            }
            agent.WakeUp = true;
        }
    }

    // ---------------------------------------------------------------- decisions

    void ScheduleDecisions(double now)
    {
        var candidates = new List<Candidate>();
        foreach (var agent in agents.Values)
        {
            try
            {
                var c = Evaluate(agent, now);
                if (c != null) candidates.Add(c);
            }
            catch (Exception e) { Log.Error($"[{agent.Name}] scheduling failed: {e}"); }
        }

        // Highest priority first, then whoever has waited longest since their last decision.
        foreach (var c in candidates.OrderBy(c => c.Tier).ThenBy(c => c.Agent.LastRequestAt).ThenBy(c => c.Agent.Id))
        {
            var g = Settings.Global;
            if (!router.TryReserve(ProviderFor(c.Agent), out var reservation, out _))
            {
                // Every slot is busy or a budget is spent: keep the turn (and the inbox), look again shortly.
                c.Agent.NextUrgencyCheckAt = now + g.RetrySeconds;
                if (c.Due) c.Agent.WakeUp = true;
                continue;
            }
            try
            {
                StartDecision(c.Agent, c.Obs, reservation!, now, routine: c.Tier == Tier.Routine && !c.Agent.HasPlayerOrder);
            }
            catch (Exception e)
            {
                reservation!.Dispose();
                c.Agent.Pending = false;
                Log.Error($"[{c.Agent.Name}] could not start decision: {e}");
            }
        }
    }

    Candidate? Evaluate(BotAgent agent, double now)
    {
        if (agent.Pending) return null;
        var g = Settings.Global;
        if (agent.RetrySoon)
        {
            // previous attempt never reached a model: allow another try after RetrySeconds instead of the full gap
            agent.RetrySoon = false;
            agent.LastRequestAt -= Math.Max(0, g.MinSecondsBetweenCallsPerBot - g.RetrySeconds);
            agent.WakeUp = true;
        }

        double since = now - agent.LastRequestAt;
        bool playerOrder = agent.HasPlayerOrder;
        // "order" autonomy: without full AI control a bot only thinks when asked (orders, messages, task results).
        bool autonomous = Settings.Bots.Autonomy == "always" || agent.AiControl;
        double interval = agent.AiControl ? Math.Min(g.DecisionIntervalSeconds, Settings.Bots.ControlDecisionIntervalSeconds) : g.DecisionIntervalSeconds;
        if (g.IdleBackoff && agent.IdleStreak > 0)
            interval = Math.Max(interval, Math.Min(interval * Math.Pow(2, Math.Min(agent.IdleStreak, 8)), g.MaxIdleIntervalSeconds));
        // A player's order is answered right away; everything else respects the minimum gap.
        if (!playerOrder && since < g.MinSecondsBetweenCallsPerBot) return null;
        if (playerOrder && since < Math.Min(g.RetrySeconds, g.MinSecondsBetweenCallsPerBot)) return null;

        // Mail is read at the next decision, not instantly: waking the recipient at once made bots answer
        // each other back and forth ("Roger!" - "Thanks!") faster than anything else happened.
        bool mailDue = agent.HasMail && since >= Math.Max(g.MinSecondsBetweenCallsPerBot, interval / 2);
        // Carrying out a plan: no routine thinking (and mail waits) until it is done or has run too long.
        bool busy = agent.Busy && g.SequentialActions;
        bool overdue = busy && now - agent.PlanStartedAt >= g.MaxPlanSeconds;
        bool due = playerOrder || agent.WakeUp || (busy ? overdue : mailDue || (autonomous && since >= interval));
        if (!due && !autonomous) return null;

        // Observe() walks hulls/gaps/characters, so between scheduled decisions only check urgency now and then.
        if (!due && now < agent.NextUrgencyCheckAt) return null;

        Observation obs = game.Observe(agent.Id);
        // mid-plan only a NEW urgent condition interrupts (the plan was made knowing the old ones)
        bool urgent = busy ? NewUrgency(obs.UrgencyKey, agent.PlanUrgency) : obs.Urgent;
        if (!due && !urgent)
        {
            agent.NextUrgencyCheckAt = now + g.UrgencyCheckSeconds;
            return null;
        }

        var tier = playerOrder ? Tier.PlayerOrder
                 : urgent || agent.HasMail || agent.WakeUp ? Tier.Urgent
                 : Tier.Routine;
        return new Candidate(agent, obs, tier, due);
    }

    static bool NewUrgency(string current, string known)
    {
        if (current.Length == 0) return false;
        var old = known.Split(',', StringSplitOptions.RemoveEmptyEntries);
        return current.Split(',').Any(f => !old.Contains(f));
    }

    void StartDecision(BotAgent agent, Observation obs, LlmRouter.Reservation reservation, double now, bool routine = false)
    {
        // nothing happened and the previous decision changed nothing either: think less often next time
        agent.IdleStreak = routine && !agent.ActedSinceDecision && agent.Decisions > 0 ? agent.IdleStreak + 1 : 0;
        agent.ActedSinceDecision = false;
        var g = Settings.Global;
        var takenOrders = agent.DrainOrders();
        obs.PlayerOrders = takenOrders.Select(o => o.Text).ToList();
        obs.Inbox = agent.DrainInbox();
        obs.Notes = agent.Notes(now);
        obs.LastActionResults = agent.TakeResults();
        if (agent.AiControl) obs.Control = $"FULL AI CONTROL (given by {agent.ControlBy ?? "a player"})";
        if (MemoryOn) obs.Memory = MemoryFor(agent, obs);
        if (agent.Goal != null && g.GoalTimeoutMinutes > 0 && now - agent.GoalSetAt > g.GoalTimeoutMinutes * 60)
        {
            // a forgotten goal ("come to me" from minutes ago) must not pin the bot for the rest of the round
            obs.LastActionResults.Add($"your goal '{agent.Goal}' expired after {g.GoalTimeoutMinutes:0} min; set it again only if it still matters");
            agent.SetGoal(null, now);
        }
        if (agent.Goal != null) obs.Goal = $"{agent.Goal} (set {ModStatus.Ago(now - agent.GoalSetAt)})";
        if (g.SequentialActions) obs.Plan = agent.PlanText();
        agent.DecisionUrgency = obs.UrgencyKey;
        foreach (var o in takenOrders) agent.LogEvent("order: " + o.Text);

        agent.WakeUp = false;
        agent.Pending = true;
        agent.LastRequestAt = now;

        string obsJson = g.CompactObservation ? obs.ToCompactJson() : obs.ToJson();
        string historyLine = obs.HistorySummary();
        if (takenOrders.Count > 0)
        {
            // Said in plain words before the JSON as well: small local models tend to miss it inside a long observation.
            obsJson = "NEW ORDER" + (takenOrders.Count > 1 ? "S" : "") + " - carry out now, before anything else:\n" +
                      string.Join("\n", takenOrders.Select(o => "- " + o.Text)) + "\n\nObservation:\n" + obsJson;
        }
        Settings.Bots.ExtraPromptByJob.TryGetValue(agent.Job, out var extra);
        string system = Prompts.System(agent, g.MaxActionsPerDecision, extra, agent.AiControl, MemoryOn,
            persona: PersonaPrompt(agent), language: Settings.Bots.SpeechLanguage);
        var history = agent.HistorySnapshot();
        var takenInbox = obs.Inbox;
        var takenResults = obs.LastActionResults;
        history.Add(new LlmTurn("user", obsJson));
        var tools = Tools.For(Settings.Bots.AllowedTools, agent.AiControl, MemoryOn);
        int botId = agent.Id;
        int gen = generation;
        var token = cts.Token;

        if (g.LogPrompts) Log.Info($"[{agent.Name}] -> {reservation.Provider}: {obsJson}");

        Track(Task.Run(async () =>
        {
            try
            {
                var r = await router.CompleteAsync(reservation, system, history, tools, token).ConfigureAwait(false);
                if (r.Status != RouteStatus.Ok || r.Response == null)
                {
                    Log.Warn($"[{agent.Name}] no decision ({r.Status}): {r.Detail}");
                    if (gen == Volatile.Read(ref generation))
                    {
                        agent.Restore(takenInbox, takenResults, takenOrders);
                        if (r.Status == RouteStatus.Limited || takenOrders.Count > 0) agent.RetrySoon = true;
                    }
                    return;
                }
                if (gen != Volatile.Read(ref generation)) return;

                var calls = r.Response.ToolCalls.Take(g.MaxActionsPerDecision).ToList();
                if (r.Response.Truncated)
                {
                    agent.RecordResult($"warning: your last reply was too long and got cut off, so part of it was lost. Use fewer and shorter calls (short say texts).");
                    WarnTruncated(r.Provider, r.Response);
                }
                // Only canonical calls go into the history: raw broken replies would teach the model to keep writing them.
                var summary = calls.Count == 0 ? "(no tool call)" : string.Join("; ", calls.Select(c => $"{c.Name}({Tools.ArgsToString(c)})"));
                // Past turns are kept short: the model only needs what happened then, not the whole old observation
                // (with historyTurns=6 the old observations were most of every request).
                agent.AddExchange(g.CompactObservation ? historyLine : obsJson, summary, g.HistoryTurns);
                agent.LastDecision = summary;
                agent.LastDecisionAt = clock();
                Interlocked.Increment(ref agent.Decisions);
                if (calls.Count == 0 && !string.IsNullOrWhiteSpace(r.Response.Text))
                {
                    agent.RecordResult("error: your last reply contained no valid tool call, so nothing happened. Reply only with tool calls.");
                    Log.Warn($"[{agent.Name}] reply had no recognizable tool call ({r.Provider}); if this repeats, set \"toolMode\": \"prompt\" for this provider. Reply: {Tools.Clip(JsonUtil.OneLine(r.Response.Text), 300)}");
                }
                // keep player orders visible after this exchange scrolls out of the history
                foreach (var o in takenOrders) agent.AddNote((o.Authority ? "Player order: " : "Order: ") + GoalText(o.Text), g.MaxNotes, clock());

                if (g.LogPrompts) Log.Info($"[{agent.Name}] <- {r.Provider}: {summary}" +
                    (g.LogRawReplies && !string.IsNullOrWhiteSpace(r.Response.Text) ? $"  [raw: {Tools.Clip(JsonUtil.OneLine(r.Response.Text), 500)}]" : ""));
                // A human player's order may replace orders given through the command menu (under full AI control the
                // game adapter allows that anyway, but a locked diving suit still needs a human's word).
                bool authority = takenOrders.Any(o => o.Authority);
                batches.Enqueue(new AgentBatch(botId, calls, authority, AutoGoal(takenOrders, calls)));
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { Log.Error($"[{agent.Name}] decision failed: {e.Message}"); }
            finally { agent.Pending = false; }
        }));
    }

    readonly ConcurrentDictionary<string, double> truncatedWarnedAt = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Worker thread. Says once a minute per provider that replies hit maxOutputTokens, and what to do.</summary>
    void WarnTruncated(string provider, LlmResponse res)
    {
        double now = clock();
        if (truncatedWarnedAt.TryGetValue(provider, out var at) && now - at < 60) return;
        truncatedWarnedAt[provider] = now;
        var cfg = Settings.Providers.FirstOrDefault(p => p.Name.Equals(provider, StringComparison.OrdinalIgnoreCase));
        var limit = cfg?.MaxOutputTokens.ToString() ?? "?";
        Log.Warn($"reply from '{provider}' was cut off at maxOutputTokens={limit} ({res.OutputTokens} output tokens" +
                 (res.ReasoningTokens > 0 ? $", {res.ReasoningTokens} of them spent on reasoning" : "") +
                 $"): calls were lost. Raise maxOutputTokens for '{provider}' (Models tab, e.g. 800-1500)" +
                 (res.ReasoningTokens > 0 || res.OutputTokens >= (cfg?.MaxOutputTokens ?? int.MaxValue) && res.ToolCalls.Count == 0
                     ? ", or turn the model's reasoning off (thinking: off; for some servers extraBody, e.g. {\"reasoning\": {\"enabled\": false}})" : "") + ".");
    }

    /// <summary>A player's order becomes the bot's goal, so it is not forgotten after a few small steps.</summary>
    static string? AutoGoal(List<PendingOrder> orders, List<ToolCall> calls)
    {
        if (orders.Count == 0 || calls.Any(c => c.Name == "set_goal")) return null;
        if (calls.Any(c => c.Name == "return_to_default")) return ""; // "dismissed"
        if (calls.All(c => TalkTools.Contains(c.Name))) return null;   // a greeting or a question, not a job
        return "carry out: " + string.Join(" | ", orders.Select(o => GoalText(o.Text)));
    }

    static readonly System.Text.RegularExpressions.Regex OrderNoise = new(
        @"\s*(Hint:.*$|\[also sent to:[^\]]*\]|\('me' = [^)]*\))", System.Text.RegularExpressions.RegexOptions.Singleline);

    /// <summary>The order without the dispatcher's routing notes and hints.</summary>
    public static string GoalText(string order) => OrderNoise.Replace(order, "").Trim();

    void Track(Task task)
    {
        inFlight.TryAdd(task, 0);
        task.ContinueWith(t => inFlight.TryRemove(t, out _), TaskScheduler.Default);
    }

    // ---------------------------------------------------------------- chat orders

    /// <summary>
    /// A human player wrote in the chat (game thread). Step 1: the dispatcher LLM picks the recipients
    /// among the bots that heard it. Step 2: each recipient handles the order with its own LLM call.
    /// </summary>
    public void SubmitChat(ChatCommand cmd)
    {
        var chat = Settings.Chat;
        if (!Settings.Enabled || !chat.Enabled) return;
        var text = cmd.Text.Trim();
        if (!string.IsNullOrEmpty(chat.Prefix))
        {
            if (!text.StartsWith(chat.Prefix, StringComparison.OrdinalIgnoreCase)) return;
            text = text.Substring(chat.Prefix.Length).Trim();
        }
        if (text.Length == 0) return;
        // "!command" of another mod, or one of ours with a typo: not an order for the crew
        if (text.StartsWith("!") || text.StartsWith("/"))
        {
            Log.Info($"chat from {cmd.SenderName}: \"{Tools.Clip(text, 60)}\" looks like a command, not passed to the crew");
            return;
        }
        cmd = cmd with { Text = Tools.Clip(text, 500) };

        if (pendingChats.Count >= Math.Max(1, chat.MaxQueue)) pendingChats.RemoveAt(0);
        pendingChats.Add((clock(), cmd));
        Log.Info($"chat from {cmd.SenderName}: \"{cmd.Text}\" heard by {cmd.ListenerIds.Count} bot(s)");
        nextTick = double.NegativeInfinity; // react on the next frame
    }

    void DispatchChats(double now)
    {
        var chat = Settings.Chat;
        for (int i = 0; i < pendingChats.Count;)
        {
            var (at, cmd) = pendingChats[i];
            if (now - at > chat.MaxAgeSeconds)
            {
                Log.Warn($"chat from {cmd.SenderName} dropped: could not be dispatched in {chat.MaxAgeSeconds}s");
                pendingChats.RemoveAt(i);
                continue;
            }

            var roster = BuildRoster(cmd);
            if (roster.Count == 0)
            {
                Log.Info($"chat from {cmd.SenderName}: no AI crew heard it");
                pendingChats.RemoveAt(i);
                continue;
            }

            if (roster.Count == 1 && chat.DirectIfSingleListener)
            {
                Deliver(cmd, roster, "only listener");
                pendingChats.RemoveAt(i);
                continue;
            }

            var provider = string.IsNullOrEmpty(chat.Provider) ? Settings.DefaultProvider : chat.Provider;
            if (!router.TryReserve(provider, out var reservation, out var why))
            {
                if (now - at >= chat.DispatchWaitSeconds)
                {
                    // no LLM slot for the dispatcher (limits, busy, provider down): route by names instead of dropping the order
                    Deliver(cmd, ChatDispatcher.Fallback(cmd, roster), "fallback: no free LLM slot for the dispatcher (" + why + ")");
                    pendingChats.RemoveAt(i);
                    continue;
                }
                i++; // try again next tick
                continue;
            }
            pendingChats.RemoveAt(i);
            StartDispatch(cmd, roster, reservation!);
        }
    }

    List<RosterEntry> BuildRoster(ChatCommand cmd)
    {
        var roster = new List<RosterEntry>();
        foreach (var id in cmd.ListenerIds.Distinct())
        {
            if (!agents.TryGetValue(id, out var a) || !game.IsAlive(id)) continue;
            var o = game.Observe(id);
            roster.Add(new RosterEntry(a.Id, a.Name, a.Job, o.Room, o.CurrentOrder));
        }
        return roster;
    }

    void StartDispatch(ChatCommand cmd, List<RosterEntry> roster, LlmRouter.Reservation reservation)
    {
        int gen = generation;
        var user = ChatDispatcher.UserPrompt(cmd, roster);
        if (Settings.Global.LogPrompts) Log.Info($"dispatch -> {reservation.Provider}: {user.Replace('\n', ' ')}");
        Track(Task.Run(async () =>
        {
            List<RosterEntry> recipients;
            string why;
            try
            {
                var r = await router.CompleteAsync(reservation, ChatDispatcher.SystemPrompt,
                    new[] { new LlmTurn("user", user) }, new[] { ChatDispatcher.Tool }, cts.Token).ConfigureAwait(false);
                var parsed = r.Status == RouteStatus.Ok ? ChatDispatcher.ParseRecipients(r.Response, roster) : null;
                if (parsed != null)
                {
                    recipients = parsed;
                    why = Tools.Str(r.Response!.ToolCalls.First(c => c.Name == ChatDispatcher.ToolName).Arguments, "reason") ?? "dispatcher";
                }
                else
                {
                    recipients = ChatDispatcher.Fallback(cmd, roster);
                    why = r.Status == RouteStatus.Ok ? "fallback: dispatcher gave no route" : "fallback: " + r.Detail;
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception e)
            {
                recipients = ChatDispatcher.Fallback(cmd, roster);
                why = "fallback: " + e.Message;
            }
            routedChats.Enqueue((gen, cmd, recipients));
            Log.Info($"dispatch <- {string.Join(", ", recipients.Select(x => x.Name))} ({why})");
        }));
    }

    void DeliverRoutedChats()
    {
        while (routedChats.TryDequeue(out var item))
        {
            if (item.Generation != generation) continue;
            Deliver(item.Cmd, item.Recipients, "dispatcher");
        }
    }

    void Deliver(ChatCommand cmd, IReadOnlyList<RosterEntry> recipients, string how)
    {
        if (recipients.Count == 0)
        {
            Log.Info($"chat from {cmd.SenderName}: not addressed to the AI crew ({how})");
            return;
        }
        foreach (var r in recipients)
        {
            if (!agents.TryGetValue(r.Id, out var a)) continue;
            a.ReceivePlayerOrder(ChatDispatcher.FormatOrder(cmd, recipients, r));
        }
        nextTick = double.NegativeInfinity;
        Log.Info($"order from {cmd.SenderName} -> {string.Join(", ", recipients.Select(x => x.Name))} ({how})");
    }

    // ---------------------------------------------------------------- bot-to-bot messages

    /// <summary>Delivers a bot-to-bot message. Called from the tool executor on the game thread.</summary>
    public string DeliverMessage(BotAgent from, string to, string text)
    {
        if (!from.TryConsumeMessageQuota(clock(), Settings.Global.MaxMessagesPerMinute))
            return $"error: you already sent {Settings.Global.MaxMessagesPerMinute} messages this minute; act instead of talking";
        var targets = ResolveTargets(from, to, out var label, out var error);
        if (targets == null)
        {
            // A human player can't read the AI inbox: say it to them out loud / on the radio instead.
            var player = FindPlayer(from, to);
            if (player == null) return error!;
            game.Speak(from.Id, Tools.Clip($"{player}, {text}", MaxChatEcho));
            from.LogEvent($"told {player}: {text}");
            return $"ok (said to player {player} over the radio; to talk to players you can also use say)";
        }

        string line = $"{from.Name}: {text}";
        foreach (var t in targets)
        {
            t.Receive(line);
            t.LogEvent("message " + line);
        }
        from.LogEvent($"told {label}: {text}");
        Echo(from, label, text);
        return targets.Count == 1 ? "ok" : $"ok (sent to {targets.Count})";
    }

    /// <summary>A bot under full AI control commands other AI crew. Their orders are binding but do not override players.</summary>
    public string OrderCrew(BotAgent from, string to, string order)
    {
        if (!from.AiControl) return "error: order_crew is only available under full AI control; use message_crew to ask";
        if (!from.TryConsumeMessageQuota(clock(), Settings.Global.MaxMessagesPerMinute))
            return $"error: you already sent {Settings.Global.MaxMessagesPerMinute} messages this minute; let them work";
        var targets = ResolveTargets(from, to, out var label, out var error);
        if (targets == null) return error!;

        foreach (var t in targets)
        {
            t.ReceivePlayerOrder($"Order from {from.Name} (in command: full AI control given by {from.ControlBy ?? "a player"}): {order}", authority: false);
            t.LogEvent($"order from {from.Name}: {order}");
        }
        from.LogEvent($"ordered {label}: {order}");
        Echo(from, label, order);
        nextTick = double.NegativeInfinity;
        return targets.Count == 1 ? "ok" : $"ok (ordered {targets.Count})";
    }

    /// <summary>Players hear what bots tell each other (on the radio or nearby, like say).</summary>
    readonly Queue<double> echoes = new();

    void Echo(BotAgent from, string label, string text)
    {
        if (!Settings.Global.ShowBotMessagesInChat) return;
        int cap = Settings.Global.MaxChatEchoPerMinute;
        double now = clock();
        while (echoes.Count > 0 && echoes.Peek() <= now - 60) echoes.Dequeue();
        if (cap > 0 && echoes.Count >= cap) return; // delivered, but not spoken: players are not flooded
        echoes.Enqueue(now);
        game.Speak(from.Id, Tools.Clip($"[→ {label}] {text}", MaxChatEcho));
    }

    /// <summary>A human player aboard whose name matches (exact, part, or transliterated), or null.</summary>
    string? FindPlayer(BotAgent from, string to)
    {
        List<string> players;
        try { players = game.Observe(from.Id).Crew.Where(c => c.IsPlayer).Select(c => c.Name).ToList(); }
        catch { return null; }
        var t = to.Trim();
        return players.FirstOrDefault(p => p.Equals(t, StringComparison.OrdinalIgnoreCase))
            ?? players.FirstOrDefault(p => p.Contains(t, StringComparison.OrdinalIgnoreCase) || t.Contains(p, StringComparison.OrdinalIgnoreCase))
            ?? players.FirstOrDefault(p => Translit.ToLatin(p).Equals(Translit.ToLatin(t), StringComparison.OrdinalIgnoreCase));
    }

    List<BotAgent>? ResolveTargets(BotAgent from, string to, out string label, out string? error)
    {
        var others = agents.Values.Where(a => a.Id != from.Id).ToList();
        error = null;
        label = to;
        if (string.Equals(to, "all", StringComparison.OrdinalIgnoreCase) || string.Equals(to, "everyone", StringComparison.OrdinalIgnoreCase))
        {
            label = "all";
            if (others.Count > 0) return others;
            error = "error: there are no other AI crew members";
            return null;
        }

        var exact = others.FirstOrDefault(a => a.Name.Equals(to, StringComparison.OrdinalIgnoreCase));
        var byJob = others.Where(a => a.Job.Equals(to, StringComparison.OrdinalIgnoreCase)).ToList();
        var partial = others.Where(a => a.Name.Contains(to, StringComparison.OrdinalIgnoreCase)).ToList();
        var targets = exact != null ? new List<BotAgent> { exact }
                    : byJob.Count > 0 ? byJob
                    : partial.Count == 1 ? partial
                    : new List<BotAgent>();
        if (targets.Count == 0)
        {
            error = partial.Count > 1
                ? $"error: '{to}' is ambiguous ({string.Join(", ", partial.Select(p => p.Name))}); use the full name"
                : $"error: no AI crew member named '{to}' (human players do not read private messages; use say)";
            return null;
        }
        if (targets.Count == 1) label = targets[0].Name;
        return targets;
    }

    /// <summary>The character's persona; without one, the game's own personality trait (if enabled).</summary>
    string? PersonaPrompt(BotAgent agent)
    {
        var p = personas.PromptFor(agent.Name);
        if (p != null) return p;
        if (Settings.Bots.UseGamePersonality && !string.IsNullOrWhiteSpace(agent.Trait))
            return $"Your personality trait: {agent.Trait}. Let it colour how you talk and react.";
        return null;
    }

    // ---------------------------------------------------------------- long-term memory

    MemoryView? MemoryFor(BotAgent agent, Observation obs)
    {
        var m = Settings.Memory;
        var bot = memory.Bot(agent.Name);
        var playersAboard = obs.Crew.Where(c => c.IsPlayer).Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var prefs = memory.Read(d => d.Players
            .Where(kv => playersAboard.Contains(kv.Key))
            .SelectMany(kv => kv.Value.Select(p => $"{kv.Key}: {p}"))
            .Take(12).ToList());
        var crewNames = obs.Crew.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var view = new MemoryView
        {
            Facts = bot?.Facts.Count > 0 ? bot.Facts.ToList() : null,
            PastRounds = bot?.Journal.Count > 0 ? bot.Journal.Skip(Math.Max(0, bot.Journal.Count - Math.Max(0, m.JournalInPrompt))).ToList() : null,
            // people aboard first, then the rest
            Relationships = bot?.Relationships.Count > 0
                ? bot.Relationships.OrderByDescending(kv => crewNames.Contains(kv.Key)).Take(10)
                    .Select(kv => $"{kv.Key}: {kv.Value.Feeling}" + (kv.Value.Note.Length > 0 ? " - " + kv.Value.Note : "")).ToList()
                : null,
            PlayerPreferences = prefs.Count > 0 ? prefs : null,
        };
        if (view.PastRounds?.Count == 0) view.PastRounds = null;
        return view.IsEmpty ? null : view;
    }

    public string Remember(BotAgent agent, string note, bool longTerm)
    {
        agent.AddNote(note, Settings.Global.MaxNotes, clock());
        if (!longTerm) return "ok";
        if (!MemoryOn) return "ok (long-term memory is disabled, kept for this round)";
        memory.AddFact(agent.Name, agent.Job, note, Settings.Memory.MaxFacts);
        return "ok, kept for future rounds";
    }

    public string Forget(BotAgent agent, string text)
    {
        if (!MemoryOn) return "error: long-term memory is disabled";
        int n = memory.ForgetFact(agent.Name, text);
        return n == 0 ? $"error: no long-term fact contains '{text}'" : $"ok, forgot {n}";
    }

    public string NoteRelationship(BotAgent agent, string person, string feeling, string note)
    {
        if (!MemoryOn) return "error: long-term memory is disabled";
        if (person.Equals(agent.Name, StringComparison.OrdinalIgnoreCase)) return "error: that is you";
        memory.SetRelationship(agent.Name, agent.Job, person.Trim(), feeling, note, Settings.Memory.MaxRelationships);
        agent.LogEvent($"feel {feeling} about {person}: {note}");
        return "ok";
    }

    public string NotePreference(BotAgent agent, string player, string preference)
    {
        if (!MemoryOn) return "error: long-term memory is disabled";
        memory.AddPreference(player.Trim(), preference, Settings.Memory.MaxPreferencesPerPlayer);
        return "ok, the whole AI crew will know";
    }

    /// <summary>
    /// Round over (game thread, before <see cref="Reset"/>): every bot writes a short summary of the round into its
    /// journal. One LLM call per bot when there is capacity, otherwise a plain summary from the round log.
    /// </summary>
    public void EndRound()
    {
        if (!MemoryOn) return;
        var m = Settings.Memory;
        foreach (var agent in agents.Values.ToList())
        {
            var log = agent.RoundLog();
            var notes = agent.Notes();
            if (log.Count == 0 && notes.Count == 0)
            {
                memory.AddJournal(agent.Name, agent.Job, "", m.MaxJournal, countRound: true);
                continue;
            }
            string fallback = PlainSummary(log);
            string provider = string.IsNullOrEmpty(m.SummaryProvider) ? ProviderFor(agent) : m.SummaryProvider;
            if (!m.RoundSummary)
            {
                memory.AddJournal(agent.Name, agent.Job, fallback, m.MaxJournal, countRound: true);
                continue;
            }
            var user = "Events of the round:\n- " + string.Join("\n- ", log) +
                       (notes.Count > 0 ? "\nYour notes:\n- " + string.Join("\n- ", notes) : "");
            var system = $"You are {agent.Name}, {agent.Job} on a submarine in Barotrauma. The round just ended. " +
                         $"Write ONE or two sentences (max {MemoryStore.MaxJournalLength} characters) for your long-term memory: what happened, " +
                         "who did what, what you learned. First person, past tense, in the language the crew speaks. Plain text only, no tool calls.";
            string name = agent.Name, job = agent.Job;
            int max = m.MaxJournal;
            Track(Task.Run(async () =>
            {
                string entry = fallback;
                try
                {
                    // Slots may be busy (local models often allow one request at a time): wait for one, but not forever.
                    LlmRouter.Reservation? reservation = null;
                    var deadline = DateTime.UtcNow.AddSeconds(SummaryWaitSeconds);
                    while (!router.TryReserve(provider, out reservation, out _))
                    {
                        if (DateTime.UtcNow >= deadline) throw new TimeoutException("no free LLM slot");
                        await Task.Delay(500, cts.Token).ConfigureAwait(false);
                    }
                    var r = await router.CompleteAsync(reservation!, system, new[] { new LlmTurn("user", user) }, Array.Empty<ToolSpec>(), cts.Token).ConfigureAwait(false);
                    var text = r.Status == RouteStatus.Ok ? JsonUtil.OneLine(r.Response?.Text ?? "").Trim().Trim('"') : "";
                    if (text.Length >= 10) entry = text;
                    else Log.Warn($"[{name}] round summary failed ({r.Status}): {r.Detail}; using the plain one");
                }
                catch (OperationCanceledException) { }
                catch (Exception e) { Log.Warn($"[{name}] round summary failed: {e.Message}; using the plain one"); }
                memory.AddJournal(name, job, entry, max, countRound: true);
                memory.Flush();
                Log.Info($"[{name}] journal: {entry}");
            }));
        }
        memory.Flush();
    }

    static string PlainSummary(List<string> log)
    {
        if (log.Count == 0) return "";
        // orders and finished tasks say the most about a round
        var important = log.Where(l => l.StartsWith("order") || l.StartsWith("task") || l.Contains("full control")).ToList();
        var pick = (important.Count > 0 ? important : log).TakeLast(4);
        return "Last round: " + string.Join("; ", pick);
    }

    public void Dispose()
    {
        cts.Cancel();
        router.Dispose();
    }
}
