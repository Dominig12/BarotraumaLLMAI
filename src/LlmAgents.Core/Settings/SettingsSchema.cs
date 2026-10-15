using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using LlmAgents.Personas;

namespace LlmAgents.SettingsUi;

/// <summary>A crew member the settings window can give a persona to.</summary>
public sealed record CrewEntry(string Name, string Job, string? Trait, bool AiControl, bool IsBot = true);

/// <summary>What the game knows right now: who is aboard and which jobs exist.</summary>
public sealed record GameInfo(IReadOnlyList<CrewEntry> Crew, IReadOnlyList<string> Jobs);

/// <summary>
/// Builds the list of fields the in-game settings window shows (and the keys !llm get/set accept).
/// Values are the effective ones (defaults included), not just what the file contains.
/// </summary>
public static class SettingsSchema
{
    public const string StatusTab = "Статус";
    public const string General = "Общее";
    public const string Limits = "Лимиты";
    public const string Models = "Модели";
    public const string Jobs = "Профессии";
    public const string Crew = "Экипаж";
    public const string Presets = "Характеры";
    public const string ChatAndMemory = "Чат и память";

    public static readonly IReadOnlyList<string> Categories = new[] { StatusTab, General, Crew, Presets, Jobs, Models, Limits, ChatAndMemory };

    static SettingField F(string category, string label, string type, JsonNode? value, params string[] path) =>
        new() { Category = category, Label = label, Type = type, Value = value, Path = path };

    static JsonNode? V(object? o) => o switch
    {
        null => null,
        bool b => JsonValue.Create(b),
        int i => JsonValue.Create(i),
        double d => JsonValue.Create(d),
        string s => JsonValue.Create(s),
        _ => JsonValue.Create(o.ToString()),
    };

