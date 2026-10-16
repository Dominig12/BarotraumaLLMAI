using System;
using System.Collections.Generic;
using System.Linq;
using LlmAgents.Llm;

namespace LlmAgents.Agents;

/// <summary>
/// Carrying out replies. The calls of one reply are a plan: a multi-step task (equip, craft...) or go_to_room finishes
/// before the next step starts. A failed step cancels the rest and the bot thinks again; when a plan with tasks is done,
/// the bot looks at the results once. Orders a player gives through the command menu cancel the plan (the game has
/// already cancelled the running task), so the player's word is never fought by an old plan.
/// </summary>
public sealed partial class AgentManager
{
    void ApplyPlan(BotAgent agent, List<ToolCall> calls, bool authority, AgentGoal? autoGoal, double now)
    {
        if (!game.IsAlive(agent.Id)) return;
        if (autoGoal is { } goal) agent.SetGoal(goal.Text, now, goal.Source, goal.Player);
        bool keep = calls.Any(c => c.Name == "keep_plan");
        var steps = calls.Select(c => new PlanStep(c, authority)).ToList();
        bool lookup = calls.Any(c => Tools.Def(c.Name)?.Kind == ToolKind.Lookup);

        if (!Settings.Global.SequentialActions)
        {
            agent.ClearPlan();
            foreach (var step in steps) Run(agent, step);
            if (lookup) agent.AddEvent(EventKind.Lookup, "the answer to your inspect is ready (lastActionResults)", now);
            return;
        }

        // a plan with conditions: "plan" writes the tree, "update_plan" changes a part of it
        var planCall = calls.FirstOrDefault(c => c.Name == "plan");
        var updates = calls.Where(c => c.Name == "update_plan").ToList();
        if (planCall == null && updates.Count > 0 && agent.Tree != null)
        {
            foreach (var u in updates)
            {
                var node = Tools.Str(u.Arguments, "node") ?? Tools.Str(u.Arguments, "id") ?? "";
                var err = agent.Tree.Replace(node.Trim(), u.Arguments["steps"], PlanToolNames);
                var line = $"update_plan(node={node}) -> {(err == null ? "ok, plan now: " + agent.Tree.Describe(300) : "error: " + err)}";
                agent.RecordResult(line);
                Log.Info($"[{agent.Name}] {line}");
                if (err != null) { agent.AddError(line); agent.BatchErrors.Add(line); }
            }
            foreach (var step in steps.Where(s => s.Call.Name != "update_plan")) Run(agent, step);
            agent.PlanReviewed = false;
            if (agent.Waiting == PlanWait.Condition) agent.Waiting = PlanWait.None;   // the wait may be gone: look again
            if (Advance(agent, now) && !agent.Busy) PlanFinished(agent, "plan done", now);
            return;
        }
        if (planCall != null)
        {
            // the reply's other calls become steps of the tree, in their place
            var all = new System.Text.Json.Nodes.JsonArray();
            foreach (var c in calls.Where(c => c.Name != "update_plan" && c.Name != "keep_plan"))
            {
                if (c.Name == "plan")
                {
                    var s = c.Arguments["steps"] ?? c.Arguments["plan"] ?? c.Arguments["tree"];
                    if (s is System.Text.Json.Nodes.JsonArray arr) foreach (var n in arr) all.Add(n?.DeepClone());
                    else if (s != null) all.Add(s.DeepClone());
                }
                else all.Add(new System.Text.Json.Nodes.JsonObject { ["do"] = c.Name, ["args"] = c.Arguments.DeepClone() });
            }
            PlanTree tree;
            try { tree = PlanTree.Build(all, PlanToolNames); }
            catch (FormatException e)
            {
                var line = "plan -> error: " + e.Message + ". Nothing was done; send the plan again, fixed";
                agent.RecordResult(line);
                agent.AddError(line);
                Log.Info($"[{agent.Name}] {line}");
                if (agent.ErrorStreak == 0)
                {
                    agent.ErrorStreak = 1;
                    agent.AddEvent(EventKind.TaskFailed, "your plan could not be read: " + Tools.Clip(e.Message, 160), now);
                }
                return;
            }
            if (agent.Busy) Log.Info($"[{agent.Name}] new plan replaces: {agent.PlanText()}");
            agent.ClearPlan();
            agent.Tree = tree;
            agent.TreeAuthority = authority;
            agent.PlanStartedAt = now;
            agent.BatchErrors.Clear();
            agent.ErrorStreak = 0;
            Log.Info($"[{agent.Name}] plan: {tree.Describe(500)}");
            if (Advance(agent, now) && !agent.Busy) PlanFinished(agent, "plan done", now);
            return;
        }

        if (keep && agent.Busy)
        {
            // a word in between (say, message...), then the old plan goes on
            foreach (var step in steps) Run(agent, step);
            agent.PlanStartedAt = now;
            agent.PlanReviewed = false;
            return;
        }
        if (agent.Plan.Count > 0) Log.Info($"[{agent.Name}] new decision replaces planned steps: {agent.PlanText()}");
        // the new plan starts now (a task still running goes on until a new task replaces it; its report just wakes the bot)
        agent.ClearPlan();
        agent.Plan.AddRange(steps);
        agent.PlanStartedAt = now;
        agent.BatchErrors.Clear();
        Advance(agent, now);
        // a lookup at the end of a plan: read it right away (in the middle of a plan it waits for the plan's end)
        if (lookup && !agent.Busy) agent.AddEvent(EventKind.Lookup, "the answer to your inspect is ready (lastActionResults)", now);
        else if (lookup) agent.PlanLookup = true;
        // a call that failed at once (unknown room, wrong name...): one more try with the error in view, not a loop
        if (agent.BatchErrors.Count > 0 && !agent.Busy && !agent.HasEvent(EventKind.TaskFailed) && agent.ErrorStreak == 0)
        {
            agent.ErrorStreak = 1;
            agent.AddEvent(EventKind.TaskFailed, "your last reply had an error: " + Tools.Clip(agent.BatchErrors[0], 160), now);
        }
        else if (agent.BatchErrors.Count == 0) agent.ErrorStreak = 0;
    }

