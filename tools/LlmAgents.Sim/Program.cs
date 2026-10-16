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
// and player chat orders at t=14s ("Механики, откачайте воду насосами"), t=28s ("Анна, иди в медотсек"),
// t=33s ("Всем надеть водолазные костюмы, идёт бой"), t=40s ("Анна, вылечи Макса, только бинтами"), t=44s ("Ольга, перезаряди реактор"),
// t=47s ("Механики, приоритет: сначала тушить пожар, потом течи") and at t=50s the player gives Ivan the "Full AI control" order.
// Also: t=5s "Все ко мне" (come_to), t=55s a command-menu order for Oleg (fix leaks), t=58s "Скрафтите гарпун" (one crafter),
// t=62s "Медик, возьми медикаменты и за мной" (take_item, then follow).
// Bots think only on events (autonomy "events"): watch "why" in the log and the request count at the end.
// At the end every bot writes a round summary into its long-term memory (sim_memory/sim.json by default).
// Characters come from sim_memory/personas.json (created with a few examples). Console lines starting with "!llm"
// are settings commands, exactly like in the game chat (e.g. "!llm persona Max preset grumpy", "!llm set maxNotes 3").
// While it runs you can also type your own chat orders into the console (as the player "Daniil").
// Bots react via their LLM. Actions change the fake world (fix_leaks seals the breach, etc.).
// ---------------------------------------------------------------------------------------------

string configPath = Path.Combine("Content", "llm_config.json");
double seconds = 70, speed = 1;
string? forceProvider = null;
string memoryDir = "sim_memory";
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
        case "--memory": memoryDir = args[++i]; break;
        case "-h":
        case "--help":
            Console.WriteLine("usage: LlmAgents.Sim [--config path] [--seconds N] [--speed X] [--provider name] [--quiet]");
            return 0;
    }
}

var editor = LlmAgents.SettingsUi.ConfigEditor.Load(configPath);
var settings = editor.Settings;
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
var memory = new LlmAgents.Memory.MemoryStore(memoryDir);
var personasPath = Path.Combine(memoryDir, "personas.json");
bool newPersonas = !File.Exists(personasPath);
var personas = new LlmAgents.Personas.PersonaStore(personasPath);
if (newPersonas)
{
    personas.SetCharacter("Ivan Petrov", preset: "veteran");
    personas.SetCharacter("Max Volkov", preset: "joker", prompt: "Зовёт всех по прозвищам.");
    personas.SetCharacter("Oleg Sidorov", preset: "coward");
    personas.Save();
}
// limits per minute count simulated time, so --speed does not use them up faster than a real round would
var simStart = DateTime.UtcNow;
DateTime SimUtc() => simStart.AddSeconds(SimClock.Now);
using var mgr = new AgentManager(settings, world, new LlmRouter(settings, null, SimUtc), () => SimClock.Now, memory, personas);
mgr.BeginRound("sim");
// in the sim the settings file is never written: commands change a copy in memory
var simEditor = new LlmAgents.SettingsUi.ConfigEditor(editor.ToJson());
var settingsService = new LlmAgents.SettingsUi.SettingsService(simEditor, personas,
    (s, providersChanged) => mgr.ApplySettings(ApplySimOverrides(s), providersChanged ? new LlmRouter(s, null, SimUtc) : null),
    () => new LlmAgents.SettingsUi.GameInfo(
        world.GetBots().Select(b => new LlmAgents.SettingsUi.CrewEntry(b.Name, b.Job, b.Trait, false)).ToList(),
        new[] { "captain", "engineer", "mechanic", "medicaldoctor", "assistant", "securityofficer" }),
    () => mgr.Status(new[] { new FeatureStatus("Simulator (no game patches)", true) }));

ModSettings ApplySimOverrides(ModSettings s)
{
    if (forceProvider != null) { s.DefaultProvider = forceProvider; s.Bots.ProviderByJob.Clear(); }
    s.Global.LogFile = "";
    return s;
}

