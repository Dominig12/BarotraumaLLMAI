using System.Collections.Generic;
using System.Linq;
using Barotrauma;
using Barotrauma.Items.Components;

namespace LlmAgents;

/// <summary>Approach and press exactly one native interaction key; never retries a toggle or custom callback.</summary>
internal sealed class InteractObjectTask : BotTask
{
    readonly Item item;
    readonly string key;
    readonly bool mayReplaceOperator;
    bool invoked;
    public override string Name => "native " + key + " interaction with #" + item.ID;
    protected override double TimeoutSeconds => 180;
    public override IEnumerable<Item> Claimed => new[] { item };

    public InteractObjectTask(Character bot, HumanAIController ai, Item item, string key, bool mayReplaceOperator) : base(bot, ai)
    {
        this.item = item;
        this.key = key;
        this.mayReplaceOperator = mayReplaceOperator;
    }

    protected override string? Update()
    {
        if (invoked) return "failed: interaction was already attempted; inspect current state before another key press";
        if (GameAdapter.NativeInteractionProblem(Bot, item, key, requireRange: false) is { } problem)
            return "failed: " + problem;
        if (!mayReplaceOperator && item.GetComponents<Controller>().Any(controller => controller.User is { Removed: false } user && user != Bot))
            return "failed: another character is now using this controller";
        if (!Bot.CanInteractWith(item))
        {
            if (ObjectiveFailed || ObjectiveDone || Stalled(20)) return "failed: cannot reach native interaction range of #" + item.ID;
            if (!HasObjective)
            {
                var target = item.GetRootInventoryOwner() ?? item;
                if (target is not ISpatialEntity spatial) return "failed: item has no navigable position";
                Run(new AIObjectiveGoTo(spatial, Bot, Ai.ObjectiveManager, repeat: false, getDivingGearIfNeeded: true, closeEnough: 40f)
                    { IgnoreIfTargetDead = false, AllowGoingOutside = true }, "approaching #" + item.ID + " for native " + key + " interaction");
            }
            return null;
        }
        if (GameAdapter.NativeInteractionProblem(Bot, item, key, requireRange: true) is { } access)
            return "failed: " + access;
        ClearObjective();
        var before = GameAdapter.NativeInteractionState(Bot, item);
        invoked = true; // A rejected or throwing custom callback must never be automatically pressed twice.
        bool accepted = item.TryInteract(Bot, ignoreRequiredItems: false, forceSelectKey: key == "select", forceUseKey: key == "use");
        var after = GameAdapter.NativeInteractionState(Bot, item);
        return (accepted ? "ok: native interaction accepted" : "failed: native interaction did not confirm acceptance")
            + "; key=" + key + "; target=#" + item.ID + "; before=" + before + "; after=" + after
            + ". This confirms the single Pick/Select key interaction only; machine production, sustained use, remote signal effects and custom mod outcomes need separate observation. Do not repeat automatically after an unconfirmed result.";
    }
}
