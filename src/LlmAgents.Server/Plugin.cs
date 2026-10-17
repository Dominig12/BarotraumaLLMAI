using System;
using System.IO;
using System.Linq;
using Barotrauma;
using Barotrauma.LuaCs;
using Barotrauma.LuaCs.Events;
using LlmAgents.Agents;
using LlmAgents.Llm;
using LlmAgents.Memory;
using LlmAgents.Personas;
using LlmAgents.SettingsUi;

namespace LlmAgents;

/// <summary>
/// Server-side LuaCs plugin. In multiplayer the server decides bot AI, so this mod runs on the host /
/// dedicated server only. Services are property-injected by LuaCs after construction.
/// </summary>
public sealed class Plugin : IAssemblyPlugin, IEventUpdate, IEventRoundStarted, IEventRoundEnded
{
    public const string ConfigFileName = "llm_config.json";
    public const string PersonasFileName = "personas.json";

    public ILoggerService LoggerService { get; set; } = null!;
    public IEventService EventService { get; set; } = null!;
    public IPluginManagementService PluginService { get; set; } = null!;

    AgentManager? manager;
    GameAdapter? adapter;
    MemoryStore? memory;
    PersonaStore? personas;
    SettingsService? settingsService;
    SettingsBridge? settingsBridge;
    object? chatListener; // ChatListener; typed as object so Plugin itself never depends on the internal interface
    IEventService? chatService;
    string? modDir;
    string? memoryDir;

    public void PreInitPatching() { }

    public void Initialize()
    {
        Log.Sink = (level, msg) =>
        {
            switch (level)
            {
                case LogLevel.Error: LoggerService?.LogError(msg); break;
                case LogLevel.Warn: LoggerService?.LogWarning(msg); break;
                default: LoggerService?.LogMessage(msg); break;
            }
        };

        try
        {
            modDir = FindModDir();
            Start();
        }
        catch (Exception e)
        {
            Log.Error("failed to start: " + e);
        }

        EventService?.Subscribe<IEventUpdate>(this);
        EventService?.Subscribe<IEventRoundStarted>(this);
        EventService?.Subscribe<IEventRoundEnded>(this);

        // Kept in its own class: the chat event interface is internal to LuaCs, so if it ever fails to bind,
        // only chat orders are lost, not the whole mod.
        try
        {
            GearLock.Patch();
        }
        catch (Exception e)
        {
            Log.Error("diving gear lock disabled, could not patch the game: " + e.Message);
        }

        try
        {
            ControlOrder.Patch();
        }
        catch (Exception e)
        {
            Log.Error("'Full AI control' order disabled, could not patch the game: " + e.Message);
        }

        try
        {
            ContextMarks.Patch();
        }
        catch (Exception e)
        {
            Log.Error("'mark for the AI' disabled, could not patch the game: " + e.Message);
        }

        settingsBridge = new SettingsBridge(() => settingsService, () => adapter, r => manager?.Monitor(r.Bot, Features(), r.Activity));
        TryRegisterSettingsWindow();

        try
        {
            SubscribeChat();
        }
        catch (Exception e)
        {
            chatListener = null;
            Log.Error("chat orders disabled, could not subscribe to chat: " + e.Message);
        }
    }

