using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using LlmAgents.Agents;
using LlmAgents.Llm;
using Xunit;

namespace LlmAgents.Tests;

public class ArchitectureTests
{
    [Fact]
    public void Every_tool_is_defined_once_with_a_group()
    {
        var names = Tools.Defs.Select(d => d.Name).ToList();
        Assert.Equal(names.Count, names.Distinct().Count());
        Assert.All(Tools.Defs, d => Assert.NotEqual((ToolGroup)0, d.Group));
        Assert.All(Tools.Defs, d => Assert.False(string.IsNullOrWhiteSpace(d.Description)));
    }

    [Fact]
    public void Every_permitted_tool_is_described_without_word_or_job_filters()
    {
        var offered = Tools.For(new List<string>(), false, false);
        Assert.Equal(Tools.Defs.Where(d => !d.ControlOnly && (d.Group != ToolGroup.Memory || d.Name == "remember")).Select(d => d.Name), offered.Select(t => t.Name));
        foreach (var name in new[] { "say", "craft", "treat", "attack", "put_in", "drag_body", "loot_body" })
            Assert.Contains(offered, t => t.Name == name);
    }

    [Fact]
    public void Tool_restrictions_follow_configuration_and_capabilities()
    {
        var memory = Tools.Defs.First(d => d.Group == ToolGroup.Memory && d.Name != "remember").Name;
        var control = Tools.Defs.First(d => d.ControlOnly).Name;
        Assert.DoesNotContain(Tools.For(new List<string>(), false, false), t => t.Name == memory || t.Name == control);
        var enabled = Tools.For(new List<string>(), true, true);
        Assert.Contains(enabled, t => t.Name == memory);
        Assert.Contains(enabled, t => t.Name == control);
        var restricted = Tools.For(new[] { "say", "craft" }, false, false);
        Assert.Equal(new[] { "craft", "say" }, restricted.Select(t => t.Name).OrderBy(n => n));
    }

    [Theory]
    [InlineData("assistant", "Réapprovisionne la tourelle avec les caisses partiellement utilisées")]
    [InlineData("medicaldoctor", "Ларисса, загрузи C4 в снаряд из фабрикатора")]
    [InlineData("captain", "運んだ死体から包帯を取ってください")]
    public async Task EveryDecisionIncludesTheFullPermittedSchemasRegardlessOfLanguageAndJob(string job, string order)
    {
        var clock = new FakeClock();
        var game = new FakeGame().AddBot(1, "Ann", job);
        var settings = Fixtures.Settings();
        var offered = new ConcurrentQueue<string[]>();
        var handler = new FakeHandler(body =>
        {
            offered.Enqueue(body["tools"]!.AsArray().Select(t => t!["function"]!["name"]!.GetValue<string>()).ToArray());
            return (HttpStatusCode.OK, Fixtures.OpenAiToolResponse(("carry_on", "{}")));
        });
        using var manager = new AgentManager(settings, game, new LlmRouter(settings, handler, clock.Utc), clock.Get);
        manager.Update(); await manager.WhenIdle(); clock.Advance(1); manager.Update();
        manager.GetAgent(1)!.ReceivePlayerOrder(order);
        for (int i = 0; i < 16; i++) { manager.Update(); await manager.WhenIdle(); clock.Advance(0.5); }
        Assert.True(offered.Count >= 2);
        var expected = Tools.For(settings.Bots.AllowedTools, false, true).Select(t => t.Name).ToArray();
        Assert.All(offered, names => Assert.Equal(expected, names));
    }

    [Fact]
    public void Context_guard_drops_oldest_history_and_keeps_current_turn()
    {
        var turns = new List<LlmTurn>();
        for (int i = 0; i < 10; i++)
        {
            turns.Add(new LlmTurn("user", "old " + i + new string('x', 3000)));
            turns.Add(new LlmTurn("assistant", "did " + i));
        }
        turns.Add(new LlmTurn("user", "NOW" + new string('y', 3000)));
        var tools = new[] { new ToolSpec("say", "speak", new JsonObject()) };
        int dropped = ContextGuard.Fit("system", turns, tools, 4096, 1024);
        Assert.True(dropped > 0);
        Assert.StartsWith("NOW", turns[^1].Text);
        Assert.Equal("user", turns[0].Role);
        Assert.True(ContextGuard.Estimate("system", turns, tools) <= 4096 - 1024);
        Assert.Equal(0, ContextGuard.Fit("system", turns, tools, 0, 1024));
    }
}

