using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using LlmAgents.Agents;
using LlmAgents.Llm;
using LlmAgents.Personas;
using LlmAgents.SettingsUi;
using Xunit;

namespace LlmAgents.Tests;

public sealed class PersonaStoreTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "llmagents-personas-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(dir, true); } catch { } }

    [Fact]
    public void MissingFileIsCreatedWithBuiltInPresets()
    {
        var path = Path.Combine(dir, "personas.json");
        var s = new PersonaStore(path);
        Assert.True(File.Exists(path));
        Assert.Contains(s.Presets(), p => p.Id == "veteran" && p.Preset.Name == "Ветеран");
        Assert.True(s.Presets().Count >= 8);
        Assert.Contains("Ветеран", File.ReadAllText(path)); // readable Cyrillic
    }

    [Fact]
    public void PromptCombinesPresetAndOwnTextAndSurvivesReload()
    {
        var path = Path.Combine(dir, "personas.json");
        var s = new PersonaStore(path);
        Assert.Null(s.PromptFor("Ivan"));
        Assert.Null(s.SetCharacter("Ivan", preset: "grumpy", prompt: "Бывший шахтёр, любит рассказывать про шахты."));
        var p = s.PromptFor("ivan")!;
        Assert.StartsWith("Ты вечно недоволен", p);
        Assert.EndsWith("про шахты.", p);
        s.Save();

        var again = new PersonaStore(path);
        Assert.Equal(p, again.PromptFor("Ivan"));
        Assert.Null(again.SetCharacter("Ivan", enabled: false));
        Assert.Null(again.PromptFor("Ivan"));
    }

    [Fact]
    public void ValidationAndCleanup()
    {
        var s = new PersonaStore(null);
        Assert.StartsWith("error: no preset", s.SetCharacter("Ann", preset: "nope"));
        Assert.Null(s.SetCharacter("Ann", preset: "joker", provider: "cloud"));
        Assert.Equal("cloud", s.ProviderFor("Ann"));
        Assert.True(s.RemovePreset("joker"));
        Assert.Equal("", s.Get("Ann")!.Preset);       // reference cleared
        Assert.Null(s.SetCharacter("Ann", provider: "")); // nothing left -> forgotten
        Assert.Null(s.Get("Ann"));
        Assert.Equal(PersonaStore.MaxPromptLength, s.SetCharacter("Bob", prompt: new string('x', 5000)) == null ? s.Get("Bob")!.Prompt.Length : -1);
    }

    [Theory]
    [InlineData("Старый ворчун", "staryy_vorchun")]
    [InlineData("  Cool Guy 2 ", "cool_guy_2")]
    [InlineData("!!!", "")]
    public void PresetIds(string name, string id) => Assert.Equal(id, PersonaStore.MakeId(name));
}

public sealed class ConfigEditorTests
{
    static ConfigEditor Editor() => new(Fixtures.Config(extraRoot: "\"myCustomKey\": 42,"));

    [Fact]
    public void SetsValuesValidatesAndKeepsUnknownKeys()
    {
        var e = Editor();
        Assert.Null(e.TrySet(new[] { (new[] { "global", "decisionIntervalSeconds" }, (JsonNode?)JsonValue.Create(7.5)) }));
        Assert.Equal(7.5, e.Settings.Global.DecisionIntervalSeconds);
        Assert.Contains("\"myCustomKey\": 42", e.ToJson());

        // invalid result: nothing changes
        var err = e.TrySet(new[]
        {
            (new[] { "global", "maxNotes" }, (JsonNode?)JsonValue.Create(9)),
            (new[] { "defaultProvider" }, (JsonNode?)JsonValue.Create("nope")),
        });
        Assert.StartsWith("error:", err);
        Assert.Equal(2, e.Settings.Global.MaxNotes);
        Assert.Equal("a", e.Settings.DefaultProvider);
    }

