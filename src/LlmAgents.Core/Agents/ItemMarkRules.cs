using System;
using System.Globalization;

namespace LlmAgents.Agents;

/// <summary>Exact marker request contract, shared with the game backend. No interpretation of item words.</summary>
public static class ItemMarkRules
{
    public const int MaxNoteLength = 300;

    public static string? Validate(string? item, string? mark, bool on, string? note, string? from = null)
    {
        if (from != null && !IsExactSource(from)) return "from must be an exact current body [cID] or container #ID";
        if (item == "*")
        {
            if (from == null) return "item='*' needs an exact body [cID] or container #ID in from; your mixed inventory is not a scrap scope";
        }
        else if (item == null || item.Length < 2 || item[0] != '#' ||
            !ushort.TryParse(item.Substring(1), NumberStyles.None, CultureInfo.InvariantCulture, out _))
            return "item must be an exact current #ID, or '*' with an exact body/container from; inspect items before marking";
        if (mark is not ("attention" or "deconstruct")) return "mark must be attention or deconstruct";
        if (!string.IsNullOrWhiteSpace(note) && (mark != "attention" || !on))
            return "note is allowed only when setting an attention marker";
        if (note?.Length > MaxNoteLength) return $"note must be at most {MaxNoteLength} characters";
        return null;
    }

    static bool IsExactSource(string source)
    {
        if (source.StartsWith("#", StringComparison.Ordinal))
            return ushort.TryParse(source.Substring(1), NumberStyles.None, CultureInfo.InvariantCulture, out _);
        return source.StartsWith("[c", StringComparison.Ordinal) && source.EndsWith("]", StringComparison.Ordinal)
            && ushort.TryParse(source.Substring(2, source.Length - 3), NumberStyles.None, CultureInfo.InvariantCulture, out _);
    }
}