    public static List<SettingField> Build(ModSettings s, PersonaStore personas, GameInfo game, Agents.ModStatus? status = null)
    {
        var list = new List<SettingField>();
        if (status != null) AddStatus(list, s, status, game);
        var providerNames = s.Providers.Select(p => p.Name).ToList();
        var optionalProviders = new List<string> { "" }.Concat(providerNames).ToList();
        var optionalProviderLabels = new List<string> { "(по умолчанию)" }.Concat(providerNames).ToList();

        void Add(SettingField f, string? help = null, double? min = null, double? max = null,
                 List<string>? options = null, List<string>? labels = null, string? group = null)
        {
            f.Help = help; f.Min = min; f.Max = max; f.Options = options; f.OptionLabels = labels; f.Group = group;
            list.Add(f);
        }

        var g = s.Global;
        // ---------------------------------------------------------------- general
        Add(F(General, "Мод включён", FieldType.Bool, V(s.Enabled), "enabled"), "Выключено: все боты на ванильном ИИ.");
        Add(F(General, "Самостоятельность ботов", FieldType.Enum, V(s.Bots.Autonomy), "bots", "autonomy"),
            "Всегда: все боты думают сами. По приказу: сами думают только боты с «Полным контролем ИИ», остальные — по приказам и сообщениям.",
            options: new() { "always", "order" }, labels: new() { "Всегда", "По приказу" });
        Add(F(General, "Язык речи ботов", FieldType.String, V(s.Bots.SpeechLanguage), "bots", "speechLanguage"),
            "Например Russian или English. Пусто = на языке игроков.");
        Add(F(General, "Черты характера из игры", FieldType.Bool, V(s.Bots.UseGamePersonality), "bots", "useGamePersonality"),
            "Если у бота нет характера из вкладки «Экипаж», учитывать его черту из игры (вспыльчивый, ленивый...).");
        Add(F(General, "Плановое решение, с", FieldType.Float, V(g.DecisionIntervalSeconds), "global", "decisionIntervalSeconds"), min: 2, max: 120);
        Add(F(General, "Не чаще раза в, с", FieldType.Float, V(g.MinSecondsBetweenCallsPerBot), "global", "minSecondsBetweenCallsPerBot"),
            "Минимальная пауза между решениями одного бота, даже при тревоге.", 0, 60);
        Add(F(General, "Решение при полном контроле, с", FieldType.Float, V(s.Bots.ControlDecisionIntervalSeconds), "bots", "controlDecisionIntervalSeconds"), min: 2, max: 120);
        Add(F(General, "Действий за решение", FieldType.Int, V(g.MaxActionsPerDecision), "global", "maxActionsPerDecision"), min: 1, max: 6);
        Add(F(General, "Сообщений боту в минуту", FieldType.Int, V(g.MaxMessagesPerMinute), "global", "maxMessagesPerMinute"),
            "Ограничение message_crew/order_crew, чтобы боты не болтали бесконечно. 0 = без ограничения.", 0, 30);
        Add(F(General, "Ходов истории", FieldType.Int, V(g.HistoryTurns), "global", "historyTurns"), min: 0, max: 20);
        Add(F(General, "Заметок на раунд", FieldType.Int, V(g.MaxNotes), "global", "maxNotes"), min: 1, max: 20);
        Add(F(General, "Канал речи", FieldType.Enum, V(g.SpeechChannel), "global", "speechChannel"),
            options: new() { "auto", "radio", "local" }, labels: new() { "Авто (радио при гарнитуре)", "Радио", "Только вслух" });
        Add(F(General, "Фразы ботов друг другу слышны игрокам", FieldType.Bool, V(g.ShowBotMessagesInChat), "global", "showBotMessagesInChat"));
        Add(F(General, "Фраз ботов друг другу в чате в минуту", FieldType.Int, V(g.MaxChatEchoPerMinute), "global", "maxChatEchoPerMinute"),
            "Сколько сообщений между ботами в минуту (на весь экипаж) произносится вслух; остальные только доставляются. 0 = без ограничения.", 0, 60);
        Add(F(General, "Показывать приказы ботов клиентам", FieldType.Bool, V(g.SyncOrdersToClients), "global", "syncOrdersToClients"));
        Add(F(General, "Не снимать костюм при опасности", FieldType.Bool, V(g.KeepDivingGearInDanger), "global", "keepDivingGearInDanger"));
        Add(F(General, "Радиус опасности монстров, м", FieldType.Float, V(g.DangerMonsterRadiusMeters), "global", "dangerMonsterRadiusMeters"), min: 0, max: 500);
        Add(F(General, "Сжатое наблюдение", FieldType.Bool, V(g.CompactObservation), "global", "compactObservation"),
            "Короче запрос к модели: без пустых полей, экипаж одной строкой на человека. Быстрее и понятнее маленьким моделям.");
        Add(F(General, "Писать запросы в лог", FieldType.Bool, V(g.LogPrompts), "global", "logPrompts"));
        Add(F(General, "Писать сырые ответы в лог", FieldType.Bool, V(g.LogRawReplies), "global", "logRawReplies"));

        // ---------------------------------------------------------------- limits
        Add(F(Limits, "Одновременных запросов", FieldType.Int, V(g.MaxConcurrentRequests), "global", "maxConcurrentRequests"), min: 1, max: 32);
        Add(F(Limits, "Запросов в минуту", FieldType.Int, V(g.RequestsPerMinute), "global", "requestsPerMinute"), "0 = без лимита.", 0, 10000);
        Add(F(Limits, "Запросов в сутки", FieldType.Int, V(g.DailyRequestBudget), "global", "dailyRequestBudget"), "0 = без лимита (сутки UTC).", 0, 1000000);
        Add(F(Limits, "Пауза провайдера после 401/403/404, с", FieldType.Float, V(g.ProviderAuthCooldownSeconds), "global", "providerAuthCooldownSeconds"), min: 0, max: 3600);
        Add(F(Limits, "Макс. пауза после 429/5xx, с", FieldType.Float, V(g.ProviderBackoffMaxSeconds), "global", "providerBackoffMaxSeconds"), min: 5, max: 3600);

        // ---------------------------------------------------------------- models
        Add(F(Models, "Модель по умолчанию", FieldType.Enum, V(s.DefaultProvider), "defaultProvider"), options: providerNames);
        Add(F(Models, "Запасная модель", FieldType.Enum, V(s.FallbackProvider), "fallbackProvider"),
            "Куда идти, если основная упала или занята.", options: optionalProviders, labels: new List<string> { "(нет)" }.Concat(providerNames).ToList());
        foreach (var p in s.Providers)
        {
            string grp = $"{p.Name} ({p.Type})";
            Add(F(Models, "Ключ API", FieldType.Info,
                V(string.IsNullOrEmpty(p.ResolvedKey) ? (p.ApiKeyEnv.Length > 0 ? $"не задан (переменная {p.ApiKeyEnv} пуста)" : "не нужен / не задан") : "задан"),
                "providers", p.Name, "keyStatus"), "Ключи в игре не редактируются: задайте переменную окружения на сервере и укажите её имя ниже.", group: grp);
            Add(F(Models, "Модель", FieldType.String, V(p.Model), "providers", p.Name, "model"), group: grp);
            Add(F(Models, "Адрес API", FieldType.String, V(p.BaseUrl), "providers", p.Name, "baseUrl"), group: grp);
            Add(F(Models, "Переменная с ключом", FieldType.String, V(p.ApiKeyEnv), "providers", p.Name, "apiKeyEnv"), group: grp);
            Add(F(Models, "Вызов инструментов", FieldType.Enum, V(p.ToolMode), "providers", p.Name, "toolMode"),
                "native: tool calling самого API. prompt: инструменты описаны в промпте, ответ JSON (для Gemma и т.п.).",
                options: new() { "native", "prompt" }, group: grp);
            Add(F(Models, "Температура", FieldType.Float, V(p.Temperature), "providers", p.Name, "temperature"), min: 0, max: 2, group: grp);
            Add(F(Models, "Токенов в ответе", FieldType.Int, V(p.MaxOutputTokens), "providers", p.Name, "maxOutputTokens"), min: 50, max: 4000, group: grp);
            Add(F(Models, "Таймаут, с", FieldType.Float, V(p.TimeoutSeconds), "providers", p.Name, "timeoutSeconds"), min: 1, max: 300, group: grp);
            Add(F(Models, "Одновременных запросов", FieldType.Int, V(p.MaxConcurrent), "providers", p.Name, "maxConcurrent"), min: 1, max: 32, group: grp);
            Add(F(Models, "Запросов в минуту", FieldType.Int, V(p.RequestsPerMinute), "providers", p.Name, "requestsPerMinute"), "0 = без лимита.", 0, 10000, group: grp);
            Add(F(Models, "Токенов в минуту", FieldType.Int, V(p.TokensPerMinute), "providers", p.Name, "tokensPerMinute"), "0 = без лимита.", 0, 10000000, group: grp);
            Add(F(Models, "Запросов в сутки", FieldType.Int, V(p.DailyRequestBudget), "providers", p.Name, "dailyRequestBudget"), "0 = без лимита.", 0, 1000000, group: grp);
        }

        // ---------------------------------------------------------------- jobs
        var jobs = game.Jobs.Concat(s.Bots.ProviderByJob.Keys).Concat(s.Bots.ExtraPromptByJob.Keys).Concat(s.Bots.DisabledJobs)
            .Where(j => !string.IsNullOrWhiteSpace(j)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(j => j).ToList();
        foreach (var job in jobs)
        {
            Add(F(Jobs, "Модель", FieldType.Enum, V(s.Bots.ProviderByJob.TryGetValue(job, out var pj) ? pj : ""), "bots", "providerByJob", job),
                options: optionalProviders, labels: optionalProviderLabels, group: job);
            Add(F(Jobs, "Ванильный ИИ (без LLM)", FieldType.Bool, V(!s.IsJobEnabled(job)), "bots", "disabledJobs", job), group: job);
            Add(F(Jobs, "Промпт профессии", FieldType.Text, V(s.Bots.ExtraPromptByJob.TryGetValue(job, out var ej) ? ej : ""), "bots", "extraPromptByJob", job),
                "Добавляется всем ботам этой профессии (обязанности, приоритеты).", group: job);
        }

        // ---------------------------------------------------------------- crew personas
        var presets = personas.Presets();
        var presetIds = new List<string> { "" }.Concat(presets.Select(p => p.Id)).ToList();
        var presetLabels = new List<string> { "(нет)" }.Concat(presets.Select(p => p.Preset.Name.Length > 0 ? p.Preset.Name : p.Id)).ToList();
        var stored = personas.Characters().ToDictionary(c => c.Name, c => c.Persona, StringComparer.OrdinalIgnoreCase);
        var names = game.Crew.Where(c => c.IsBot).Select(c => c.Name)
            .Concat(stored.Keys).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var name in names)
        {
            var crew = game.Crew.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            stored.TryGetValue(name, out var persona);
            string grp = crew == null ? $"{name} (нет на борту)"
                : $"{name} — {crew.Job}" + (crew.Trait != null ? $", черта: {crew.Trait}" : "") + (crew.AiControl ? ", ПОЛНЫЙ КОНТРОЛЬ ИИ" : "");
            Add(F(Crew, "Характер", FieldType.Enum, V(persona?.Preset ?? ""), "characters", name, "preset"), options: presetIds, labels: presetLabels, group: grp);
            Add(F(Crew, "Свой промпт", FieldType.Text, V(persona?.Prompt ?? ""), "characters", name, "prompt"),
                "Кто он, как говорит, как себя ведёт. Добавляется после характера.", group: grp);
            Add(F(Crew, "Модель", FieldType.Enum, V(persona?.Provider ?? ""), "characters", name, "provider"),
                options: optionalProviders, labels: optionalProviderLabels, group: grp);
            Add(F(Crew, "Характер включён", FieldType.Bool, V(persona?.Enabled ?? true), "characters", name, "enabled"), group: grp);
        }
        foreach (var f in list.Where(f => f.Category == Crew)) f.Target = "personas";

        // ---------------------------------------------------------------- presets
        foreach (var (id, p) in presets)
        {
            string grp = $"{p.Name} [{id}]";
            Add(F(Presets, "Название", FieldType.String, V(p.Name), "presets", id, "name"), group: grp);
            Add(F(Presets, "Промпт", FieldType.Text, V(p.Prompt), "presets", id, "prompt"), group: grp);
            Add(F(Presets, "Удалить", FieldType.Bool, V(false), "presets", id, "delete"), group: grp);
        }
        Add(F(Presets, "Название нового характера", FieldType.String, V(""), "presets", "+", "name"), group: "Новый характер");
        Add(F(Presets, "Промпт нового характера", FieldType.Text, V(""), "presets", "+", "prompt"), group: "Новый характер");
        foreach (var f in list.Where(f => f.Category == Presets)) f.Target = "personas";

        // ---------------------------------------------------------------- chat & memory
        var c = s.Chat;
        Add(F(ChatAndMemory, "Приказы из чата", FieldType.Bool, V(c.Enabled), "chat", "enabled"), group: "Чат");
        Add(F(ChatAndMemory, "Префикс приказов", FieldType.String, V(c.Prefix), "chat", "prefix"), "Пусто = любое сообщение может быть приказом.", group: "Чат");
        Add(F(ChatAndMemory, "Модель-диспетчер", FieldType.Enum, V(c.Provider), "chat", "provider"), options: optionalProviders, labels: optionalProviderLabels, group: "Чат");
        Add(F(ChatAndMemory, "Зрители командуют ботами", FieldType.Bool, V(c.AllowSpectators), "chat", "allowSpectators"), group: "Чат");
        Add(F(ChatAndMemory, "Услышал один бот → без диспетчера", FieldType.Bool, V(c.DirectIfSingleListener), "chat", "directIfSingleListener"), group: "Чат");
        var m = s.Memory;
        Add(F(ChatAndMemory, "Долговременная память", FieldType.Bool, V(m.Enabled), "memory", "enabled"), group: "Память");
        Add(F(ChatAndMemory, "Итог раунда через LLM", FieldType.Bool, V(m.RoundSummary), "memory", "roundSummary"), group: "Память");
        Add(F(ChatAndMemory, "Модель для итогов", FieldType.Enum, V(m.SummaryProvider), "memory", "summaryProvider"), options: optionalProviders, labels: optionalProviderLabels, group: "Память");
        Add(F(ChatAndMemory, "Фактов на бота", FieldType.Int, V(m.MaxFacts), "memory", "maxFacts"), min: 1, max: 50, group: "Память");
        Add(F(ChatAndMemory, "Записей журнала", FieldType.Int, V(m.MaxJournal), "memory", "maxJournal"), min: 1, max: 50, group: "Память");
        Add(F(ChatAndMemory, "Итогов в промпте", FieldType.Int, V(m.JournalInPrompt), "memory", "journalInPrompt"), min: 0, max: 10, group: "Память");
        Add(F(ChatAndMemory, "Отношений на бота", FieldType.Int, V(m.MaxRelationships), "memory", "maxRelationships"), min: 1, max: 50, group: "Память");
        Add(F(ChatAndMemory, "Предпочтений на игрока", FieldType.Int, V(m.MaxPreferencesPerPlayer), "memory", "maxPreferencesPerPlayer"), min: 1, max: 30, group: "Память");

        return list;
    }

