using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace LlmAgents.Agents;

/// <summary>
/// Persistent desired contents of a bot's own inventory. Unspecified slots are unmanaged; a null prefab
/// explicitly requests an empty slot. Paths contain inventory locations, never round-specific entity IDs.
/// </summary>
public sealed class InventoryLoadout
{
    public bool Enabled { get; set; } = true;
    public int CheckIntervalSeconds { get; set; } = 15;
    public int ReviewIntervalSeconds { get; set; } = 120;
    public List<InventorySlotRule> Slots { get; set; } = new();

    public InventoryLoadout Clone() => new()
    {
        Enabled = Enabled,
        CheckIntervalSeconds = CheckIntervalSeconds,
        ReviewIntervalSeconds = ReviewIntervalSeconds,
        // Preserve malformed nulls for Validate rather than silently turning a corrupt profile into a
        // different instruction. Well-formed profiles get independent lists and rule instances.
        Slots = Slots is null ? null! : Slots.Select(rule => rule is null ? null! : new InventorySlotRule
        {
            Slot = rule.Slot,
            Prefab = rule.Prefab,
            Count = rule.Count,
            Path = rule.Path is null ? null! : rule.Path.Select(step => step is null
                ? null! : new InventoryContainerStep(step.Slot, step.ContainerIndex)).ToList(),
        }).ToList(),
    };
}

public sealed class InventorySlotRule
{
    public int Slot { get; set; }
    public string? Prefab { get; set; }
    public int Count { get; set; } = 1;
    public List<InventoryContainerStep> Path { get; set; } = new();
}

/// <summary>Select the item at Slot in the current inventory, then its indicated ItemContainer component.</summary>
public sealed record InventoryContainerStep(int Slot, int ContainerIndex = 0);

public static class InventoryLoadoutRules
{
    public const int MaximumRules = 64;
    public const int MaximumDepth = 4;
    public const int MaximumCount = 32;
    public const int MaximumPrefabLength = 256;

    /// <summary>Validate profile structure; the game adapter separately checks actual slot and prefab compatibility.</summary>
    public static string? Validate(InventoryLoadout? profile)
    {
        if (profile is null) return "inventory loadout is required";
        if (profile.CheckIntervalSeconds < 5 || profile.CheckIntervalSeconds > 300)
            return "check_interval_seconds must be between 5 and 300";
        if (profile.ReviewIntervalSeconds < 30 || profile.ReviewIntervalSeconds > 3600)
            return "review_interval_seconds must be between 30 and 3600";
        if (profile.Slots is null) return "slots must be an array";
        if (profile.Slots.Count > MaximumRules) return "slots must contain at most 64 rules";

        var destinations = new Dictionary<string, InventorySlotRule>(StringComparer.Ordinal);
        for (int i = 0; i < profile.Slots.Count; i++)
        {
            var rule = profile.Slots[i];
            if (rule is null) return $"slots[{i}] must be a slot rule";
            if (rule.Slot < 0 || rule.Slot > 255) return $"slots[{i}].slot must be between 0 and 255";
            if (rule.Count < 1 || rule.Count > MaximumCount) return $"slots[{i}].count must be between 1 and 32";
            if (rule.Path is null) return $"slots[{i}].path must be an array";
            if (rule.Path.Count > MaximumDepth) return $"slots[{i}].path may have at most 4 container steps";
            for (int stepIndex = 0; stepIndex < rule.Path.Count; stepIndex++)
            {
                var step = rule.Path[stepIndex];
                if (step is null) return $"slots[{i}].path[{stepIndex}] must be a container step";
                if (step.Slot < 0 || step.Slot > 255)
                    return $"slots[{i}].path[{stepIndex}].slot must be between 0 and 255";
                if (step.ContainerIndex < 0 || step.ContainerIndex > 31)
                    return $"slots[{i}].path[{stepIndex}].container_index must be between 0 and 31";
            }

            if (rule.Prefab is null)
            {
                if (rule.Count != 1) return $"slots[{i}]: an explicitly empty slot must use count 1";
            }
            else if (ValidatePrefab(rule.Prefab) is { } error) return $"slots[{i}].prefab: {error}";

            string address = Address(rule);
            if (!destinations.TryAdd(address, rule)) return $"duplicate inventory slot {address}";
        }

        // Rule order cannot conceal a contradiction: a slot declared empty cannot also hold a container
        // through which another rule addresses an inventory. Different component indices remain distinct.
        foreach (var rule in profile.Slots)
        {
            for (int depth = 0; depth < rule.Path.Count; depth++)
            {
                string ancestor = Address(rule.Path.Take(depth).ToArray(), rule.Path[depth].Slot);
                if (destinations.TryGetValue(ancestor, out var parent) && parent.Prefab is null)
                    return $"inventory slot {Address(rule)} uses a container at {ancestor}, which is explicitly empty";
            }
        }
        return null;
    }

    static string? ValidatePrefab(string prefab)
    {
        if (string.IsNullOrWhiteSpace(prefab)) return "must be an exact nonempty prefab identifier or null for an empty slot";
        if (prefab.Length > MaximumPrefabLength) return "identifier must contain at most 256 characters";
        if (prefab.Any(char.IsControl)) return "identifier must not contain control characters";
        if (prefab.IndexOfAny(new[] { '*', '?' }) >= 0) return "wildcards are not supported; use an exact prefab identifier";
        string trimmed = prefab.Trim();
        if (trimmed.StartsWith("#", StringComparison.Ordinal) || trimmed[0] == '[' && trimmed[^1] == ']')
            return "entity references are not persistent prefab identifiers";
        return null;
    }

    /// <summary>Canonical identity for a validated destination; component indices distinguish multiple containers.</summary>
    public static string Address(InventorySlotRule rule) => Address(rule.Path, rule.Slot);

    public static string Address(IReadOnlyList<InventoryContainerStep> path, int slot) =>
        "root/" + string.Concat(path.Select(step => step.Slot.ToString(CultureInfo.InvariantCulture)
            + ":" + step.ContainerIndex.ToString(CultureInfo.InvariantCulture) + "/"))
        + slot.ToString(CultureInfo.InvariantCulture);
}
