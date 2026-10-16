using System;
using System.Linq;

namespace LlmAgents.Agents;

/// <summary>What an item is, as far as the storage rules care. The server fills it from the item's components.</summary>
public readonly record struct ContainerTraits(
    bool HasContainer, bool IsMachine, bool IsTurretAmmoSource, bool Pickable, bool IsGearOrTool, int Capacity);

/// <summary>
/// The item rules that need no game types, so they are tested here and the server only maps game objects onto them:
/// what counts as storage, where an item goes, when a tank or a magazine needs a refill, which words mean the flora.
/// </summary>
public static class ItemRules
{
    // ------------------------------------------------------------------ storage

    /// <summary>Cabinets, lockers, shelves, crates: not machines (fabricator, reactor, turret loaders...) and not the
    /// magazine of a gun, the tank of a suit or the battery of a flashlight.</summary>
    public static bool IsStorage(ContainerTraits t)
    {
        if (!t.HasContainer || t.IsMachine || t.IsTurretAmmoSource) return false;
        if (!t.Pickable) return true;          // fixed to the wall or the floor
        if (t.IsGearOrTool) return false;
        return t.Capacity >= 3;                // a crate or a toolbox
    }

    public const int MinStorageScore = 15;

    /// <summary>How good a container is for an item, before distance. What the cabinet holds NOW counts more than what it
    /// was built for (crews repurpose them), and stacking saves space. Null = not a place for it.</summary>
    public static (int Score, string Why)? StorageScore(bool holdsSame, bool holdsSimilar, bool preferred, bool secondary, bool pickable)
    {
        int score; string why;
        if (holdsSame) { score = 100; why = "with the same items"; }
        else if (holdsSimilar) { score = 40; why = "with similar items"; }
        else if (preferred && !secondary) { score = 30; why = "its usual place"; }
        else if (preferred) { score = 15; why = "a fitting place"; }
        else return null;
        if (pickable) score -= 20;             // a crate on the floor: only if nothing fixed fits
        return score < MinStorageScore ? null : (score, why);
    }

    /// <summary>Score with distance: nearer is better, but never by more than a stack is worth.</summary>
    public static float StorageValue(int score, float meters) => score - Math.Min(Math.Max(0, meters) * 0.3f, 25f);

    // ------------------------------------------------------------------ refills

    public const float SpentBelow = 0.15f;
    public const float UsableAbove = 0.3f;

    /// <summary>A slot of something the bot carries that takes refills (welding tool, suit, gun, flashlight): small, not
    /// storage, and it says what fits.</summary>
    public static bool TakesRefills(int capacity, bool isStorage, int containableCount) =>
        capacity <= 4 && !isStorage && containableCount > 0;

    public static bool IsSpent(float conditionFraction) => conditionFraction < SpentBelow;

    public static bool IsUsableRefill(float conditionFraction) => conditionFraction > UsableAbove;

    // ------------------------------------------------------------------ crafting

    /// <summary>Crafts needed for this many items when one craft makes perCraft of them (harpoons: 6 per craft).</summary>
    public static int CraftRuns(int items, int perCraft)
    {
        perCraft = Math.Max(1, perCraft);
        return Math.Max(1, (Math.Max(1, items) + perCraft - 1) / perCraft);
    }

    // ------------------------------------------------------------------ flora

    static readonly string[] FloraWords = { "flora", "флор", "ballast", "балластн", "росток", "растени", "plant", "vine", "лиан", "побег" };

    public static bool MeansFlora(string? target)
    {
        var t = (target ?? "").ToLowerInvariant();
        return FloraWords.Any(w => t.Contains(w));
    }
}
