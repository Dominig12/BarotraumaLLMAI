using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using LlmAgents.Llm;

namespace LlmAgents.Agents;

/// <summary>
/// When bots think. Nothing is polled on a timer: a bot asks its model when an event arrives (see <see cref="EventKind"/>).
/// Each tick, for every bot:
/// 1. implicit events are noted (an order or a message waiting in its queues);
/// 2. its situation is checked now and then, and only conditions that newly appear become events (a long fight is
///    one event, not one per second); when the danger is gone for a while, a "calm" event follows;
/// 3. the most important waiting event decides when it asks: after a short delay that lets related events arrive
///    (several chat lines, a burst of messages), and not more often than minSecondsBetweenCallsPerBot;
/// 4. while a plan is being carried out, only orders and new dangers interrupt it; everything else waits for its end.
/// Bots that must act on their own (full AI control, autonomy "always") also get a slow heartbeat that slows down
/// further while their decisions change nothing.
/// </summary>
public sealed partial class AgentManager
{
    /// <summary>A situation flag that disappeared for this long counts as new when it comes back. Long enough that a
    /// monster circling at the edge of the danger radius, or a walk through a wet room, is one event and not ten.</summary>
    const double FlagForgetSeconds = 60;
    /// <summary>No danger for this long, and a calm sub, = "the danger is over".</summary>
    const double CalmAfterSeconds = 30;

    sealed record Candidate(BotAgent Agent, int Tier);

    /// <summary>Whether this bot reacts to this kind of event, by autonomy.</summary>
    bool Accepts(BotAgent a, EventKind k)
    {
        if (a.AiControl) return true;
        if (IsDirected(a)) return k != EventKind.Heartbeat;   // the director hears of everything about its work
        return Settings.Bots.Autonomy switch
        {
            "order" => k is not (EventKind.Danger or EventKind.Calm or EventKind.Heartbeat or EventKind.RoundStart),
            "events" => k != EventKind.Heartbeat,
            _ => true,
        };
    }

    bool HeartbeatOn(BotAgent a) => !IsDirected(a) && (a.AiControl || Settings.Bots.Autonomy == "always");

    /// <summary>Adds an event if this bot reacts to it.</summary>
    void Notify(BotAgent a, EventKind kind, string text, double now)
    {
        if (Accepts(a, kind)) a.AddEvent(kind, text, now);
    }

    void ScheduleDecisions(double now)
    {
        var candidates = new List<Candidate>();
        foreach (var agent in agents.Values)
        {
            try
            {
                CollectEvents(agent, now);
                RouteToDirector(agent);
                var c = Due(agent, now);
                if (c != null) candidates.Add(c);
            }
            catch (Exception e) { Log.Error($"[{agent.Name}] scheduling failed: {e}"); }
        }

        // a player's message waits for the dispatcher: it gets the next free request, before the bots
        // (with one request at a time the dispatcher otherwise waited behind them until it gave up)
        if (holdForDispatch) return;

        // Most important first, then whoever has waited longest since their last decision.
        foreach (var c in candidates.OrderBy(c => c.Tier).ThenBy(c => c.Agent.LastRequestAt).ThenBy(c => c.Agent.Id))
        {
            // orders (tier 0) may use the whole budget; everything else leaves a part of it for them
            if (!router.TryReserve(ProviderFor(c.Agent), out var reservation, out var why, priority: c.Tier == 0))
            {
                // every slot is busy or a budget is spent: the events stay, try again next tick
                if (why.Contains("budget") || why.Contains("limit"))
                {
                    c.Agent.RetryNotBefore = now + Settings.Global.RetrySeconds;
                    // an order that has to wait: say so, instead of leaving the player wondering
                    if (c.Tier == 0 && now - c.Agent.LimitToldAt > 30)
                    {
                        c.Agent.LimitToldAt = now;
                        foreach (var from in c.Agent.OrderSenders())
                            Tell(from, $"{c.Agent.Name}: приказ ждёт, лимит запросов к модели ({why}). Пока бот действует как обычно.");
                    }
                }
                continue;
            }
            try
            {
                StartDecision(c.Agent, reservation!, now);
            }
            catch (Exception e)
            {
                reservation!.Dispose();
                c.Agent.Pending = false;
                Log.Error($"[{c.Agent.Name}] could not start decision: {e}");
            }
        }
    }

