using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LlmAgents.Llm;

namespace LlmAgents.Agents;

/// <summary>
/// Carrying out replies. Every reply becomes one plan, a <see cref="PlanTree"/>: plain calls are a sequence, a "plan"
/// call adds conditions, waits, repeats and fallbacks. One engine runs both: a step that starts a task, a walk or a
/// wait is waited for (task reports, arrivals and conditions are checked every tick) before the next one runs. A failed
/// step stops the plan unless a "try" catches it, and the bot thinks again; a reply that fails at once is asked again
/// only once, so a confused model cannot loop. Orders a player gives through the command menu cancel the plan (the game
/// has already cancelled the running task), so the player's word is never fought by an old plan.
/// </summary>
public sealed partial class AgentManager
{
    enum Progress { Waiting, Done, Failed }

    /// <summary>Calls that are about the plan itself, not steps of it.</summary>
    static bool IsPlanControl(ToolCall c) => c.Name is "plan" or "update_plan" or "keep_plan";

    /// <summary>Tools a plan tree may contain.</summary>
    static readonly IReadOnlyCollection<string> PlanToolNames = Tools.All.Select(t => t.Name).ToHashSet();

    // ---------------------------------------------------------------- a reply

    void ApplyPlan(BotAgent agent, List<ToolCall> calls, bool authority, AgentGoal? autoGoal, double now)
    {
        if (!game.IsAlive(agent.Id)) return;
        if (autoGoal is { } goal) agent.SetGoal(goal.Text, now, goal.Source, goal.Player);
        bool lookup = calls.Any(c => Tools.Def(c.Name)?.Kind == ToolKind.Lookup);
        bool rewrite = calls.Any(c => c.Name == "plan");

        // the running plan goes on: changed in one place (update_plan) or kept (keep_plan), this reply's other calls in between
        if (!rewrite && agent.Plan is { } running && calls.Any(c => c.Name is "update_plan" or "keep_plan"))
        {
            ContinueRunning(agent, running, calls, authority, lookup, now);
            return;
        }

        PlanTree tree;
        try
        {
            tree = rewrite ? PlanTree.Build(TreeSteps(calls), PlanToolNames)
                           : PlanTree.FromCalls(calls.Where(c => c.Name != "keep_plan"));
        }
        catch (FormatException e)
        {
            var line = "plan -> error: " + e.Message + ". Nothing was done; send the plan again, fixed";
            agent.RecordResult(line);
            agent.AddError(line);
            Log.Info($"[{agent.Name}] {line}");
            AskAgainOnce(agent, "your plan could not be read: " + Tools.Clip(e.Message, 160), now);
            return;
        }

        if (agent.Plan != null) Log.Info($"[{agent.Name}] new decision replaces the plan: {agent.PlanText()}");
        // the new plan starts now (a task still running goes on until a new task replaces it; its report just wakes the bot)
        var plan = new ActivePlan(tree, authority, now) { Lookup = lookup, FromReply = true };
        agent.SetPlan(plan);
        if (rewrite) Log.Info($"[{agent.Name}] plan: {tree.Describe(500)}");
        var progress = Advance(agent, plan, now);
        plan.FromReply = false;
        if (progress != Progress.Failed) agent.ErrorStreak = 0;
        Settle(agent, plan, progress, "plan done", now);
    }

    /// <summary>The calls of a reply with a "plan" call as one list of tree nodes, each in its place.</summary>
    static JsonArray TreeSteps(IEnumerable<ToolCall> calls)
    {
        var all = new JsonArray();
        foreach (var c in calls.Where(c => c.Name is not ("update_plan" or "keep_plan")))
        {
            if (c.Name != "plan")
            {
                all.Add(new JsonObject { ["do"] = c.Name, ["args"] = c.Arguments.DeepClone() });
                continue;
            }
            var steps = c.Arguments["steps"] ?? c.Arguments["plan"] ?? c.Arguments["tree"];
            if (steps is JsonArray arr) foreach (var n in arr) all.Add(n?.DeepClone());
            else if (steps != null) all.Add(steps.DeepClone());
        }
        return all;
    }

