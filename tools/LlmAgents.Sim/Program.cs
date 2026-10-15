using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using LlmAgents;
using LlmAgents.Agents;
using LlmAgents.Llm;

// ---------------------------------------------------------------------------------------------
// LlmAgents simulator: the real AgentManager + LLM providers + limits, driving a tiny fake sub.
//
//   dotnet run --project tools/LlmAgents.Sim -- [--config path] [--seconds 60] [--speed 1]
//                                              [--provider name] [--quiet] [--no-chat]
//
// Scripted events: a hull breach at t=8s, a fire at t=20s, a crawler boarding at t=35s,
// and player chat orders at t=14s ("Механики, откачайте воду насосами") and t=28s ("Анна, иди в медотсек").
// While it runs you can also type your own chat orders into the console (as the player "Daniil").
// Bots react via their LLM. Actions change the fake world (fix_leaks seals the breach, etc.).
// ---------------------------------------------------------------------------------------------

string configPath = Path.Combine("Content", "llm_config.json");
double seconds = 60, speed = 1;
string? forceProvider = null;
bool quiet = false, scriptedChat = true;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--config": configPath = args[++i]; break;
        case "--seconds": seconds = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture); break;
        case "--speed": speed = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture); break;
        case "--provider": forceProvider = args[++i]; break;
        case "--quiet": quiet = true; break;
        case "--no-chat": scriptedChat = false; break;
        case "-h":
        case "--help":
            Console.WriteLine("usage: LlmAgents.Sim [--config path] [--seconds N] [--speed X] [--provider name] [--quiet]");
            return 0;
    }
}

var settings = ModSettings.LoadFile(configPath);
if (forceProvider != null)
{
    settings.DefaultProvider = forceProvider;
    settings.Bots.ProviderByJob.Clear();
}
settings.Global.LogFile = "";
Log.ConsoleLevel = quiet ? LogLevel.Warn : LogLevel.Info;
Log.Sink = (level, msg) =>
{
    var color = level switch { LogLevel.Error => ConsoleColor.Red, LogLevel.Warn => ConsoleColor.Yellow, _ => ConsoleColor.Gray };
    lock (Console.Out) { Console.ForegroundColor = color; Console.WriteLine($"[{SimClock.Now,6:0.0}s] {msg}"); Console.ResetColor(); }
};

var world = new SimWorld();
using var mgr = new AgentManager(settings, world, new LlmRouter(settings), () => SimClock.Now);

Console.WriteLine($"Simulating {seconds}s at x{speed} with providers: {string.Join(", ", settings.Providers.Select(p => $"{p.Name}={p.BaseUrl} ({p.Model})"))}");
var typed = new System.Collections.Concurrent.ConcurrentQueue<string>();
if (!Console.IsInputRedirected)
{
    Console.WriteLine("Type a chat order and press Enter to send it to the crew.");
    new Thread(() => { string? line; while ((line = Console.ReadLine()) != null) typed.Enqueue(line); }) { IsBackground = true }.Start();
}
var script = new List<(double At, string Text)>();
if (scriptedChat)
{
    script.Add((14, "Механики, откачайте воду насосами"));
    script.Add((28, "Анна, иди в медотсек"));
}

void SendChat(string text)
{
    lock (Console.Out) { Console.ForegroundColor = ConsoleColor.Green; Console.WriteLine($"[{SimClock.Now,6:0.0}s] Daniil (radio): {text}"); Console.ResetColor(); }
    world.ActionLog.Add($"[{SimClock.Now,6:0.0}s] PLAYER: {text}");
    mgr.SubmitChat(new ChatCommand("Daniil", "Captain", text, world.GetBots().Select(b => b.Id).ToList(), ViaRadio: true));
}

var wall = Stopwatch.StartNew();
while (SimClock.Now < seconds)
{
    SimClock.Now = wall.Elapsed.TotalSeconds * speed;
    world.Tick(SimClock.Now);
    while (script.Count > 0 && script[0].At <= SimClock.Now) { SendChat(script[0].Text); script.RemoveAt(0); }
    while (typed.TryDequeue(out var line)) if (!string.IsNullOrWhiteSpace(line)) SendChat(line.Trim());
    mgr.Update();
    Thread.Sleep(20);
}
mgr.WhenIdle().Wait(TimeSpan.FromSeconds(30));
mgr.Update();

Console.WriteLine();
Console.WriteLine("=== Summary ===");
Console.WriteLine($"Actions taken: {world.ActionLog.Count}");
foreach (var a in world.ActionLog) Console.WriteLine("  " + a);
Console.WriteLine("Decisions per bot: " + string.Join(", ", mgr.Agents.Select(a => $"{a.Name}={a.Notes().Count(n => n.StartsWith("Player order"))} orders")));
Console.WriteLine($"Final state: breach={(world.Breach ? "OPEN" : "sealed")}, fire={(world.Fire ? "BURNING" : "out")}, intruder={(world.Intruder ? "ABOARD" : "gone")}");
return 0;

static class SimClock { public static double Now; }

sealed class SimWorld : IGameApi
{
    sealed class Bot
    {
        public int Id; public string Name = ""; public string Job = ""; public string Room = ""; public string Order = "none";
        public int Health = 100;
    }

    readonly List<Bot> bots = new()
    {
        new() { Id = 1, Name = "Ivan Petrov", Job = "captain", Room = "Command Room [h1]" },
        new() { Id = 2, Name = "Olga Smirnova", Job = "engineer", Room = "Engine Room [h2]" },
        new() { Id = 3, Name = "Max Volkov", Job = "mechanic", Room = "Crew Quarters [h3]" },
        new() { Id = 5, Name = "Oleg Sidorov", Job = "mechanic", Room = "Ballast [h5]" },
        new() { Id = 4, Name = "Anna Kuznetsova", Job = "medicaldoctor", Room = "Medbay [h4]" },
    };
    readonly string[] rooms = { "Command Room [h1]", "Engine Room [h2]", "Crew Quarters [h3]", "Medbay [h4]", "Ballast [h5]" };
    public readonly Dictionary<string, int> Speech = new();