    static void AddStatus(List<SettingField> list, ModSettings s, Agents.ModStatus st, GameInfo game)
    {
        void Info(string group, string label, string value, string? help = null) =>
            list.Add(new SettingField { Category = StatusTab, Group = group, Label = label, Type = FieldType.Info, Value = V(value), Help = help,
                                        Path = new[] { "status", group, label } });

        string mod = $"Мод {st.Version}";
        Info(mod, "Состояние", st.Enabled ? "включён" : "ВЫКЛЮЧЕН");
        foreach (var f in st.Features) Info(mod, f.Name, f.Ok ? "работает" : "НЕ РАБОТАЕТ" + (f.Note != null ? ": " + f.Note : ""));
        Info(mod, "Запросов сегодня", st.RequestsToday + (st.DailyBudget > 0 ? $" из {st.DailyBudget}" : ""));
        if (st.MemoryScope != null) Info(mod, "Память", st.MemoryScope);
        foreach (var p in st.Providers)
            Info("Модели", p.Name, $"{p.Model}: ответов {p.Ok}, ошибок {p.Errors}, в среднем {p.AvgSeconds:0.0} с, сейчас {p.Active}" +
                 (p.PausedForSeconds > 0 ? $", НА ПАУЗЕ ещё {p.PausedForSeconds:0} с" : "") + (p.LastError != null ? $"; последняя ошибка: {p.LastError}" : ""));

        foreach (var b in st.Bots)
        {
            string grp = $"{b.Name} — {b.Job}" + (b.AiControl ? ", ПОЛНЫЙ КОНТРОЛЬ" : "") + (b.HasPersona ? ", с характером" : "");
            Info(grp, "Приказы", b.Orders);
            if (b.Task != null) Info(grp, "Задача", b.Task);
            Info(grp, "Последнее решение", (b.Thinking ? "думает сейчас; " : "") + $"{Agents.ModStatus.Ago(b.SecondsSinceDecision)}: {b.LastDecision ?? "—"}");
            Info(grp, "Модель", $"{b.Provider}, решений: {b.Decisions}");
            if (b.RecentErrors.Count > 0) Info(grp, "Ошибки", string.Join(" | ", b.RecentErrors));
            AddDisableToggle(list, b.Name, grp, false);
        }
        // bots switched to the vanilla AI are no agents any more: still list them so they can be switched back
        foreach (var name in s.Bots.DisabledCharacters)
            AddDisableToggle(list, name, $"{name} — ванильный ИИ", true);
        var others = game.Crew.Where(c => c.IsBot && st.Bots.All(b => b.Name != c.Name)
                                          && !s.Bots.DisabledCharacters.Contains(c.Name, StringComparer.OrdinalIgnoreCase)).ToList();
        foreach (var c in others) Info($"{c.Name} — {c.Job}", "LLM", "не управляется (профессия выключена или бот ещё не создан)");
    }

    static void AddDisableToggle(List<SettingField> list, string name, string group, bool disabled) =>
        list.Add(new SettingField
        {
            Category = StatusTab, Group = group, Label = "Только ванильный ИИ", Type = FieldType.Bool, Value = V(disabled),
            Help = "Выключить LLM для этого персонажа: им управляет обычный ИИ игры (сохраняется в llm_config.json).",
            Path = new[] { "bots", "disabledCharacters", name },
        });

    /// <summary>"global.decisionIntervalSeconds" style key for chat commands.</summary>
    public static string DottedKey(SettingField f) => (f.Target == "personas" ? "personas." : "") + string.Join(".", f.Path);
}