    void ContinueRunning(BotAgent agent, ActivePlan plan, List<ToolCall> calls, bool authority, bool lookup, double now)
    {
        foreach (var u in calls.Where(c => c.Name == "update_plan"))
        {
            var node = (Tools.Str(u.Arguments, "node") ?? Tools.Str(u.Arguments, "id") ?? "").Trim();
            var err = plan.Tree.Replace(node, u.Arguments["steps"], PlanToolNames);
            var line = $"update_plan(node={node}) -> {(err == null ? "ok, plan now: " + plan.Tree.Describe(300) : "error: " + err)}";
            agent.RecordResult(line);
            Log.Info($"[{agent.Name}] {line}");
            if (err != null) agent.AddError(line);
            // the wait may be gone: look at the tree again
            else if (plan.Waiting == PlanWait.Condition) plan.StopWaiting();
        }
        foreach (var c in calls.Where(c => !IsPlanControl(c))) Run(agent, c, authority);
        if (calls.Any(c => c.Name == "keep_plan")) plan.StartedAt = now;
        plan.Reviewed = false;
        plan.Lookup |= lookup;
        if (plan.Waiting == PlanWait.None) Continue(agent, plan, "plan done", now);
    }

    // ---------------------------------------------------------------- running steps

    /// <summary>Runs steps until one has to be waited for, the plan is done or it failed (then it is stopped already).</summary>
    Progress Advance(BotAgent agent, ActivePlan plan, double now)
    {
        while (plan.Waiting == PlanWait.None)
        {
            switch (plan.Tree.Next(ConditionContext(agent, plan, now)))
            {
                case PlanTree.ActAction act:
                    if (!RunStep(agent, plan, act, now)) return Progress.Failed;
                    break;
                case PlanTree.WaitAction w:
                    plan.WaitFor(PlanWait.Condition, now, deadline: now + w.Node.Timeout);
                    return Progress.Waiting;
                case PlanTree.FailedAction f:
                    StopPlan(agent, f.Reason, now);
                    return Progress.Failed;
                default:
                    return Progress.Done;
            }
        }
        return Progress.Waiting;
    }

    /// <returns>false = the step failed and the plan was stopped.</returns>
    bool RunStep(BotAgent agent, ActivePlan plan, PlanTree.ActAction act, double now)
    {
        var call = act.Call;
        // the room reached before was only on the way: its "stay there" order would pull the bot back after the job
        if (plan.TravelPending && Tools.MovesOn(call.Name))
        {
            game.EndTravel(agent.Id);
            plan.TravelPending = false;
        }
        var result = Run(agent, call, plan.Authority);
        if (IsError(result) && !SoftError(call.Name))
        {
            if (plan.Tree.Report(false, result) is not { } why) return true;   // a "try" caught it
            StopPlan(agent, $"step {act.NodeId} {call.Name} failed: {Tools.Clip(why, 120)}", now);
            return false;
        }
        if (!IsError(result) && TaskTools.Contains(call.Name) && result.StartsWith("ok, started"))
        {
            plan.WaitFor(PlanWait.Task, now);
            agent.TaskTool = call.Name;
        }
        // (a walk kept below a player's orders waits for them: the bot gets there later, not now, so it is not awaited)
        else if (!IsError(result) && call.Name == "go_to_room" && !result.Contains("already") && !result.Contains("priority lowered"))
            plan.WaitFor(PlanWait.Arrival, now, ArrivalRoom(result) ?? Tools.Str(call.Arguments, "room"));
        else plan.Tree.Report(true, result);   // done at once (an order, a word: a failed word does not stop the work)
        return true;
    }

    static bool IsError(string result) => result.StartsWith("error", StringComparison.Ordinal);

    /// <summary>A failed word, note or lookup (order_crew without an order, an empty say) does not stop the work around it:
    /// the error is in the results for the next turn, the plan goes on.</summary>
    static bool SoftError(string tool) => Tools.Def(tool)?.Kind is ToolKind.Talk or ToolKind.Memory or ToolKind.Lookup
        || tool is "order_crew" or "set_order_priority";

    void Continue(BotAgent agent, ActivePlan plan, string last, double now) =>
        Settle(agent, plan, Advance(agent, plan, now), last, now);

    /// <summary>A plan that is done is over. One that waited for something is finished properly (the player hears how it
    /// went, results that need a look get one); instant orders just go on in the game, a lookup among them is read now.</summary>
    void Settle(BotAgent agent, ActivePlan plan, Progress progress, string last, double now)
    {
        if (progress != Progress.Done || agent.Plan != plan) return;
        agent.ClearPlan();
        if (plan.HadWait) PlanFinished(agent, plan, last, now);
        else if (plan.Lookup) agent.AddEvent(EventKind.Lookup, "the answer to your inspect is ready (lastActionResults)", now);
    }

    IConditionContext ConditionContext(BotAgent agent, ActivePlan plan, double now) =>
        new PlanConditions.ObservationContext(() => Look(agent, now), now - plan.StartedAt, plan.Tree.LastOk, plan.Tree.LastResult,
            plan.Waiting == PlanWait.Task);

