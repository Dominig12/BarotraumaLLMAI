using System.Collections.Generic;
using System.Linq;
using Barotrauma;
using LlmAgents.Agents;

namespace LlmAgents;

public sealed partial class GameAdapter
{
    readonly LootProvenanceLedger<Character, Character, Item> lootOrigins = new();

    void RecordBodyLoot(Character bot, Character body, IReadOnlyList<Item> transferred)
    {
        PruneBodyLoot();
        lootOrigins.Record(bot, body, transferred.Where(item => !item.Removed && item.GetRootInventoryOwner() == bot));
    }

    void PruneBodyLoot() => lootOrigins.Prune((bot, body, item) => !bot.Removed && !body.Removed && !item.Removed);

    List<Item> BodyLootScope(Character bot, Character body)
    {
        PruneBodyLoot();
        bool observeBody = InInventoryScope(bot, body);
        return Item.ItemList.Where(item => !item.Removed && lootOrigins.InScope(bot, body, item, item.GetRootInventoryOwner())
            && (observeBody || item.GetRootInventoryOwner() == bot)).Distinct().ToList();
    }
}
