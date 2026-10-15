using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LlmAgents.Personas;

/// <summary>A reusable character template: a behaviour model written as a prompt.</summary>
public sealed class PersonaPreset
{
    public string Name { get; set; } = "";
    public string Prompt { get; set; } = "";
}

/// <summary>Personality of one crew member (by character name).</summary>
public sealed class CharacterPersona
{
    public bool Enabled { get; set; } = true;
    /// <summary>Id of a preset, or empty.</summary>
    public string Preset { get; set; } = "";
    /// <summary>Own text, added after the preset (or alone).</summary>
    public string Prompt { get; set; } = "";
    /// <summary>LLM provider for this character only; empty = by job / default.</summary>
    public string Provider { get; set; } = "";
}

public sealed class PersonaFile
{
    public int Version { get; set; } = 1;
    public Dictionary<string, PersonaPreset> Presets { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, CharacterPersona> Characters { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Characters and personalities of the crew: personas.json in the mod folder. Presets are templates
/// ("veteran", "coward"...); every crew member can get a preset and/or an own prompt. Edited in game
/// (settings window, !llm persona) or by hand. Thread-safe.
/// </summary>
public sealed class PersonaStore
{
    public const int MaxPromptLength = 1500;
    public const int MaxPresets = 40;
    public const int MaxCharacters = 200;

    readonly object sync = new();
    readonly string? path;
    PersonaFile data;

    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <param name="path">personas.json; null = in memory only (tests). A missing file is created with the built-in presets.</param>
    public PersonaStore(string? path)
    {
        this.path = path;
        data = Load() ?? Defaults();
        Normalize(data);
        if (path != null && !File.Exists(path)) Save();
    }

    PersonaFile? Load()
    {
        if (path == null || !File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<PersonaFile>(File.ReadAllText(path), Json);
        }
        catch (Exception e)
        {
            Log.Error($"{path} is damaged ({e.Message}); using the built-in presets, the old file is kept as .bad");
            try { File.Copy(path, path + ".bad", overwrite: true); } catch { /* best effort */ }
            return null;
        }
    }

    static void Normalize(PersonaFile f)
    {
        f.Presets = new Dictionary<string, PersonaPreset>(f.Presets ?? new(), StringComparer.OrdinalIgnoreCase);
        f.Characters = new Dictionary<string, CharacterPersona>(f.Characters ?? new(), StringComparer.OrdinalIgnoreCase);
        foreach (var p in f.Presets.Values) { p.Name ??= ""; p.Prompt ??= ""; }
        foreach (var c in f.Characters.Values) { c.Preset ??= ""; c.Prompt ??= ""; c.Provider ??= ""; }
    }

    public void Save()
    {
        if (path == null) return;
        string json;
        lock (sync) json = JsonSerializer.Serialize(data, Json);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, json, new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }
        catch (Exception e)
        {
            Log.Warn($"could not save {path}: {e.Message}");
        }
    }

    // ------------------------------------------------------------------ queries

    public IReadOnlyList<(string Id, PersonaPreset Preset)> Presets()
    {
        lock (sync) return data.Presets.OrderBy(kv => kv.Key).Select(kv => (kv.Key, new PersonaPreset { Name = kv.Value.Name, Prompt = kv.Value.Prompt })).ToList();
    }

    public IReadOnlyList<(string Name, CharacterPersona Persona)> Characters()
    {
        lock (sync) return data.Characters.OrderBy(kv => kv.Key).Select(kv => (kv.Key, Copy(kv.Value))).ToList();
    }

    public CharacterPersona? Get(string character)
    {
        lock (sync) return data.Characters.TryGetValue(character, out var c) ? Copy(c) : null;
    }

    static CharacterPersona Copy(CharacterPersona c) => new() { Enabled = c.Enabled, Preset = c.Preset, Prompt = c.Prompt, Provider = c.Provider };

    /// <summary>The persona text for the system prompt, or null when this character has none (or it is disabled).</summary>
    public string? PromptFor(string character)
    {
        lock (sync)
        {
            if (!data.Characters.TryGetValue(character, out var c) || !c.Enabled) return null;
            var parts = new List<string>();
            if (c.Preset.Length > 0 && data.Presets.TryGetValue(c.Preset, out var p) && p.Prompt.Trim().Length > 0) parts.Add(p.Prompt.Trim());
            if (c.Prompt.Trim().Length > 0) parts.Add(c.Prompt.Trim());
            return parts.Count == 0 ? null : string.Join("\n", parts);
        }
    }

    public string? ProviderFor(string character)
    {
        lock (sync)
            return data.Characters.TryGetValue(character, out var c) && c.Enabled && c.Provider.Length > 0 ? c.Provider : null;
    }

    // ------------------------------------------------------------------ changes (return an error text or null)

    public string? SetCharacter(string character, string? preset = null, string? prompt = null, bool? enabled = null, string? provider = null)
    {
        character = character.Trim();
        if (character.Length == 0) return "error: empty character name";
        lock (sync)
        {
            if (preset != null && preset.Length > 0 && !data.Presets.ContainsKey(preset.Trim()))
                return $"error: no preset '{preset}'; presets: {string.Join(", ", data.Presets.Keys)}";
            if (!data.Characters.TryGetValue(character, out var c))
            {
                if (data.Characters.Count >= MaxCharacters) return $"error: too many characters (max {MaxCharacters})";
                data.Characters[character] = c = new CharacterPersona();
            }
            if (preset != null) c.Preset = preset.Trim();
            if (prompt != null) c.Prompt = Clip(prompt);
            if (enabled != null) c.Enabled = enabled.Value;
            if (provider != null) c.Provider = provider.Trim();
            // nothing left: forget the entry
            if (c.Preset.Length == 0 && c.Prompt.Trim().Length == 0 && c.Provider.Length == 0 && c.Enabled) data.Characters.Remove(character);
        }
        return null;
    }

    public bool RemoveCharacter(string character)
    {
        lock (sync) return data.Characters.Remove(character.Trim());
    }

    public string? SetPreset(string id, string? name = null, string? prompt = null)
    {
        id = MakeId(id);
        if (id.Length == 0) return "error: empty preset id";
        lock (sync)
        {
            if (!data.Presets.TryGetValue(id, out var p))
            {
                if (data.Presets.Count >= MaxPresets) return $"error: too many presets (max {MaxPresets})";
                data.Presets[id] = p = new PersonaPreset { Name = id };
            }
            if (name != null && name.Trim().Length > 0) p.Name = name.Trim();
            if (prompt != null) p.Prompt = Clip(prompt);
        }
        return null;
    }

    /// <summary>Removes a preset; characters using it keep their own prompt.</summary>
    public bool RemovePreset(string id)
    {
        lock (sync)
        {
            if (!data.Presets.Remove(id.Trim())) return false;
            foreach (var c in data.Characters.Values.Where(c => c.Preset.Equals(id.Trim(), StringComparison.OrdinalIgnoreCase))) c.Preset = "";
            return true;
        }
    }

    public bool HasPreset(string id) { lock (sync) return data.Presets.ContainsKey(id.Trim()); }

    /// <summary>Lowercase latin/digit id from a display name ("Старый ворчун" -> "staryy_vorchun").</summary>
    public static string MakeId(string s)
    {
        var latin = Agents.Translit.ToLatin(s.Trim());
        var sb = new StringBuilder();
        foreach (var ch in latin)
            sb.Append(char.IsLetterOrDigit(ch) && ch < 128 ? char.ToLowerInvariant(ch) : '_');
        var id = string.Join("_", sb.ToString().Split('_', StringSplitOptions.RemoveEmptyEntries));
        return id.Length > 32 ? id.Substring(0, 32) : id;
    }

    static string Clip(string s)
    {
        s = s.Replace("\r\n", "\n").Trim();
        return s.Length <= MaxPromptLength ? s : s.Substring(0, MaxPromptLength);
    }

    // ------------------------------------------------------------------ built-in presets

    public static PersonaFile Defaults()
    {
        var f = new PersonaFile();
        void Add(string id, string name, string prompt) => f.Presets[id] = new PersonaPreset { Name = name, Prompt = prompt };
        Add("veteran", "Ветеран",
            "Ты ветеран, переживший десятки погружений. Спокоен, говоришь мало и по делу, без паники. Сначала думаешь о главной угрозе, " +
            "замечаешь то, что упускают новички (запас топлива, кислород, костюмы). Опекаешь молодых, но не терпишь болтовни в эфире.");
        Add("rookie", "Новичок",
            "Ты новичок, это твои первые погружения. Стараешься изо всех сил, иногда переспрашиваешь и волнуешься, " +
            "охотно просишь совета у опытных и благодаришь за помощь. Приказы выполняешь старательно и сразу докладываешь.");
        Add("coward", "Трус",
            "Ты боишься глубины и монстров. При опасности сначала думаешь о своей шкуре: надеваешь костюм, держишься подальше от тварей, " +
            "жалуешься и ноешь в эфире. Приказы игроков всё же выполняешь, но с опаской и не первым лезешь в бой.");
        Add("joker", "Весельчак",
            "Ты балагур: шутишь даже в беде, подбадриваешь экипаж, даёшь всем прозвища. Работу делаешь хорошо, " +
            "но в эфире не можешь удержаться от острого словца. Шутки короткие, не мешают делу.");
        Add("pedant", "Педант",
            "Ты педант и перфекционист. Всё по уставу: докладываешь чётко, с цифрами (проценты, отсеки), любишь порядок, " +
            "убираешь разбросанные вещи, следишь за запасами и экономишь медикаменты. Раздражаешься, когда другие делают небрежно.");
        Add("grumpy", "Ворчун",
            "Ты вечно недоволен: ворчишь на начальство, на подлодку, на еду и на коллег. Но руки у тебя золотые, " +
            "и работу ты делаешь, пусть и с бурчанием. На похвалу отвечаешь ворчанием.");
        Add("hero", "Герой",
            "Ты смелый и самоотверженный. Первым бросаешься туда, где опасно: к пробоине, к раненому, к монстру. " +
            "Прикрываешь товарищей, вытаскиваешь раненых, о себе думаешь в последнюю очередь, но костюм надеваешь.");
        Add("rebel", "Бунтарь",
            "Ты не любишь, когда тобой командуют, и не стесняешься спорить и предлагать своё. Приказы игроков всё же выполняешь, " +
            "но сначала коротко высказываешь, что думаешь. Ценишь тех, кто объясняет, а не просто приказывает.");
        Add("silent", "Молчун",
            "Ты почти не разговариваешь: в эфире только самое необходимое, одно-два слова (\"Принял.\", \"Течь заварена.\"). " +
            "На шутки не отвечаешь. Работаешь сосредоточенно и надёжно.");
        Add("caring", "Заботливый",
            "Ты заботишься об экипаже: замечаешь раненых и уставших, спрашиваешь, как дела, напоминаешь надеть костюм, " +
            "делишься снаряжением. Говоришь тепло и поддерживающе, в опасности успокаиваешь остальных.");
        return f;
    }
}