Console.WriteLine($"Simulating {seconds}s at x{speed} with providers: {string.Join(", ", settings.Providers.Select(p => $"{p.Name}={p.BaseUrl} ({p.Model})"))}");
var typed = new System.Collections.Concurrent.ConcurrentQueue<string>();
if (!Console.IsInputRedirected) Console.WriteLine("Type a chat order (or an !llm command) and press Enter.");
// piped input works too: echo "!llm persona Max preset grumpy" | dotnet run --project tools/LlmAgents.Sim
new Thread(() => { string? line; while ((line = Console.ReadLine()) != null) typed.Enqueue(line); }) { IsBackground = true }.Start();
var script = new List<(double At, string Text)>();
if (scriptedChat)
{
    script.Add((5, "Все ко мне"));
    script.Add((14, "Механики, откачайте воду насосами"));
    script.Add((28, "Анна, иди в медотсек"));
    script.Add((33, "Всем надеть водолазные костюмы, идёт бой"));
    script.Add((40, "Анна, вылечи Макса, только бинтами"));
    script.Add((44, "Ольга, перезаряди реактор"));
    script.Add((47, "Механики, приоритет: сначала тушить пожар, потом течи"));
    script.Add((58, "Скрафтите гарпун"));
    script.Add((62, "Медик, возьми медикаменты и за мной"));

}

void SendChat(string text)
{
    lock (Console.Out) { Console.ForegroundColor = ConsoleColor.Green; Console.WriteLine($"[{SimClock.Now,6:0.0}s] Daniil (radio): {text}"); Console.ResetColor(); }
    world.ActionLog.Add($"[{SimClock.Now,6:0.0}s] PLAYER: {text}");
    mgr.SubmitChat(new ChatCommand("Daniil", "Captain", text, world.GetBots().Select(b => b.Id).ToList(), ViaRadio: true));
}

var wall = Stopwatch.StartNew();
bool menuGiven = false;
while (SimClock.Now < seconds)
{
    SimClock.Now = wall.Elapsed.TotalSeconds * speed;
    world.Tick(SimClock.Now);
    while (script.Count > 0 && script[0].At <= SimClock.Now) { SendChat(script[0].Text); script.RemoveAt(0); }
    if (scriptedChat && SimClock.Now >= 55 && !menuGiven) { menuGiven = true; world.GiveMenuOrder(5, "Daniil", "fixleaks"); }
    if (scriptedChat && SimClock.Now >= 50 && world.GiveControlOnce(1, "Daniil"))
        lock (Console.Out) { Console.ForegroundColor = ConsoleColor.Green; Console.WriteLine($"[{SimClock.Now,6:0.0}s] Daniil gives Ivan Petrov the order: Full AI control"); Console.ResetColor(); }
    while (typed.TryDequeue(out var line))
    {
        if (string.IsNullOrWhiteSpace(line)) continue;
        if (LlmAgents.SettingsUi.SettingsService.IsCommand(line))
        {
            var reply = settingsService.HandleCommand(line.Trim(), canEdit: true);
            lock (Console.Out) { Console.ForegroundColor = ConsoleColor.Magenta; Console.WriteLine(reply); Console.ResetColor(); }
            continue;
        }
        SendChat(line.Trim());
    }
    mgr.Update();
    Thread.Sleep(20);
}
mgr.WhenIdle().Wait(TimeSpan.FromSeconds(30));
mgr.Update();
mgr.EndRound();
mgr.WhenIdle().Wait(TimeSpan.FromSeconds(60));
memory.Flush();

Console.WriteLine();
Console.WriteLine("=== Summary ===");
Console.WriteLine($"Actions taken: {world.ActionLog.Count}");
foreach (var a in world.ActionLog) Console.WriteLine("  " + a);
Console.WriteLine("Decisions per bot: " + string.Join(", ", mgr.Agents.Select(a => $"{a.Name}={a.Decisions} (orders {a.PastOrders.Count})")));
Console.WriteLine($"Requests in the last minute: {mgr.RequestsLastMinute}");
Console.WriteLine("Notes to the player: " + string.Join(" | ", world.Notices));
Console.WriteLine($"Memory ({memory.FilePath}):");
foreach (var b in memory.Read(d => d.Bots.Values.ToList()))
    Console.WriteLine($"  {b.Name}: rounds={b.Rounds}, control={b.AiControl}, facts={b.Facts.Count}, relationships={b.Relationships.Count}; last: {b.Journal.LastOrDefault()}");
foreach (var (player, prefs) in memory.Read(d => d.Players.Select(kv => (kv.Key, kv.Value.ToList())).ToList()))
    Console.WriteLine($"  player {player}: {string.Join("; ", prefs)}");