    /// <summary>Notes what happened to this bot since the last tick as events.</summary>
    void CollectEvents(BotAgent agent, double now)
    {
        var g = Settings.Global;
        // orders and messages put straight into the queues (other code, tests) are events too
        if (agent.HasPlayerOrder && !agent.HasEvent(EventKind.Order)) agent.AddEvent(EventKind.Order, "new order (see playerOrders)", now);
        if (agent.HasMail && !agent.HasEvent(EventKind.Mail)) agent.AddEvent(EventKind.Mail, "message from the crew (see inbox)", now);

        if (now >= agent.NextSituationCheckAt && Accepts(agent, EventKind.Danger))
        {
            agent.NextSituationCheckAt = now + Math.Max(0.2, g.UrgencyCheckSeconds);
            CheckSituation(agent, now);
        }

        if (agent.Plan is { Reviewed: false } plan && now - plan.StartedAt >= g.MaxPlanSeconds)
        {
            plan.Reviewed = true;
            agent.AddEvent(EventKind.PlanReview, $"your plan has been running for {ModStatus.Ago(now - plan.StartedAt).Replace(" ago", "")}: keep it (keep_plan) or change it", now);
        }

        if (HeartbeatOn(agent) && !agent.Busy && agent.Events.Count == 0 && !agent.Pending && now - agent.LastRequestAt >= HeartbeatInterval(agent))
            agent.AddEvent(EventKind.Heartbeat, agent.AiControl
                ? "routine check (full AI control): anything to plan or improve?"
                : "routine check: nothing new happened", now);
    }

    double HeartbeatInterval(BotAgent a)
    {
        var g = Settings.Global;
        double baseInterval = a.AiControl ? Settings.Bots.ControlDecisionIntervalSeconds : g.DecisionIntervalSeconds;
        if (!g.IdleBackoff || a.IdleStreak <= 0) return baseInterval;
        return Math.Max(baseInterval, Math.Min(baseInterval * Math.Pow(2, Math.Min(a.IdleStreak, 8)), g.MaxIdleIntervalSeconds));
    }

    /// <summary>Looks at the bot's situation; conditions that newly appear become a Danger event, the end of danger a Calm one.</summary>
    void CheckSituation(BotAgent agent, double now)
    {
        if (!game.IsAlive(agent.Id)) return;
        var obs = game.Observe(agent.Id);
        agent.LastObservation = obs;
        agent.LastObservationAt = now;

        var flags = Situation.Assess(obs, agent.Job, agent.AiControl);
        var fresh = flags.Where(f => !agent.FlagsSeen.TryGetValue(f.Key, out var seen) || now - seen > FlagForgetSeconds).ToList();
        foreach (var f in flags) agent.FlagsSeen[f.Key] = now;
        if (fresh.Count > 0) agent.AddEvent(EventKind.Danger, string.Join("; ", fresh.Select(f => f.Text)), now);

        // the danger is not over while the sub itself is in trouble, even where this bot stands it is dry
        if (flags.Any(f => f.Danger) || SubInDanger(obs))
        {
            agent.InDanger = true;
            agent.DangerGoneSince = double.NaN;
        }
        else if (agent.InDanger)
        {
            if (double.IsNaN(agent.DangerGoneSince)) agent.DangerGoneSince = now;
            else if (now - agent.DangerGoneSince >= CalmAfterSeconds)
            {
                agent.InDanger = false;
                agent.DangerGoneSince = double.NaN;
                Notify(agent, EventKind.Calm, "the danger is over (no monsters near the sub, no breaches, fire or flooding aboard): back to normal duties; diving gear can come off", now);
            }
        }
    }

    /// <summary>Monsters near the sub, breaches, a flooded room or a fire anywhere aboard.</summary>
    static bool SubInDanger(Observation o) =>
        o.Threats.Count > 0 || o.Leaks.Count > 0 || o.RoomFires > 0 || o.Rooms.Any(r => r.Fires > 0 || r.WaterPercent >= 50);