    /// <summary>Runs the plan's next steps until one has to be waited for (a task or walking to a room).</summary>
    /// <returns>false = a step failed (the bot was told and will think again), so the plan is not "done".</returns>
    bool Advance(BotAgent agent, double now)
    {
        while (agent.Waiting == PlanWait.None && (agent.Plan.Count > 0 || agent.Tree != null))
        {
            if (agent.Plan.Count == 0)
            {
                // the tree decides what comes next (conditions are checked against the bot's situation right now)
                var next = agent.Tree!.Next(ConditionContext(agent, now));
                switch (next)
                {
                    case PlanTree.ActAction a:
                        agent.Plan.Add(new PlanStep(a.Call, agent.TreeAuthority, a.NodeId));
                        continue;
                    case PlanTree.WaitAction w:
                        agent.Waiting = PlanWait.Condition;
                        agent.WaitingSince = now;
                        agent.WaitDeadline = now + w.Node.Timeout;
                        agent.NextConditionCheckAt = now + 1;
                        agent.PlanHadWait = true;
                        return true;
                    case PlanTree.FailedAction f:
                        StopPlan(agent, f.Reason, now);
                        return false;
                    default:
                        agent.Tree = null;   // done
                        return true;
                }
            }
            var step = agent.Plan[0];
            agent.Plan.RemoveAt(0);
            if (step.Node != null && agent.Tree != null)
            {
                if (agent.TravelPending && MovesOn(step.Call.Name)) { game.EndTravel(agent.Id); agent.TravelPending = false; }
                var r = Run(agent, step);
                if (r.StartsWith("error"))
                {
                    if (agent.Tree.Report(false, r) is { } why)
                    {
                        StopPlan(agent, $"step {step.Node} {step.Call.Name} failed: {Tools.Clip(why, 120)}", now);
                        return false;
                    }
                    continue;   // a "try" caught it
                }
                if (TaskTools.Contains(step.Call.Name) && r.StartsWith("ok, started"))
                {
                    agent.Waiting = PlanWait.Task;
                    agent.WaitingSince = now;
                    agent.PlanHadWait = true;
                    agent.TaskTool = step.Call.Name;
                }
                else if (step.Call.Name == "go_to_room" && r.StartsWith("ok") && !r.Contains("already"))
                {
                    agent.Waiting = PlanWait.Arrival;
                    agent.WaitRoom = ArrivalRoom(r) ?? Tools.Str(step.Call.Arguments, "room");
                    agent.WaitingSince = now;
                    agent.NextArrivalCheckAt = now;
                    agent.PlanHadWait = true;
                }
                else agent.Tree.Report(true, r);
                continue;
            }
            var result = Run(agent, step);
            if (result.StartsWith("error"))
            {
                if (agent.Plan.Count > 0) StopPlan(agent, $"step {step.Call.Name} failed", now);
                else if (agent.PlanHadWait)
                {
                    agent.AddEvent(EventKind.TaskFailed, $"the last step of your plan failed: {Tools.Clip(result, 160)}", now);
                    TellGoalOutcome(agent, false, result);
                }
                return false;
            }
            if (TaskTools.Contains(step.Call.Name) && result.StartsWith("ok, started"))
            {
                agent.Waiting = PlanWait.Task;
                agent.WaitingSince = now;
                agent.PlanHadWait = true;
                agent.TaskTool = step.Call.Name;
            }
            else if (step.Call.Name == "go_to_room" && agent.Plan.Count > 0)
            {
                agent.Waiting = PlanWait.Arrival;
                agent.WaitRoom = ArrivalRoom(result) ?? Tools.Str(step.Call.Arguments, "room");
                agent.WaitingSince = now;
                agent.NextArrivalCheckAt = now;
                agent.PlanHadWait = true;
            }
        }
        return true;
    }

