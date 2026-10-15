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

/// <param name="PlayerAuthority">The decision carried out a human player's chat order.</param>
public sealed record AgentCommand(int BotId, ToolCall Call, bool PlayerAuthority = false);

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
    readonly ConcurrentQueue<AgentCommand> commands = new();
    readonly ConcurrentQueue<(int Generation, ChatCommand Cmd, List<RosterEntry> Recipients)> routedChats = new();
    readonly List<(double At, ChatCommand Cmd)> pendingChats = new();
    readonly ConcurrentDictionary<Task, byte> inFlight = new();
    readonly CancellationTokenSource cts = new();
    double nextTick = double.NegativeInfinity;
    double nextFlush;
    int generation; // bumped on Reset, so late answers from a previous round are dropped

    public const int MaxCommandsPerUpdate = 32;
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
                double.IsNaN(a.LastDecisionAt) ? null : now - a.LastDecisionAt, a.RecentErrors(), a.Pending, a.Decisions);
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
        DrainReports();
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

    void DrainCommands()
    {
        int budget = MaxCommandsPerUpdate;
        while (budget-- > 0 && commands.TryDequeue(out var cmd))
        {
            if (!agents.TryGetValue(cmd.BotId, out var agent) || !game.IsAlive(cmd.BotId)) continue;
            game.BeginCommand(cmd.BotId, cmd.PlayerAuthority);
            var result = Tools.Execute(cmd.Call, agent, game, this);
            var line = $"{cmd.Call.Name}({Tools.ArgsToString(cmd.Call)}) -> {result}";
            agent.RecordResult(line);
            if (result.StartsWith("error")) agent.AddError(line);
            // the round log feeds the end-of-round summary: keep what matters, skip routine noise
            if (cmd.Call.Name is not ("inspect" or "remember" or "return_to_default" or "wait" or "keep_gear" or "forget")
                && !result.Contains("already")) agent.LogEvent(line);
            Log.Info($"[{agent.Name}] {line}");
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
    void DrainReports()
    {
        foreach (var r in game.DrainReports())
        {
            if (!agents.TryGetValue(r.BotId, out var agent)) continue;
            agent.RecordResult(r.Text);
            agent.LogEvent(r.Text);
            if (r.Text.Contains("failed")) agent.AddError(r.Text);
            agent.WakeUp = true;
            Log.Info($"[{agent.Name}] {r.Text}");
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
        // "order" autonomy: without full AI control a bot only thinks when asked (orders, messages, task results).
        bool autonomous = Settings.Bots.Autonomy == "always" || agent.AiControl;
        double interval = agent.AiControl ? Math.Min(g.DecisionIntervalSeconds, Settings.Bots.ControlDecisionIntervalSeconds) : g.DecisionIntervalSeconds;
        // A player's order is answered right away; everything else respects the minimum gap.
        if (!playerOrder && since < g.MinSecondsBetweenCallsPerBot) return null;
        if (playerOrder && since < Math.Min(g.RetrySeconds, g.MinSecondsBetweenCallsPerBot)) return null;

        // Mail is read at the next decision, not instantly: waking the recipient at once made bots answer
        // each other back and forth ("Roger!" - "Thanks!") faster than anything else happened.
        bool mailDue = agent.HasMail && since >= Math.Max(g.MinSecondsBetweenCallsPerBot, interval / 2);
        bool due = playerOrder || agent.WakeUp || mailDue || (autonomous && since >= interval);
        if (!due && !autonomous) return null;

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
        var takenOrders = agent.DrainOrders();
        obs.PlayerOrders = takenOrders.Select(o => o.Text).ToList();
        obs.Inbox = agent.DrainInbox();
        obs.Notes = agent.Notes();
        obs.LastActionResults = agent.TakeResults();
        if (agent.AiControl) obs.Control = $"FULL AI CONTROL (given by {agent.ControlBy ?? "a player"})";
        if (MemoryOn) obs.Memory = MemoryFor(agent, obs);
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
                foreach (var o in takenOrders) agent.AddNote((o.Authority ? "Player order: " : "Order: ") + o.Text, g.MaxNotes);

                if (g.LogPrompts) Log.Info($"[{agent.Name}] <- {r.Provider}: {summary}" +
                    (g.LogRawReplies && !string.IsNullOrWhiteSpace(r.Response.Text) ? $"  [raw: {Tools.Clip(JsonUtil.OneLine(r.Response.Text), 500)}]" : ""));
                // A human player's order may replace orders given through the command menu (under full AI control the
                // game adapter allows that anyway, but a locked diving suit still needs a human's word).
                bool authority = takenOrders.Any(o => o.Authority);
                foreach (var c in calls) commands.Enqueue(new AgentCommand(botId, c, authority));
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
        agent.AddNote(note, Settings.Global.MaxNotes);
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
