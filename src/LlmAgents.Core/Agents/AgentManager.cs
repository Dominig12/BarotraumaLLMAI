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

/// <summary>
/// Owns all agents. <see cref="Update"/> must be called every frame on the game thread; it is cheap
/// (it only does work every TickSeconds), and each of its phases is isolated: a fault in one is logged (throttled)
/// and the others go on.
///
/// How it fits together (one file per part):
/// - AgentManager.Scheduling: bots think when something happens (<see cref="EventKind"/>): an order, a new danger,
///   a finished task, a message... Events close together become one request. LLM requests run on the thread pool;
///   their results are applied back on the game thread, so agent state is only ever touched there.
/// - AgentManager.Plans: every reply is a plan (<see cref="PlanTree"/>), carried out step by step by one engine.
/// - AgentManager.Inputs: what players do through the game's UI (full AI control, command-menu orders).
/// - AgentManager.Chat: chat lines, the conversation, and the dispatcher that decides who a message is for.
/// - AgentManager.Crew: bot-to-bot messages and orders from a bot under full AI control.
/// - AgentManager.Director: the optional director that hands out work to the whole crew.
/// - AgentManager.Memory: long-term memory and the end-of-round summaries.
/// - AgentManager.Monitor: the snapshot for the in-game bot monitor.
/// </summary>
public sealed partial class AgentManager : IDisposable
{
    public ModSettings Settings { get; private set; }
    readonly IGameApi game;
    LlmRouter router;
    readonly PersonaStore personas;
    readonly Func<double> clock;
    readonly MemoryStore memory;
    readonly Dictionary<int, BotAgent> agents = new();
    /// <summary>Work handed back to the game thread by finished LLM requests.</summary>
    readonly ConcurrentQueue<Action<double>> gameThread = new();
    readonly ConcurrentDictionary<Task, byte> inFlight = new();
    readonly CancellationTokenSource cts = new();
    double nextTick = double.NegativeInfinity;
    double nextFlush;
    int generation; // bumped on Reset, so late answers from a previous round are dropped
    int agentsCreated; // staggers the first look of bots at round start

