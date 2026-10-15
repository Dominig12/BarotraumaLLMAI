using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LlmAgents.Agents;
using LlmAgents.Personas;

namespace LlmAgents.SettingsUi;

/// <summary>
/// Everything behind in-game configuration: requests from the settings window (JSON over LuaCs networking)
/// and "!llm ..." chat commands. Validates, saves the files and hands new settings to <see cref="apply"/>.
/// Runs on the game thread.
/// </summary>
public sealed class SettingsService
{
    public static readonly string[] CommandPrefixes = { "!llm", "!ии", "!ai" };

    readonly ConfigEditor editor;
    readonly PersonaStore personas;
    readonly Action<ModSettings, bool> apply;
    readonly Func<GameInfo> game;
    readonly Func<Agents.ModStatus?>? status;

    /// <param name="apply">(new settings, providers or limits changed) — called after every successful config change.</param>
    public SettingsService(ConfigEditor editor, PersonaStore personas, Action<ModSettings, bool> apply, Func<GameInfo> game,
        Func<Agents.ModStatus?>? status = null)
    {
        this.status = status;
        this.editor = editor;
        this.personas = personas;
        this.apply = apply;
        this.game = game;
    }

    public ModSettings Settings => editor.Settings;

    public List<SettingField> Fields() => SettingsSchema.Build(editor.Settings, personas, game(), SafeStatus());

    Agents.ModStatus? SafeStatus()
    {
        try { return status?.Invoke(); }
        catch (Exception e) { Log.Warn("status failed: " + e.Message); return null; }
    }

    public SettingsResponse State(bool canEdit, string? message = null, bool error = false)
    {
        if (!canEdit)
            return new SettingsResponse { CanEdit = false, Error = true, Message = message ?? "Нет прав: нужен владелец сервера или право «Управление настройками»." };
        return new SettingsResponse
        {
            CanEdit = true, Message = message, Error = error,
            Categories = SettingsSchema.Categories.ToList(),
            Fields = Fields(),
        };
    }

    // ------------------------------------------------------------------ window protocol

    public SettingsResponse Handle(SettingsRequest? req, bool canEdit)
    {
        if (req == null) return State(canEdit, "Неверный запрос", true);
        if (!canEdit) return State(false);
        switch (req.Op)
        {
            case "get":
                return State(true);
            case "set":
            {
                var err = Apply(req.Changes ?? new List<SettingChange>(), out var summary);
                return State(true, err ?? summary, err != null);
            }
            case "reload":
            {
                var err = Reload();
                return State(true, err ?? "Файлы настроек перечитаны", err != null);
            }
            default:
                return State(true, $"Неизвестная операция '{req.Op}'", true);
        }
    }

    public string HandleJson(string json, bool canEdit)
    {
        SettingsRequest? req = null;
        try { req = SettingsNet.Deserialize<SettingsRequest>(json); } catch (JsonException) { }
        return SettingsNet.Serialize(Handle(req, canEdit));
    }

    /// <summary>Applies a batch of changes: all config changes together (validated as a whole), then personas.</summary>
    public string? Apply(IReadOnlyList<SettingChange> changes, out string summary)
    {
        summary = "Нет изменений";
        if (changes.Count == 0) return null;
        var configChanges = changes.Where(c => c.Target == "config").ToList();
        var personaChanges = changes.Where(c => c.Target == "personas").ToList();
        if (changes.Count != configChanges.Count + personaChanges.Count) return "error: unknown target";

        if (configChanges.Count > 0)
        {
            var before = LimitsSignature(editor.Settings);
            var err = editor.TrySet(configChanges.Select(c => (c.Path, c.Value)));
            if (err != null) return err;
            try { editor.Save(); }
            catch (Exception e) { return "error: settings applied but not saved: " + e.Message; }
            apply(editor.Settings, LimitsSignature(editor.Settings) != before);
        }

        int personaCount = 0;
        if (personaChanges.Count > 0)
        {
            var err = ApplyPersonas(personaChanges, out personaCount);
            personas.Save();
            if (err != null) return err;
        }

        summary = $"Сохранено: {configChanges.Count + personaCount} изм.";
        Log.Info($"settings changed in game: {string.Join(", ", changes.Select(c => c.Target + ":" + string.Join(".", c.Path)))}");
        return null;
    }