Console.WriteLine($"Final state: breach={(world.Breach ? "OPEN" : "sealed")}, fire={(world.Fire ? "BURNING" : "out")}, intruder={(world.Intruder ? "ABOARD" : "gone")}");
return 0;

static class SimClock { public static double Now; }

sealed class SimWorld : IGameApi
{
    sealed class Bot
    {
        public int Id; public string Name = ""; public string Job = ""; public string Room = "";
        public int Health = 100;
        /// <summary>Like vanilla: up to 3 orders, priority 3 = most important.</summary>
        public readonly List<(string Id, int P)> Orders = new();
        public bool Has(string id) => Orders.Any(o => o.Id == id);
        public string Order => Orders.Count == 0 ? "none" : string.Join(", ", Orders.OrderByDescending(o => o.P).Select(o => $"{o.Id} [p{o.P}]"));
        public void Add(string id, int p)
        {
            Orders.RemoveAll(o => o.Id == id);
            for (int i = 0; i < Orders.Count; i++) if (Orders[i].P <= p) Orders[i] = (Orders[i].Id, Orders[i].P - 1);
            Orders.RemoveAll(o => o.P <= 0);
            Orders.Add((id, p));
        }
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
        if (Breach && bots.Any(b => b.Has("fixleaks")) && ++fixProgress >= 4) { Breach = false; Console.WriteLine($"[{t,6:0.0}s] *** breach sealed"); }
        if (Fire && bots.Any(b => b.Has("extinguishfires")) && ++fireProgress >= 3) { Fire = false; Console.WriteLine($"[{t,6:0.0}s] *** fire is out"); }
        if (Intruder)
        {
            var victim = bots.FirstOrDefault(b => b.Room == "Crew Quarters [h3]");
            if (victim != null) victim.Health = Math.Max(5, victim.Health - 4);
            if (bots.Any(b => b.Has("fightintruders")) && ++fightProgress >= 4) { Intruder = false; Console.WriteLine($"[{t,6:0.0}s] *** crawler killed"); }
        }
        foreach (var b in bots.Where(b => b.Has("rescue") && b.Health > 0))
            foreach (var p in bots.Where(p => p.Health < 100)) p.Health = Math.Min(100, p.Health + 5);
    }

    void Event(double t, double at, string key, Action a) { if (t >= at && fired.Add(key)) a(); }

    Bot B(int id) => bots.First(b => b.Id == id);