    /// <summary>Whether the bot should ask now, and with what priority.</summary>
    Candidate? Due(BotAgent agent, double now)
    {
        if (agent.Pending || agent.Events.Count == 0 || now < agent.RetryNotBefore) return null;
        var g = Settings.Global;
        var eligible = agent.Events.Where(e => !agent.Busy || EventPolicy.InterruptsPlan(e.Kind)).ToList();
        if (eligible.Count == 0) return null;

        int tier = eligible.Min(e => EventPolicy.Tier(e.Kind));
        // the first event whose delay is over starts the decision; the others (also the not yet due ones) go along
        double dueAt = eligible.Min(e => e.At + EventPolicy.Delay(e.Kind, Settings));
        if (now < dueAt) return null;
        // a player's order is answered almost at once; everything else keeps the minimum gap
        double gap = tier == 0 ? Math.Min(1.0, g.MinSecondsBetweenCallsPerBot) : g.MinSecondsBetweenCallsPerBot;
        if (now - agent.LastRequestAt < gap) return null;
        return new Candidate(agent, tier);
    }

    // ---------------------------------------------------------------- one decision

    /// <summary>What was taken from the agent for one request (put back if the request fails).</summary>
    /// <param name="Seq">The agent's request number: an answer that is not for its latest request is dropped.</param>
    sealed record DecisionRequest(BotAgent Agent, int Generation, int Seq, double StartedAt, List<AgentEvent> Events, List<PendingOrder> Orders,
        List<string> Inbox, List<string> Results, string HistoryLine, bool HeartbeatOnly, bool IsRetry);

    void StartDecision(BotAgent agent, LlmRouter.Reservation reservation, double now)
    {
        var g = Settings.Global;
        // looked at first: if the game call fails, nothing has been taken from the bot yet
        var obs = agent.LastObservation != null && now - agent.LastObservationAt < 0.3 ? agent.LastObservation : game.Observe(agent.Id);
        agent.LastObservation = null;

        var events = agent.Events.OrderBy(e => EventPolicy.Tier(e.Kind)).ThenBy(e => e.At).ToList();
        agent.Events.Clear();
        var orders = agent.DrainOrders();
        var inbox = agent.DrainInbox();
        var results = agent.TakeResults();
        try
        {
            var req = PrepareDecision(agent, obs, events, orders, inbox, results, reservation, now);
            Track(Task.Run(async () =>
            {
                RouteResult r;
                try
                {
                    r = await req.Router.CompleteAsync(reservation, req.System, req.History, req.Tools, req.Token, req.Boost).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception e) { r = new RouteResult(RouteStatus.Failed, null, reservation.Provider, e.Message); }
                OnGameThread(req.Request.Generation, t => ApplyDecision(req.Request, r, t));
            }));
        }
        catch
        {
            // the request never left: everything goes back, the bot tries again later
            agent.Restore(inbox, results, orders, events);
            agent.Pending = false;
            throw;
        }
    }

    sealed record PreparedDecision(DecisionRequest Request, LlmRouter Router, string System, List<LlmTurn> History,
        IReadOnlyList<ToolSpec> Tools, int Boost, System.Threading.CancellationToken Token);

    PreparedDecision PrepareDecision(BotAgent agent, Observation obs, List<AgentEvent> events, List<PendingOrder> orders,
        List<string> inbox, List<string> results, LlmRouter.Reservation reservation, double now)
    {
        var g = Settings.Global;
        obs.Why = events.Select(e => $"[{EventPolicy.Label(e.Kind)}] {e.Text}").ToList();
        obs.PlayerOrders = orders.Select(o => o.Text).ToList();
        obs.Inbox = inbox;
        obs.Notes = agent.Notes(now);
        obs.Conversation = ConversationFor(agent, now);
        obs.LastActionResults = results.ToList();
        obs.EarlierOrders = agent.PastOrders.Select(p => $"({ModStatus.Ago(now - p.At)}) {p.Text}").ToList();
        if (agent.AiControl) obs.Control = $"FULL AI CONTROL (given by {agent.ControlBy ?? "a player"})";
        if (MemoryOn) obs.Memory = MemoryFor(agent, obs);
        if (agent.CurrentGoal is { } goal && g.GoalTimeoutMinutes > 0 && now - goal.SetAt > g.GoalTimeoutMinutes * 60)
        {
            // a forgotten goal ("come to me" from minutes ago) must not pin the bot for the rest of the round
            obs.LastActionResults.Add($"your goal '{goal.Text}' expired after {g.GoalTimeoutMinutes:0} min; set it again only if it still matters");
            agent.SetGoal(null, now);
        }
        if (agent.CurrentGoal is { } cur) obs.Goal = $"{cur.Text} ({GoalSource(cur)}, set {ModStatus.Ago(now - cur.SetAt)})";
        obs.Plan = agent.PlanText();

        // Every permitted tool is described. The model decides which capability fits an order, in any language.
        var tools = ToolsFor(agent);
        string user = UserMessage(obs, obs.ToCompactJson());
        Settings.Bots.ExtraPromptByJob.TryGetValue(agent.Job, out var extra);
        string system = Prompts.System(agent, g.MaxActionsPerDecision, extra, agent.AiControl, MemoryOn,
            persona: PersonaPrompt(agent), language: Settings.Bots.SpeechLanguage);
        var history = agent.HistorySnapshot(now);
        history.Add(new LlmTurn("user", user));

        // from here on the request goes out
        foreach (var o in orders) agent.LogEvent("order: " + GoalText(o.Text));
        agent.Pending = true;
        agent.RequestSeq++;
        agent.LastRequestAt = now;
        agent.LastWhy = string.Join("; ", events.Select(e => $"{EventPolicy.Label(e.Kind)}: {Tools.Clip(e.Text, 80)}"));
        CountRequest();
        if (g.LogPrompts) Log.Info($"[{agent.Name}] -> {reservation.Provider}: {user}");

        // earlier turns are one short line each, not the whole old observation
        var req = new DecisionRequest(agent, generation, agent.RequestSeq, now, events, orders, inbox, results, obs.HistorySummary(),
            events.All(e => e.Kind == EventKind.Heartbeat), events.Any(e => e.Kind == EventKind.Retry));
        return new PreparedDecision(req, router, system, history, tools, agent.OutputBoost, cts.Token);
    }

