using System;
using System.Collections.Generic;

namespace LlmAgents.Agents;

/// <summary>What a running plan is waiting for before its next step.</summary>
public enum PlanWait { None, Task, Arrival, Condition, Yield }

/// <summary>
/// The plan a bot is carrying out: always a <see cref="PlanTree"/>. A reply of plain calls is a tree of one sequence,
/// a "plan" call writes a tree with conditions; both run the same way, step by step (see AgentManager.Plans).
/// </summary>
public sealed class ActivePlan
{
    public ActivePlan(PlanTree tree, bool authority, double startedAt)
    {
        Tree = tree;
        Authority = authority;
        StartedAt = startedAt;
        LastReviewedAt = startedAt;
    }

    public PlanTree Tree { get; internal set; }
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public const int MaximumExecutedSteps = 1024;
    public const int MaximumImmediateSteps = 16;
    public int ExecutedSteps { get; set; }
    public long? AwaitedTaskId { get; set; }
    public bool TaskTimedOut { get; set; }
    public string? CancellationFailure { get; set; }
    /// <summary>The steps carry the authority of the reply that set the plan (a player's order may override menu orders).</summary>
    public bool Authority { get; }
    public double StartedAt { get; set; }
    /// <summary>Review scheduling is separate from elapsed plan time used in conditions.</summary>
    public double LastReviewedAt { get; set; }
    /// <summary>A "your plan runs long" review was already asked for.</summary>
    public bool Reviewed { get; set; }

    public PlanWait Waiting { get; private set; } = PlanWait.None;
    public double WaitingSince { get; private set; }
    /// <summary>go_to_room: the room to reach.</summary>
    public string? WaitRoom { get; private set; }
    /// <summary>wait_until: give up at this time.</summary>
    public double WaitDeadline { get; private set; }
    /// <summary>Arrivals and conditions are checked about once a second, not every tick.</summary>
    public double NextCheckAt { get; set; }

    /// <summary>The plan has waited for a task, a walk or a condition (it was more than instant orders).</summary>
    public bool HadWait { get; private set; }
    /// <summary>It looked something up: the answer is read when the plan is over.</summary>
    public bool Lookup { get; set; }
    /// <summary>A go_to_room arrived: its "stay there" order goes when a later step takes the bot elsewhere.</summary>
    public bool TravelPending { get; set; }
    /// <summary>The steps run right after the reply that set the plan: a failure now means the reply itself was wrong
    /// (asked again once), not that the world changed under a running plan.</summary>
    public bool FromReply { get; set; }

    public void WaitFor(PlanWait what, double now, string? room = null, double deadline = 0)
    {
        Waiting = what;
        TaskTimedOut = false;
        CancellationFailure = null;
        WaitingSince = now;
        WaitRoom = room;
        WaitDeadline = deadline;
        NextCheckAt = what == PlanWait.Condition ? now + 1 : now;
        HadWait = true;
    }

    public void StopWaiting()
    {
        Waiting = PlanWait.None;
        WaitRoom = null;
        WaitDeadline = 0;
        AwaitedTaskId = null;
        TaskTimedOut = false;
        CancellationFailure = null;
    }

    public void YieldUntilNextTick(double now)
    {
        Waiting = PlanWait.Yield;
        WaitDeadline = 0;
        NextCheckAt = now + 0.001;
    }

    /// <summary>For the model and the status: what it waits for and the tree with ids and progress marks.</summary>
    public string Describe(int max = 700)
    {
        var parts = new List<string>(2);
        switch (Waiting)
        {
            case PlanWait.Task: parts.Add("now: waiting for the current task"); break;
            case PlanWait.Arrival: parts.Add($"now: walking to {WaitRoom}"); break;
            case PlanWait.Condition: parts.Add("now: waiting for a condition"); break;
            case PlanWait.Yield: parts.Add("now: queued for next tick"); break;
        }
        parts.Add($"plan_id={Id}, revision={Tree.Revision}; steps: " + Tree.Describe(max));
        return string.Join("; ", parts);
    }
}
