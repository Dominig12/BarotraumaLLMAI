using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace LlmAgents.Agents;

/// <summary>
/// Routes the clear cases of a chat message without asking an LLM: a bot named in it, a job ("механики, ..."),
/// or an order for everybody ("все ко мне", "наденьте костюмы"). Returns null for everything else (small talk,
/// one job for one person like "скрафтите гарпун", "кроме ..."), which goes to the LLM dispatcher.
/// Faster than the dispatcher and costs no request.
/// </summary>
public static class ChatRouter
{
    public sealed record Route(List<RosterEntry> Recipients, string Reason);

    static Regex R(string p) => new(p, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>"Everyone except X", "not you": needs understanding, not rules.</summary>
    static readonly Regex Except = R(@"(?<!\w)(кроме|except|but\s+not|не\s+ты|не\s+вы)(?!\w)");

    static readonly Regex Everyone = R(@"(?<!\w)(все|всем|всех|экипаж\w*|команда|народ|ребята|парни|everyone|everybody|all|crew|guys)(?!\w)");

    /// <summary>Russian plural imperative: "откачайте", "идите", "наденьте".</summary>
    static readonly Regex PluralImperative = R(@"(?<!\w)\w{2,}(ите|йте|ьте)(?!\w)");

    /// <summary>Orders that everybody who hears them carries out together (moving, safety, fighting, emergencies).</summary>
    static readonly Regex Collective = R(
        @"ко\s*мне|сюда|за\s*мной|(?<!\w)(стой|стоп|ждите|стойте|идите|бегите|уходите|отступ\w*|назад|по\s*местам|в\s*укрытие)(?!\w)|" +
        @"костюм|скафандр|наденьте|туши|пожар|огонь|пробоин|теч[ьи]|заварит|атак|убей|убейте|монстр|тварь|враг|стреляй|насос|откача|" +
        @"follow\s*me|come\s*(here|to\s*me)|on\s*me|stop|hold|retreat|fall\s*back|suits?\b|diving|fire|leaks?|breach|attack|kill|fight|monster|pump");

    /// <summary>Orders for the technical crew when said to everybody: leaks, pumps, repairs.</summary>
    static readonly Regex TechWork = R(@"пробоин|теч[ьи]|заварит|насос|откача|почин|ремонт|leak|breach|pump|repair");

    sealed record JobWords(string Job, Regex Any, Regex Plural);

    static readonly JobWords[] Jobs =
    {
        new("mechanic", R(@"(?<!\w)(механик\w*|mechanic\w*)"), R(@"(?<!\w)(механики|механикам|механиков|mechanics)(?!\w)")),
        new("engineer", R(@"(?<!\w)(инженер\w*|engineer\w*)"), R(@"(?<!\w)(инженеры|инженерам|инженеров|engineers)(?!\w)")),
        new("medicaldoctor", R(@"(?<!\w)(медик\w*|врач\w*|доктор\w*|medic\w*|doctor\w*)"),
            R(@"(?<!\w)(медики|медикам|медиков|врачи|врачам|доктора|докторам|medics|doctors)(?!\w)")),
        new("securityofficer", R(@"(?<!\w)(охран\w*|офицер\w*\s+безопасност\w*|security|guard\w*)"),
            R(@"(?<!\w)(охрана|охране|охранники|охранникам|security|guards)(?!\w)")),
        new("captain", R(@"(?<!\w)(капитан\w*|captain)(?!\w)"), R(@"(?!x)x")),
        new("assistant", R(@"(?<!\w)(ассистент\w*|помощник\w*|assistant\w*)"),
            R(@"(?<!\w)(ассистенты|ассистентам|помощники|помощникам|assistants)(?!\w)")),
    };

    /// <summary>The recipients if the message is clear, else null (ask the dispatcher).</summary>
    public static Route? TryRoute(ChatCommand cmd, IReadOnlyList<RosterEntry> roster)
    {
        if (roster.Count == 0) return null;
        var text = cmd.Text;
        if (Except.IsMatch(text)) return null;

        var named = ByName(text, roster);
        if (named.Count > 0) return new Route(named, "named");

        foreach (var j in Jobs)
        {
            if (!j.Any.IsMatch(text)) continue;
            var withJob = roster.Where(r => r.Job.Equals(j.Job, StringComparison.OrdinalIgnoreCase)).ToList();
            if (withJob.Count == 0) continue;
            if (j.Plural.IsMatch(text)) return new Route(withJob, "job: " + j.Job + " (all)");
            if (withJob.Count == 1) return new Route(withJob, "job: " + j.Job);
            return null; // "механик, почини насос" with two mechanics: the dispatcher picks the right one
        }

        bool toEveryone = Everyone.IsMatch(text) || PluralImperative.IsMatch(text);
        if (toEveryone && Collective.IsMatch(text))
        {
            if (TechWork.IsMatch(text))
            {
                var tech = roster.Where(r => r.Job is "mechanic" or "engineer").ToList();
                if (tech.Count > 0) return new Route(tech, "technical work for everyone: mechanics and engineers");
            }
            return new Route(roster.ToList(), "everyone");
        }
        return null;
    }

    /// <summary>
    /// When the dispatcher cannot be asked: the bots named in the text; an order for everybody goes to all;
    /// anything else (one job, like "скрафтите гарпун") to the single most suitable bot, not to the whole crew.
    /// </summary>
    public static List<RosterEntry> Fallback(ChatCommand cmd, IReadOnlyList<RosterEntry> roster)
    {
        var named = ByName(cmd.Text, roster);
        if (named.Count > 0) return named;
        if (roster.Count <= 1 || Everyone.IsMatch(cmd.Text) || Collective.IsMatch(cmd.Text)) return roster.ToList();
        foreach (var (pattern, jobs) in Affinity)
        {
            if (!pattern.IsMatch(cmd.Text)) continue;
            foreach (var job in jobs)
            {
                var fit = roster.Where(r => r.Job == job).OrderBy(r => r.CurrentOrder == "none" ? 0 : 1).FirstOrDefault();
                if (fit != null) return new List<RosterEntry> { fit };
            }
        }
        var lead = roster.FirstOrDefault(r => r.Job == "captain") ?? roster.OrderBy(r => r.CurrentOrder == "none" ? 0 : 1).First();
        return new List<RosterEntry> { lead };
    }

    static readonly (Regex Pattern, string[] Jobs)[] Affinity =
    {
        (R(@"скрафт|сделай|изготов|craft|make|разбер|деконстр|salvage|deconstruct|почин|ремонт|repair|насос|pump"), new[] { "mechanic", "engineer", "assistant" }),
        (R(@"реактор|топлив|электр|reactor|fuel|power|wire"), new[] { "engineer", "mechanic" }),
        (R(@"леч|ранен|бинт|перевяж|медик|heal|treat|bandage|wound"), new[] { "medicaldoctor" }),
        (R(@"охран|арест|стрел|оруж|security|arrest|weapon|gun|shoot"), new[] { "securityofficer" }),
        (R(@"курс|рул|навиг|steer|course|navigat"), new[] { "captain" }),
    };

    /// <summary>Bots named in the text. Players type names in Cyrillic while bots have Latin names (or the other way
    /// round), and Russian declines names ("Анну", "Олегу"): transliterated word stems are compared.</summary>
    public static List<RosterEntry> ByName(string text, IReadOnlyList<RosterEntry> roster)
    {
        var words = Words(Translit.ToLatin(text)).ToList();
        return roster.Where(r => Words(Translit.ToLatin(r.Name)).Where(p => p.Length >= 3)
            .Any(part => words.Any(w => w.Length >= 3 && Stem(w) == Stem(part)))).ToList();
    }

    /// <summary>Worth a note to the sender that nobody heard it (rather than ordinary chat between players).</summary>
    public static bool LooksLikeOrder(string text) =>
        OrderHints.Suggest(text, "", null) != null || PluralImperative.IsMatch(text) || Collective.IsMatch(text);

    static IEnumerable<string> Words(string s) =>
        s.ToLowerInvariant().Split(new[] { ' ', ',', '.', '!', '?', ':', ';', '-', '"', '\'', '(', ')' }, StringSplitOptions.RemoveEmptyEntries);

    static string Stem(string w)
    {
        // "Макс" -> "maks" = "Max", "Алекс" = "Alex"
        w = w.Replace("ks", "x").Replace("y", "i").Replace("j", "i").Replace("h", "").Replace("w", "v");
        return w.Length > 4 ? w.Substring(0, 4) : w; // "Олегу"/"Oleg" -> "oleg"
    }
}
