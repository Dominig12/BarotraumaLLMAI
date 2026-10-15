using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LlmAgents;

public sealed class ModSettings
{
    public bool Enabled { get; set; } = true;
    public string DefaultProvider { get; set; } = "local";
    public string FallbackProvider { get; set; } = "";
    public GlobalSettings Global { get; set; } = new();
    public List<ProviderConfig> Providers { get; set; } = new();
    public BotSettings Bots { get; set; } = new();
    public ChatSettings Chat { get; set; } = new();
    public MemorySettings Memory { get; set; } = new();

    static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static ModSettings LoadFile(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("LLM agents config not found", path);
        return Parse(File.ReadAllText(path));
    }

    public static ModSettings Parse(string json)
    {
        ModSettings s;
        try
        {
            s = JsonSerializer.Deserialize<ModSettings>(json, Json) ?? throw new InvalidDataException("Empty config");
        }
        catch (JsonException e)
        {
            throw new InvalidDataException("Invalid llm_config.json: " + e.Message, e);
        }
        s.Normalize();
        s.Validate();
        return s;
    }

    void Normalize()
    {
        Global ??= new GlobalSettings();
        Bots ??= new BotSettings();
        Chat ??= new ChatSettings();
        Memory ??= new MemorySettings();
        Memory.Folder ??= "memory";
        Memory.SummaryProvider ??= "";
        Bots.Autonomy = (Bots.Autonomy ?? "always").Trim().ToLowerInvariant();
        Bots.SpeechLanguage = (Bots.SpeechLanguage ?? "").Trim();
        Chat.Prefix ??= "";
        Chat.Provider ??= "";
        Global.SpeechChannel = (Global.SpeechChannel ?? "auto").Trim().ToLowerInvariant();
        Providers ??= new List<ProviderConfig>();
        foreach (var p in Providers)
        {
            p.Type = (p.Type ?? "").Trim().ToLowerInvariant();
            p.ExtraHeaders ??= new Dictionary<string, string>();
            p.ToolMode = (p.ToolMode ?? "native").Trim().ToLowerInvariant();
            p.Thinking = (p.Thinking ?? "auto").Trim().ToLowerInvariant();
        }
        Bots.ProviderByJob = new Dictionary<string, string>(Bots.ProviderByJob ?? new(), StringComparer.OrdinalIgnoreCase);
        Bots.ExtraPromptByJob = new Dictionary<string, string>(Bots.ExtraPromptByJob ?? new(), StringComparer.OrdinalIgnoreCase);
        Bots.DisabledJobs ??= new List<string>();
        Bots.DisabledCharacters ??= new List<string>();
        Bots.AllowedTools ??= new List<string>();
    }

