using System;
using Barotrauma;
using Barotrauma.LuaCs;
using Barotrauma.LuaCs.Events;
using Barotrauma.Networking;
using LlmAgents.SettingsUi;
using Microsoft.Xna.Framework.Input;

namespace LlmAgents.Client;

/// <summary>
/// Client half of the mod: the in-game settings window, the "mark for the AI" context menu entries and marks, and the AI control icon in the crew list. Opens with "!llm" in the chat (the server asks for it)
/// or Ctrl+Shift+L. The bot monitor: "!llm monitor" or Ctrl+Shift+M. All settings live on the server; this window only shows and edits them.
/// </summary>
public sealed class ClientPlugin : IAssemblyPlugin, IEventUpdate
{
    public ILoggerService LoggerService { get; set; } = null!;
    public IEventService EventService { get; set; } = null!;

    SettingsWindow? window;
    MonitorWindow? monitor;
    MonitorButton? monitorButton;
    INetworkingService? net;
    ControlMarks? controlMarks;

    public void PreInitPatching() { }

    public void Initialize()
    {
        monitor = new MonitorWindow(SendMonitor);
        window = new SettingsWindow(Send, () => monitor?.Open());
        monitorButton = new MonitorButton(() => monitor?.Toggle());
        try
        {
            RegisterNet();
        }
        catch (Exception e)
        {
            LoggerService?.LogError("[LlmAgents] settings window unavailable: " + e.Message);
        }
        try
        {
            if (net != null) controlMarks = new ControlMarks(net);
        }
        catch (Exception e)
        {
            LoggerService?.LogError("[LlmAgents] crew list marks for AI control unavailable: " + e.Message);
        }
        try
        {
            ContextMarkMenu.Patch();
        }
        catch (Exception e)
        {
            LoggerService?.LogError("[LlmAgents] 'mark for the AI' menu entries unavailable: " + e.Message);
        }
        EventService?.Subscribe<IEventUpdate>(this);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    void RegisterNet()
    {
        net = LuaCsSetup.Instance?.NetworkingService ?? throw new InvalidOperationException("LuaCs networking service not available");
        net.Receive(SettingsNet.MessageId, (NetMessageReceived)OnMessage);
        net.Receive(MonitorNet.MessageId, (NetMessageReceived)OnMonitorMessage);
    }

    void OnMonitorMessage(IReadMessage msg)
    {
        try
        {
            var snap = SettingsNet.Deserialize<MonitorSnapshot>(msg.ReadString());
            if (snap != null) monitor?.OnSnapshot(snap);
        }
        catch (Exception e)
        {
            LoggerService?.LogError("[LlmAgents] bad monitor message: " + e.Message);
        }
    }

    void SendMonitor(MonitorRequest request)
    {
        if (net == null || GameMain.Client == null) return;
        var m = net.Start(MonitorNet.MessageId);
        m.WriteString(SettingsNet.Serialize(request));
        net.SendToServer(m, DeliveryMethod.Reliable);
    }

    public void OnLoadCompleted() { }

    void OnMessage(IReadMessage msg)
    {
        try
        {
            var response = SettingsNet.Deserialize<SettingsResponse>(msg.ReadString());
            if (response?.Type == "monitor") monitor?.Open();
            else if (response != null) window?.OnResponse(response);
        }
        catch (Exception e)
        {
            LoggerService?.LogError("[LlmAgents] bad settings message: " + e.Message);
        }
    }

    void Send(SettingsRequest request)
    {
        if (net == null || GameMain.Client == null)
        {
            window?.SetStatus("Настройки доступны только в мультиплеере, на сервере с модом LlmAgents.", error: true);
            return;
        }
        var m = net.Start(SettingsNet.MessageId);
        m.WriteString(SettingsNet.Serialize(request));
        net.SendToServer(m, DeliveryMethod.Reliable);
    }

    public void OnUpdate(double deltaTime)
    {
        try
        {
            if (PlayerInput.IsCtrlDown() && PlayerInput.IsShiftDown() && PlayerInput.KeyHit(Keys.L)) window?.Toggle();
            if (PlayerInput.IsCtrlDown() && PlayerInput.IsShiftDown() && PlayerInput.KeyHit(Keys.M)) monitor?.Toggle();
        }
        catch { /* input not ready (loading screens) */ }
        try { monitor?.Update(); }
        catch { /* GUI not ready */ }
        try { monitorButton?.Update(); }
        catch { /* the crew list is not there */ }
        try { controlMarks?.Update(); }
        catch { /* the crew list is not there (loading, menus) */ }
    }

    public void Dispose()
    {
        try { EventService?.Unsubscribe<IEventUpdate>(this); } catch { }
        try { ContextMarkMenu.Unpatch(); } catch { }
        try { controlMarks?.Clear(); } catch { }
        controlMarks = null;
        window?.Close();
        window = null;
        monitor?.Close();
        monitor = null;
        try { monitorButton?.Remove(); } catch { }
        monitorButton = null;
    }
}
