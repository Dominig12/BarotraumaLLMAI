using System.Collections.Generic;
using System.Linq;

namespace LlmAgents.Agents;

/// <summary>Alternative slot masks are complete combinations, not interchangeable individual slots.</summary>
public static class EquipmentSlotRules
{
    /// <summary>Wear an item when it has a wearable alternative; holding a bag does not satisfy equipping it.</summary>
    public static IReadOnlyList<int> Preferred(IEnumerable<int> alternatives, int storageMask, int handMask)
    {
        var equip = alternatives.Where(mask => mask != 0 && mask != storageMask).Distinct().ToArray();
        var wearable = equip.Where(mask => (mask & ~(storageMask | handMask)) != 0).ToArray();
        return wearable.Length > 0 ? wearable : equip;
    }

    public static bool Satisfies(int occupiedMask, int requiredMask) => requiredMask != 0 && (occupiedMask & requiredMask) == requiredMask;
}