    void Validate()
    {
        if (Providers.Count == 0) throw new InvalidDataException("No providers configured");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Providers)
        {
            if (string.IsNullOrWhiteSpace(p.Name)) throw new InvalidDataException("Provider without a name");
            if (!names.Add(p.Name)) throw new InvalidDataException($"Duplicate provider '{p.Name}'");
            if (p.Type is not ("openai" or "anthropic" or "ollama"))
                throw new InvalidDataException($"Provider '{p.Name}': type must be 'openai', 'ollama' or 'anthropic'");
            if (p.Thinking is not ("auto" or "off" or "on"))
                throw new InvalidDataException($"Provider '{p.Name}': thinking must be 'auto', 'off' or 'on'");
            if (p.ContextLength < 0) throw new InvalidDataException($"Provider '{p.Name}': contextLength must be >= 0");
            if (!Uri.TryCreate(p.BaseUrl, UriKind.Absolute, out _))
                throw new InvalidDataException($"Provider '{p.Name}': baseUrl is not a valid absolute URL");
            if (string.IsNullOrWhiteSpace(p.Model)) throw new InvalidDataException($"Provider '{p.Name}': model is required");
            if (p.ToolMode is not ("native" or "prompt"))
                throw new InvalidDataException($"Provider '{p.Name}': toolMode must be 'native' or 'prompt'");
            if (p.TimeoutSeconds <= 0) throw new InvalidDataException($"Provider '{p.Name}': timeoutSeconds must be > 0");
        }
        if (!names.Contains(DefaultProvider)) throw new InvalidDataException($"defaultProvider '{DefaultProvider}' not found in providers");
        if (!string.IsNullOrEmpty(FallbackProvider) && !names.Contains(FallbackProvider))
            throw new InvalidDataException($"fallbackProvider '{FallbackProvider}' not found in providers");
        foreach (var kv in Bots.ProviderByJob)
            if (!names.Contains(kv.Value))
                throw new InvalidDataException($"bots.providerByJob['{kv.Key}'] = '{kv.Value}' not found in providers");
        if (!string.IsNullOrEmpty(Chat.Provider) && !names.Contains(Chat.Provider))
            throw new InvalidDataException($"chat.provider '{Chat.Provider}' not found in providers");
        if (!string.IsNullOrEmpty(Memory.SummaryProvider) && !names.Contains(Memory.SummaryProvider))
            throw new InvalidDataException($"memory.summaryProvider '{Memory.SummaryProvider}' not found in providers");
        if (Bots.Autonomy is not ("always" or "order"))
            throw new InvalidDataException("bots.autonomy must be 'always' or 'order'");
        if (Global.SpeechChannel is not ("auto" or "radio" or "local"))
            throw new InvalidDataException("global.speechChannel must be 'auto', 'radio' or 'local'");
        if (Global.TickSeconds <= 0) throw new InvalidDataException("global.tickSeconds must be > 0");
        if (Global.MaxActionsPerDecision < 1) throw new InvalidDataException("global.maxActionsPerDecision must be >= 1");
        if (Global.MaxPlanSeconds <= 0) throw new InvalidDataException("global.maxPlanSeconds must be > 0");
        if (Global.PlanStepTimeoutSeconds <= 0) throw new InvalidDataException("global.planStepTimeoutSeconds must be > 0");
    }

    public string ProviderFor(string job) =>
        Bots.ProviderByJob.TryGetValue(job, out var p) ? p : DefaultProvider;

    public bool IsJobEnabled(string job) =>
        !Bots.DisabledJobs.Any(j => string.Equals(j, job, StringComparison.OrdinalIgnoreCase));

    public bool IsCharacterEnabled(string name) =>
        !Bots.DisabledCharacters.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
}