    [Fact]
    public void ProvidersByNameJobsAndMaps()
    {
        var e = Editor();
        Assert.Null(e.TrySet(new[]
        {
            (new[] { "providers", "b", "model" }, (JsonNode?)JsonValue.Create("gemma3:27b")),
            (new[] { "bots", "disabledJobs", "assistant" }, (JsonNode?)JsonValue.Create(true)),
            (new[] { "bots", "providerByJob", "captain" }, (JsonNode?)JsonValue.Create("b")),
            (new[] { "bots", "speechLanguage" }, (JsonNode?)JsonValue.Create("Russian")), // new key, created
        }));
        Assert.Equal("gemma3:27b", e.Settings.Providers.Single(p => p.Name == "b").Model);
        Assert.False(e.Settings.IsJobEnabled("assistant"));
        Assert.Equal("b", e.Settings.ProviderFor("captain"));
        Assert.Equal("Russian", e.Settings.Bots.SpeechLanguage);
        Assert.True(e.Get(new[] { "bots", "disabledJobs", "assistant" })!.GetValue<bool>());

        Assert.Null(e.TrySet(new[]
        {
            (new[] { "bots", "providerByJob", "captain" }, (JsonNode?)JsonValue.Create("")), // empty = remove the entry
            (new[] { "bots", "disabledJobs", "assistant" }, (JsonNode?)JsonValue.Create(false)),
        }));
        Assert.Equal("a", e.Settings.ProviderFor("captain"));
        Assert.True(e.Settings.IsJobEnabled("assistant"));
        Assert.StartsWith("error: no provider", e.TrySet(new[] { (new[] { "providers", "zzz", "model" }, (JsonNode?)JsonValue.Create("x")) }));
    }

    [Fact]
    public void ApiKeysCannotBeSetFromTheGame()
    {
        var e = Editor();
        Assert.Contains("API keys", e.TrySet(new[] { (new[] { "providers", "a", "apiKey" }, (JsonNode?)JsonValue.Create("sk-123")) }));
        Assert.DoesNotContain("sk-123", e.ToJson());
    }

    [Fact]
    public void SavesAndReloadsFromDisk()
    {
        var path = Path.Combine(Path.GetTempPath(), "llmcfg-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, Fixtures.Config());
            var e = ConfigEditor.Load(path);
            Assert.Null(e.TrySet(new[] { (new[] { "chat", "prefix" }, (JsonNode?)JsonValue.Create("!")) }));
            e.Save();
            Assert.Equal("!", ModSettings.LoadFile(path).Chat.Prefix);

            File.WriteAllText(path, File.ReadAllText(path).Replace("\"!\"", "\"?\""));
            Assert.Null(e.Reload());
            Assert.Equal("?", e.Settings.Chat.Prefix);
            File.WriteAllText(path, "{ broken");
            Assert.StartsWith("error:", e.Reload());
            Assert.Equal("?", e.Settings.Chat.Prefix); // kept
        }
        finally { File.Delete(path); }
    }
}

public sealed class SettingsServiceTests
{
    readonly ConfigEditor editor = new(Fixtures.Config());
    readonly PersonaStore personas = new(null);
    readonly List<(ModSettings S, bool Providers)> applied = new();
    readonly SettingsService svc;
    GameInfo game = new(
        new[] { new CrewEntry("Иван Петров", "captain", "Вспыльчивый", false), new CrewEntry("Olga Smirnova", "engineer", null, true),
                new CrewEntry("Daniil", "captain", null, false, IsBot: false) },
        new[] { "captain", "engineer", "mechanic", "medicaldoctor", "securityofficer", "assistant" });

    public SettingsServiceTests()
    {
        svc = new SettingsService(editor, personas, (s, p) => applied.Add((s, p)), () => game);
    }

    [Fact]
    public void DirectorIsSwitchedOnFromChat()
    {
        Assert.Equal("director.enabled = true", svc.HandleCommand("!llm set director.enabled true", true));
        Assert.True(editor.Settings.Director.Enabled);
        Assert.Equal("director.provider = b", svc.HandleCommand("!llm set director.provider b", true));
        Assert.Equal("b", applied[^1].S.Director.Provider);
    }