    /// <summary>The user message: why the bot is asked, new orders in plain words (small models miss them inside a long
    /// JSON), then the observation.</summary>
    static string UserMessage(Observation obs, string json)
    {
        var sb = new StringBuilder();
        if (obs.Why.Count > 0)
        {
            sb.Append("Why you are asked now:\n");
            foreach (var w in obs.Why) sb.Append("- ").Append(w).Append('\n');
            sb.Append('\n');
        }
        if (obs.PlayerOrders.Count > 0)
        {
            sb.Append(obs.PlayerOrders.Count > 1 ? "NEW ORDERS" : "NEW ORDER").Append(" - carry out now, before anything else:\n");
            foreach (var o in obs.PlayerOrders) sb.Append("- ").Append(o).Append('\n');
            sb.Append('\n');
        }
        sb.Append("Observation:\n").Append(json);
        return sb.ToString();
    }

    /// <summary>Game thread: the answer of a decision request.</summary>
    void ApplyDecision(DecisionRequest req, RouteResult r, double now)
    {
        var agent = req.Agent;
        if (req.Seq != agent.RequestSeq)
        {
            Log.Warn($"[{agent.Name}] a late answer to an old request was dropped");
            return;
        }
        agent.Pending = false;
        if (!agents.ContainsKey(agent.Id)) return;
        var g = Settings.Global;

        agent.Stats.Requests++;
        CountTokens(r);
        if (r.Status != RouteStatus.Ok || r.Response == null)
        {
            agent.Stats.FailedRequests++;
            agent.AddActivity("error", $"request failed ({r.Provider}, {r.Status}): {Tools.Clip(r.Detail, 200)}", now);
            agent.FailedRequests++;
            Log.Warn($"[{agent.Name}] no decision ({r.Status}): {r.Detail}");
            agent.AddError($"no decision ({r.Status}): {Tools.Clip(r.Detail, 150)}");
            // nothing was used: everything goes back, and the bot tries again a little later
            agent.Restore(req.Inbox, req.Results, req.Orders, req.Events);
            double wait = r.Status == RouteStatus.Limited ? g.RetrySeconds : Math.Min(60, 5 * Math.Pow(2, Math.Min(agent.FailedRequests - 1, 4)));
            agent.RetryNotBefore = now + wait;
            if (req.Orders.Count > 0 && agent.FailedRequests >= 3) GiveUpOrders(agent, r.Detail);
            return;
        }
        agent.FailedRequests = 0;
        agent.Stats.TokensIn += r.Response.InputTokens;
        agent.Stats.TokensOut += r.Response.OutputTokens;
        agent.Stats.ReasoningTokens += r.Response.ReasoningTokens;
        agent.Stats.LatencySum += Math.Max(0, now - req.StartedAt);
        agent.Stats.Answered++;

        var calls = r.Response.ToolCalls.Take(g.MaxActionsPerDecision).ToList();
        // a call without any of its required arguments ({"name":"say"}) is a broken reply, not an action: left out, and
        // the bot is told, instead of failing the plan on it
        var empty = calls.Where(c => !IsPlanControl(c) && MissingAllRequired(agent, c)).ToList();
        if (empty.Count > 0)
        {
            calls.RemoveAll(empty.Contains);
            agent.RecordResult($"ignored {string.Join(", ", empty.Select(c => c.Name + "()"))}: called without the required arguments");
            Log.Info($"[{agent.Name}] ignored calls without arguments: {string.Join(", ", empty.Select(c => c.Name))}");
        }
        if (r.Response.Truncated)
        {
            agent.RecordResult("warning: your last reply was too long and got cut off, so part of it was lost. Use fewer and shorter calls (short say texts).");
            WarnTruncated(r.Provider, r.Response);
        }
        // a thinking model used the whole budget before writing its calls: the retry gets twice the room (up to 4096)
        var limit = Settings.Providers.FirstOrDefault(p => p.Name.Equals(r.Provider, StringComparison.OrdinalIgnoreCase))?.MaxOutputTokens ?? 0;
        bool boosted = false;
        if (calls.Count == 0 && (r.Response.Truncated || (limit > 0 && r.Response.OutputTokens >= limit * 0.9)))
        {
            int next = Math.Min(MaxOutputBoost, Math.Max(limit, agent.OutputBoost) * 2);
            boosted = next > Math.Max(limit, agent.OutputBoost);
            if (boosted) Log.Info($"[{agent.Name}] the reply ran out of output tokens; the retry may use up to {next}");
            agent.OutputBoost = next;
        }
        else if (calls.Count > 0) agent.OutputBoost = 0;
        // Only canonical calls go into the history: raw broken replies would teach the model to keep writing them.
        var summary = calls.Count == 0 ? "(no tool call)" : string.Join("; ", calls.Select(c => $"{c.Name}({Tools.ArgsToString(c)})"));
        // Past turns are kept short: what happened then and what the bot did, not the whole old observation.
        agent.AddExchange(req.HistoryLine, summary, g.HistoryTurns, req.StartedAt);
        agent.LastDecision = summary;
        agent.LastDecisionAt = now;
        agent.AddActivity("think", $"{(string.IsNullOrEmpty(agent.LastWhy) ? "" : "why: " + Tools.Clip(agent.LastWhy!, 160) + " → ")}{Tools.Clip(summary, 220)}" +
            $"  [{r.Provider}, {now - req.StartedAt:0.0}s, {r.Response.InputTokens}+{r.Response.OutputTokens} tok]", now);
        agent.Decisions++;
        if (g.LogPrompts) Log.Info($"[{agent.Name}] <- {r.Provider}: {summary}" +
            (g.LogRawReplies && !string.IsNullOrWhiteSpace(r.Response.Text) ? $"  [raw: {Tools.Clip(JsonUtil.OneLine(r.Response.Text), 500)}]" : ""));

        if (calls.Count == 0)
        {
            agent.RecordResult("error: your last reply contained no valid tool call, so nothing happened. Reply only with tool calls.");
            if (!string.IsNullOrWhiteSpace(r.Response.Text))
                Log.Warn($"[{agent.Name}] reply had no recognizable tool call ({r.Provider}); if this repeats, set \"toolMode\": \"prompt\" for this provider. Reply: {Tools.Clip(JsonUtil.OneLine(r.Response.Text), 300)}");
            // what made the bot think must not be lost to one broken (or cut off) reply: ask once more
            // (and again while a cut-off reply can still get more room)
            if ((!req.IsRetry || boosted) && !req.HeartbeatOnly)
            {
                agent.Restore(Array.Empty<string>(), Array.Empty<string>(), req.Orders, req.Events);
                agent.AddEvent(EventKind.Retry, "your last reply could not be used: answer again, with tool calls only", now);
            }
            else if (req.Orders.Count > 0) GiveUpOrders(agent, "the model gave no usable answer twice");
            return;
        }

        foreach (var o in req.Orders) agent.AddPastOrder(GoalText(o.Text), now);
        // A human player's order may replace orders given through the command menu (under full AI control the
        // game adapter allows that anyway, but a locked diving suit still needs a human's word).
        bool authority = req.Orders.Any(o => o.Authority);
        agent.ActedSinceDecision = false;
        Guarded(agent, "plan", () => ApplyPlan(agent, calls, authority, AutoGoal(req.Orders, calls, agent), now));
        // heartbeats that change nothing come less and less often; anything else resets the pace
        agent.IdleStreak = req.HeartbeatOnly && !agent.ActedSinceDecision ? agent.IdleStreak + 1 : 0;
    }