public class ArchitectureFixesTests
{
    [Fact]
    public void Gate_counters_survive_a_settings_reload()
    {
        var now = new System.DateTime(2026, 10, 1, 12, 0, 0, System.DateTimeKind.Utc);
        var old = new LlmGate(5, requestsPerMinute: 2, dailyBudget: 10, utcNow: () => now);
        old.TryEnter(out _)!.Dispose();
        old.TryEnter(out _)!.Dispose();
        var fresh = new LlmGate(5, requestsPerMinute: 2, dailyBudget: 10, utcNow: () => now);
        fresh.CarryOver(old);
        Assert.Equal(2, fresh.UsedToday);
        Assert.Null(fresh.TryEnter(out var why));
        Assert.Contains("minute", why);
    }

    [Fact]
    public void Journal_goes_to_the_campaign_it_belongs_to()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "llm_mem_" + System.Guid.NewGuid().ToString("N"));
        try
        {
            var store = new LlmAgents.Memory.MemoryStore(dir);
            store.Open("campaign_a");
            store.Flush();
            store.Open("campaign_b");
            store.AddJournal("Ann", "mechanic", "fixed the reactor", 10, countRound: true, scope: "campaign_a");
            store.Flush();
            Assert.Null(store.Bot("Ann"));
            store.Open("campaign_a");
            Assert.Contains("fixed the reactor", store.Bot("Ann")!.Journal);
        }
        finally { try { System.IO.Directory.Delete(dir, true); } catch { } }
    }
}

[CollectionDefinition("Log writer", DisableParallelization = true)]
public sealed class LogWriterCollection { }

[Collection("Log writer")]
public class LogWriterTests
{
    [Fact]
    public void Flush_acknowledges_every_batch_even_when_the_writer_has_just_become_idle()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "llm_flush_" + System.Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        var path = System.IO.Path.Combine(dir, "a.log");
        var oldPath = Log.FilePath;
        try
        {
            Log.FilePath = path;
            for (int i = 0; i < 100; i++)
            {
                string marker = $"flush batch [{i}]";
                Log.Info(marker);
                Assert.True(Log.Flush(5000));
                using var stream = new System.IO.FileStream(path, System.IO.FileMode.Open, System.IO.FileAccess.Read,
                    System.IO.FileShare.ReadWrite | System.IO.FileShare.Delete);
                using var reader = new System.IO.StreamReader(stream);
                Assert.Contains(marker, reader.ReadToEnd());
            }
        }
        finally
        {
            Log.FilePath = oldPath;
            Log.Flush();
            try { System.IO.Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void Log_file_is_written_in_background_and_rotated()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "llm_log_" + System.Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        var path = System.IO.Path.Combine(dir, "a.log");
        var oldPath = Log.FilePath; var oldMax = Log.MaxFileBytes;
        try
        {
            Log.MaxFileBytes = 2000;
            Log.FilePath = path;
            for (int i = 0; i < 100; i++) Log.Info("line " + i + new string('x', 50));
            Assert.True(Log.Flush(5000));
            Assert.True(System.IO.File.Exists(path + ".1"));
            using (var stream = new System.IO.FileStream(path, System.IO.FileMode.Open, System.IO.FileAccess.Read,
                System.IO.FileShare.ReadWrite | System.IO.FileShare.Delete))
            using (var reader = new System.IO.StreamReader(stream))
                Assert.Contains("line 99", reader.ReadToEnd());
            Assert.True(new System.IO.FileInfo(path).Length < 2000 + 200);
        }
        finally
        {
            Log.FilePath = oldPath; Log.MaxFileBytes = oldMax;
            Log.Flush();
            try { System.IO.Directory.Delete(dir, true); } catch { }
        }
    }
}

public class VersionTests
{
    static string Root()
    {
        var d = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory);
        while (d != null && !System.IO.File.Exists(System.IO.Path.Combine(d.FullName, "Directory.Build.props"))) d = d.Parent;
        return d!.FullName;
    }

    [Fact]
    public void Version_has_one_source()
    {
        var props = System.IO.File.ReadAllText(System.IO.Path.Combine(Root(), "Directory.Build.props"));
        var v = System.Text.RegularExpressions.Regex.Match(props, "<Version>(.*?)</Version>").Groups[1].Value;
        Assert.Equal(v, ModInfo.Version);
        var filelist = System.IO.File.ReadAllText(System.IO.Path.Combine(Root(), "Content", "filelist.xml"));
        Assert.Contains($"modversion=\"{v}\"", filelist);
    }
}

public class OrderPhraseTests
{
    public static IEnumerable<object[]> Rows()
    {
        var path = System.IO.Path.Combine(System.AppContext.BaseDirectory, "Data", "order_phrases.txt");
        foreach (var line in System.IO.File.ReadAllLines(path))
        {
            if (line.Length == 0 || line.StartsWith("#")) continue;
            yield return new object[] { line };
        }
    }