    // Separate, non-inlined methods: a type-load failure surfaces at the call site, where it is caught.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    void SubscribeChat()
    {
        // Subscribe on the *main* LuaCs event service: it only forwards an event to plugin event services when it
        // has a subscriber of its own for that event type, which is not the case for chat messages.
        var listener = new ChatListener(this);
        var main = LuaCsSetup.Instance?.EventService ?? EventService;
        main?.Subscribe<IEventChatMessage>(listener);
        chatListener = listener;
        chatService = main;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    void UnsubscribeChat()
    {
        if (chatListener is ChatListener l) chatService?.Unsubscribe<IEventChatMessage>(l);
        chatListener = null;
    }

    /// <summary>"!llm ..." settings commands. True = handled (hide the message).</summary>
    internal bool OnChatCommand(Barotrauma.Networking.Client sender, string text)
    {
        if (sender == null || !SettingsService.IsCommand(text)) return false;
        settingsBridge ??= new SettingsBridge(() => settingsService, () => adapter, r => manager?.Monitor(r.Bot, Features(), r.Activity));
        return settingsBridge.TryHandleChat(sender, text);
    }

    internal void OnPlayerChat(string text, Barotrauma.Networking.Client sender, Barotrauma.Networking.ChatMessageType type)
    {
        if (manager == null || adapter == null || sender == null) return;
        if (GameMain.GameSession?.IsRunning != true) return;
        var cmd = adapter.BuildChatCommand(sender, text, type);
        if (cmd != null) manager.SubmitChat(cmd);
    }

    public void OnLoadCompleted() { }

    void Start()
    {
        string cfgPath = Path.Combine(modDir!, ConfigFileName);
        if (!File.Exists(cfgPath)) throw new FileNotFoundException("LLM agents config not found", cfgPath);
        var editor = ConfigEditor.Load(cfgPath);
        // an old config from a previous version: upgrade untouched old defaults, add the new settings
        var migrated = editor.MigrateDefaults();
        var added = editor.AddMissingDefaults();
        if (migrated.Count + added.Count > 0)
        {
            try
            {
                File.Copy(cfgPath, cfgPath + ".bak", overwrite: true);
                editor.Save();
                if (migrated.Count > 0)
                    Log.Warn($"llm_config.json upgraded (backup: llm_config.json.bak): {string.Join(", ", migrated)}");
                if (added.Count > 0)
                    Log.Warn($"llm_config.json: added {added.Count} new setting(s) with default values: {string.Join(", ", added.Take(20))}{(added.Count > 20 ? ", ..." : "")}");
            }
            catch (Exception e) { Log.Warn("could not update llm_config.json with the new settings: " + e.Message); }
        }
        var settings = editor.Settings;
        personas = new PersonaStore(Path.Combine(modDir!, PersonasFileName));
        Log.FilePath = Path.IsPathRooted(settings.Global.LogFile)
            ? settings.Global.LogFile
            : Path.Combine(modDir!, settings.Global.LogFile);

        manager?.Dispose();
        adapter?.Reset();
        adapter = new GameAdapter(settings);
        ControlOrder.Handler = adapter.OnControlOrder;
        ControlOrder.MenuHandler = adapter.OnMenuOrder;
        ContextMarks.Changed = (item, by, on) =>
            Log.Info($"{by} {(on ? "marked" : "unmarked")} {item.Name}{(item.CurrentHull != null ? " in " + GameAdapter.RoomId(item.CurrentHull) : "")} for the AI");

        // Long-term memory: one JSON file per campaign save in <mod>/memory (or memory.folder).
        string memDir = Path.IsPathRooted(settings.Memory.Folder) ? settings.Memory.Folder : Path.Combine(modDir!, settings.Memory.Folder);
        if (memory == null || memoryDir != memDir)
        {
            memory?.Flush();
            memory = new MemoryStore(memDir);
            memoryDir = memDir;
        }
        manager = new AgentManager(settings, adapter, new LlmRouter(settings), memory: memory, personas: personas);
        manager.BeginRound(MemoryScope());
        settingsService = new SettingsService(editor, personas, ApplySettings, () => SettingsBridge.GameInfo(adapter),
            () => manager?.Status(Features()));
        Log.Warn($"LlmAgents {ModInfo.Version} loaded from {cfgPath}. Providers: {string.Join(", ", settings.Providers.Select(p => $"{p.Name} ({p.Type}, {p.Model})"))}; " +
                 $"default '{settings.DefaultProvider}', fallback '{settings.FallbackProvider}'. Enabled: {settings.Enabled}");
    }

    /// <summary>Settings changed in game: apply them right away; agents keep their state.</summary>
    void ApplySettings(ModSettings s, bool providersChanged)
    {
        if (modDir != null)
            Log.FilePath = string.IsNullOrEmpty(s.Global.LogFile) ? null
                : Path.IsPathRooted(s.Global.LogFile) ? s.Global.LogFile : Path.Combine(modDir, s.Global.LogFile);
        if (adapter != null) adapter.Settings = s;
        manager?.ApplySettings(s, providersChanged ? new LlmRouter(s) : null);
        Log.Info($"settings applied{(providersChanged ? " (providers and limits reloaded)" : "")}");
    }

    /// <summary>Re-reads llm_config.json at the start of every round, so settings can be tweaked without restarting.</summary>
    public void OnRoundStart()
    {
        roundStarted = true;
        try
        {
            adapter?.Reset();
            if (modDir != null) Start();
            manager?.Reset();
        }
        catch (Exception e)
        {
            Log.Error("reload failed, keeping previous settings: " + e.Message);
            manager?.Reset();
            manager?.BeginRound(MemoryScope());
        }
    }

    public void OnRoundEnd()
    {
        roundStarted = false;
        try { manager?.EndRound(); } catch (Exception e) { Log.Error("round summary failed: " + e.Message); }
        adapter?.Reset();
        manager?.Reset();
    }

    bool settingsWindowRegistered, settingsWindowFailed;

    /// <summary>What actually works in this game, for !llm status.</summary>
    System.Collections.Generic.List<FeatureStatus> Features() => new()
    {
        new("Не снимать костюмы при опасности (патч)", GearLock.Patched, GearLock.Patched ? null : "патч не встал, см. консоль сервера"),
        new("Приказ «Полный контроль ИИ» и приказы из меню (патч)", ControlOrder.Patched, ControlOrder.Patched ? null : "патч не встал, см. консоль сервера"),
        new("Приказы из чата", chatListener != null, chatListener != null ? null : "не удалось подписаться на чат"),
        new("Окно настроек (сеть LuaCs)", settingsWindowRegistered,
            settingsWindowRegistered ? null : settingsWindowFailed ? "не удалось зарегистрировать; работают команды !llm" : "ещё не зарегистрировано"),
    };

    /// <summary>The network message can only be registered once the server is up; retried from OnUpdate until then.</summary>
    void TryRegisterSettingsWindow()
    {
        if (settingsWindowRegistered || settingsWindowFailed || settingsBridge == null || GameMain.Server == null) return;
        try
        {
            settingsBridge.Register();
            settingsWindowRegistered = true;
        }
        catch (Exception e)
        {
            settingsWindowFailed = true;
            Log.Error("in-game settings window disabled (the !llm chat commands still work): " + e.Message);
        }
    }

    bool roundStarted;
    double sessionSeenAt = -1;

    public void OnUpdate(double fixedDeltaTime)
    {
        TryRegisterSettingsWindow();
        if (manager == null) return;
        if (GameMain.GameSession?.IsRunning != true) { sessionSeenAt = -1; return; }
        // The round-start hook reloads the config and recreates everything a moment after the session starts; bots
        // created before it would send requests that are thrown away. Wait for it (or a few seconds, when the mod
        // was loaded in the middle of a round and the hook will not come).
        if (sessionSeenAt < 0) sessionSeenAt = Timing.TotalTime;
        if (!roundStarted && Timing.TotalTime - sessionSeenAt < 5) return;
        try
        {
            adapter?.Tick();
            manager.Update();
        }
        catch (Exception e)
        {
            Log.Error("update failed: " + e);
        }
    }

    public void Dispose()
    {
        try
        {
            EventService?.Unsubscribe<IEventUpdate>(this);
            EventService?.Unsubscribe<IEventRoundStarted>(this);
            EventService?.Unsubscribe<IEventRoundEnded>(this);
            if (chatListener != null) UnsubscribeChat();
        }
        catch { /* shutting down */ }
        try { GearLock.Unpatch(); } catch { /* shutting down */ }
        try { ControlOrder.Unpatch(); ControlOrder.Handler = null; ControlOrder.MenuHandler = null; } catch { /* shutting down */ }
        try { ContextMarks.Unpatch(); ContextMarks.Changed = null; } catch { /* shutting down */ }
        try { memory?.Flush(); } catch { /* shutting down */ }
        manager?.Dispose();
        manager = null;
        adapter = null;
        Log.Sink = null;
        Log.Shutdown();
    }

    /// <summary>One memory per campaign save; everything outside campaigns shares "default".</summary>
    static string MemoryScope()
    {
        try
        {
            var session = GameMain.GameSession;
            if (session?.Campaign != null)
            {
                var save = session.DataPath.SavePath;
                if (!string.IsNullOrEmpty(save)) return "campaign_" + Path.GetFileNameWithoutExtension(save);
            }
        }
        catch (Exception e) { Log.Warn("could not determine the campaign for memory: " + e.Message); }
        return "default";
    }

    string FindModDir()
    {
        var env = Environment.GetEnvironmentVariable("LLM_AGENTS_DIR");
        if (!string.IsNullOrEmpty(env) && Directory.Exists(env)) return env;

        if (PluginService != null && PluginService.TryGetPackageForPlugin<Plugin>(out var pkg) && pkg != null)
            return Path.GetFullPath(pkg.Dir);

        throw new DirectoryNotFoundException("Cannot locate the mod folder. Set LLM_AGENTS_DIR to the folder that contains " + ConfigFileName);
    }
}