public sealed class GlobalSettings
{
    public int MaxConcurrentRequests { get; set; } = 3;
    public int RequestsPerMinute { get; set; } = 60;
    /// <summary>0 = unlimited. Counted per UTC day.</summary>
    public int DailyRequestBudget { get; set; } = 0;
    public double TickSeconds { get; set; } = 0.5;
    public double DecisionIntervalSeconds { get; set; } = 10;
    public double MinSecondsBetweenCallsPerBot { get; set; } = 4;
    public double UrgencyCheckSeconds { get; set; } = 1.5;
    /// <summary>How soon a bot tries again when its request was skipped because of limits.</summary>
    public double RetrySeconds { get; set; } = 1.0;
    /// <summary>Per bot, private messages (message_crew) per minute. Prevents "Roger!" ping-pong between agents. 0 = unlimited.</summary>
    public int MaxMessagesPerMinute { get; set; } = 3;
    /// <summary>Most tool calls one reply may contain (with sequentialActions: the length of a plan).</summary>
    public int MaxActionsPerDecision { get; set; } = 6;
    /// <summary>Earlier turns the model sees (compact: one short line each, plus what it did then).</summary>
    public int HistoryTurns { get; set; } = 10;
    /// <summary>
    /// The calls of one reply are a plan carried out step by step: a multi-step task (equip, craft...) or go_to_room
    /// finishes before the next step starts, and the bot is not asked again until the plan is done, a step fails,
    /// a player gives an order or something new and urgent happens. false = all calls run at once (old behaviour).
    /// </summary>
    public bool SequentialActions { get; set; } = true;
    /// <summary>A plan running longer than this lets the bot think again (it may keep the plan with keep_plan).</summary>
    public double MaxPlanSeconds { get; set; } = 180;
    /// <summary>A go_to_room step that has not arrived after this long stops the plan.</summary>
    public double PlanStepTimeoutSeconds { get; set; } = 90;
    public int MaxNotes { get; set; } = 5;
    public string LogFile { get; set; } = "llm_agents.log";
    public bool LogPrompts { get; set; } = true;
    /// <summary>Also log the model's raw text next to the parsed calls (useful when tuning a local model).</summary>
    public bool LogRawReplies { get; set; } = true;
    /// <summary>Broadcast LLM-issued orders to clients as order chat messages, so the crew UI shows them.</summary>
    public bool SyncOrdersToClients { get; set; } = true;
    /// <summary>Private bot-to-bot messages (message_crew, order_crew) are also spoken in the game chat, so players hear them.</summary>
    public bool ShowBotMessagesInChat { get; set; } = true;
    /// <summary>At most this many bot-to-bot messages per minute are spoken in the chat (crew-wide); the rest are only delivered. 0 = no limit.</summary>
    public int MaxChatEchoPerMinute { get; set; } = 6;
    /// <summary>Shorter observation for the LLM (no empty fields, one line per crew member). Faster and clearer for small models.</summary>
    public bool CompactObservation { get; set; } = true;
    /// <summary>
    /// Where bots speak: "auto" = radio if the bot wears a working headset, otherwise out loud (like vanilla bots);
    /// "radio" = same as auto (radio is always preferred); "local" = always out loud, nearby only.
    /// </summary>
    public string SpeechChannel { get; set; } = "auto";
    /// <summary>While the sub is in danger (monsters nearby, breaches, water aboard, intruders) bots never take off their diving gear.</summary>
    public bool KeepDivingGearInDanger { get; set; } = true;
    /// <summary>Monsters closer than this to the sub count as danger.</summary>
    public double DangerMonsterRadiusMeters { get; set; } = 80;
    /// <summary>A provider that answers 401/403/404 (bad key, region block, unknown model) is skipped for this long.</summary>
    public double ProviderAuthCooldownSeconds { get; set; } = 300;
    /// <summary>After 429/5xx/timeouts a provider is paused 5 s, 10 s, 20 s... up to this many seconds.</summary>
    public double ProviderBackoffMaxSeconds { get; set; } = 120;
}

/// <summary>Orders typed by human players in the game chat.</summary>
public sealed class ChatSettings
{
    public bool Enabled { get; set; } = true;
    /// <summary>Provider for the dispatcher that decides who an order is for. Empty = defaultProvider.</summary>
    public string Provider { get; set; } = "";
    /// <summary>If set, only messages starting with this prefix are treated as orders (prefix is removed). Empty = every message.</summary>
    public string Prefix { get; set; } = "";
    /// <summary>Players without a character (spectators, a host watching) can command every bot.</summary>
    public bool AllowSpectators { get; set; } = true;
    /// <summary>If only one bot could hear the message, skip the dispatcher and give it to that bot.</summary>
    public bool DirectIfSingleListener { get; set; } = true;
    public int MaxQueue { get; set; } = 10;
    /// <summary>Messages that could not be dispatched within this time (limits, outage) are dropped.</summary>
    public double MaxAgeSeconds { get; set; } = 30;
}