    string? ApplyPersonas(List<SettingChange> changes, out int count)
    {
        count = 0;
        // a new preset needs its name and prompt together
        var newName = changes.FirstOrDefault(c => Is(c.Path, "presets", "+", "name"));
        var newPrompt = changes.FirstOrDefault(c => Is(c.Path, "presets", "+", "prompt"));
        string? created = null;
        var name = Str(newName?.Value);
        if (!string.IsNullOrWhiteSpace(name))
        {
            var id = PersonaStore.MakeId(name!);
            if (id.Length == 0) return "error: the preset name needs letters or digits";
            if (personas.HasPreset(id)) return $"error: preset '{id}' already exists";
            var err = personas.SetPreset(id, name, Str(newPrompt?.Value) ?? "");
            if (err != null) return err;
            created = id;
            count++;
        }

        foreach (var c in changes.Where(c => c != newName && c != newPrompt))
        {
            var p = c.Path;
            string? err = null;
            if (p.Length == 3 && p[0] == "characters")
            {
                err = p[2] switch
                {
                    "preset" => personas.SetCharacter(p[1], preset: Str(c.Value) ?? ""),
                    "prompt" => personas.SetCharacter(p[1], prompt: Str(c.Value) ?? ""),
                    "provider" => ValidProvider(Str(c.Value)) ?? personas.SetCharacter(p[1], provider: Str(c.Value) ?? ""),
                    "enabled" => personas.SetCharacter(p[1], enabled: Bool(c.Value) ?? true),
                    _ => $"error: unknown persona setting '{p[2]}'",
                };
            }
            else if (p.Length == 3 && p[0] == "presets" && p[1] != "+")
            {
                err = p[2] switch
                {
                    "name" => personas.SetPreset(p[1], name: Str(c.Value)),
                    "prompt" => personas.SetPreset(p[1], prompt: Str(c.Value) ?? ""),
                    "delete" => Bool(c.Value) == true ? (personas.RemovePreset(p[1]) ? null : $"error: no preset '{p[1]}'") : null,
                    _ => $"error: unknown preset setting '{p[2]}'",
                };
            }
            else if (!(p.Length == 3 && p[0] == "presets")) err = "error: unknown path " + string.Join(".", p);
            if (err != null) return err;
            count++;
        }
        return null;
    }

    string? ValidProvider(string? name) =>
        string.IsNullOrEmpty(name) || editor.Settings.Providers.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            ? null : $"error: no provider '{name}'";

    public string? Reload()
    {
        var err = editor.Reload();
        if (err != null) return err;
        apply(editor.Settings, true);
        return null;
    }

    /// <summary>Providers and limits: when these change the LLM router is rebuilt.</summary>
    static string LimitsSignature(ModSettings s) => JsonSerializer.Serialize(new
    {
        s.Providers, s.DefaultProvider, s.FallbackProvider,
        s.Global.MaxConcurrentRequests, s.Global.RequestsPerMinute, s.Global.DailyRequestBudget,
        s.Global.ProviderAuthCooldownSeconds, s.Global.ProviderBackoffMaxSeconds,
    });

    static bool Is(string[] path, params string[] expected) =>
        path.Length == expected.Length && path.Zip(expected).All(t => t.First == t.Second);

    static string? Str(JsonNode? n)
    {
        if (n is not JsonValue v) return null;
        if (v.TryGetValue<string>(out var s)) return s;
        return v.ToJsonString().Trim('"');
    }

    static bool? Bool(JsonNode? n)
    {
        if (n is JsonValue v && v.TryGetValue<bool>(out var b)) return b;
        return Str(n)?.ToLowerInvariant() switch { "true" or "1" or "on" or "yes" or "да" => true, "false" or "0" or "off" or "no" or "нет" => false, _ => null };
    }

    // ------------------------------------------------------------------ chat commands

    public static bool IsCommand(string text)
    {
        var t = text.TrimStart();
        return CommandPrefixes.Any(p => t.Equals(p, StringComparison.OrdinalIgnoreCase) || t.StartsWith(p + " ", StringComparison.OrdinalIgnoreCase));
    }

    public const string Help =
        "LLM-агенты. Окно настроек открывается, если у вас стоит клиентская часть мода (LuaCs), иначе — команды:\n" +
        "!llm status — что работает, модели, чем занят каждый бот; !llm ai <имя> on|off — LLM или обычный ИИ для бота\n" +
        "!llm list [вкладка] — ключи и значения; !llm get <ключ>; !llm set <ключ> <значение>\n" +
        "!llm persona <имя> — характер бота; !llm persona <имя> preset <id> | prompt <текст> | provider <модель> | clear\n" +
        "!llm presets — список характеров; !llm preset <id> <текст> — создать/изменить; !llm preset <id> delete\n" +
        "!llm reload — перечитать файлы. Имена с пробелами — в кавычках.";