    /// <summary>Tools a plan tree may contain.</summary>
    static readonly IReadOnlyCollection<string> PlanToolNames = Tools.All.Select(t => t.Name).ToHashSet();

    IConditionContext ConditionContext(BotAgent agent, double now)
    {
        // the situation check looks every second anyway: a fresh observation from it is as good as a new one
        Observation o;
        if (agent.LastObservation != null && now - agent.LastObservationAt < 1.0) o = agent.LastObservation;
        else
        {
            try { o = game.Observe(agent.Id); }
            catch { o = agent.LastObservation ?? new Observation(); }
        }
        return new PlanConditions.ObservationContext(o, now - agent.PlanStartedAt, agent.Tree?.LastOk, agent.Tree?.LastResult, agent.Waiting == PlanWait.Task);
    }

    /// <summary>wait_until steps of plan trees: go on when the condition comes true, fail the step after its timeout.</summary>
    void CheckConditions(double now)
    {
        foreach (var agent in agents.Values)
        {
            if (agent.Waiting != PlanWait.Condition || agent.Tree == null || now < agent.NextConditionCheckAt) continue;
            agent.NextConditionCheckAt = now + 1.0;
            if (!game.IsAlive(agent.Id)) { agent.ClearPlan(); continue; }
            var next = agent.Tree.Next(ConditionContext(agent, now));   // a met condition moves on by itself
            if (next is PlanTree.WaitAction && now < agent.WaitDeadline) continue;
            agent.Waiting = PlanWait.None;
            if (next is PlanTree.WaitAction w)
            {
                if (agent.Tree.Report(false, $"waited {w.Node.Timeout:0}s, but '{w.Node.Cond.Text}' did not come true") is { } why)
                {
                    StopPlan(agent, why, now);
                    continue;
                }
                if (Advance(agent, now) && !agent.Busy) PlanFinished(agent, "plan done", now);
                continue;
            }
            // the tree already gave the next thing to do: carry it out
            switch (next)
            {
                case PlanTree.ActAction a: agent.Plan.Add(new PlanStep(a.Call, agent.TreeAuthority, a.NodeId)); break;
                case PlanTree.FailedAction f: StopPlan(agent, f.Reason, now); continue;
                default: agent.Tree = null; break;
            }
            if (Advance(agent, now) && !agent.Busy) PlanFinished(agent, "plan done", now);
        }
    }

    void StopPlan(BotAgent agent, string why, double now)
    {
        var rest = agent.PlanText();
        agent.ClearPlan();
        var line = $"plan stopped ({why})" + (rest != null ? "; not done: " + rest : "");
        agent.RecordResult(line);
        agent.LogEvent(line);
        agent.AddEvent(EventKind.TaskFailed, line, now);
        TellGoalOutcome(agent, false, why);
        EndDirectorGoal(agent, now);
        Log.Info($"[{agent.Name}] {line}");
    }