public sealed class ProviderConfig
{
    public string Name { get; set; } = "";
    /// <summary>
    /// "openai" (OpenAI-compatible: OpenAI, LM Studio, vLLM, OpenRouter, Ollama's /v1...), "ollama" (Ollama's native
    /// API: thinking switch and context length work) or "anthropic".
    /// </summary>
    public string Type { get; set; } = "openai";
    public string BaseUrl { get; set; } = "";
    public string ApiKey { get; set; } = "";
    /// <summary>Preferred over ApiKey: read the key from this environment variable.</summary>
    public string ApiKeyEnv { get; set; } = "";
    public string Model { get; set; } = "";
    public double TimeoutSeconds { get; set; } = 30;
    public int MaxConcurrent { get; set; } = 1;
    /// <summary>0 = unlimited.</summary>
    public int RequestsPerMinute { get; set; } = 0;
    /// <summary>0 = unlimited. Uses token counts reported by the provider.</summary>
    public int TokensPerMinute { get; set; } = 0;
    /// <summary>0 = unlimited. Counted per UTC day.</summary>
    public int DailyRequestBudget { get; set; } = 0;
    public int MaxOutputTokens { get; set; } = 300;
    public double Temperature { get; set; } = 0.6;
    public Dictionary<string, string> ExtraHeaders { get; set; } = new();
    /// <summary>
    /// "native": the API's tool calling (best when the server supports it for this model).
    /// "prompt": tools are described in the prompt and the model replies with JSON. Use for local models whose
    /// tool calls come back as text (e.g. Gemma on some servers).
    /// </summary>
    public string ToolMode { get; set; } = "native";
    /// <summary>prompt mode, OpenAI-compatible servers: ask for response_format json_object.</summary>
    public bool JsonResponseFormat { get; set; } = true;
    /// <summary>
    /// Reasoning ("thinking") of models like Qwen3: "off" makes answers several times faster and is right for bots.
    /// "auto" = the model's default. type "ollama": sent as think=false; type "openai": the /no_think switch
    /// (Qwen3) is added to the prompt and any &lt;think&gt; text is ignored.
    /// </summary>
    public string Thinking { get; set; } = "auto";
    /// <summary>type "ollama": context window in tokens (num_ctx). 0 = the server's default, which is often too small.</summary>
    public int ContextLength { get; set; } = 0;
    /// <summary>type "ollama": keep the model loaded this many minutes between requests (0 = server default, 5 min).</summary>
    public int KeepAliveMinutes { get; set; } = 0;

    [JsonIgnore]
    public string ResolvedKey =>
        !string.IsNullOrEmpty(ApiKey) ? ApiKey :
        !string.IsNullOrEmpty(ApiKeyEnv) ? (Environment.GetEnvironmentVariable(ApiKeyEnv) ?? "") : "";
}

public sealed class BotSettings
{
    public Dictionary<string, string> ProviderByJob { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Extra system-prompt text per job identifier (e.g. personality, priorities).</summary>
    public Dictionary<string, string> ExtraPromptByJob { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> DisabledJobs { get; set; } = new();
    /// <summary>Characters (by name) left to the vanilla AI.</summary>
    public List<string> DisabledCharacters { get; set; } = new();
    /// <summary>Empty = all tools.</summary>
    public List<string> AllowedTools { get; set; } = new();
    /// <summary>
    /// "always": every AI bot thinks on its own all the time (orders from players come first).
    /// "order": bots only react to chat orders and messages; full autonomy only for bots given the
    /// "Full AI control" order from the command menu. Everything else is the vanilla AI.
    /// </summary>
    public string Autonomy { get; set; } = "always";
    /// <summary>Language bots speak in (say, message_crew), e.g. "Russian". Empty = the language the players use.</summary>
    public string SpeechLanguage { get; set; } = "";
    /// <summary>Without a persona, the game's own personality trait of the character (Hothead, Lazy...) shapes the bot.</summary>
    public bool UseGamePersonality { get; set; } = true;
    /// <summary>Decision interval for bots under full AI control (they plan more often).</summary>
    public double ControlDecisionIntervalSeconds { get; set; } = 6;
}

/// <summary>Long-term memory kept on disk between rounds (per campaign save).</summary>
public sealed class MemorySettings
{
    public bool Enabled { get; set; } = true;
    /// <summary>Folder for the memory files, relative to the mod folder or absolute.</summary>
    public string Folder { get; set; } = "memory";
    public int MaxFacts { get; set; } = 12;
    public int MaxJournal { get; set; } = 10;
    /// <summary>How many of the latest round summaries are shown to the bot.</summary>
    public int JournalInPrompt { get; set; } = 3;
    public int MaxRelationships { get; set; } = 15;
    public int MaxPreferencesPerPlayer { get; set; } = 6;
    /// <summary>At the end of a round every bot writes a short summary of it into its journal (one LLM call per bot).</summary>
    public bool RoundSummary { get; set; } = true;
    /// <summary>Provider for round summaries. Empty = the bot's own provider.</summary>
    public string SummaryProvider { get; set; } = "";
}