    /// <summary>Orders that could not get a decision after several tries: the senders are told, the vanilla AI goes on.</summary>
    void GiveUpOrders(BotAgent agent, string why)
    {
        var orders = agent.DrainOrders();
        agent.Events.RemoveAll(e => e.Kind is EventKind.Order or EventKind.Retry);
        foreach (var o in orders)
        {
            Log.Warn($"[{agent.Name}] order dropped ({why}): {GoalText(o.Text)}");
            agent.LogEvent("order dropped: " + GoalText(o.Text));
            if (o.From != null)
                Tell(o.From, $"{agent.Name} не может выполнить приказ через модель ({Tools.Clip(why, 80)}). Бот продолжает как обычный ИИ.");
        }
    }

    const int MaxOutputBoost = 4096;

    readonly System.Collections.Concurrent.ConcurrentDictionary<string, double> truncatedWarnedAt = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Says once a minute per provider that replies hit maxOutputTokens, and what to do.</summary>
    void WarnTruncated(string provider, LlmResponse res)
    {
        double now = clock();
        if (truncatedWarnedAt.TryGetValue(provider, out var at) && now - at < 60) return;
        truncatedWarnedAt[provider] = now;
        var cfg = Settings.Providers.FirstOrDefault(p => p.Name.Equals(provider, StringComparison.OrdinalIgnoreCase));
        var limit = cfg?.MaxOutputTokens.ToString() ?? "?";
        Log.Warn($"reply from '{provider}' was cut off at maxOutputTokens={limit} ({res.OutputTokens} output tokens" +
                 (res.ReasoningTokens > 0 ? $", {res.ReasoningTokens} of them spent on reasoning" : "") +
                 $"): calls were lost. Raise maxOutputTokens for '{provider}' (Models tab, e.g. {Math.Max(1500, (cfg?.MaxOutputTokens ?? 800) * 2)})" +
                 (res.ReasoningTokens > 0 || res.OutputTokens >= (cfg?.MaxOutputTokens ?? int.MaxValue) && res.ToolCalls.Count == 0
                     ? ", or turn the model's reasoning off (thinking: off; for some servers extraBody, e.g. {\"reasoning\": {\"enabled\": false}})" : "") + ".");
    }