    [Fact]
    public void WithoutPermissionNothingIsShownOrChanged()
    {
        var r = svc.Handle(new SettingsRequest { Op = "get" }, canEdit: false);
        Assert.False(r.CanEdit);
        Assert.Empty(r.Fields);
        var set = svc.Handle(new SettingsRequest { Op = "set", Changes = new() { new() { Path = new[] { "enabled" }, Value = false } } }, canEdit: false);
        Assert.False(set.CanEdit);
        Assert.True(editor.Settings.Enabled);
        Assert.StartsWith("Нет прав", svc.HandleCommand("!llm set enabled false", canEdit: false));
    }

    [Fact]
    public void SchemaFieldsAreUniqueAndEveryEditableOneRoundTrips()
    {
        var fields = svc.Fields();
        Assert.Equal(fields.Count, fields.Select(f => f.Key).Distinct().Count());
        Assert.All(fields, f => Assert.Contains(f.Category, SettingsSchema.Categories));
        // the whole schema written back unchanged must be accepted (catches path/type mistakes)
        var changes = fields.Where(f => f.Type != FieldType.Info && !(f.Target == "personas" && f.Path[0] == "presets"))
            .Select(f => new SettingChange { Target = f.Target, Path = f.Path, Value = f.Value?.DeepClone() }).ToList();
        Assert.Null(svc.Apply(changes, out _));
        Assert.Equal(Fixtures.Settings().Global.DecisionIntervalSeconds, editor.Settings.Global.DecisionIntervalSeconds);
        // crew bots get persona fields, players do not; known jobs get job fields
        Assert.Contains(fields, f => f.Target == "personas" && f.Path.SequenceEqual(new[] { "characters", "Иван Петров", "preset" }));
        Assert.DoesNotContain(fields, f => f.Path.Length > 1 && f.Path[1] == "Daniil");
        Assert.Contains(fields, f => f.Path.SequenceEqual(new[] { "bots", "extraPromptByJob", "securityofficer" }));
        Assert.Contains(fields, f => f.Group != null && f.Group.Contains("ПОЛНЫЙ КОНТРОЛЬ ИИ"));
    }

    [Fact]
    public void WindowSetAppliesConfigAndPersonas()
    {
        var r = svc.Handle(new SettingsRequest
        {
            Op = "set",
            Changes = new()
            {
                new() { Path = new[] { "global", "decisionIntervalSeconds" }, Value = 15.0 },
                new() { Target = "personas", Path = new[] { "characters", "Иван Петров", "preset" }, Value = "veteran" },
                new() { Target = "personas", Path = new[] { "characters", "Иван Петров", "prompt" }, Value = "Любит чай." },
                new() { Target = "personas", Path = new[] { "presets", "+", "name" }, Value = "Старый ворчун" },
                new() { Target = "personas", Path = new[] { "presets", "+", "prompt" }, Value = "Ворчит, но всё делает." },
            },
        }, canEdit: true);
        Assert.False(r.Error, r.Message);
        Assert.Equal(15, editor.Settings.Global.DecisionIntervalSeconds);
        Assert.Single(applied);
        Assert.False(applied[0].Providers); // no provider/limit change: the router is kept
        Assert.Contains("Любит чай.", personas.PromptFor("Иван Петров"));
        Assert.True(personas.HasPreset("staryy_vorchun"));
        Assert.Contains(r.Fields, f => f.Path.SequenceEqual(new[] { "presets", "staryy_vorchun", "prompt" }));

        var limits = svc.Handle(new SettingsRequest { Op = "set", Changes = new() { new() { Path = new[] { "providers", "a", "maxConcurrent" }, Value = 2 } } }, true);
        Assert.False(limits.Error, limits.Message);
        Assert.True(applied.Last().Providers);

        var bad = svc.Handle(new SettingsRequest { Op = "set", Changes = new() { new() { Path = new[] { "global", "tickSeconds" }, Value = 0 } } }, true);
        Assert.True(bad.Error);
        Assert.Equal(2, applied.Count);
    }

