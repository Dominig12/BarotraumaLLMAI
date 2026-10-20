using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Barotrauma;
using Barotrauma.Items.Components;
using LlmAgents.Agents;
using Microsoft.Xna.Framework;

namespace LlmAgents;

public sealed partial class GameAdapter
{
    // Entity IDs are recycled. A generated handle must also identify this exact instance in this round.
    // CreationIndex comes from the loaded game, including dynamically spawned/modded entity subclasses.
    string objectEpoch = Guid.NewGuid().ToString("N");
    sealed record ObjectSnapshot(double Tick, ulong BotInstance, ulong? SubmarineInstance, CharacterTeamType Team,
        ulong[] Instances, IReadOnlyList<GameObjectInfo> Objects);
    readonly Dictionary<ushort, ObjectSnapshot> objectSnapshots = new();

    void ResetObjectCatalog()
    {
        objectEpoch = Guid.NewGuid().ToString("N");
        objectSnapshots.Clear();
    }

    string ObjectIdentity(Entity entity) => objectEpoch + ":" + entity.CreationIndex;

    static string ObjectReference(Entity entity) => entity switch
    {
        Item => "#" + entity.ID,
        Character => "[c" + entity.ID + "]",
        Hull => "[h" + entity.ID + "]",
        Submarine => "[s" + entity.ID + "]",
        _ => "[e" + entity.ID + "]",
    };

    static Entity? ExactObject(string reference)
    {
        var text = reference.Trim();
        char kind;
        string number;
        if (text.StartsWith("#", StringComparison.Ordinal)) { kind = '#'; number = text.Substring(1); }
        else if (text.Length > 3 && text[0] == '[' && text[^1] == ']') { kind = text[1]; number = text.Substring(2, text.Length - 3); }
        else return null;
        if (!ushort.TryParse(number, out var id) || Entity.FindEntityByID(id) is not { Removed: false } entity) return null;
        return kind switch
        {
            '#' when entity is Item => entity,
            'c' when entity is Character => entity,
            'h' when entity is Hull => entity,
            's' when entity is Submarine => entity,
            'e' when entity is not (Item or Character or Hull or Submarine) => entity,
            _ => null,
        };
    }

    static string ObjectName(Entity entity) => entity switch
    {
        Item item => item.Name,
        Character character => character.Name,
        Hull hull => hull.DisplayName?.Value ?? hull.RoomName,
        Submarine submarine => submarine.Info?.Name ?? "Submarine",
        MapEntity map => map.Name,
        _ => entity.GetType().Name,
    };

    static string ObjectKind(Entity entity) => entity switch
    {
        Item item => MachineKind(item),
        Character character => IsBody(character) ? "body" : character.IsHuman ? "character" : "creature",
        Hull => "room",
        Submarine => "submarine",
        Structure => "structure",
        Gap => "gap",
        _ => entity.GetType().Name.ToLowerInvariant(),
    };

    static string ObjectPrefab(Entity entity) => entity switch
    {
        Item item => item.Prefab.Identifier.Value,
        Character character => character.SpeciesName.Value,
        MapEntity map => map.Prefab?.Identifier.Value ?? "",
        _ => "",
    };

    static Hull? ObjectHull(Entity entity) => entity switch
    {
        Item item => item.CurrentHull,
        Character character => character.CurrentHull,
        Hull hull => hull,
        MapEntity map => Hull.FindHull(map.WorldPosition),
        _ => null,
    };

    /// <summary>
    /// Called only through IGameApi on the game thread. Do not retain live Entity references in the
    /// LLM worker: the returned records/JSON contain copied values only. No prefab-name allowlist,
    /// distance limit, submarine limit, or result cap hides modded/dynamic objects from the registry.
    /// </summary>
    public IReadOnlyList<GameObjectInfo> GetObjects(int botId)
    {
        foreach (var key in objectSnapshots.Keys.Where(id => Find(id) is not { Removed: false, IsDead: false }).ToArray())
            objectSnapshots.Remove(key);
        if (Find(botId) is not { Removed: false, IsDead: false } bot) return Array.Empty<GameObjectInfo>();
        var entities = Entity.GetEntities().Where(e => !e.Removed).ToArray();
        var instances = entities.Select(e => e.CreationIndex).ToArray();
        if (objectSnapshots.TryGetValue(bot.ID, out var previous) && previous.Tick == Timing.TotalTime
            && previous.BotInstance == bot.CreationIndex && previous.SubmarineInstance == bot.Submarine?.CreationIndex
            && previous.Team == bot.TeamID && previous.Instances.SequenceEqual(instances))
            return previous.Objects;
        var nativeOrders = ObjectOrderBindings(bot);
        var objects = entities.OrderBy(e => e.ID)
            .Select(entity => DescribeObjectHandle(bot, entity, nativeOrders)).ToArray();
        objectSnapshots[bot.ID] = new ObjectSnapshot(Timing.TotalTime, bot.CreationIndex, bot.Submarine?.CreationIndex, bot.TeamID, instances, objects);
        return objects;
    }