    /// <summary>The bot's situation for a condition: the situation check looks every second anyway, so a fresh
    /// observation from it is as good as a new one.</summary>
    Observation Look(BotAgent agent, double now)
    {
        if (agent.LastObservation != null && now - agent.LastObservationAt < 1.0) return agent.LastObservation;
        try { return game.Observe(agent.Id); }
        catch { return agent.LastObservation ?? new Observation(); }
    }

    // ---------------------------------------------------------------- what plans wait for

    /// <summary>go_to_room and wait_until steps: go on once the bot is there or the condition is true, fail after the timeout.</summary>
    void CheckWaits(double now)
    {
        foreach (var agent in agents.Values.ToList())
        {
            if (agent.Plan is not { Waiting: PlanWait.Arrival or PlanWait.Condition } plan || now < plan.NextCheckAt) continue;
            plan.NextCheckAt = now + 1.0;
            Guarded(agent, "plan", () =>
            {
                if (!game.IsAlive(agent.Id)) agent.ClearPlan();
                else if (plan.Waiting == PlanWait.Arrival) CheckArrival(agent, plan, now);
                else CheckCondition(agent, plan, now);
            });
        }
    }

    void CheckArrival(BotAgent agent, ActivePlan plan, double now)
    {
        var room = plan.WaitRoom;
        if (InRoom(game.RoomOf(agent.Id), room))
        {
            plan.StopWaiting();
            // the next steps decide whether the bot stays there: the "stay" order goes once one takes it elsewhere
            plan.TravelPending = true;
            plan.Tree.Report(true, $"arrived in {room}");
            Continue(agent, plan, $"arrived in {room}", now);
        }
        else if (now - plan.WaitingSince > Settings.Global.PlanStepTimeoutSeconds)
            Resume(agent, plan, false, $"did not reach {room} in {Settings.Global.PlanStepTimeoutSeconds:0}s", now);
    }

    void CheckCondition(BotAgent agent, ActivePlan plan, double now)
    {
        if (plan.Tree.TryResume(ConditionContext(agent, plan, now)))
        {
            plan.StopWaiting();
            Continue(agent, plan, "plan done", now);
        }
        else if (now >= plan.WaitDeadline)
        {
            var w = plan.Tree.CurrentWait;
            Resume(agent, plan, false, $"waited {w?.Timeout ?? 0:0}s, but '{w?.Cond.Text}' did not come true", now);
        }
    }

    /// <summary>What the plan waited for is over: it goes on, or stops when nothing catches a failure.</summary>
    void Resume(BotAgent agent, ActivePlan plan, bool ok, string result, double now)
    {
        plan.StopWaiting();
        if (plan.Tree.Report(ok, result) is { } why) StopPlan(agent, why, now);
        else Continue(agent, plan, result, now);
    }

    /// <summary>Results of multi-step tasks: the plan goes on, or stops.</summary>
    void DrainReports(double now)
    {
        foreach (var r in game.DrainReports())
        {
            if (!agents.TryGetValue(r.BotId, out var agent)) continue;
            bool failed = r.Text.Contains("failed") || r.Text.Contains("cancelled");
            agent.RecordResult(r.Text);
            agent.LogEvent(r.Text);
            agent.AddActivity(failed ? "task-failed" : "task", r.Text, now);
            if (failed)
            {
                agent.AddError(r.Text);
                agent.Stats.TasksFailed++;
            }
            else agent.Stats.TasksDone++;
            Log.Info($"[{agent.Name}] {r.Text}");

            if (agent.Plan is { Waiting: PlanWait.Task } plan)
                Guarded(agent, "task report", () =>
                {
                    plan.StopWaiting();
                    if (plan.Tree.Report(!failed, r.Text) is { } why)
                    {
                        StopPlan(agent, plan.Tree.HasPendingSteps ? "a task failed: " + Tools.Clip(why, 120) : r.Text, now);
                        agent.TaskTool = null;
                    }
                    else Continue(agent, plan, r.Text, now);
                });
            // a task outside of the current plan (replaced by a newer decision): only a failure matters
            else if (failed) agent.AddEvent(EventKind.TaskFailed, Tools.Clip(r.Text, 200), now);
        }
    }

    // ---------------------------------------------------------------- the end of a plan