    /// <summary>Tools that start a multi-step task: the plan's next step waits for its result.</summary>
    public static IReadOnlyCollection<string> TaskTools => Tools.TaskTools;
    /// <summary>Calls that only talk or note things down: they change nothing in the game.</summary>
    static IReadOnlyCollection<string> TalkTools => Tools.PassiveTools;
    /// <summary>Echoed bot-to-bot messages are clipped to this (the game's chat limit is about 200).</summary>
    public const int MaxChatEcho = 190;
    const double FlushSeconds = 15;
    /// <summary>How long a round summary waits for a free LLM slot before the plain summary is used.</summary>
    public const double SummaryWaitSeconds = 45;

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
    public MemoryStore Memory => memory;
    public PersonaStore Personas => personas;
    bool MemoryOn => Settings.Memory.Enabled;

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
            newRouter.CarryOver(old);
            router = newRouter;
            // give in-flight requests time to finish before their HTTP clients go away
            Task.Delay(TimeSpan.FromSeconds(180)).ContinueWith(_ => { try { old.Dispose(); } catch { } }, TaskScheduler.Default);
        }
        nextTick = double.NegativeInfinity;
    }

    /// <summary>Provider for this bot: its persona's provider if set and known, else by job, else the default.</summary>
    public string ProviderFor(BotAgent agent)
    {
        var p = personas.ProviderFor(agent.Name);
        return p != null && Settings.Providers.Any(x => x.Name.Equals(p, StringComparison.OrdinalIgnoreCase)) ? p : Settings.ProviderFor(agent.Job);
    }

    /// <summary>Selects the long-term memory for this round (one scope per campaign save). Call at round start.</summary>
    public void BeginRound(string scope)
    {
        if (MemoryOn)
        {
            memory.Open(scope);
            memory.RecoverPendingNotes();
        }
    }

    /// <summary>For tests and diagnostics: completes when all in-flight LLM requests have finished.</summary>
    public async Task WhenIdle()
    {
        while (!inFlight.IsEmpty) await Task.WhenAll(inFlight.Keys.ToArray()).ConfigureAwait(false);
    }

    public void Reset()
    {
        EndRound(summarize: false);
        foreach (var agent in agents.Values)
            if (agent.Plan is { } plan)
                try { StopAwaitedWork(agent, plan); } catch (Exception e) { Log.Warn("plan reset cleanup: " + e.Message); }
        Interlocked.Increment(ref generation);
        agents.Clear();
        objectTools.Clear();
        ResetMemoryRound();
        pendingChats.Clear();
        while (gameThread.TryDequeue(out _)) { }
        agentsCreated = 0;
        nextTick = double.NegativeInfinity;
        ResetDirector();
        conversation.Clear();
        tokensIn = tokensOut = 0;
        dispatchRequests = 0;
        roundStartedAt = double.NaN;
    }

    // ---------------------------------------------------------------- game thread

    public void Update()
    {
        if (!Settings.Enabled) return;
        double now = clock();
        if (double.IsNaN(roundStartedAt)) roundStartedAt = now;
        if (now < nextTick) return;
        nextTick = now + Settings.Global.TickSeconds;

        // Every phase on its own: a fault in one (a broken game call, a bug) must not stop the others, or the bots
        // would freeze until the round ends.
        Phase("agents", () => SyncAgents(now));
        Phase("control", () => DrainControl(now));
        Phase("menu orders", () => DrainMenuOrders(now));
        Phase("answers", () => RunGameThreadWork(now));     // apply only after current membership and human orders
        Phase("reports", () => DrainReports(now));
        Phase("waits", () => CheckWaits(now));
        Phase("watchdog", () => Watchdog(now));
        Phase("chat", () => DispatchChats(now));            // chat routing gets LLM slots before the bots
        Phase("decisions", () => ScheduleDecisions(now));
        Phase("director", () => ScheduleDirector(now));     // after the bots: players' orders to them get the slots first

        if (now >= nextFlush)
        {
            nextFlush = now + FlushSeconds;
            if (MemoryOn) Phase("memory", () => memory.Flush());
        }
    }

    static void Phase(string name, Action work)
    {
        try { work(); }
        catch (Exception e) { Log.ErrorThrottled("phase:" + name, $"update ({name}) failed: {e}"); }
    }

    /// <summary>One bot's piece of work. A fault stops only that bot's plan (so it cannot fail again every tick), and the
    /// bot hears of it and thinks again.</summary>
    void Guarded(BotAgent agent, string what, Action work)
    {
        try { work(); }
        catch (Exception e)
        {
            Log.ErrorThrottled($"{agent.Id}:{what}", $"[{agent.Name}] {what} failed: {e}");
            if (agent.Plan == null) return;
            try { StopAwaitedWork(agent, agent.Plan); } catch (Exception cleanup) { Log.Warn("plan error cleanup: " + cleanup.Message); }
            agent.ClearPlan();
            agent.TaskTool = null;
            agent.AddEvent(EventKind.TaskFailed, $"your plan was stopped by an internal error ({Tools.Clip(e.Message, 120)}); decide again", clock());
        }
    }

    /// <summary>A request that never came back (it cannot happen by design: every request ends with an answer, a failure
    /// or a timeout) must not silence the bot for the rest of the round.</summary>
    void Watchdog(double now)
    {
        double limit = Settings.Providers.Select(p => p.TimeoutSeconds).DefaultIfEmpty(30).Max() * 2 + 60;
        if (directorPending && now - directorLastRequestAt >= limit)
        {
            directorPending = false;
            directorSeq++;
            Log.Warn($"[director] its request has not come back for {now - directorLastRequestAt:0}s: it may be asked again");
        }
        foreach (var p in pendingChats.Where(p => p.InFlight && now - p.At >= limit).ToList())
        {
            pendingChats.Remove(p);
            Log.Warn($"chat from {p.Cmd.SenderName}: the dispatcher never answered; the message goes to everyone who heard it");
            var roster = BuildRoster(p.Cmd);
            if (roster.Count > 0) DeliverToListeners(p, roster, "the dispatcher never answered");
        }
        foreach (var a in agents.Values)
        {
            if (!a.Pending || now - a.LastRequestAt < limit) continue;
            a.Pending = false;
            a.RequestSeq++;   // its answer, should it still come, is dropped
            a.RetryNotBefore = now + Settings.Global.RetrySeconds;
            Log.Warn($"[{a.Name}] its request has not come back for {now - a.LastRequestAt:0}s: the bot may think again");
        }
    }

    void RunGameThreadWork(double now)
    {
        int budget = 64;
        while (budget-- > 0 && gameThread.TryDequeue(out var work))
        {
            try { work(now); }
            catch (Exception e) { Log.Error("applying an LLM answer failed: " + e); }
        }
    }

    /// <summary>Runs <paramref name="work"/> on the game thread in the next Update, unless the round changed meanwhile.</summary>
    void OnGameThread(int gen, Action<double> work) =>
        gameThread.Enqueue(now => { if (gen == generation) work(now); });

    void SyncAgents(double now)
    {
        var seen = new HashSet<int>();
        foreach (var h in game.GetBots())
        {
            if (!Settings.IsJobEnabled(h.Job) || !Settings.IsCharacterEnabled(h.Name)) continue;
            seen.Add(h.Id);
            if (!agents.TryGetValue(h.Id, out var agent))
            {
                agent = new BotAgent(h) { Clock = clock };
                agents[h.Id] = agent;
                RestoreInventoryLoadout(agent, now);
                Log.Info($"agent created: {h.Name} ({h.Job}) -> provider '{ProviderFor(agent)}'" + (personas.PromptFor(h.Name) != null ? " (persona)" : ""));
                // full AI control given in an earlier round stays until a player takes it back
                if (MemoryOn && memory.Bot(h.Name) is { AiControl: true } m)
                {
                    agent.AiControl = true;
                    agent.ControlBy = m.AiControlBy;
                    game.SetAiControl(h.Id, true);
                    Log.Info($"[{h.Name}] full AI control restored (given by {m.AiControlBy})");
                }
                // a first look around, staggered so the whole crew does not ask at the same moment
                agent.NextSituationCheckAt = now + (agentsCreated % 8) * 0.2;
                if (Settings.Bots.ThinkAtRoundStart && Accepts(agent, EventKind.RoundStart))
                    agent.AddEvent(EventKind.RoundStart, "the round has started: look around, get ready for your job", now + agentsCreated * 1.5);
                agentsCreated++;
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
            if (agents[id].Plan is { } plan)
                try { StopAwaitedWork(agents[id], plan); } catch (Exception e) { Log.Warn("removed agent plan cleanup: " + e.Message); }
            ArchiveMemory(agents[id]);
            agents.Remove(id);
            objectTools.Remove(id);
        }
    }

    void Track(Task task)
    {
        inFlight.TryAdd(task, 0);
        task.ContinueWith(t => inFlight.TryRemove(t, out _), TaskScheduler.Default);
    }

    // ---------------------------------------------------------------- status

    readonly Queue<double> requestTimes = new();
    long tokensIn, tokensOut;
    double roundStartedAt = double.NaN;

    /// <summary>Tokens of every request (bots, dispatcher, director) for the monitor.</summary>
    void CountTokens(RouteResult r)
    {
        if (r.Response == null) return;
        tokensIn += r.Response.InputTokens;
        tokensOut += r.Response.OutputTokens;
    }

    /// <summary>Counts an LLM request for "requests per minute" in the status.</summary>
    void CountRequest()
    {
        lock (requestTimes) requestTimes.Enqueue(clock());
    }

    public int RequestsLastMinute
    {
        get
        {
            double now = clock();
            lock (requestTimes)
            {
                while (requestTimes.Count > 0 && requestTimes.Peek() <= now - 60) requestTimes.Dequeue();
                return requestTimes.Count;
            }
        }
    }

    /// <summary>Snapshot for !llm status and the settings window (game thread).</summary>
    public ModStatus Status(IReadOnlyList<FeatureStatus> features)
    {
        double now = clock();
        var bots = agents.Values.OrderBy(a => a.Name).Select(a =>
        {
            BotBrief? o = null;
            try { if (game.IsAlive(a.Id)) o = game.Brief(a.Id); } catch { /* status must never fail */ }
            return new BotStatus(a.Id, a.Name, a.Job, ProviderFor(a), a.AiControl, personas.PromptFor(a.Name) != null,
                o?.CurrentOrder ?? "?", o?.CurrentTask, a.LastDecision,
                double.IsNaN(a.LastDecisionAt) ? null : now - a.LastDecisionAt, a.RecentErrors(), a.Pending, a.Decisions,
                a.CurrentGoal != null ? $"{a.CurrentGoal.Text} ({GoalSource(a.CurrentGoal)}, {ModStatus.Ago(now - a.CurrentGoal.SetAt)})" : null,
                a.PlanText(), a.LastWhy, o?.Doing,
                a.Events.Count > 0 ? string.Join("; ", a.Events.Select(e => EventPolicy.Label(e.Kind))) : null);
        }).ToList();
        if (DirectorFeature() is { } df) features = features.Append(df).ToList();
        return new ModStatus(ModInfo.Version, Settings.Enabled, features, router.Status(), router.GlobalGate.UsedToday,
            Settings.Global.DailyRequestBudget, bots, Settings.Bots.DisabledCharacters.ToList(), MemoryOn ? memory.Scope : null,
            RequestsLastMinute, Settings.Bots.Autonomy);
    }

    public void Dispose()
    {
        EndRound(summarize: false);
        foreach (var agent in agents.Values)
            if (agent.Plan is { } plan)
                try { StopAwaitedWork(agent, plan); } catch (Exception e) { Log.Warn("plan shutdown cleanup: " + e.Message); }
        memory.Flush();
        cts.Cancel();
        router.Dispose();
    }
}