    /// <summary>Runs a "!llm ..." command and returns the reply for the sender.</summary>
    public string HandleCommand(string text, bool canEdit)
    {
        var args = Tokenize(text.Trim());
        if (args.Count > 0) args.RemoveAt(0); // the prefix
        if (args.Count == 0 || args[0] is "help" or "?" or "помощь") return Help;
        if (!canEdit) return "Нет прав: нужен владелец сервера или право «Управление настройками».";

        var cmd = args[0].ToLowerInvariant();
        if (cmd is "status" or "статус" or "diag")
            return SafeStatus()?.ToText() ?? "Статус недоступен: агенты не запущены (проверьте llm_config.json и консоль сервера).";
        if (cmd is "ai" or "ии" && args.Count >= 3)
        {
            // !llm ai <name> on|off : LLM or the vanilla AI for one character
            var name = ResolveCrewName(args[1]);
            var on = Bool(JsonValue.Create(args[2]));
            if (on == null) return "Использование: !llm ai <имя> on|off";
            var err = Apply(new[] { new SettingChange { Path = new[] { "bots", "disabledCharacters", name }, Value = !on.Value } }, out _);
            return err ?? $"{name}: {(on.Value ? "управляет LLM" : "только ванильный ИИ")}";
        }
        try
        {
            switch (cmd)
            {
                case "list":
                {
                    var fields = Fields().Where(f => f.Type != FieldType.Info);
                    if (args.Count > 1) fields = fields.Where(f => f.Category.StartsWith(args[1], StringComparison.OrdinalIgnoreCase)
                                                                 || SettingsSchema.DottedKey(f).StartsWith(args[1], StringComparison.OrdinalIgnoreCase));
                    var lines = fields.Where(f => f.Type != FieldType.Text).Take(40)
                        .Select(f => $"{SettingsSchema.DottedKey(f)} = {Show(f.Value)}");
                    var text2 = string.Join("\n", lines);
                    return text2.Length == 0 ? "Ничего не найдено. Вкладки: " + string.Join(", ", SettingsSchema.Categories) : text2;
                }
                case "get":
                {
                    if (args.Count < 2) return "Использование: !llm get <ключ>";
                    var f = FindField(args[1]);
                    return f == null ? $"Нет такого ключа: {args[1]}. Посмотрите !llm list" : $"{SettingsSchema.DottedKey(f)} = {Show(f.Value)}";
                }
                case "set":
                {
                    if (args.Count < 3) return "Использование: !llm set <ключ> <значение>";
                    var f = FindField(args[1]);
                    if (f == null || f.Type == FieldType.Info) return $"Нет такого ключа: {args[1]}. Посмотрите !llm list";
                    var raw = string.Join(" ", args.Skip(2));
                    var (value, error) = Coerce(f, raw);
                    if (error != null) return error;
                    var err = Apply(new[] { new SettingChange { Target = f.Target, Path = f.Path, Value = value } }, out _);
                    return err ?? $"{SettingsSchema.DottedKey(f)} = {Show(value)}";
                }
                case "persona":
                case "персонаж":
                    return PersonaCommand(args.Skip(1).ToList());
                case "presets":
                    return string.Join("\n", personas.Presets().Select(p => $"{p.Id} — {p.Preset.Name}: {Tools.Clip(p.Preset.Prompt, 80)}"));
                case "preset":
                {
                    if (args.Count < 3) return "Использование: !llm preset <id> <текст> | !llm preset <id> delete";
                    var id = PersonaStore.MakeId(args[1]);
                    if (args[2] is "delete" or "удалить")
                    {
                        var ok = personas.RemovePreset(id);
                        personas.Save();
                        return ok ? $"Характер {id} удалён" : $"Нет характера {id}";
                    }
                    bool isNew = !personas.HasPreset(id);
                    var err = personas.SetPreset(id, isNew ? args[1] : null, string.Join(" ", args.Skip(2)));
                    personas.Save();
                    return err ?? $"Характер {id} {(isNew ? "создан" : "обновлён")}";
                }
                case "reload":
                    return Reload() ?? "Файлы настроек перечитаны";
                default:
                    return Help;
            }
        }
        catch (Exception e)
        {
            return "error: " + e.Message;
        }
    }

    string PersonaCommand(List<string> a)
    {
        if (a.Count == 0) return "Использование: !llm persona <имя> [preset <id> | prompt <текст> | provider <модель> | clear]";
        var name = ResolveCrewName(a[0]);
        if (a.Count == 1)
        {
            var p = personas.Get(name);
            if (p == null) return $"{name}: характер не задан";
            return $"{name}: характер {(p.Preset.Length > 0 ? p.Preset : "—")}, модель {(p.Provider.Length > 0 ? p.Provider : "по умолчанию")}" +
                   (p.Enabled ? "" : ", ВЫКЛЮЧЕН") + (p.Prompt.Length > 0 ? $"\nПромпт: {p.Prompt}" : "");
        }
        var what = a[1].ToLowerInvariant();
        var rest = string.Join(" ", a.Skip(2));
        string? err = what switch
        {
            "preset" => personas.SetCharacter(name, preset: rest),
            "prompt" => personas.SetCharacter(name, prompt: rest),
            "provider" => ValidProvider(rest) ?? personas.SetCharacter(name, provider: rest),
            "on" => personas.SetCharacter(name, enabled: true),
            "off" => personas.SetCharacter(name, enabled: false),
            "clear" => personas.RemoveCharacter(name) ? null : $"{name}: характер и так не задан",
            _ => "Использование: !llm persona <имя> [preset <id> | prompt <текст> | provider <модель> | on | off | clear]",
        };
        personas.Save();
        return err ?? $"{name}: сохранено";
    }