    GameObjectInfo DescribeObjectHandle(Character bot, Entity entity, IReadOnlyDictionary<ushort, List<ObjectToolBinding>> orders)
    {
        var actions = ObjectActions(bot, entity, orders);
        var components = entity is Item item
            ? item.Components.Select(component => component.GetType().FullName ?? component.GetType().Name).Distinct().OrderBy(n => n, StringComparer.Ordinal).ToArray()
            : new[] { entity.GetType().FullName ?? entity.GetType().Name };
        return new GameObjectInfo(ObjectReference(entity), ObjectIdentity(entity), ObjectName(entity), ObjectPrefab(entity),
            ObjectKind(entity), components, actions, ObjectHull(entity) is { } hull ? RoomId(hull) : null);
    }

    static ObjectToolBinding ObjectAction(string tool, string key, string value, string description) =>
        new(tool, new JsonObject { [key] = value }, description);

    static List<ObjectToolBinding> ObjectActions(Character bot, Entity entity, IReadOnlyDictionary<ushort, List<ObjectToolBinding>> orders)
    {
        var reference = ObjectReference(entity);
        var actions = new List<ObjectToolBinding>();
        if (entity is MapEntity or Character or Submarine)
            actions.Add(ObjectAction("approach_object", "target", reference, "Approach this exact object before inspecting or interacting with a remote inventory. Reachability and access are checked at execution."));
        if (entity is Item item)
        {
            actions.Add(new ObjectToolBinding("mark_item", new JsonObject { ["item"] = reference, ["mark"] = "attention" },
                "Set/remove the visible crew attention marker on this exact item. Optional note explains what needs attention. This does not destroy or transfer it."));
            if (item.AllowDeconstruct && item.Prefab.AllowDeconstruct && item.Prefab.DeconstructItems.Any())
                actions.Add(new ObjectToolBinding("mark_item", new JsonObject { ["item"] = reference, ["mark"] = "deconstruct" },
                    "Set/remove the native deconstruction marker for this item's current stack. Existing deconstruction orders may act on the marked stack; this is distinct from attention."));
            foreach (var key in new[] { "select", "use" })
                if (NativeInteractionComponents(item, key).Any())
                    actions.Add(new ObjectToolBinding("interact_object", new JsonObject { ["target"] = reference, ["key"] = key },
                        "Press this exact object's native " + key + " interaction key once through the game's Pick/Select path. Loaded components may pick up the item, open/select a panel, toggle a door/controller or emit a button signal. This is not sustained Item.Use, weapon firing or proof that a machine produced anything. Read the accepted result and observed state; verify downstream/custom effects separately."));
            if (item.GetComponent<Pickable>() != null)
            {
                actions.Add(ObjectAction("take_item", "item", reference, "Take this exact item into your inventory. Current access, pickup restrictions, ownership and available space are checked at execution."));
                actions.Add(ObjectAction("transfer_item", "item", reference, "Transfer this exact instance. Supply destination target and optionally zero-based container_index and slot. One exact #ID selects one item; occupied items are never replaced or dropped."));
                actions.Add(ObjectAction("move_item", "item", reference, "Move this exact item; supply an explicit destination. Does not select other items of the same prefab."));
                actions.Add(ObjectAction("drop", "item", reference, "Drop this exact carried item, or remove this exact product from a machine output onto the floor."));
                if (item.AllowedSlots.Any(slot => slot != InvSlotType.Any && slot != InvSlotType.None))
                {
                    actions.Add(ObjectAction("equip", "item", reference, "Fetch and equip this exact item in its game-defined allowed slots; heavy gear may require both hands."));
                    actions.Add(ObjectAction("unequip", "item", reference, "Move this exact equipped item to ordinary carried inventory. Requires compatible free space; gear locks still apply."));
                }
                if (item.AllowDeconstruct && item.Prefab.AllowDeconstruct && item.Prefab.DeconstructItems.Any())
                    actions.Add(ObjectAction("deconstruct", "item", reference, "Destroy this exact item in a deconstructor; inspect its salvage yields before choosing this action."));
            }
            if (item.GetComponents<ItemContainer>().Any())
            {
                actions.Add(ObjectAction("inspect_inventory", "target", reference, "Inspect each distinct inventory compartment, slot, stack, access restriction and insertion compatibility of this exact object."));
                var containers = item.GetComponents<ItemContainer>().ToArray();
                for (int index = 0; index < containers.Length; index++)
                    actions.Add(new ObjectToolBinding("transfer_item", new JsonObject { ["target"] = reference, ["container_index"] = index },
                        "Insert an item into this exact object's compartment " + index + ". Supply item and optional slot; the game checks current compartment access, per-slot compatibility, stack capacity, ownership and containment cycles."));
                if (containers.Length == 1 && IsStorage(item))
                    actions.Add(ObjectAction("unpack", "container", reference, "Empty this container into suitable storage. For an individual item or compartment use transfer_item with an explicit container_index instead."));
                if (item.GetComponent<Pickable>() != null && containers.Any(container =>
                    ItemRules.TakesRefills(container.Capacity, IsStorage(item), container.ContainableItems.Count)
                    && ItemRules.IsRefillSlot(container.ContainableItems.SelectMany(rule => rule.Identifiers).Select(id => id.Value))))
                    actions.Add(ObjectAction("refill", "item", reference, "Refill this exact gear after it is carried, using compatible available supplies. Does not craft supplies."));
            }
            if (item.GetComponent<Fabricator>() != null)
            {
                actions.Add(ObjectAction("craft", "machine", reference, "Craft in this exact fabricator. Supply an available recipe identifier; real machine input and linked supply inventories are used natively. Completion is confirmed by actual fabricated items."));
                actions.Add(new ObjectToolBinding("inspect", new JsonObject { ["kind"] = "machines", ["query"] = reference }, "Inspect this exact machine's power, state, input/output and real directed links."));
            }
            if (orders.TryGetValue(item.ID, out var boundOrders)) actions.AddRange(boundOrders);
        }
        else if (entity is Character character)
        {
            if (character.Inventory != null)
            {
                actions.Add(ObjectAction("inspect_inventory", "target", reference, "Inspect this exact character's inventory when permitted. Conscious human players retain control over their items."));
                actions.Add(ObjectAction("transfer_item", "target", reference, "Transfer an exact item to this character's inventory. Supply item and optional slot; native access and consent rules still apply, occupied contents are never replaced."));
            }
            if (IsBody(character))
            {
                actions.Add(ObjectAction("drag_body", "target", reference, "Drag this exact corpse/incapacitated character. Supply destination room or crew member; the native drag and proximity rules still apply."));
                if (character.Inventory != null)
                {
                    actions.Add(ObjectAction("loot_body", "target", reference, "Loot this exact corpse/incapacitated character, including modded monster corpses. Limited by real loot and carrying capacity."));
                    actions.Add(new ObjectToolBinding("mark_item", new JsonObject { ["item"] = "*", ["from"] = reference, ["mark"] = "deconstruct" },
                        "Mark deconstructable items of this exact body and the verified loot you already carry from it. Own supplies and mixed native stacks outside this source are protected. Reports marked and skipped #IDs."));
                    actions.Add(new ObjectToolBinding("deconstruct", new JsonObject { ["from"] = reference },
                        "Deconstruct only this exact body's contents and its verified loot you carry. Supply item='*' or an exact item #ID. Native marks are applied to the safe selection; unrelated own items are excluded."));
                }
            }
            else if (character != bot && character.IsHuman && character.TeamID == bot.TeamID)
            {
                actions.Add(ObjectAction("follow", "target", reference, "Follow this exact living crew member."));
                actions.Add(ObjectAction("come_to", "target", reference, "Walk to this exact crew member's current position and wait briefly."));
                actions.Add(ObjectAction("treat", "target", reference, "Treat this exact crew member using appropriate available medicine."));
            }
            else if (character != bot && !character.IsDead && IsHostileTo(character, bot))
                actions.Add(ObjectAction("attack", "target", reference, "Attack this exact hostile character. The executor rechecks hostility and available weapons."));
        }
        else if (entity is Hull)
            actions.Add(ObjectAction("go_to_room", "room", reference, "Walk to this exact room and stay there using the game's movement objective."));
        return actions;
    }

