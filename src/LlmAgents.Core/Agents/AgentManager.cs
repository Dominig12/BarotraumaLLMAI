using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LlmAgents.Llm;

namespace LlmAgents.Agents;

public sealed record AgentCommand(int BotId, ToolCall Call);

/// <summary>
/// Owns all agents. <see cref="Update"/> must be called every frame on the game thread; it is cheap
/// (it only does work every TickSeconds). LLM calls run on the thread pool and only enqueue
/// commands, which are executed back on the game thread in the next Update.
/// </summary>
public sealed class AgentManager : IDisposable
{
    public ModSettings Settings { get; }
    readonly IGameApi game;
    readonly LlmRouter router;
    readonly Func<double> clock;
    readonly Dictionary<int, BotAgent> agents = new();
    readonly ConcurrentQueue<AgentCommand> commands = new();
    readonly ConcurrentDictionary<Task, byte> inFlight = new();
    readonly CancellationTokenSource cts = new();
    double nextTick = double.NegativeInfinity;
    int generation; // bumped on Reset, so late answers from a previous round are dropped

    public const int MaxCommandsPerUpdate = 32;

    public AgentManager(ModSettings settings, IGameApi game, LlmRouter router, Func<double>? clock = null)
    {
        Settings = settings;
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
    public BotAgent? GetAgent(int id) => agents.TryGetValue(id, out var a) ? a : null;

    /// <summary>For tests and diagnostics: completes when all in-flight LLM requests have finished.</summary>
    public Task WhenIdle() => Task.WhenAll(inFlight.Keys.ToArray());

    public void Reset()
    {
        Interlocked.Increment(ref generation);
        agents.Clear();
        while (commands.TryDequeue(out _)) { }
        nextTick = double.NegativeInfinity;
    }

    // ---------------------------------------------------------------- game thread

    public void Update()
    {
        if (!Settings.Enabled) return;
        double now = clock();
        if (now < nextTick) return;
        nextTick = now + Settings.Global.TickSeconds;

        DrainCommands();

        var seen = new HashSet<int>();
        foreach (var h in game.GetBots())
        {
            if (!Settings.IsJobEnabled(h.Job)) continue;
            seen.Add(h.Id);
            if (!agents.TryGetValue(h.Id, out var agent))
            {
                agent = new BotAgent(h);
                agents[h.Id] = agent;
                Log.Info($"agent created: {h.Name} ({h.Job}) -> provider '{Settings.ProviderFor(h.Job)}'");
            }
            else
            {
                agent.Name = h.Name;
                agent.Job = h.Job;
            }

            try { TryScheduleDecision(agent, now); }
            catch (Exception e) { Log.Error($"[{agent.Name}] scheduling failed: {e}"); }
        }

        foreach (var id in agents.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            Log.Info($"agent removed: {agents[id].Name}");
            agents.Remove(id);
        }
    }

    void DrainCommands()
    {
        int budget = MaxCommandsPerUpdate;
        while (budget-- > 0 && commands.TryDequeue(out var cmd))
        {
            if (!agents.TryGetValue(cmd.BotId, out var agent) || !game.IsAlive(cmd.BotId)) continue;
            var result = Tools.Execute(cmd.Call, agent, game, this);
            var line = $"{cmd.Call.Name}({Tools.ArgsToString(cmd.Call)}) -> {result}";
            agent.RecordResult(line);
            Log.Info($"[{agent.Name}] {line}");
        }
    }

    void TryScheduleDecision(BotAgent agent, double now)
    {
        if (agent.Pending) return;
        var g = Settings.Global;
        if (agent.RetrySoon)
        {
            // previous attempt never reached a model: allow another try after RetrySeconds instead of the full gap
            agent.RetrySoon = false;
            agent.LastRequestAt -= Math.Max(0, g.MinSecondsBetweenCallsPerBot - g.RetrySeconds);
            agent.WakeUp = true;
        }
        double since = now - agent.LastRequestAt;
        if (since < g.MinSecondsBetweenCallsPerBot) return;

        bool due = since >= g.DecisionIntervalSeconds || agent.WakeUp || agent.HasMail;

        // Observe() walks hulls/gaps/characters, so between scheduled decisions only check urgency now and then.
        if (!due && now < agent.NextUrgencyCheckAt) return;

        Observation obs = game.Observe(agent.Id);
        if (!due && !obs.Urgent)
        {
            agent.NextUrgencyCheckAt = now + g.UrgencyCheckSeconds;
            return;
        }

        string provider = Settings.ProviderFor(agent.Job);
        if (!router.TryReserve(provider, out var reservation, out var reason))
        {
            // Every slot is busy or a budget is spent: keep the turn (and the inbox), look again shortly.
            agent.NextUrgencyCheckAt = now + g.RetrySeconds;
            if (due) agent.WakeUp = true;
            return;
        }

        try
        {
            StartDecision(agent, obs, reservation!, now);
        }
        catch
        {
            reservation!.Dispose();
            agent.Pending = false;
            throw;
        }
    }

    void StartDecision(BotAgent agent, Observation obs, LlmRouter.Reservation reservation, double now)
    {
        var g = Settings.Global;
        obs.Inbox = agent.DrainInbox();
        obs.Notes = agent.Notes();
        obs.LastActionResults = agent.TakeResults();

        agent.WakeUp = false;
        agent.Pending = true;
        agent.LastRequestAt = now;

        string obsJson = obs.ToJson();
        Settings.Bots.ExtraPromptByJob.TryGetValue(agent.Job, out var extra);
        string system = Prompts.System(agent, g.MaxActionsPerDecision, extra);
        var history = agent.HistorySnapshot();
        var takenInbox = obs.Inbox;
        var takenResults = obs.LastActionResults;
        history.Add(new LlmTurn("user", obsJson));
        var tools = Tools.Filtered(Settings.Bots.AllowedTools);
        int botId = agent.Id;
        int gen = generation;
        var token = cts.Token;

        if (g.LogPrompts) Log.Info($"[{agent.Name}] -> {reservation.Provider}: {obsJson}");

        var task = Task.Run(async () =>
        {
            try
            {
                var r = await router.CompleteAsync(reservation, system, history, tools, token).ConfigureAwait(false);
                if (r.Status != RouteStatus.Ok || r.Response == null)
                {
                    Log.Warn($"[{agent.Name}] no decision ({r.Status}): {r.Detail}");
                    if (gen == Volatile.Read(ref generation))
                    {
                        agent.Restore(takenInbox, takenResults);
                        if (r.Status == RouteStatus.Limited) agent.RetrySoon = true;
                    }
                    return;
                }
                if (gen != Volatile.Read(ref generation)) return;

                var calls = r.Response.ToolCalls.Take(g.MaxActionsPerDecision).ToList();
                var summary = calls.Count == 0
                    ? (string.IsNullOrWhiteSpace(r.Response.Text) ? "(no action)" : Tools.Clip(r.Response.Text.Trim(), 300))
                    : string.Join("; ", calls.Select(c => $"{c.Name}({Tools.ArgsToString(c)})"));
                agent.AddExchange(obsJson, summary, g.HistoryTurns);

                if (g.LogPrompts) Log.Info($"[{agent.Name}] <- {r.Provider}: {summary}");
                foreach (var c in calls) commands.Enqueue(new AgentCommand(botId, c));
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { Log.Error($"[{agent.Name}] decision failed: {e.Message}"); }
            finally { agent.Pending = false; }
        });
        inFlight.TryAdd(task, 0);
        task.ContinueWith(t => inFlight.TryRemove(t, out _), TaskScheduler.Default);
    }

    /// <summary>Delivers a bot-to-bot message. Called from the tool executor on the game thread.</summary>
    public string DeliverMessage(BotAgent from, string to, string text)
    {
        if (!from.TryConsumeMessageQuota(clock(), Settings.Global.MaxMessagesPerMinute))
            return $"error: you already sent {Settings.Global.MaxMessagesPerMinute} messages this minute; act instead of talking";
        string line = $"{from.Name}: {text}";
        if (string.Equals(to, "all", StringComparison.OrdinalIgnoreCase) || string.Equals(to, "everyone", StringComparison.OrdinalIgnoreCase))
        {
            int n = 0;
            foreach (var a in agents.Values.Where(a => a.Id != from.Id)) { a.Receive(line); a.WakeUp = true; n++; }
            return n == 0 ? "error: there are no other AI crew members" : $"ok (sent to {n})";
        }
        var target = agents.Values.FirstOrDefault(a => a.Id != from.Id && a.Name.Equals(to, StringComparison.OrdinalIgnoreCase))
                  ?? agents.Values.FirstOrDefault(a => a.Id != from.Id && a.Name.Contains(to, StringComparison.OrdinalIgnoreCase));
        if (target == null) return $"error: no AI crew member named '{to}' (human players do not read private messages; use say)";
        target.Receive(line);
        target.WakeUp = true;
        return "ok";
    }

    public void Dispose()
    {
        cts.Cancel();
        router.Dispose();
    }
}
