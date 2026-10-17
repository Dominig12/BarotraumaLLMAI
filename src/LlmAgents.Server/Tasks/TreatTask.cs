using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Barotrauma;
using Barotrauma.Items.Components;
using Microsoft.Xna.Framework;

namespace LlmAgents;

internal sealed class TreatTask : BotTask
{
    readonly Character patient;
    readonly string? onlyItemQuery;
    ItemQueries.TreatmentChoice? choice;
    double waitUntil;
    int applied;
    readonly List<string> used = new();
    const int MaxApplications = 8;
    public override string Name => $"treat {patient.Name}";
    protected override double TimeoutSeconds => 150;

    public TreatTask(Character bot, HumanAIController ai, Character patient, string? onlyItemQuery) : base(bot, ai)
    {
        this.patient = patient;
        this.onlyItemQuery = onlyItemQuery;
    }

    protected override void OnFinish()
    {
        if (Bot.SelectedCharacter == patient) Bot.SelectedCharacter = null;
    }

    string Summary() => used.Count == 0 ? "nothing used" : "used " + string.Join(", ", used.GroupBy(u => u).Select(g => $"{g.Key} x{g.Count()}"));

    protected override string? Update()
    {
        if (patient.Removed || patient.IsDead) return $"failed: {patient.Name} is dead ({Summary()})";
        if (Timing.TotalTime < waitUntil) return null;   // let the last treatment take effect

        if (!ItemQueries.TreatableAfflictions(patient).Any())
            return $"ok: {patient.Name} needs no more treatment ({Summary()}), health {GameAdapter.HealthPercent(patient)}%";
        if (applied >= MaxApplications)
            return $"ok: stopped after {applied} treatments ({Summary()}); remaining: {ItemQueries.DescribeInjuries(patient)}";

        if (choice == null || choice.Item.Removed || !CanUse(choice.Item))
        {
            ClearObjective();
            choice = Choose();
            if (choice == null)
                return applied > 0
                    ? $"ok: nothing suitable left ({Summary()}); remaining: {ItemQueries.DescribeInjuries(patient)}"
                    : onlyItemQuery != null
                        ? $"failed: no '{onlyItemQuery}' that helps with {ItemQueries.DescribeInjuries(patient)}"
                        : $"failed: no suitable medicine available for {ItemQueries.DescribeInjuries(patient)}; craft or find some";
        }

        // 1. get the medicine
        if (!InHands(choice.Item))
        {
            if (!HasObjective)
                Run(new AIObjectiveGetItem(Bot, choice.Item, Ai.ObjectiveManager, equip: false) { SpeakIfFails = false },
                    $"getting {choice.Item.Name} from {ItemQueries.Where(choice.Item, Bot)}");
            else if (ObjectiveFailed) { choice = null; ClearObjective(); }
            else if (ObjectiveDone) ClearObjective();
            return null;
        }

        // 2. go to the patient
        if (patient != Bot && !IsNear(patient, 120f))
        {
            if (!HasObjective) Run(GoTo(patient, 60f), $"going to {patient.Name} with {choice.Item.Name}");
            else if (ObjectiveFailed) return $"failed: cannot reach {patient.Name} ({Summary()})";
            return null;
        }

        // 3. apply it to the limb with the affliction it was chosen for
        ClearObjective();
        if (patient != Bot) Bot.SelectCharacter(patient);
        var limb = patient.CharacterHealth.GetAfflictionLimb(choice.Affliction);
        var name = choice.Item.Name;
        choice.Item.ApplyTreatment(Bot, patient, limb);
        used.Add(name);
        applied++;
        Step = $"applied {name} for {choice.Affliction.Prefab.Name.Value}";
        choice = null;
        waitUntil = Timing.TotalTime + 2.0;
        return null;
    }

    bool InHands(Item i) => i.GetRootInventoryOwner() == Bot || (patient.IsIncapacitated && i.GetRootInventoryOwner() == patient);
    bool CanUse(Item i) => InHands(i) || ItemQueries.IsAvailableTo(i, Bot);

    ItemQueries.TreatmentChoice? Choose()
    {
        if (onlyItemQuery == null) return ItemQueries.ChooseTreatment(Bot, patient);
        foreach (var candidate in ItemQueries.Find(Bot, onlyItemQuery, ItemQueries.IsMedical).Take(5))
        {
            var c = ItemQueries.ChooseTreatment(Bot, patient, candidate);
            if (c != null) return c;
        }
        return null;
    }
}
