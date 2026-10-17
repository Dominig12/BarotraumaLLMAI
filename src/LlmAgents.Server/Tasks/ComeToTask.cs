using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Barotrauma;
using Barotrauma.Items.Components;
using Microsoft.Xna.Framework;

namespace LlmAgents;

/// <summary>
/// "Come to me": walk to where the person is NOW (keeps up if they move), stay next to them for a while, then the order
/// is over. A go_to_room order instead pinned bots to the room the player had already left, for the rest of the round.
/// </summary>
internal sealed class ComeToTask : BotTask
{
    readonly Character target;
    readonly double hold;
    double arrivedAt = -1;
    public override string Name => $"come to {target.Name}";
    protected override double TimeoutSeconds => 150 + hold;

    public ComeToTask(Character bot, HumanAIController ai, Character target, double holdSeconds) : base(bot, ai)
    {
        this.target = target;
        hold = holdSeconds;
    }

    protected override string? Update()
    {
        if (target.Removed || target.IsDead) return $"failed: {target.Name} is dead";
        // repeat = keeps following, like the vanilla follow order, so the bot stays with them until the hold is over
        if (!HasObjective || ObjectiveDone)
            Run(new AIObjectiveGoTo(target, Bot, Ai.ObjectiveManager, repeat: true, getDivingGearIfNeeded: true, closeEnough: 120f),
                $"walking to {target.Name}");
        else if (ObjectiveFailed) return $"failed: cannot reach {target.Name}";

        bool near = IsNear(target, 250f);
        if (near && arrivedAt < 0)
        {
            arrivedAt = Timing.TotalTime;
            Step = $"next to {target.Name}, waiting for orders";
        }
        if (arrivedAt >= 0 && Timing.TotalTime - arrivedAt >= hold)
            return $"ok: you came to {target.Name} and stayed {hold:0}s. This order is DONE: set_goal(done=true) and go back to your duties, unless {target.Name} gave you a new order";
        return null;
    }
}
