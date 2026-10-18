using System;
using System.Collections.Generic;
using System.Linq;

namespace LlmAgents.Agents;

/// <summary>Ingredient allocation shared by stock checks and the actual trip to the fabricator.</summary>
public static class CraftingRules
{
    /// <summary>Follow the allocation of all stock, which may reassign an item already in the input to another rule.</summary>
    public static (int Requirement, T Item)? NextIngredient<T>(IReadOnlyList<List<T>> allocation, Func<T, bool> inInput)
    {
        for (int requirement = 0; requirement < allocation.Count; requirement++)
            foreach (var item in allocation[requirement])
                if (!inInput(item)) return (requirement, item);
        return null;
    }

    /// <summary>Make space for the next craft, falling back to the floor for bulky products that cannot be carried.</summary>
    public static (List<T> Carried, List<T> Dropped, bool HasRoom) ClearOutput<T>(IEnumerable<T> output,
        Func<bool> hasRoom, Func<T, bool> carry, Func<T, bool> drop)
    {
        var carried = new List<T>();
        var dropped = new List<T>();
        foreach (var item in output.ToList())
        {
            if (hasRoom()) break;
            if (carry(item)) carried.Add(item);
            else if (drop(item)) dropped.Add(item);
        }
        return (carried, dropped, hasRoom());
    }

    /// <summary>Assign every physical item at most once, even when several ingredient rules match it.
    /// Reassign flexible ingredients when a later requirement can use only that item.</summary>
    public static List<T>[] Allocate<T>(IReadOnlyList<T> items, IReadOnlyList<int> amounts, Func<int, T, bool> matches)
    {
        var slots = amounts.SelectMany((amount, requirement) => Enumerable.Repeat(requirement, Math.Max(0, amount))).ToArray();
        var candidates = slots.Select(requirement => Enumerable.Range(0, items.Count).Where(i => matches(requirement, items[i])).ToArray()).ToArray();
        var owners = Enumerable.Repeat(-1, items.Count).ToArray();

        bool Assign(int slot, bool[] visited)
        {
            foreach (int item in candidates[slot])
            {
                if (visited[item]) continue;
                visited[item] = true;
                if (owners[item] >= 0 && !Assign(owners[item], visited)) continue;
                owners[item] = slot;
                return true;
            }
            return false;
        }

        // Starting with the least flexible requirements also produces useful deficits when stock is insufficient.
        foreach (int slot in Enumerable.Range(0, slots.Length).OrderBy(s => candidates[s].Length))
            Assign(slot, new bool[items.Count]);
        var result = amounts.Select(_ => new List<T>()).ToArray();
        for (int item = 0; item < items.Count; item++)
            if (owners[item] >= 0) result[slots[owners[item]]].Add(items[item]);
        return result;
    }
}

public enum FabricationCycleState { Running, AwaitingProducts, Completed, Stalled, StoppedWithoutProducts }

/// <summary>Completion requires newly spawned products; an inactive machine may have been cancelled.
/// The game's spawn queue can deliver its products after the machine has already stopped.</summary>
public sealed class FabricationCycle
{
    public const double SpawnGraceSeconds = 5;
    public const double NoProgressSeconds = 45;
    readonly int expectedProducts;
    float bestTimeRemaining;
    double lastProgressAt;
    double stoppedAt = -1;
    public int Products { get; private set; }

    public FabricationCycle(int expectedProducts, float timeRemaining, double now)
    {
        this.expectedProducts = Math.Max(1, expectedProducts);
        bestTimeRemaining = timeRemaining;
        lastProgressAt = now;
    }

    public void ProductCreated() => Products++;

    public FabricationCycleState Observe(bool active, float timeRemaining, double now)
    {
        if (active)
        {
            stoppedAt = -1;
            if (timeRemaining < bestTimeRemaining - 0.01f)
            {
                bestTimeRemaining = timeRemaining;
                lastProgressAt = now;
            }
            return now - lastProgressAt > NoProgressSeconds ? FabricationCycleState.Stalled : FabricationCycleState.Running;
        }
        if (Products >= expectedProducts) return FabricationCycleState.Completed;
        if (stoppedAt < 0) stoppedAt = now;
        return now - stoppedAt > SpawnGraceSeconds
            ? FabricationCycleState.StoppedWithoutProducts
            : FabricationCycleState.AwaitingProducts;
    }
}