    void StopPlan(BotAgent agent, string why, double now)
    {
        var plan = agent.Plan;
        var rest = plan is { Tree.HasPendingSteps: true } ? plan.Describe() : null;
        agent.ClearPlan();
        var line = $"plan stopped ({why})" + (rest != null ? "; not done: " + rest : "");
        agent.RecordResult(line);
        agent.LogEvent(line);
        Log.Info($"[{agent.Name}] {line}");
        // failed right after the reply: the reply itself was wrong (an unknown room, a wrong name...). One more try with
        // the error in view, then the bot waits for something new instead of looping.
        if (plan?.FromReply == true)
        {
            if (AskAgainOnce(agent, "your last reply had an error: " + Tools.Clip(why, 160), now)) return;
        }
        // with nothing left of the plan the failure itself is the news
        else agent.AddEvent(EventKind.TaskFailed, rest != null ? line : why, now);
        TellGoalOutcome(agent, false, why);
        EndDirectorGoal(agent, now);
    }

    /// <returns>true = the bot is asked again; false = it was asked again already, the failure stands.</returns>
    bool AskAgainOnce(BotAgent agent, string why, double now)
    {
        if (agent.ErrorStreak > 0 || agent.HasEvent(EventKind.TaskFailed)) return false;
        agent.ErrorStreak = 1;
        agent.AddEvent(EventKind.TaskFailed, why, now);
        return true;
    }

    /// <summary>
    /// A plan that waited for something is over. The plan was the answer to what the bot was asked, so a goal that came
    /// from an order is done (the player is told), and the model is not asked again just to say so. Only results that may
    /// need more work (a crafted or salvaged item, a lookup) and bots under full AI control get another look.
    /// </summary>
    void PlanFinished(BotAgent agent, ActivePlan plan, string last, double now)
    {
        bool look = agent.AiControl || plan.Lookup || agent.TaskTool is "craft" or "deconstruct";
        TellGoalOutcome(agent, true, last);
        agent.TaskTool = null;
        bool fromPlayer = agent.CurrentGoal is { Player: not null };
        EndDirectorGoal(agent, now);
        // a player's order carried out by vanilla orders goes on after the plan: with the director on, the goal stays
        // (until it expires), so the director does not hand the bot other work in the middle of it
        bool keepPlayerGoal = fromPlayer && !plan.HadWait && Settings.Director.Enabled;
        if (look && !IsDirected(agent)) agent.AddEvent(EventKind.TaskDone, "your plan is done: " + Tools.Clip(last, 160), now);
        else if (fromPlayer && !keepPlayerGoal) agent.SetGoal(null, now);
        // the director hears that a bot is free again (also after a player's order)
        if (IsDirected(agent))
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

    // ---------------------------------------------------------------- one call

    /// <summary>The calls that leave no trace in the end-of-round summary (routine noise).</summary>
    static readonly HashSet<string> NotLogged = new() { "inspect", "recall", "remember", "return_to_default", "wait", "keep_gear", "forget", "keep_plan", "set_goal" };

    string Run(BotAgent agent, ToolCall call, bool authority)
    {
        game.BeginCommand(agent.Id, authority);
        agent.UsedTool(call.Name, clock());
        var result = Tools.Execute(call, agent, game, this);
        NoteSpeech(agent, call, result);
        var line = $"{call.Name}({Tools.ArgsToString(call)}) -> {result}";
        bool error = IsError(result);
        agent.Stats.Call(call.Name, error);
        if (call.Name != "keep_plan")
        {
            agent.RecordResult(line);
            agent.AddActivity(error ? "error" : "do", line, clock());
        }
        if (!TalkTools.Contains(call.Name) && result.StartsWith("ok") && !result.Contains("already")) agent.ActedSinceDecision = true;
        if (error) agent.AddError(line);
        if (!NotLogged.Contains(call.Name) && !result.Contains("already")) agent.LogEvent(line);
        Log.Info($"[{agent.Name}] {line}");
        return result;
    }

    static readonly Regex HeadingTo = new(@"heading to (.+?\[[^\]]+\])", RegexOptions.Compiled);
    static readonly Regex RoomTag = new(@"\[[^\]]+\]", RegexOptions.Compiled);

    /// <summary>The room id the game resolved ("ok, heading to Engine Room [h57]").</summary>
    static string? ArrivalRoom(string result) => HeadingTo.Match(result) is { Success: true } m ? m.Groups[1].Value : null;

    public static bool InRoom(string? current, string? target)
    {
        if (current == null || string.IsNullOrWhiteSpace(target)) return false;
        if (string.Equals(current, target, StringComparison.OrdinalIgnoreCase)) return true;
        var tag = RoomTag.Match(target);
        return tag.Success ? current.Contains(tag.Value, StringComparison.OrdinalIgnoreCase)
                           : current.StartsWith(target.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}