    /// <summary>
    /// A plan with tasks is over. The plan was the answer to what the bot was asked, so a goal that came from an order is
    /// done (the player is told), and the model is not asked again just to say so. Only results that may need more
    /// work (a crafted or salvaged item) and bots under full AI control get another look.
    /// </summary>
    void PlanFinished(BotAgent agent, string last, double now)
    {
        bool look = agent.AiControl || agent.PlanLookup || agent.TaskTool is "craft" or "deconstruct";
        agent.PlanLookup = false;
        TellGoalOutcome(agent, true, last);
        agent.TaskTool = null;
        bool fromPlayer = agent.CurrentGoal is { Player: not null };
        EndDirectorGoal(agent, now);
        // a player's order carried out by vanilla orders goes on after the plan: with the director on, the goal stays
        // (until it expires), so the director does not hand the bot other work in the middle of it
        bool keepPlayerGoal = fromPlayer && !agent.PlanHadWait && Settings.Director.Enabled;
        if (look && !IsDirected(agent)) agent.AddEvent(EventKind.TaskDone, "your plan is done: " + Tools.Clip(last, 160), now);
        else if (fromPlayer && !keepPlayerGoal) agent.SetGoal(null, now);
        // the director hears that a bot is free again (also after a player's order)
        // (a plan of orders only is "done" at once while the bot works on them: no news)
        if (IsDirected(agent) && (fromPlayer || agent.PlanHadWait))
            agent.AddEvent(EventKind.TaskDone, (fromPlayer ? "finished a player's order, free for work: " : "plan done, free for work: ") + Tools.Clip(last, 160), now);
    }

    /// <summary>The player whose order the bot is carrying out hears how it went (not for walking and gear: they see that).</summary>
    void TellGoalOutcome(BotAgent agent, bool ok, string detail)
    {
        if (agent.CurrentGoal?.Player is not { } player) return;
        if (ok && agent.TaskTool is "come_to" or "equip") return;
        var what = Tools.Clip(Quoted(GoalText(agent.CurrentGoal.Text)), 70);
        Tell(player, ok
            ? $"{agent.Name}: выполнено ({what})"
            : $"{agent.Name}: не получилось ({what}): {Tools.Clip(detail, 100)}");
    }

    string Run(BotAgent agent, PlanStep step)
    {
        game.BeginCommand(agent.Id, step.Authority);
        agent.UsedTool(step.Call.Name, clock());
        var result = Tools.Execute(step.Call, agent, game, this);
        NoteSpeech(agent, step.Call, result);
        var line = $"{step.Call.Name}({Tools.ArgsToString(step.Call)}) -> {result}";
        if (step.Call.Name != "keep_plan") agent.RecordResult(line);
        if (!TalkTools.Contains(step.Call.Name) && result.StartsWith("ok") && !result.Contains("already"))
            agent.ActedSinceDecision = true;
        if (result.StartsWith("error"))
        {
            agent.AddError(line);
            agent.BatchErrors.Add(line);
        }
        // the round log feeds the end-of-round summary: keep what matters, skip routine noise
        if (step.Call.Name is not ("inspect" or "recall" or "remember" or "return_to_default" or "wait" or "keep_gear" or "forget" or "keep_plan" or "set_goal")
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

    /// <summary>Steps that take the bot somewhere else or give it other work (not talk, lookups or gear locks).</summary>
    static bool MovesOn(string tool) => Tools.MovesOn(tool);

    /// <summary>go_to_room steps: go on with the plan once the bot is there.</summary>
    void CheckArrivals(double now)
    {
        foreach (var agent in agents.Values)
        {
            if (agent.Waiting != PlanWait.Arrival || now < agent.NextArrivalCheckAt) continue;
            agent.NextArrivalCheckAt = now + 1.0;
            if (!game.IsAlive(agent.Id)) { agent.ClearPlan(); continue; }
            if (InRoom(game.RoomOf(agent.Id), agent.WaitRoom))
            {
                var room = agent.WaitRoom;
                agent.Waiting = PlanWait.None;
                agent.WaitRoom = null;
                if (agent.Tree != null)
                {
                    // in a tree the next steps are not known yet: the "stay here" order goes once one takes the bot elsewhere
                    agent.TravelPending = true;
                    agent.Tree.Report(true, $"arrived in {room}");
                }
                // the room was only on the way to the next job: its "stay here" order would pull the bot back
                // there after the job and keep it from its other orders
                else if (agent.Plan.Any(s => MovesOn(s.Call.Name))) game.EndTravel(agent.Id);
                if (Advance(agent, now) && !agent.Busy) PlanFinished(agent, $"arrived in {room}", now);
            }
            else if (now - agent.WaitingSince > Settings.Global.PlanStepTimeoutSeconds)
            {
                var why = $"did not reach {agent.WaitRoom} in {Settings.Global.PlanStepTimeoutSeconds:0}s";
                if (agent.Tree != null)
                {
                    agent.Waiting = PlanWait.None;
                    agent.WaitRoom = null;
                    if (agent.Tree.Report(false, why) == null)
                    {
                        if (Advance(agent, now) && !agent.Busy) PlanFinished(agent, why, now);
                        continue;
                    }
                }
                StopPlan(agent, why, now);
            }
        }
    }

    /// <summary>Results of multi-step tasks: the plan goes on, or the bot looks at the result.</summary>
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
            if (agent.Waiting == PlanWait.Task && agent.Tree != null)
            {
                agent.Waiting = PlanWait.None;
                if (agent.Tree.Report(!failed, r.Text) is { } why)
                {
                    StopPlan(agent, "a task failed: " + Tools.Clip(why, 120), now);
                    agent.TaskTool = null;
                    continue;
                }
                if (Advance(agent, now) && !agent.Busy) PlanFinished(agent, r.Text, now);
                continue;
            }
            if (agent.Waiting == PlanWait.Task)
            {
                agent.Waiting = PlanWait.None;
                if (failed)
                {
                    if (agent.Plan.Count > 0) StopPlan(agent, "a task failed: " + Tools.Clip(r.Text, 120), now);
                    else
                    {
                        agent.AddEvent(EventKind.TaskFailed, Tools.Clip(r.Text, 200), now);
                        TellGoalOutcome(agent, false, r.Text);
                    }
                    agent.TaskTool = null;
                    continue;
                }
                // next step right away; the model sees all results when it is asked next
                bool ok = agent.Plan.Count == 0 || Advance(agent, now);
                if (ok && !agent.Busy) PlanFinished(agent, r.Text, now);
                else if (!ok) agent.TaskTool = null;
                continue;
            }
            // a task outside of the current plan (replaced by a newer decision): only a failure matters
            if (failed) agent.AddEvent(EventKind.TaskFailed, Tools.Clip(r.Text, 200), now);
        }
    }

