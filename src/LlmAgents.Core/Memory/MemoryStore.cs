using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using LlmAgents.Agents;

namespace LlmAgents.Memory;

/// <summary>What one bot remembers between rounds.</summary>
public sealed class BotMemory
{
    public string Name { get; set; } = "";
    public string Job { get; set; } = "";
    /// <summary>Rounds this character has played with the mod.</summary>
    public int Rounds { get; set; }
    /// <summary>Full AI control granted through the mod order; survives into the next round.</summary>
    public bool AiControl { get; set; }
    public string? AiControlBy { get; set; }
    /// <summary>Long-term facts the bot chose to remember (remember with long_term=true).</summary>
    public List<string> Facts { get; set; } = new();
    /// <summary>One short summary per round, newest last.</summary>
    public List<string> Journal { get; set; } = new();
    /// <summary>How this bot feels about other people, by name.</summary>
    public Dictionary<string, Relationship> Relationships { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class Relationship
{
    /// <summary>One of <see cref="MemoryStore.Feelings"/>.</summary>
    public string Feeling { get; set; } = "neutral";
    public string Note { get; set; } = "";
    /// <summary>Round counter of the bot when this was last changed (for pruning: oldest go first).</summary>
    public int Round { get; set; }
}

/// <summary>Contents of one memory file (one per campaign save, plus "default" outside campaigns).</summary>
public sealed class MemoryFile
{
    public int Version { get; set; } = 1;
    public Dictionary<string, BotMemory> Bots { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>What players like or want, by player name. Shared by the whole AI crew.</summary>
    public Dictionary<string, List<string>> Players { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Long-term memory in local JSON files: memory/&lt;scope&gt;.json. One scope per campaign save, so two campaigns
/// never share memories. Thread-safe (round summaries are written from worker threads). Writes are atomic
/// (temp file + replace) and batched: <see cref="Flush"/> writes only when something changed.
/// With folder = null nothing touches the disk (tests, memory disabled).
/// </summary>
public sealed class MemoryStore
{
    public static readonly IReadOnlyList<string> Feelings = new[] { "trust", "like", "neutral", "dislike", "distrust" };

    public const int MaxFactLength = 200;
    public const int MaxNoteLength = 160;
    public const int MaxJournalLength = 300;

    readonly object sync = new();
    readonly string? folder;
    MemoryFile data = new();
    bool dirty;

    public MemoryStore(string? folder) { this.folder = folder; }

    public string Scope { get; private set; } = "";
    public string? FilePath => folder == null || Scope.Length == 0 ? null : Path.Combine(folder, Scope + ".json");

    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // Cyrillic names stay readable in the file
    };

    /// <summary>Switches to the memory of another campaign (saves the current one first). Cheap if unchanged.</summary>
    public void Open(string scope)
    {
        scope = SafeName(scope);
        lock (sync)
        {
            if (scope == Scope) return;
            FlushLocked();
            Scope = scope;
            data = Load() ?? new MemoryFile();
            Normalize(data);
            dirty = false;
        }
    }

    MemoryFile? Load() => Load(FilePath);

    static MemoryFile? Load(string? path)
    {
        if (path == null || !File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<MemoryFile>(File.ReadAllText(path), Json);
        }
        catch (Exception e)
        {
            // Never lose a damaged file: keep it next to the new one for manual repair.
            Log.Error($"memory file {path} is damaged ({e.Message}); starting empty, the old file is kept as .bad");
            try { File.Copy(path, path + ".bad", overwrite: true); } catch { /* best effort */ }
            return null;
        }
    }

    static void Normalize(MemoryFile f)
    {
        f.Bots = new Dictionary<string, BotMemory>(f.Bots ?? new(), StringComparer.OrdinalIgnoreCase);
        f.Players = new Dictionary<string, List<string>>(f.Players ?? new(), StringComparer.OrdinalIgnoreCase);
        foreach (var b in f.Bots.Values)
        {
            b.Facts ??= new();
            b.Journal ??= new();
            b.Relationships = new Dictionary<string, Relationship>(b.Relationships ?? new(), StringComparer.OrdinalIgnoreCase);
        }
        foreach (var k in f.Players.Keys.ToList()) f.Players[k] ??= new();
    }

    public void Flush()
    {
        lock (sync) FlushLocked();
    }

    void FlushLocked()
    {
        if (!dirty) return;
        var path = FilePath;
        dirty = false;
        if (path == null) return;
        if (!Save(data, path)) dirty = true; // try again at the next flush
    }

    static bool Save(MemoryFile file, string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(file, Json), new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
            return true;
        }
        catch (Exception e)
        {
            Log.Warn($"could not save memory to {path}: {e.Message}");
            return false;
        }
    }

    /// <summary>A change to the memory of a given campaign. A round summary arrives seconds after the round ended,
    /// when the store may already have opened the next campaign: it still goes to the one it belongs to.</summary>
    public void ChangeIn(string scope, Action<MemoryFile> f)
    {
        lock (sync)
        {
            if (SafeName(scope) == Scope) { f(data); dirty = true; return; }
            var path = folder == null || scope.Length == 0 ? null : Path.Combine(folder, SafeName(scope) + ".json");
            if (path == null) return;
            var other = Load(path) ?? new MemoryFile();
            Normalize(other);
            f(other);
            Save(other, path);
        }
    }

    public bool IsDirty { get { lock (sync) return dirty; } }

    /// <summary>Read access; the callback must not keep references to the data.</summary>
    public T Read<T>(Func<MemoryFile, T> f)
    {
        lock (sync) return f(data);
    }

    public void Change(Action<MemoryFile> f)
    {
        lock (sync)
        {
            f(data);
            dirty = true;
        }
    }

    BotMemory BotLocked(string name, string job) => BotIn(data, name, job);

    static BotMemory BotIn(MemoryFile data, string name, string job)
    {
        if (!data.Bots.TryGetValue(name, out var b))
            data.Bots[name] = b = new BotMemory { Name = name, Job = job };
        if (!string.IsNullOrEmpty(job)) b.Job = job;
        return b;
    }

    // ------------------------------------------------------------------ operations used by the agents

    public BotMemory? Bot(string name) => Read(d => d.Bots.TryGetValue(name, out var b) ? Copy(b) : null);

    static BotMemory Copy(BotMemory b) => new()
    {
        Name = b.Name, Job = b.Job, Rounds = b.Rounds, AiControl = b.AiControl, AiControlBy = b.AiControlBy,
        Facts = b.Facts.ToList(), Journal = b.Journal.ToList(),
        Relationships = b.Relationships.ToDictionary(kv => kv.Key, kv => new Relationship { Feeling = kv.Value.Feeling, Note = kv.Value.Note, Round = kv.Value.Round }, StringComparer.OrdinalIgnoreCase),
    };

    public void AddFact(string bot, string job, string fact, int max) => Change(d =>
    {
        var b = BotLocked(bot, job);
        fact = Tools.Clip(fact.Trim(), MaxFactLength);
        b.Facts.RemoveAll(f => f.Equals(fact, StringComparison.OrdinalIgnoreCase));
        b.Facts.Add(fact);
        while (b.Facts.Count > Math.Max(1, max)) b.Facts.RemoveAt(0);
    });

    /// <summary>Removes facts containing the text. Returns how many were removed.</summary>
    public int ForgetFact(string bot, string text)
    {
        int n = 0;
        Change(d =>
        {
            if (d.Bots.TryGetValue(bot, out var b)) n = b.Facts.RemoveAll(f => f.Contains(text, StringComparison.OrdinalIgnoreCase));
        });
        return n;
    }

    public void AddJournal(string bot, string job, string entry, int max, bool countRound, string? scope = null)
    {
        Action<MemoryFile> f = d => AddJournalTo(d, bot, job, entry, max, countRound);
        if (scope == null) Change(f); else ChangeIn(scope, f);
    }

    static void AddJournalTo(MemoryFile d, string bot, string job, string entry, int max, bool countRound)
    {
        var b = BotIn(d, bot, job);
        if (countRound) b.Rounds++;
        if (!string.IsNullOrWhiteSpace(entry))
        {
            b.Journal.Add(Tools.Clip(entry.Trim(), MaxJournalLength));
            while (b.Journal.Count > Math.Max(1, max)) b.Journal.RemoveAt(0);
        }
    }

    public void SetRelationship(string bot, string job, string person, string feeling, string note, int max) => Change(d =>
    {
        var b = BotLocked(bot, job);
        b.Relationships[person] = new Relationship { Feeling = feeling, Note = Tools.Clip(note.Trim(), MaxNoteLength), Round = b.Rounds };
        // over the limit: forget the neutral and oldest ones first
        while (b.Relationships.Count > Math.Max(1, max))
        {
            var drop = b.Relationships
                .Where(kv => !kv.Key.Equals(person, StringComparison.OrdinalIgnoreCase))
                .OrderBy(kv => kv.Value.Feeling == "neutral" ? 0 : 1).ThenBy(kv => kv.Value.Round)
                .First().Key;
            b.Relationships.Remove(drop);
        }
    });

    public void AddPreference(string player, string preference, int max) => Change(d =>
    {
        if (!d.Players.TryGetValue(player, out var list)) d.Players[player] = list = new();
        preference = Tools.Clip(preference.Trim(), MaxNoteLength);
        list.RemoveAll(p => p.Equals(preference, StringComparison.OrdinalIgnoreCase));
        list.Add(preference);
        while (list.Count > Math.Max(1, max)) list.RemoveAt(0);
    });

    public void SetControl(string bot, string job, bool on, string? by) => Change(d =>
    {
        var b = BotLocked(bot, job);
        b.AiControl = on;
        b.AiControlBy = on ? by : null;
    });

    public static string SafeName(string s)
    {
        var sb = new StringBuilder();
        foreach (var ch in s.Trim())
            sb.Append(char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_');
        var r = sb.ToString().Trim('_');
        if (r.Length > 60) r = r.Substring(0, 60);
        return r.Length == 0 ? "default" : r;
    }
}
