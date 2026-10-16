using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using LlmAgents.Agents;
using LlmAgents.Llm;
using Xunit;

namespace LlmAgents.Tests;

public class ArchitectureTests
{
    static BotAgent Bot(string job) => new(new BotHandle(1, "Ann", job));

    [Fact]
    public void Every_tool_is_defined_once_with_a_group()
    {
        var names = Tools.Defs.Select(d => d.Name).ToList();
        Assert.Equal(names.Count, names.Distinct().Count());
        Assert.All(Tools.Defs, d => Assert.NotEqual((ToolGroup)0, d.Group));
        Assert.All(Tools.Defs, d => Assert.False(string.IsNullOrWhiteSpace(d.Description)));
    }

    [Fact]
    public void Assistant_without_trouble_gets_the_core_tools_only()
    {
        var g = ToolSelection.For(Bot("assistant"), new Observation(), new string[0], 0, memory: false);
        Assert.True(g.HasFlag(ToolGroup.Core));
        Assert.False(g.HasFlag(ToolGroup.Combat));
        Assert.False(g.HasFlag(ToolGroup.Medical));
        Assert.False(g.HasFlag(ToolGroup.Memory));
        var offered = Tools.For(new List<string>(), false, false, g);
        Assert.True(offered.Count < Tools.For(new List<string>(), false, false).Count);
        Assert.Contains(offered, t => t.Name == "say");
    }

    [Fact]
    public void Orders_and_situation_add_groups()
    {
        var obs = new Observation();
        obs.Infestations.Add("ballast flora in Ballast");
        var g = ToolSelection.For(Bot("assistant"), obs, new[] { "вылечи капитана" }, 0, memory: true);
        Assert.True(g.HasFlag(ToolGroup.Combat));
        Assert.True(g.HasFlag(ToolGroup.Medical));
        Assert.True(g.HasFlag(ToolGroup.Memory));
        var unpack = ToolSelection.For(Bot("assistant"), new Observation(), new[] { "разложи ящик по местам" }, 0, false);
        Assert.True(unpack.HasFlag(ToolGroup.Items));
        Assert.False(unpack.HasFlag(ToolGroup.Inventory));
        Assert.True(ToolSelection.For(Bot("assistant"), new Observation(), new[] { "заправь сварку" }, 0, false).HasFlag(ToolGroup.Inventory));
        var full = new Observation { InventorySpace = "0 free slots; hands: right empty, left empty" };
        Assert.True(ToolSelection.For(Bot("assistant"), full, new string[0], 0, false).HasFlag(ToolGroup.Inventory));
    }

    [Fact]
    public void Used_group_stays_for_a_while()
    {
        var bot = Bot("assistant");
        var attack = Tools.Defs.First(d => d.Group == ToolGroup.Combat);
        bot.UsedTool(attack.Name, 100);
        Assert.True(ToolSelection.For(bot, new Observation(), new string[0], 150, false).HasFlag(ToolGroup.Combat));
        Assert.False(ToolSelection.For(bot, new Observation(), new string[0], 100 + ToolSelection.StickySeconds + 1, false).HasFlag(ToolGroup.Combat));
    }

    [Fact]
    public void Hidden_tools_are_listed_and_still_parsed()
    {
        var g = ToolSelection.For(Bot("assistant"), new Observation(), new string[0], 0, false);
        var offered = Tools.For(new List<string>(), false, false, g);
        var line = ToolSelection.HiddenLine(offered.Select(t => t.Name).ToList(), false);
        Assert.NotNull(line);
        var hidden = Tools.Defs.First(d => !offered.Any(o => o.Name == d.Name) && !d.ControlOnly);
        Assert.Contains(hidden.Name, line);
        Assert.DoesNotContain(Tools.Defs.First(d => d.ControlOnly).Name + ",", line);
        var req = new LlmRequest("s", new List<LlmTurn>(), offered, 100, 0, Tools.All);
        Assert.Contains(req.Understood, t => t.Name == hidden.Name);
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

public class LogWriterTests
{
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
            Assert.Contains("line 99", System.IO.File.ReadAllText(path));
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

public class OrderHintTableTests
{
    public static IEnumerable<object[]> Rows()
    {
        var path = System.IO.Path.Combine(System.AppContext.BaseDirectory, "Data", "order_hints.tsv");
        foreach (var line in System.IO.File.ReadAllLines(path))
        {
            if (line.Length == 0 || line.StartsWith("#")) continue;
            var parts = line.Split('\t');
            yield return new object[] { parts[0], parts[1] };
        }
    }

    [Theory, MemberData(nameof(Rows))]
    public void Phrase_gets_its_hint(string phrase, string expected)
    {
        var hint = OrderHints.Suggest(phrase, "Dan", "Engine [h4]");
        if (expected == "-") { Assert.Null(hint); return; }
        foreach (var part in expected.Split(" && "))
        {
            if (part.StartsWith("!")) Assert.DoesNotContain(part[1..], hint ?? "");
            else Assert.Contains(part, hint ?? "(no hint)");
        }
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
    }

    [Theory]
    [InlineData("ballast flora", true)]
    [InlineData("Балластная флора", true)]
    [InlineData("побеги в балласте", true)]
    [InlineData("crawler", false)]
    [InlineData(null, false)]
    public void Flora_words(string? target, bool flora) => Assert.Equal(flora, ItemRules.MeansFlora(target));
}