    /// <summary>A player gave or took back full AI control through the command menu.</summary>
    void DrainControl(double now)
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
            a.IdleStreak = 0;
            game.SetAiControl(a.Id, e.Granted);
            if (MemoryOn) memory.SetControl(a.Name, a.Job, e.Granted, e.By);
            a.AddEvent(EventKind.Control, e.Granted
                ? $"{e.By} handed you FULL CONTROL: from now on you decide everything yourself (see 'control'). Confirm briefly with say."
                : $"{e.By} took back full control: you are a normal crew member again. Confirm briefly with say.", now);
            a.LogEvent(e.Granted ? $"{e.By} gave me full control" : $"{e.By} took back full control");
            Log.Info($"[{a.Name}] full AI control {(e.Granted ? "given" : "taken back")} by {e.By}");
        }
    }

    /// <summary>Orders players gave through the command menu: they win over the bot's own plan.</summary>
    void DrainMenuOrders(double now)
    {
        foreach (var m in game.DrainMenuOrders())
        {
            if (!agents.TryGetValue(m.BotId, out var a)) continue;
            if (m.PriorityOnly)
            {
                // a player moved one of the bot's orders up or down: noted, the plan and the goal stay
                a.AddPastOrder($"{m.By} (command menu): {m.Order}", now);
                Log.Info($"[{a.Name}] {m.By} changed an order's priority: {m.Order}");
                continue;
            }
            if (a.Busy) Log.Info($"[{a.Name}] command menu order from {m.By} cancels the plan: {a.PlanText()}");
            a.ClearPlan();
            var text = m.Dismissal
                ? $"{m.By} dismissed your orders through the command menu ({m.Order}); do not give them to yourself again"
                : $"{m.By} gave you an order through the command menu: {m.Order}";
            a.AddPastOrder(text, now);
            a.SetGoal(m.Dismissal ? null : $"{m.Order} (command menu)", now, "player " + m.By);
            a.LogEvent(text);
            Log.Info($"[{a.Name}] {text}");
            if (Settings.Bots.ReactToMenuOrders)
                a.AddEvent(EventKind.MenuOrder, text + ". The game is already carrying it out; add steps only if they help (gear, tools), confirm briefly at most", now);
        }
    }
}