    // Resolve actual native order targets once, rather than scanning every prefab for every object.
    // Modded orders participate automatically if the game's native matching/execution supports them.
    static Dictionary<ushort, List<ObjectToolBinding>> ObjectOrderBindings(Character bot)
    {
        var result = new Dictionary<ushort, List<ObjectToolBinding>>();
        if (bot.Submarine == null) return result;
        foreach (var prefab in OrderPrefab.Prefabs.Where(p => !IsSpecialOrder(p) && p.Identifier != ControlOrder.Id && NeedsTarget(p) && !p.TargetAllCharacters))
        {
            if (prefab.Identifier == "wait" || prefab.Identifier == "follow") continue;
            var options = prefab.AllOptions.Length > 0 ? prefab.AllOptions.ToArray() : new[] { Identifier.Empty };
            foreach (var option in options)
            {
                List<Item> targets;
                try { targets = prefab.GetMatchingItems(bot.Submarine, true, bot.TeamID, bot, option); }
                catch { continue; } // An unsupported/custom order cannot invalidate discovery of other objects.
                foreach (var item in targets.Where(i => !i.Removed).Distinct())
                {
                    if (!result.TryGetValue(item.ID, out var bindings)) result[item.ID] = bindings = new();
                    var args = new JsonObject { ["order"] = prefab.Identifier.Value, ["target"] = ObjectReference(item) };
                    if (!option.IsEmpty) args["option"] = option.Value;
                    bindings.Add(new ObjectToolBinding("give_order", args,
                        "Native order " + prefab.Identifier.Value + (option.IsEmpty ? "" : ":" + option.Value) + " bound to this exact object. Required access, available operator and native objective support are checked when executed."));
                }
            }
        }
        return result;
    }