    static readonly Dictionary<string, string> Traits = new() { ["Olga Smirnova"] = "Вспыльчивая", ["Anna Kuznetsova"] = "Добродушная" };
    public IReadOnlyList<BotHandle> GetBots() => bots.Select(b => new BotHandle(b.Id, b.Name, b.Job, Traits.GetValueOrDefault(b.Name))).ToList();
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
            Gear = (gearOn.Contains(me.Id) ? "Diving Suit on (oxygen 100%)" : "no diving gear on") + (Breach || Intruder ? "; DANGER: gear locked automatically" : ""),
            Threats = Intruder ? new() { new ThreatInfo("Crawler", me.Room == "Crew Quarters [h3]" ? 2 : 14, "Crew Quarters [h3]") } : new(),
        };
    }

    /// <summary>An action that is not an order (equip, treat...): logged only.</summary>
    string Do(int id, string what)
    {
        ActionLog.Add($"[{SimClock.Now,6:0.0}s] {B(id).Name}: {what}");
        return "ok";
    }

    string Order(int id, string order, int priority, string? room = null)
    {
        var b = B(id);
        if (order is "wait" or "follow") b.Orders.RemoveAll(o => o.Id is "wait" or "follow"); // movement orders replace each other
        b.Add(order, priority);
        if (room != null) b.Room = room;
        ActionLog.Add($"[{SimClock.Now,6:0.0}s] {b.Name}: {order} [p{priority}]{(room != null ? " -> " + room : "")}  => {b.Order}");
        return priority == Tools.TopPriority ? "ok" : $"ok [p{priority}]";
    }

    public string GoToRoom(int botId, string roomId, int priority = Tools.TopPriority) =>
        rooms.Contains(roomId) ? Order(botId, "wait", priority, roomId) : $"error: unknown room '{roomId}'";

    public string ComeTo(int botId, string targetName)
    {
        var target = bots.FirstOrDefault(b => b.Name.Contains(targetName, StringComparison.OrdinalIgnoreCase));
        var room = target?.Room ?? "Command Room [h1]"; // the player stands in the command room
        B(botId).Room = room;
        Report(botId, $"task 'come to {targetName}' ok: you came to {targetName} and stayed. This order is DONE", 6);
        return Do(botId, $"come to {targetName}") + ", started: come to " + targetName;
    }

    public string EndTravel(int botId)
    {
        var b = B(botId);
        int before = b.Orders.Count;
        b.Orders.RemoveAll(o => o.Id == "wait");
        return before != b.Orders.Count ? Do(botId, "drop travel order") : "ok";
    }

    public readonly List<string> Notices = new();
    public void NotifyPlayer(string playerName, string text)
    {
        Notices.Add($"{playerName}: {text}");
        lock (Console.Out) { Console.ForegroundColor = ConsoleColor.DarkGray; Console.WriteLine($"[{SimClock.Now,6:0.0}s] (to {playerName}) {text}"); Console.ResetColor(); }
    }

    readonly List<MenuOrder> menuOrders = new();
    public void GiveMenuOrder(int botId, string by, string order)
    {
        var b = B(botId);
        b.Add(order, Tools.TopPriority);
        menuOrders.Add(new MenuOrder(botId, by, order));
        lock (Console.Out) { Console.ForegroundColor = ConsoleColor.Green; Console.WriteLine($"[{SimClock.Now,6:0.0}s] {by} gives {b.Name} through the command menu: {order}"); Console.ResetColor(); }
    }
    public IReadOnlyList<MenuOrder> DrainMenuOrders() { var r = menuOrders.ToList(); menuOrders.Clear(); return r; }

    public string Follow(int botId, string targetName, int priority = Tools.TopPriority)
    {
        var t = bots.FirstOrDefault(b => b.Name.Contains(targetName, StringComparison.OrdinalIgnoreCase));
        // the player (Daniil) stands in the command room
        if (t == null && targetName.Contains("Daniil", StringComparison.OrdinalIgnoreCase)) return Order(botId, "follow", priority, "Command Room [h1]");
        return t == null ? $"error: no crew member '{targetName}'" : Order(botId, "follow", priority, t.Room);
    }

    public string SimpleOrder(int botId, string orderKey, int priority = Tools.TopPriority) => Order(botId, Tools.SimpleOrders[orderKey], priority);
    public string Operate(int botId, string role, string? option, int priority = Tools.TopPriority) =>
        Order(botId, (Tools.OrderAliases.TryGetValue(role, out var id) ? id : role) + (option != null ? ":" + option : ""), priority);
    public string Dismiss(int botId) { B(botId).Orders.Clear(); return Do(botId, "dismiss all orders"); }

    public string SetOrderPriority(int botId, string order, int priority)
    {
        var b = B(botId);
        var q = order.Split(' ', '[')[0].ToLowerInvariant();
        var id = Tools.OrderAliases.TryGetValue(q, out var alias) ? alias : q;
        var o = b.Orders.FirstOrDefault(x => x.Id == id || x.Id.StartsWith(id + ":"));
        if (o.Id == null) return $"error: no current order '{order}'; your orders: {b.Order}";
        b.Orders.Remove(o);
        if (priority > 0) b.Add(o.Id, priority);
        ActionLog.Add($"[{SimClock.Now,6:0.0}s] {b.Name}: priority {o.Id} -> {priority}  => {b.Order}");
        return priority > 0 ? $"ok, {o.Id} is now priority {priority}" : $"ok, dropped {o.Id}";
    }

    readonly HashSet<int> controlled = new();
    readonly List<ControlEvent> controlEvents = new();
    bool controlGiven;
    public bool GiveControlOnce(int botId, string by)
    {
        if (controlGiven) return false;
        controlGiven = true;
        controlEvents.Add(new ControlEvent(botId, !controlled.Contains(botId), by));
        return true;
    }
    public void SetAiControl(int botId, bool on) { if (on) controlled.Add(botId); else controlled.Remove(botId); }
    public IReadOnlyList<ControlEvent> DrainControlEvents() { var r = controlEvents.ToList(); controlEvents.Clear(); return r; }

    readonly List<(double Due, GameReport Report)> reports = new();
    public void BeginCommand(int botId, bool playerAuthority) { }
    readonly HashSet<int> gearOn = new();
    static string Started(string what) => $"ok, started: {what} (result will be reported)";
    public string Equip(int botId, string item) { if (item.Contains("suit")) gearOn.Add(botId); Report(botId, $"task 'equip {item}' ok: equipped", 2); Do(botId, $"equip {item}"); return Started("equip " + item); }
    public string RefuelReactor(int botId) { Report(botId, "task 'refuel reactor' ok: fuel rod loaded", 5); Do(botId, "refuel reactor"); return Started("refuel reactor"); }
    public string Craft(int botId, string item, int amount, bool salvage = false) { Report(botId, $"task 'craft {item}' ok: made {amount}", 8); Do(botId, $"craft {item} x{amount}"); return Started("craft " + item); }
    public string Deconstruct(int botId, string item, string? from = null) { Report(botId, $"task 'deconstruct {item}' ok", 6); Do(botId, $"deconstruct {item}"); return Started("deconstruct " + item); }
    public string Treat(int botId, string target, string? item)
    {
        var p = bots.FirstOrDefault(b => b.Name.Contains(target, StringComparison.OrdinalIgnoreCase));
        if (p == null) return $"error: no crew member '{target}'";
        p.Health = Math.Min(100, p.Health + 40);
        Report(botId, $"task 'treat {p.Name}' ok: used {item ?? "Bandage"} x1, health {p.Health}%", 4);
        Do(botId, $"treat {p.Name} with {item ?? "cheapest"}");
        return Started("treat " + p.Name);
    }
    public string Unequip(int botId, string item) => Do(botId, $"unequip {item}");
    public string Drop(int botId, string item) => Do(botId, $"drop {item}");
    public string MoveItem(int botId, string item, string to) { Report(botId, $"task 'move {item}' ok: delivered to {to}", 5); Do(botId, $"move {item} -> {to}"); return Started("move " + item); }
    public string GiveOrder(int botId, string orderId, string? option, string? target, int priority = Tools.TopPriority) =>
        Order(botId, option != null ? $"{orderId}:{option}" : orderId, priority);
    public string Attack(int botId, string target, string? weapon) { Report(botId, $"task 'attack {target}' ok: done", 6); Do(botId, $"attack {target}" + (weapon != null ? " with " + weapon : "")); return Started("attack " + target); }
    public string Unpack(int botId, string container, bool carry = true) { Report(botId, $"task 'unpack {container}' ok: put away from {container}: Bandage x4 -> Medical Cabinet in Medbay [h4]", 6); Do(botId, $"unpack {container}"); return Started("unpack " + container); }
    public string TakeItem(int botId, string item, int count, string? from = null) { Report(botId, $"task 'take {item}' ok: took {item} x{count} (in your inventory now)", 4); Do(botId, $"take {item} x{count}"); return Started("take " + item); }
    public string KeepGear(int botId, bool on) => Do(botId, on ? "keep gear ON" : "keep gear off");
    public string Inspect(int botId, string kind, string query) => kind switch
    {
        "patient" => $"{query}: injuries: bleeding 20 (torso); treatments: Bandage [antibleeding1] (suitability 40, price 10, available 6)",
        "recipe" => $"{query}: needs Steel Bar x1 (available 4). All ingredients available.",
        _ => $"{query}: 2 in Cabinet in Medbay [h4]",
    };
    void Report(int botId, string text, double after) => reports.Add((SimClock.Now + after, new GameReport(botId, text)));
    public IReadOnlyList<GameReport> DrainReports()
    {
        var due = reports.Where(r => r.Due <= SimClock.Now).ToList();
        reports.RemoveAll(r => r.Due <= SimClock.Now);
        return due.Select(r => r.Report).ToList();
    }

    public void Speak(int botId, string text)
    {
        Speech[B(botId).Name] = Speech.GetValueOrDefault(B(botId).Name) + 1;
        ActionLog.Add($"[{SimClock.Now,6:0.0}s] {B(botId).Name} says: \"{text}\"");
        lock (Console.Out) { Console.ForegroundColor = ConsoleColor.Cyan; Console.WriteLine($"[{SimClock.Now,6:0.0}s] {B(botId).Name}: {text}"); Console.ResetColor(); }
    }
}
