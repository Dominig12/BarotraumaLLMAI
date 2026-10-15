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
///
/// Scheduling is fair: every tick the bots that want to think are sorted by priority
/// (player order &gt; urgent / has mail &gt; routine) and then by how long they have waited,
/// and only then get LLM slots. With few slots and many bots nobody starves.
/// </summary>
public sealed class AgentManager : IDisposable
{
    public ModSettings Settings { get; }
    readonly IGameApi game;
    readonly LlmRouter router;
    readonly Func<double> clock;
    readonly Dictionary<int, BotAgent> agents = new();
    readonly ConcurrentQueue<AgentCommand> commands = new();
    readonly ConcurrentQueue<(int Generation, ChatCommand Cmd, List<RosterEntry> Recipients)> routedChats = new();
    readonly List<(double At, ChatCommand Cmd)> pendingChats = new();
    readonly ConcurrentDictionary<Task, byte> inFlight = new();
    readonly CancellationTokenSource cts = new();
    double nextTick = double.NegativeInfinity;
    int generation; // bumped on Reset, so late answers from a previous round are dropped

    public const int MaxCommandsPerUpdate = 32;

    enum Tier { PlayerOrder = 0, Urgent = 1, Routine = 2 }

    sealed record Candidate(BotAgent Agent, Observation Obs, Tier Tier, bool Due);

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
    public int PendingChatCount => pendingChats.Count;

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
        while (commands.TryDequeue(out _)) { }
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

        DrainCommands();
        SyncAgents();
        DeliverRoutedChats();
        DispatchChats(now);   // chat routing gets LLM slots before routine thinking
        ScheduleDecisions(now);
    }

    void SyncAgents()
    {
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
            if (!router.TryReserve(Settings.ProviderFor(c.Agent.Job), out var reservation, out _))
            {
                // Every slot is busy or a budget is spent: keep the turn (and the inbox), look again shortly.
                c.Agent.NextUrgencyCheckAt = now + g.RetrySeconds;
                if (c.Due) c.Agent.WakeUp = true;
                continue;
            }
            try
            {
                StartDecision(c.Agent, c.Obs, reservation!, now);
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
        // A player's order is answered right away; everything else respects the minimum gap.
        if (!playerOrder && since < g.MinSecondsBetweenCallsPerBot) return null;
        if (playerOrder && since < Math.Min(g.RetrySeconds, g.MinSecondsBetweenCallsPerBot)) return null;

        bool due = playerOrder || since >= g.DecisionIntervalSeconds || agent.WakeUp || agent.HasMail;

        // Observe() walks hulls/gaps/characters, so between scheduled decisions only check urgency now and then.
        if (!due && now < agent.NextUrgencyCheckAt) return null;

        Observation obs = game.Observe(agent.Id);
        if (!due && !obs.Urgent)
        {
            agent.NextUrgencyCheckAt = now + g.UrgencyCheckSeconds;
            return null;
        }

        var tier = playerOrder ? Tier.PlayerOrder
                 : obs.Urgent || agent.HasMail || agent.WakeUp ? Tier.Urgent
                 : Tier.Routine;
        return new Candidate(agent, obs, tier, due);
    }

    void StartDecision(BotAgent agent, Observation obs, LlmRouter.Reservation reservation, double now)
    {
        var g = Settings.Global;
        obs.PlayerOrders = agent.DrainPlayerOrders();
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
        var takenOrders = obs.PlayerOrders;
        var takenInbox = obs.Inbox;
        var takenResults = obs.LastActionResults;
        history.Add(new LlmTurn("user", obsJson));
        var tools = Tools.Filtered(Settings.Bots.AllowedTools);
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
                // Only canonical calls go into the history: raw broken replies would teach the model to keep writing them.
                var summary = calls.Count == 0 ? "(no tool call)" : string.Join("; ", calls.Select(c => $"{c.Name}({Tools.ArgsToString(c)})"));
                agent.AddExchange(obsJson, summary, g.HistoryTurns);
                if (calls.Count == 0 && !string.IsNullOrWhiteSpace(r.Response.Text))
                {
                    agent.RecordResult("error: your last reply contained no valid tool call, so nothing happened. Reply only with tool calls.");
                    Log.Warn($"[{agent.Name}] reply had no recognizable tool call ({r.Provider}); if this repeats, set \"toolMode\": \"prompt\" for this provider. Reply: {Tools.Clip(JsonUtil.OneLine(r.Response.Text), 300)}");
                }
                // keep player orders visible after this exchange scrolls out of the history
                foreach (var o in takenOrders) agent.AddNote("Player order: " + o, g.MaxNotes);

                if (g.LogPrompts) Log.Info($"[{agent.Name}] <- {r.Provider}: {summary}" +
                    (g.LogRawReplies && !string.IsNullOrWhiteSpace(r.Response.Text) ? $"  [raw: {Tools.Clip(JsonUtil.OneLine(r.Response.Text), 500)}]" : ""));
                foreach (var c in calls) commands.Enqueue(new AgentCommand(botId, c));
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { Log.Error($"[{agent.Name}] decision failed: {e.Message}"); }
            finally { agent.Pending = false; }
        }));
    }

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
            if (!router.TryReserve(provider, out var reservation, out _))
            {
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
        string line = $"{from.Name}: {text}";
        var others = agents.Values.Where(a => a.Id != from.Id).ToList();

        List<BotAgent> targets;
        if (string.Equals(to, "all", StringComparison.OrdinalIgnoreCase) || string.Equals(to, "everyone", StringComparison.OrdinalIgnoreCase))
        {
            targets = others;
            if (targets.Count == 0) return "error: there are no other AI crew members";
        }
        else
        {
            var exact = others.FirstOrDefault(a => a.Name.Equals(to, StringComparison.OrdinalIgnoreCase));
            var byJob = others.Where(a => a.Job.Equals(to, StringComparison.OrdinalIgnoreCase)).ToList();
            var partial = others.Where(a => a.Name.Contains(to, StringComparison.OrdinalIgnoreCase)).ToList();
            targets = exact != null ? new() { exact }
                    : byJob.Count > 0 ? byJob
                    : partial.Count == 1 ? partial
                    : new();
            if (targets.Count == 0)
                return partial.Count > 1
                    ? $"error: '{to}' is ambiguous ({string.Join(", ", partial.Select(p => p.Name))}); use the full name"
                    : $"error: no AI crew member named '{to}' (human players do not read private messages; use say)";
        }

        foreach (var t in targets) { t.Receive(line); t.WakeUp = true; }
        return targets.Count == 1 ? "ok" : $"ok (sent to {targets.Count})";
    }

    public void Dispose()
    {
        cts.Cancel();
        router.Dispose();
    }
}
