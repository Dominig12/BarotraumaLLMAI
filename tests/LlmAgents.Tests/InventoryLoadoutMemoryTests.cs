using System;
using System.IO;
using LlmAgents.Agents;
using LlmAgents.Memory;
using Xunit;

namespace LlmAgents.Tests;

public sealed class InventoryLoadoutMemoryTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "llmagents-loadout-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(dir, recursive: true); } catch { }
    }

    static InventoryLoadout Profile(bool enabled = true) => new()
    {
        Enabled = enabled,
        CheckIntervalSeconds = 20,
        ReviewIntervalSeconds = 180,
        Slots = new()
        {
            new() { Slot = 4, Prefab = "weldingtool" },
            new() { Slot = 7, Prefab = "toolbelt" },
            new() { Slot = 0, Prefab = "oxygentank", Count = 2, Path = new() { new(7, 0) } },
            new() { Slot = 8, Prefab = null },
        },
    };

    [Fact]
    public void LayoutSurvivesDiskReloadWithNestedStableAddresses()
    {
        var store = new MemoryStore(dir);
        store.Open("campaign_a");
        store.SetInventoryLoadout("Ларисса", "mechanic", Profile());
        store.Flush();

        var next = new MemoryStore(dir);
        next.Open("campaign_a");
        var bot = next.Bot("ЛАРИССА")!;
        Assert.Equal("mechanic", bot.Job);
        var profile = bot.InventoryLoadout!;
        Assert.True(profile.Enabled);
        Assert.Equal(20, profile.CheckIntervalSeconds);
        Assert.Equal(180, profile.ReviewIntervalSeconds);
        Assert.Equal(4, profile.Slots.Count);
        Assert.Equal("weldingtool", profile.Slots[0].Prefab);
        Assert.Equal("toolbelt", profile.Slots[1].Prefab);
        Assert.Equal("oxygentank", profile.Slots[2].Prefab);
        Assert.Equal(2, profile.Slots[2].Count);
        Assert.Equal(new InventoryContainerStep(7, 0), Assert.Single(profile.Slots[2].Path));
        Assert.Null(profile.Slots[3].Prefab);
        var json = File.ReadAllText(next.FilePath!);
        Assert.DoesNotContain("EntityId", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".tmp", next.FilePath!);
        Assert.False(File.Exists(next.FilePath + ".tmp"));
    }

    [Fact]
    public void CampaignsKeepSeparateLayoutsAndDisabledStateIsRemembered()
    {
        var store = new MemoryStore(dir);
        store.Open("campaign_a");
        store.SetInventoryLoadout("Ann", "mechanic", Profile());
        store.Open("campaign_b");
        Assert.Null(store.Bot("Ann"));
        var disabled = Profile(enabled: false);
        disabled.Slots[0].Prefab = "wrench";
        store.SetInventoryLoadout("Ann", "engineer", disabled);
        store.Flush();

        var next = new MemoryStore(dir);
        next.Open("campaign_b");
        Assert.False(next.Bot("Ann")!.InventoryLoadout!.Enabled);
        Assert.Equal("wrench", next.Bot("Ann")!.InventoryLoadout!.Slots[0].Prefab);
        Assert.Equal("engineer", next.Bot("Ann")!.Job);
        next.Open("campaign_a");
        Assert.True(next.Bot("Ann")!.InventoryLoadout!.Enabled);
        Assert.Equal("weldingtool", next.Bot("Ann")!.InventoryLoadout!.Slots[0].Prefab);
    }

    [Fact]
    public void SettingAndReadingDoNotExposeMutableProfileReferences()
    {
        var store = new MemoryStore(null);
        store.Open("a");
        var supplied = Profile();
        store.SetInventoryLoadout("Ann", "mechanic", supplied);
        supplied.Enabled = false;
        supplied.Slots[0].Prefab = "wrench";
        supplied.Slots[2].Path[0] = new InventoryContainerStep(9, 1);
        supplied.Slots.Clear();

        var returned = store.Bot("Ann")!.InventoryLoadout!;
        Assert.True(returned.Enabled);
        Assert.Equal("weldingtool", returned.Slots[0].Prefab);
        Assert.Equal(new InventoryContainerStep(7, 0), returned.Slots[2].Path[0]);
        returned.Slots[0].Count = 32;
        returned.Slots[2].Path.Clear();
        returned.Slots.RemoveAt(3);

        var untouched = store.Bot("Ann")!.InventoryLoadout!;
        Assert.Equal(1, untouched.Slots[0].Count);
        Assert.Single(untouched.Slots[2].Path);
        Assert.Equal(4, untouched.Slots.Count);
    }

    [Fact]
    public void LegacyMemoryWithoutLayoutKeepsFactsAndCanSaveANewLayout()
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "old.json"),
            "{\"bots\":{\"Ann\":{\"name\":\"Ann\",\"facts\":[\"C4 is in the armory\"],\"rounds\":12}}}");
        var store = new MemoryStore(dir);
        store.Open("old");
        Assert.Null(store.Bot("Ann")!.InventoryLoadout);
        Assert.Equal(12, store.Bot("Ann")!.Rounds);
        store.SetInventoryLoadout("Ann", "mechanic", Profile());
        store.Flush();
        var next = new MemoryStore(dir);
        next.Open("old");
        Assert.Equal(new[] { "C4 is in the armory" }, next.Bot("Ann")!.Facts);
        Assert.NotNull(next.Bot("Ann")!.InventoryLoadout);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("\"bad profile\"")]
    [InlineData("{\"Enabled\":\"yes\",\"Slots\":[]}")]
    [InlineData("{\"Slots\":\"not an array\"}")]
    [InlineData("{\"Slots\":null}")]
    [InlineData("{\"Slots\":[null]}")]
    [InlineData("{\"Slots\":[{\"Slot\":-1,\"Prefab\":\"wrench\"}]}")]
    [InlineData("{\"Slots\":[{\"Slot\":4,\"Prefab\":\"#123\"}]}")]
    [InlineData("{\"Slots\":[{\"Slot\":4,\"Prefab\":\"wrench\",\"Path\":null}]}")]
    [InlineData("{\"Slots\":[{\"Slot\":4,\"Prefab\":\"wrench\",\"Path\":[null]}]}")]
    [InlineData("{\"Slots\":[{\"Slot\":4,\"Prefab\":\"wrench\",\"Count\":0}]}")]
    public void InvalidPersistedLayoutIsIgnoredWithoutDiscardingOtherMemories(string malformed)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "a.json"),
            "{\"bots\":{\"Ann\":{\"name\":\"Ann\",\"facts\":[\"keep this\"],\"InventoryLoadout\":" + malformed + "}}}");
        var store = new MemoryStore(dir);
        store.Open("a");
        var bot = store.Bot("Ann")!;
        Assert.NotNull(bot);
        Assert.Equal(new[] { "keep this" }, bot.Facts);
        Assert.Null(bot.InventoryLoadout);
        Assert.False(File.Exists(Path.Combine(dir, "a.json.bad")));
        store.AddFact("Ann", "mechanic", "new memory", 10);
        store.Flush();
        var next = new MemoryStore(dir);
        next.Open("a");
        Assert.Equal(new[] { "keep this", "new memory" }, next.Bot("Ann")!.Facts);
        Assert.Null(next.Bot("Ann")!.InventoryLoadout);
    }

    [Fact]
    public void InvalidReplacementLeavesExistingLayoutUnchanged()
    {
        var store = new MemoryStore(null);
        store.Open("a");
        store.SetInventoryLoadout("Ann", "mechanic", Profile());
        store.Flush();
        var invalid = Profile();
        invalid.Slots[0].Slot = -1;
        Assert.Throws<ArgumentException>(() => store.SetInventoryLoadout("Ann", "mechanic", invalid));
        Assert.False(store.IsDirty);
        Assert.Equal(4, store.Bot("Ann")!.InventoryLoadout!.Slots[0].Slot);
    }

    [Fact]
    public void LayoutOnlyUpdatePreservesFactsNotesJournalAndControl()
    {
        var store = new MemoryStore(null);
        store.Open("a");
        store.AddFact("Ann", "mechanic", "fact", 10);
        store.AddJournal("Ann", "mechanic", "saved round", 10, countRound: true);
        store.SetPendingNotes("Ann", "mechanic", new[] { "note" });
        store.SetControl("Ann", "mechanic", true, "Daniil");
        store.SetInventoryLoadout("Ann", "mechanic", Profile());
        var bot = store.Bot("Ann")!;
        Assert.Equal(new[] { "fact" }, bot.Facts);
        Assert.Equal(new[] { "saved round" }, bot.Journal);
        Assert.Equal(new[] { "note" }, bot.PendingNotes);
        Assert.Equal(1, bot.Rounds);
        Assert.True(bot.AiControl);
        Assert.Equal("Daniil", bot.AiControlBy);
        Assert.NotNull(bot.InventoryLoadout);
    }
}