    [Fact]
    public void JsonRoundTripLikeTheNetwork()
    {
        var reply = svc.HandleJson("{\"op\":\"get\"}", canEdit: true);
        var r = SettingsNet.Deserialize<SettingsResponse>(reply)!;
        Assert.True(r.CanEdit);
        Assert.NotEmpty(r.Fields);
        Assert.Equal(SettingsSchema.Categories, r.Categories);
        var f = r.Fields.First(x => x.Path.SequenceEqual(new[] { "global", "maxNotes" }));
        Assert.Equal(2, f.Value!.GetValue<int>());
        Assert.True(SettingsNet.Deserialize<SettingsResponse>(svc.HandleJson("garbage", true))!.Error);
    }

    [Fact]
    public void ChatCommands()
    {
        Assert.True(SettingsService.IsCommand("!llm"));
        Assert.True(SettingsService.IsCommand("  !LLM set x 1"));
        Assert.True(SettingsService.IsCommand("!ии"));
        Assert.False(SettingsService.IsCommand("!llmx"));
        Assert.False(SettingsService.IsCommand("Механики, llm"));

        Assert.Contains("!llm set", svc.HandleCommand("!llm", true));
        Assert.Equal("global.decisionIntervalSeconds = 12", svc.HandleCommand("!llm set global.decisionIntervalSeconds 12", true));
        Assert.Equal(12, editor.Settings.Global.DecisionIntervalSeconds);
        Assert.Equal("global.maxNotes = 4", svc.HandleCommand("!llm set maxNotes 4", true)); // short key
        Assert.Contains("от 1 до 40", svc.HandleCommand("!llm set maxNotes 99", true));
        Assert.Equal("bots.autonomy = order", svc.HandleCommand("!llm set bots.autonomy \"По приказу\"", true));
        Assert.Equal("enabled = false", svc.HandleCommand("!llm set enabled нет", true));
        Assert.StartsWith("Нет такого ключа", svc.HandleCommand("!llm set nope 1", true));
        Assert.Equal("bots.providerByJob.captain = b", svc.HandleCommand("!llm set bots.providerByJob.captain b", true));
        Assert.Equal("b", editor.Settings.ProviderFor("captain"));
        Assert.Contains("global.maxNotes = 4", svc.HandleCommand("!llm list global", true));
        Assert.Equal("global.maxNotes = 4", svc.HandleCommand("!llm get global.maxNotes", true));
    }

    [Fact]
    public void PersonaAndPresetCommands()
    {
        // "ivan" matches "Иван Петров" through transliteration
        Assert.Equal("Иван Петров: сохранено", svc.HandleCommand("!llm persona ivan preset hero", true));
        Assert.Equal("Иван Петров: сохранено", svc.HandleCommand("!llm persona Иван prompt Говорит стихами.", true));
        Assert.Contains("Говорит стихами.", personas.PromptFor("Иван Петров"));
        Assert.Contains("характер hero", svc.HandleCommand("!llm persona Иван", true));
        Assert.StartsWith("error: no provider", svc.HandleCommand("!llm persona Иван provider zzz", true));
        Assert.Equal("Olga Smirnova: сохранено", svc.HandleCommand("!llm persona \"Olga Smirnova\" provider b", true));
        Assert.Equal("b", personas.ProviderFor("Olga Smirnova"));

        Assert.Equal("Характер pirat создан", svc.HandleCommand("!llm preset Пират Говорит как пират, «Тысяча чертей!»", true));
        Assert.Contains("pirat", svc.HandleCommand("!llm presets", true));
        Assert.Equal("Характер pirat удалён", svc.HandleCommand("!llm preset pirat delete", true));
        Assert.Equal("Иван Петров: сохранено", svc.HandleCommand("!llm persona Иван clear", true));
        Assert.Null(personas.Get("Иван Петров"));
    }

