using System;
using Barotrauma;
using Barotrauma.LuaCs;
using Barotrauma.LuaCs.Events;
using Barotrauma.Networking;
using LlmAgents.SettingsUi;
using Microsoft.Xna.Framework.Input;

namespace LlmAgents.Client;

/// <summary>
/// Client half of the mod: only the in-game settings window. Opens with "!llm" in the chat (the server asks for it)
/// or Ctrl+Shift+L. All settings live on the server; this window only shows and edits them.
/// </summary>
public sealed class ClientPlugin : IAssemblyPlugin, IEventUpdate
{
    public ILoggerService LoggerService { get; set; } = null!;
    public IEventService EventService { get; set; } = null!;

    SettingsWindow? window;
    INetworkingService? net;

    public void PreInitPatching() { }

    public void Initialize()
    {
        window = new SettingsWindow(Send);
        try
        {
            RegisterNet();
        }
        catch (Exception e)
        {
            LoggerService?.LogError("[LlmAgents] settings window unavailable: " + e.Message);
        }
        EventService?.Subscribe<IEventUpdate>(this);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    void RegisterNet()
    {
        net = LuaCsSetup.Instance?.NetworkingService ?? throw new InvalidOperationException("LuaCs networking service not available");
        net.Receive(SettingsNet.MessageId, (NetMessageReceived)OnMessage);
    }

    public void OnLoadCompleted() { }

    void OnMessage(IReadMessage msg)
    {
        try
        {
            var response = SettingsNet.Deserialize<SettingsResponse>(msg.ReadString());
            if (response != null) window?.OnResponse(response);
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
        }
        catch { /* input not ready (loading screens) */ }
    }

    public void Dispose()
    {
        try { EventService?.Unsubscribe<IEventUpdate>(this); } catch { }
        window?.Close();
        window = null;
    }
}