    /// <summary>Exact runtime entity inspection; reads loaded definitions/components and never executes arbitrary reflected methods.</summary>
    public string InspectObject(int botId, string target)
    {
        if (Find(botId) is not { Removed: false } bot) return "error: character gone";
        if (ExactObject(target) is not { } entity)
            return "error: object is gone or reference has the wrong type; copy an exact #itemID, [cID], [hID], [sID] or [eID] from the current object catalog";
        var info = GetObjects(botId).FirstOrDefault(o => o.Reference == ObjectReference(entity));
        if (info == null) return "error: object was removed; discover the current object catalog again";
        var description = new JsonObject
        {
            ["reference"] = info.Reference, ["identity"] = info.Identity, ["name"] = info.Name,
            ["identifier"] = info.Identifier, ["kind"] = info.Kind,
            ["sourcePackage"] = entity.ContentPackage?.Name,
            ["room"] = info.Room,
            ["distanceMeters"] = Math.Round(Vector2.Distance(bot.WorldPosition, entity.WorldPosition) / UnitsPerMeter, 1),
            ["worldPosition"] = new JsonObject { ["x"] = entity.WorldPosition.X, ["y"] = entity.WorldPosition.Y },
            ["submarine"] = entity.Submarine is { } sub ? ObjectReference(sub) : null,
            ["components"] = new JsonArray(info.Components.Select(n => (JsonNode?)JsonValue.Create(n)).ToArray()),
            ["interactions"] = JsonSerializer.SerializeToNode(info.Actions, MachineJson),
        };
        if (entity is Item item) DescribeObjectItem(bot, item, description);
        else if (entity is Character character)
        {
            description["state"] = new JsonObject
            {
                ["species"] = character.SpeciesName.Value, ["human"] = character.IsHuman,
                ["dead"] = character.IsDead, ["incapacitated"] = character.IsIncapacitated,
                ["stun"] = character.Stun, ["healthPercent"] = HealthPercent(character),
                ["friendly"] = character.TeamID == bot.TeamID, ["bot"] = character.IsBot,
                ["hasInventory"] = character.Inventory != null,
                ["canBeDraggedByYou"] = IsBody(character) && character.CanBeDraggedBy(bot),
                ["selectedBy"] = character.SelectedBy is { } holder ? ObjectReference(holder) : null,
            };
        }
        else if (entity is Hull hull)
            description["state"] = new JsonObject { ["waterPercent"] = hull.WaterPercentage, ["oxygenPercent"] = hull.OxygenPercentage, ["fires"] = hull.FireSources?.Count ?? 0 };
        else if (entity is Structure structure)
            description["state"] = new JsonObject { ["health"] = structure.Health, ["maxHealth"] = structure.MaxHealth, ["damaged"] = structure.HasDamage, ["indestructible"] = structure.Indestructible, ["sections"] = structure.SectionCount };
        else if (entity is Gap gap)
            description["state"] = new JsonObject { ["open"] = gap.Open, ["roomToRoom"] = gap.IsRoomToRoom, ["connectedDoor"] = gap.ConnectedDoor != null ? ObjectReference(gap.ConnectedDoor.Item) : null };
        else if (entity is Submarine submarine)
            description["state"] = new JsonObject { ["depth"] = submarine.RealWorldDepth, ["atDamageDepth"] = submarine.AtDamageDepth, ["dockedTo"] = new JsonArray(submarine.DockedTo.Select(s => (JsonNode?)JsonValue.Create(ObjectReference(s))).ToArray()) };
        if (entity is MapEntity map)
            description["outgoingLinks"] = new JsonArray(map.linkedTo.Where(e => !e.Removed).Distinct().Select(e => (JsonNode?)new JsonObject { ["from"] = info.Reference, ["to"] = ObjectReference(e), ["name"] = ObjectName(e) }).ToArray());
        description["interactionSupport"] = info.Actions.Count == 0
            ? "Inspection only. No supported executor has been registered for this object's semantics. A mod component's type/name does not authorize arbitrary method/property execution."
            : "Only the listed executor bindings are supported. Component metadata and interaction messages describe loaded game content; custom Lua/C# effects cannot be inferred or invoked from their names. Inspect current state and verify the actual completion report.";
        return description.ToJsonString(MachineJson);
    }