    public bool Breach, Fire, Intruder;
    int water, fixProgress, fireProgress, fightProgress;
    readonly HashSet<string> fired = new();
    public readonly List<string> ActionLog = new();

    public void Tick(double t)
    {
        Event(t, 8, "breach", () => { Breach = true; Console.WriteLine($"[{t,6:0.0}s] *** EVENT: hull breach in Ballast [h5]"); });
        Event(t, 20, "fire", () => { Fire = true; Console.WriteLine($"[{t,6:0.0}s] *** EVENT: fire in Engine Room [h2]"); });
        Event(t, 35, "intruder", () => { Intruder = true; Console.WriteLine($"[{t,6:0.0}s] *** EVENT: crawler boarded in Crew Quarters [h3]"); });

        // world reacts to orders, roughly once per second
        if (!fired.Add("tick" + (int)t)) return;
        if (Breach) water = Math.Min(100, water + 3);
        else water = Math.Max(0, water - 5);
        if (Breach && bots.Any(b => b.Order == "fixleaks") && ++fixProgress >= 4) { Breach = false; Console.WriteLine($"[{t,6:0.0}s] *** breach sealed"); }
        if (Fire && bots.Any(b => b.Order == "extinguishfires") && ++fireProgress >= 3) { Fire = false; Console.WriteLine($"[{t,6:0.0}s] *** fire is out"); }
        if (Intruder)
        {
            var victim = bots.FirstOrDefault(b => b.Room == "Crew Quarters [h3]");
            if (victim != null) victim.Health = Math.Max(5, victim.Health - 4);
            if (bots.Any(b => b.Order == "fightintruders") && ++fightProgress >= 4) { Intruder = false; Console.WriteLine($"[{t,6:0.0}s] *** crawler killed"); }
        }
        foreach (var b in bots.Where(b => b.Order == "rescue" && b.Health > 0))
            foreach (var p in bots.Where(p => p.Health < 100)) p.Health = Math.Min(100, p.Health + 5);
    }

    void Event(double t, double at, string key, Action a) { if (t >= at && fired.Add(key)) a(); }

    Bot B(int id) => bots.First(b => b.Id == id);

    public IReadOnlyList<BotHandle> GetBots() => bots.Select(b => new BotHandle(b.Id, b.Name, b.Job)).ToList();
    public bool IsAlive(int botId) => bots.Any(b => b.Id == botId);

    public Observation Observe(int botId)
    {
        var me = B(botId);
        int RoomWater(string r) => r == "Ballast [h5]" ? water : r == "Engine Room [h2]" ? water / 3 : 0;
        int RoomFire(string r) => Fire && r == "Engine Room [h2]" ? 1 : 0;
        return new Observation
        {
            Name = me.Name, Job = me.Job, HealthPercent = me.Health, OxygenPercent = 100,
            Room = me.Room, RoomWaterPercent = RoomWater(me.Room), RoomFires = RoomFire(me.Room), CurrentOrder = me.Order,
            Inventory = me.Job == "mechanic" ? new() { "Welding Tool", "Wrench" } : me.Job == "medicaldoctor" ? new() { "Bandage x4", "Morphine" } : new() { "Diving Mask" },
            Submarine = new SubInfo(me.Job == "engineer" ? 55 : null, 2140, Breach ? 1 : 0),
            Rooms = rooms.Select(r => new RoomInfo(r, RoomWater(r), RoomFire(r))).ToList(),
            Crew = bots.Where(b => b != me).Select(b => new CrewInfo(b.Name, b.Job, b.Room, b.Health, false, b.Order)).ToList(),
            Leaks = Breach ? new() { "Ballast [h5]" } : new(),
            Threats = Intruder ? new() { new ThreatInfo("Crawler", me.Room == "Crew Quarters [h3]" ? 2 : 14, "Crew Quarters [h3]") } : new(),
        };
    }

    string Do(int id, string order, string? room = null)
    {
        var b = B(id);
        b.Order = order;
        if (room != null) b.Room = room;
        ActionLog.Add($"[{SimClock.Now,6:0.0}s] {b.Name}: {order}{(room != null ? " -> " + room : "")}");
        return "ok";
    }

    public string GoToRoom(int botId, string roomId) =>
        rooms.Contains(roomId) ? Do(botId, "wait", roomId) : $"error: unknown room '{roomId}'";

    public string Follow(int botId, string targetName)
    {
        var t = bots.FirstOrDefault(b => b.Name.Contains(targetName, StringComparison.OrdinalIgnoreCase));
        return t == null ? $"error: no crew member '{targetName}'" : Do(botId, "follow", t.Room);
    }

    public string SimpleOrder(int botId, string orderKey) => Do(botId, Tools.SimpleOrders[orderKey]);
    public string Operate(int botId, string role, string? option) => Do(botId, $"operate {role} {option}".Trim());
    public string Dismiss(int botId) => Do(botId, "none");

    public void Speak(int botId, string text)
    {
        Speech[B(botId).Name] = Speech.GetValueOrDefault(B(botId).Name) + 1;
        ActionLog.Add($"[{SimClock.Now,6:0.0}s] {B(botId).Name} says: \"{text}\"");
        lock (Console.Out) { Console.ForegroundColor = ConsoleColor.Cyan; Console.WriteLine($"[{SimClock.Now,6:0.0}s] {B(botId).Name}: {text}"); Console.ResetColor(); }
    }
}