    [Theory, MemberData(nameof(Rows))]
    public void Order_is_preserved_for_the_model_without_keyword_instructions(string phrase)
    {
        var me = new RosterEntry(1, "Ann", "assistant", null, "none");
        var cmd = new ChatCommand("Dan", null, phrase, new[] { 1 }, true, "Engine [h4]");
        Assert.Equal($"Dan on radio, in Engine [h4]: \"{phrase}\" ('me' = Dan)", ChatDispatcher.FormatOrder(cmd, new[] { me }, me));
    }
}

public class ItemRulesTests
{
    static ContainerTraits T(bool machine = false, bool ammo = false, bool pickable = false, bool gear = false, int cap = 10) =>
        new(true, machine, ammo, pickable, gear, cap);

    [Fact]
    public void Storage_is_cabinets_and_crates_not_machines_or_gear()
    {
        Assert.True(ItemRules.IsStorage(T()));                              // locker on the wall
        Assert.True(ItemRules.IsStorage(T(pickable: true, cap: 6)));        // metal crate
        Assert.False(ItemRules.IsStorage(T(pickable: true, cap: 1)));       // a fuel tank holder
        Assert.False(ItemRules.IsStorage(T(machine: true)));                // fabricator, reactor
        Assert.False(ItemRules.IsStorage(T(ammo: true)));                   // turret loader
        Assert.False(ItemRules.IsStorage(T(pickable: true, gear: true, cap: 6))); // diving suit, welder
        Assert.False(ItemRules.IsStorage(new ContainerTraits(false, false, false, false, false, 0)));
    }

    [Fact]
    public void Same_items_beat_similar_beat_preferred_and_fixed_beats_crate()
    {
        var same = ItemRules.StorageScore(true, false, false, false, false)!.Value.Score;
        var similar = ItemRules.StorageScore(false, true, false, false, false)!.Value.Score;
        var usual = ItemRules.StorageScore(false, false, true, false, false)!.Value.Score;
        Assert.True(same > similar && similar > usual);
        Assert.Null(ItemRules.StorageScore(false, false, false, false, false));
        Assert.Null(ItemRules.StorageScore(false, false, true, true, pickable: true));   // fitting crate: below the minimum
        Assert.True(ItemRules.StorageScore(true, false, false, false, pickable: true)!.Value.Score < same);
        // a stack far away still beats a merely usual place next to the bot
        Assert.True(ItemRules.StorageValue(same, 200) > ItemRules.StorageValue(usual, 0));
    }

    [Fact]
    public void Refill_thresholds()
    {
        Assert.True(ItemRules.TakesRefills(1, false, 2));
        Assert.False(ItemRules.TakesRefills(8, false, 2));
        Assert.False(ItemRules.TakesRefills(1, true, 2));
        Assert.False(ItemRules.TakesRefills(1, false, 0));
        Assert.True(ItemRules.IsSpent(0.1f));
        Assert.False(ItemRules.IsSpent(0.5f));
        Assert.False(ItemRules.IsUsableRefill(0.2f));
        Assert.True(ItemRules.IsUsableRefill(0.9f));
        Assert.True(ItemRules.IsRefillSlot(new[] { "weldingfuel" }));
        Assert.True(ItemRules.IsRefillSlot(new[] { "oxygensource", "weldingfuel" }));
        Assert.True(ItemRules.IsRefillSlot(new[] { "mobilebattery" }));
        Assert.True(ItemRules.IsRefillSlot(new[] { "revolverammo" }));
        Assert.False(ItemRules.IsRefillSlot(new[] { "smallitem" }));       // a cap's or a uniform's pocket
        Assert.False(ItemRules.IsRefillSlot(new string[0]));
    }

    [Theory]
    [InlineData(1, 6, 1)]     // one harpoon asked: one craft, six come out
    [InlineData(6, 6, 1)]
    [InlineData(7, 6, 2)]
    [InlineData(3, 1, 3)]
    [InlineData(0, 1, 1)]
    public void Craft_runs_count_items_not_crafts(int items, int perCraft, int runs) => Assert.Equal(runs, ItemRules.CraftRuns(items, perCraft));

    [Theory]
    [InlineData("ballast flora", true)]
    [InlineData("Балластная флора", true)]
    [InlineData("побеги в балласте", true)]
    [InlineData("crawler", false)]
    [InlineData(null, false)]
    public void Flora_words(string? target, bool flora) => Assert.Equal(flora, ItemRules.MeansFlora(target));
}