    static void DescribeObjectItem(Character bot, Item item, JsonObject description)
    {
        description["definition"] = new JsonObject
        {
            ["description"] = item.Description, ["tags"] = item.Tags,
            ["sourceFile"] = item.Prefab.FilePath.ToString(),
            ["variantOf"] = item.Prefab.VariantOf.Value,
            ["allowedSlots"] = new JsonArray(item.AllowedSlots.Select(s => (JsonNode?)JsonValue.Create(s.ToString())).ToArray()),
            ["maxStackSize"] = item.Prefab.MaxStackSize,
            ["maxCharacterStackSize"] = item.Prefab.MaxStackSizeCharacterInventory,
            ["deconstructible"] = item.AllowDeconstruct && item.Prefab.AllowDeconstruct,
        };
        description["state"] = new JsonObject
        {
            ["marked_for_deconstruction"] = Item.DeconstructItems.Contains(item),
            ["marked_for_attention"] = ContextMarks.IsMarked(item),
            ["attention_note"] = ContextMarks.All().FirstOrDefault(mark => mark.Item == item)?.Note,
            ["condition"] = item.Condition, ["maxCondition"] = item.MaxCondition, ["conditionPercent"] = item.ConditionPercentage,
            ["location"] = ItemQueries.Where(item, bot), ["container"] = item.Container != null ? ObjectReference(item.Container) : null,
            ["ownerCharacter"] = item.GetRootInventoryOwner() is Character owner ? ObjectReference(owner) : null,
            ["equippedByYou"] = bot.HasEquippedItem(item),
            ["ignoredByCrew"] = ItemQueries.IsIgnored(item), ["nonInteractable"] = item.NonInteractable,
            ["pickable"] = item.GetComponent<Pickable>() != null, ["attached"] = item.GetComponent<Pickable>()?.IsAttached ?? false,
            ["eligibleForOrdinaryPickupSearch"] = ItemQueries.IsAvailableTo(item, bot),
            ["sourceInventoryAccessProblem"] = item.ParentInventory != null ? InventoryAccessProblem(bot, item.ParentInventory) : null,
        };
        description["componentContracts"] = new JsonArray(item.Components.Select(component => (JsonNode?)new JsonObject
        {
            ["type"] = component.GetType().FullName, ["name"] = component.Name,
            ["active"] = component.IsActive, ["characterUsable"] = component.CharacterUsable,
            ["canBePicked"] = component.CanBePicked, ["canBeSelected"] = component.CanBeSelected,
            ["pickingTime"] = component.PickingTime,
            ["pickKey"] = component.PickKey.ToString(), ["selectKey"] = component.SelectKey.ToString(),
            ["interactionMessage"] = component.DisplayMsg?.Value,
            ["requirementsMet"] = component.HasRequiredItems(bot, false),
            ["requiredSkills"] = new JsonArray(component.RequiredSkills.Select(skill => (JsonNode?)new JsonObject { ["identifier"] = skill.Identifier.Value, ["level"] = skill.Level }).ToArray()),
        }).ToArray());
        var interactionKeys = new[] { "select", "use" }.Where(key => NativeInteractionComponents(item, key).Any()).ToArray();
        if (interactionKeys.Length > 0)
        {
            description["nativeInteractionKeys"] = new JsonArray(interactionKeys.Select(key => (JsonNode?)JsonValue.Create(key)).ToArray());
            description["nativeInteractionState"] = JsonNode.Parse(NativeInteractionState(bot, item));
            description["nativeInteractionGuide"] = "interact_object presses one normal Pick/Select interaction key after approach. A panel being selected is not fabrication, reactor activation, sustained use or verified remote signal effect. Repeated select may deselect/toggle; inspect the actual state and do not automatically retry an unconfirmed press. Ordinary item pickup uses take_item/equip instead.";
        }
        var containers = item.GetComponents<ItemContainer>().ToArray();
        description["inventoryCompartments"] = new JsonArray(containers.Select((container, index) => (JsonNode?)new JsonObject
        {
            ["container_index"] = index, ["capacity"] = container.Capacity, ["maxStackSize"] = container.MaxStackSize,
            ["allowAccess"] = container.AllowAccess, ["locked"] = container.Locked,
            ["accessibleNow"] = InventoryAccessProblem(bot, container.Inventory) == null,
            ["accessProblem"] = InventoryAccessProblem(bot, container.Inventory),
            ["requirementsMet"] = container.HasRequiredItems(bot, false),
            ["containableRestrictions"] = container.ContainableRestrictions,
            ["containable"] = new JsonArray(container.ContainableItems.Select(rule => (JsonNode?)new JsonObject
            {
                ["identifiersOrTags"] = rule.JoinedIdentifiers, ["excludedIdentifiers"] = rule.JoinedExcludedIdentifiers,
                ["targetSlot"] = rule.TargetSlot, ["excludeBroken"] = rule.ExcludeBroken,
                ["excludeFullCondition"] = rule.ExcludeFullCondition, ["allowVariants"] = rule.AllowVariants,
            }).ToArray()),
        }).ToArray());
        if (containers.Length > 0)
            description["inventoryGuide"] = "Use inspect_inventory(target='" + ObjectReference(item) + "') for actual slots, nested contents and current access. Use the returned container_index and slot with transfer_item; compartment indices are local to this object, slot indices are zero-based. A missing compatible free slot is an error, never an implicit swap/drop.";
        if (item.GetComponent<Fabricator>() != null || item.GetComponent<Deconstructor>() != null || item.GetComponent<Powered>() != null)
            description["machinery"] = JsonSerializer.SerializeToNode(DescribeMachine(bot, item, contents: false), MachineJson);
        if (item.GetComponent<Fabricator>() is { } fabricator)
        {
            description["recipeGuide"] = "Recipe identifiers come from this loaded fabricator, including mod recipes. inspect(kind='recipe',query=<identifier>) describes ingredients, output batches, skills and native linked stock. A listed recipe still needs current power, output capacity, skills/unlocks and matching ingredient conditions.";
            description["recipes"] = new JsonArray(fabricator.fabricationRecipes.Values.Where(r => !r.HideForNonTraitors)
                .Select(recipe => (JsonNode?)new JsonObject
                {
                    ["identifier"] = recipe.TargetItemPrefabIdentifier.Value, ["name"] = recipe.DisplayName.Value,
                    ["outputPerBatch"] = Crafting.Yield(recipe), ["requiredSeconds"] = recipe.RequiredTime,
                    ["recipeUnlocked"] = !fabricator.MissingRequiredRecipe(recipe, bot),
                    ["requiredSkills"] = new JsonArray(recipe.RequiredSkills.Select(skill => (JsonNode?)new JsonObject { ["identifier"] = skill.Identifier.Value, ["level"] = skill.Level }).ToArray()),
                    ["ingredients"] = new JsonArray(recipe.RequiredItems.Select(requirement => (JsonNode?)new JsonObject
                    {
                        ["count"] = requirement.Amount,
                        ["matchingPrefabs"] = new JsonArray(requirement.ItemPrefabs.Select(prefab => (JsonNode?)JsonValue.Create(prefab.Identifier.Value)).ToArray()),
                    }).ToArray()),
                }).ToArray());
        }
    }
}