    /// <summary>A crew name as typed ("иван", "Ivan") resolved to the character aboard, if it matches one.</summary>
    string ResolveCrewName(string typed)
    {
        var crew = game().Crew;
        var exact = crew.FirstOrDefault(c => c.Name.Equals(typed, StringComparison.OrdinalIgnoreCase));
        if (exact != null) return exact.Name;
        var partial = crew.Where(c => c.Name.Contains(typed, StringComparison.OrdinalIgnoreCase)
                                   || Translit.ToLatin(c.Name).Contains(Translit.ToLatin(typed), StringComparison.OrdinalIgnoreCase)).ToList();
        return partial.Count == 1 ? partial[0].Name : typed;
    }

    SettingField? FindField(string key)
    {
        var fields = Fields();
        return fields.FirstOrDefault(f => SettingsSchema.DottedKey(f).Equals(key, StringComparison.OrdinalIgnoreCase))
            // short form: just the last part ("decisionIntervalSeconds") when it is unique
            ?? (fields.Count(f => f.Path.Length > 0 && f.Path[^1].Equals(key, StringComparison.OrdinalIgnoreCase)) == 1
                ? fields.First(f => f.Path.Length > 0 && f.Path[^1].Equals(key, StringComparison.OrdinalIgnoreCase)) : null);
    }

    /// <summary>Text from a chat command to a typed value for the field.</summary>
    public static (JsonNode? Value, string? Error) Coerce(SettingField f, string raw)
    {
        raw = raw.Trim();
        switch (f.Type)
        {
            case FieldType.Bool:
            {
                var b = Bool(JsonValue.Create(raw));
                return b == null ? (null, "Ожидается true/false (да/нет)") : (JsonValue.Create(b.Value), null);
            }
            case FieldType.Int:
            case FieldType.Float:
            {
                if (!double.TryParse(raw.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return (null, "Ожидается число");
                if (f.Min != null && d < f.Min || f.Max != null && d > f.Max) return (null, $"Допустимо от {f.Min} до {f.Max}");
                return f.Type == FieldType.Int ? (JsonValue.Create((int)Math.Round(d)), null) : (JsonValue.Create(d), null);
            }
            case FieldType.Enum:
            {
                if (raw is "-" or "\"\"" or "default" or "нет") raw = "";
                var options = f.Options ?? new List<string>();
                var match = options.FirstOrDefault(o => o.Equals(raw, StringComparison.OrdinalIgnoreCase));
                if (match == null && f.OptionLabels != null)
                {
                    int i = f.OptionLabels.FindIndex(l => l.Equals(raw, StringComparison.OrdinalIgnoreCase));
                    if (i >= 0 && i < options.Count) match = options[i];
                }
                return match == null ? (null, "Варианты: " + string.Join(", ", options.Select(o => o.Length == 0 ? "-" : o))) : (JsonValue.Create(match), null);
            }
            default:
                return (JsonValue.Create(raw), null);
        }
    }

    static string Show(JsonNode? v) => v switch
    {
        null => "(пусто)",
        JsonValue jv when jv.TryGetValue<string>(out var s) => s.Length == 0 ? "(пусто)" : Tools.Clip(s, 120),
        _ => v.ToJsonString(),
    };

    /// <summary>Splits on spaces, keeping "quoted parts" (also «…») together.</summary>
    public static List<string> Tokenize(string s)
    {
        var list = new List<string>();
        var sb = new StringBuilder();
        char? quote = null;
        foreach (var ch in s)
        {
            if (quote != null)
            {
                if (ch == quote) { list.Add(sb.ToString()); sb.Clear(); quote = null; }
                else sb.Append(ch);
            }
            else if (ch is '"' or '«')
            {
                if (sb.Length > 0) { list.Add(sb.ToString()); sb.Clear(); }
                quote = ch == '«' ? '»' : '"';
            }
            else if (char.IsWhiteSpace(ch))
            {
                if (sb.Length > 0) { list.Add(sb.ToString()); sb.Clear(); }
            }
            else sb.Append(ch);
        }
        if (sb.Length > 0) list.Add(sb.ToString());
        return list;
    }
}
