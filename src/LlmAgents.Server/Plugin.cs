using System;
using System.IO;
using System.Linq;
using Barotrauma;
using Barotrauma.LuaCs;
using Barotrauma.LuaCs.Events;
using LlmAgents.Agents;
using LlmAgents.Llm;

namespace LlmAgents;

/// <summary>
/// Server-side LuaCs plugin. In multiplayer the server decides bot AI, so this mod runs on the host /
/// dedicated server only. Services are property-injected by LuaCs after construction.
/// </summary>
public sealed class Plugin : IAssemblyPlugin, IEventUpdate, IEventRoundStarted, IEventRoundEnded
{
    public const string ConfigFileName = "llm_config.json";

    public ILoggerService LoggerService { get; set; } = null!;
    public IEventService EventService { get; set; } = null!;
    public IPluginManagementService PluginService { get; set; } = null!;

    AgentManager? manager;
    string? modDir;

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
    }

    public void OnLoadCompleted() { }

    void Start()
    {
        string cfgPath = Path.Combine(modDir!, ConfigFileName);
        var settings = ModSettings.LoadFile(cfgPath);
        Log.FilePath = Path.IsPathRooted(settings.Global.LogFile)
            ? settings.Global.LogFile
            : Path.Combine(modDir!, settings.Global.LogFile);

        manager?.Dispose();
        manager = new AgentManager(settings, new GameAdapter(settings), new LlmRouter(settings));
        Log.Warn($"loaded from {cfgPath}. Providers: {string.Join(", ", settings.Providers.Select(p => $"{p.Name} ({p.Type}, {p.Model})"))}; " +
                 $"default '{settings.DefaultProvider}', fallback '{settings.FallbackProvider}'. Enabled: {settings.Enabled}");
    }

    /// <summary>Re-reads llm_config.json at the start of every round, so settings can be tweaked without restarting.</summary>
    public void OnRoundStart()
    {
        try
        {
            if (modDir != null) Start();
            manager?.Reset();
        }
        catch (Exception e)
        {
            Log.Error("reload failed, keeping previous settings: " + e.Message);
            manager?.Reset();
        }
    }

    public void OnRoundEnd() => manager?.Reset();

    public void OnUpdate(double fixedDeltaTime)
    {
        if (manager == null) return;
        if (GameMain.GameSession?.IsRunning != true) return;
        try
        {
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
        }
        catch { /* shutting down */ }
        manager?.Dispose();
        manager = null;
        Log.Sink = null;
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