    [Fact]
    public void Tokenizer() =>
        Assert.Equal(new[] { "!llm", "persona", "Ivan Petrov", "prompt", "a", "b c" },
            SettingsService.Tokenize("!llm persona \"Ivan Petrov\" prompt a «b c»"));
}

public sealed class PersonaPromptTests
{
    readonly FakeClock clock = new();
    readonly FakeGame game = new();
    readonly ConcurrentQueue<(string Url, string System)> requests = new();

    AgentManager Create(PersonaStore personas, Action<ModSettings>? tweak = null)
    {
        var settings = Fixtures.Settings();
        tweak?.Invoke(settings);
        var handler = new FakeHandler((req, body) =>
        {
            requests.Enqueue((req.RequestUri!.ToString(), body["messages"]![0]!["content"]!.GetValue<string>()));
            return Task.FromResult((HttpStatusCode.OK, Fixtures.OpenAiToolResponse(("wait", "{}"))));
        });
        return new AgentManager(settings, game, new LlmRouter(settings, handler, clock.Utc), clock.Get, personas: personas);
    }

    async Task Step(AgentManager mgr) { mgr.Update(); await mgr.WhenIdle(); clock.Advance(0.5); }

    [Fact]
    public async Task PersonaGoesIntoTheSystemPromptAndPicksTheProvider()
    {
        game.Bots.Add(new BotHandle(1, "Ann", "mechanic", "Lazy"));
        game.Observations[1] = new Observation { Name = "Ann", Job = "mechanic" };
        var personas = new PersonaStore(null);
        personas.SetCharacter("Ann", preset: "coward", prompt: "Боится крабов.", provider: "b");
        var mgr = Create(personas, s => s.Bots.SpeechLanguage = "Russian");
        await Step(mgr);
        var (url, system) = requests.Single();
        Assert.StartsWith("http://b.test", url);
        Assert.Contains("YOUR CHARACTER", system);
        Assert.Contains("Ты боишься глубины", system);
        Assert.Contains("Боится крабов.", system);
        Assert.DoesNotContain("Lazy", system); // a persona replaces the game's trait
        Assert.Contains("Speak Russian", system);
    }

    [Fact]
    public async Task GameTraitIsUsedWithoutPersonaUnlessDisabled()
    {
        game.Bots.Add(new BotHandle(1, "Ann", "mechanic", "Lazy"));
        game.Observations[1] = new Observation { Name = "Ann", Job = "mechanic" };
        var mgr = Create(new PersonaStore(null));
        await Step(mgr);
        Assert.Contains("Your personality trait: Lazy", requests.Single().System);
        Assert.StartsWith("http://a.test", requests.Single().Url);

        var mgr2 = Create(new PersonaStore(null), s => s.Bots.UseGamePersonality = false);
        while (requests.TryDequeue(out _)) { }
        await Step(mgr2);
        Assert.DoesNotContain("personality trait", requests.Single().System);
    }

    [Fact]
    public async Task AppliedSettingsTakeEffectWithoutLosingAgents()
    {
        game.AddBot(1, "Ann", "mechanic");
        var mgr = Create(new PersonaStore(null));
        await Step(mgr);
        var ann = mgr.GetAgent(1);
        Assert.StartsWith("http://a.test", requests.Last().Url);

        var s2 = Fixtures.Settings();
        s2.DefaultProvider = "b";
        var handler = new FakeHandler((req, body) =>
        {
            requests.Enqueue((req.RequestUri!.ToString(), ""));
            return Task.FromResult((HttpStatusCode.OK, Fixtures.OpenAiToolResponse(("wait", "{}"))));
        });
        mgr.ApplySettings(s2, new LlmRouter(s2, handler, clock.Utc));
        clock.Advance(20);
        mgr.GetAgent(1)!.ReceivePlayerOrder("Daniil: status");
        await Step(mgr);
        Assert.Same(ann, mgr.GetAgent(1));
        Assert.StartsWith("http://b.test", requests.Last().Url);
    }
}