    static string GoalSource(AgentGoal g) => g.Source == "you" ? "your own" : "from " + g.Source;

    /// <summary>A player's order becomes the bot's goal, so it is not forgotten after a few small steps.</summary>
    /// <returns>null = leave the goal; "" = clear it; text = the new goal.</returns>
    AgentGoal? AutoGoal(List<PendingOrder> orders, List<ToolCall> calls, BotAgent agent)
    {
        if (orders.Count == 0 || calls.Any(c => c.Name == "set_goal")) return null;
        if (calls.Any(c => c.Name == "return_to_default")) return new AgentGoal("", "", 0);   // "dismissed"
        if (calls.All(c => PassiveTool(agent, c.Name))) return null;                        // a greeting or a question, not a job
        var last = orders[^1];
        var source = last.From != null ? (last.Authority ? "player " + last.From : last.From + " (in command)") : "an order";
        return new AgentGoal(string.Join(" | ", orders.Select(o => GoalText(o.Text))), source, 0, last.Authority ? last.From : null);
    }

    static readonly System.Text.RegularExpressions.Regex OrderNoise = new(
        @"\s*(Hint:.*$|\[also sent to:[^\]]*\]|\('me' = [^)]*\))", System.Text.RegularExpressions.RegexOptions.Singleline);

    /// <summary>The order without the dispatcher's routing notes and hints.</summary>
    public static string GoalText(string order) => OrderNoise.Replace(order, "").Trim();

    static readonly System.Text.RegularExpressions.Regex QuotedText = new("\"(.+)\"", System.Text.RegularExpressions.RegexOptions.Singleline);

    /// <summary>What the player actually said: «Daniil (Captain) on radio: "Ольга, перезаряди реактор"» -> «Ольга, перезаряди реактор».</summary>
    public static string Quoted(string order)
    {
        var m = QuotedText.Match(order);
        return m.Success ? m.Groups[1].Value : order;
    }
}
