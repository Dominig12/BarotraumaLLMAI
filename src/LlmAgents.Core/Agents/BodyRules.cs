using System;
using System.Text.RegularExpressions;

namespace LlmAgents.Agents;

/// <summary>Body references distinguish corpses of the same species without relying on the live crew roster.</summary>
public static class BodyRules
{
    static readonly Regex Reference = new(@"\[c(\d+)\]|^c(\d+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static int MatchScore(string query, int id, string name, string species)
    {
        var q = query.Trim();
        var reference = Reference.Match(q);
        if (reference.Success)
            return int.TryParse(reference.Groups[1].Success ? reference.Groups[1].Value : reference.Groups[2].Value, out var wanted) && wanted == id ? 4 : 0;
        if (q is "" or "*" or "nearest") return 1;
        if (q.Equals(name, StringComparison.OrdinalIgnoreCase) || q.Equals(species, StringComparison.OrdinalIgnoreCase)) return 3;
        if (q.Length < 3) return 0;
        return name.Contains(q, StringComparison.OrdinalIgnoreCase) || species.Contains(q, StringComparison.OrdinalIgnoreCase) ? 2 : 0;
    }
}

/// <summary>Keep an approach destination stable through ragdoll jitter, but follow a body that moves away.</summary>
public sealed class BodyApproachAnchor
{
    float x, y;
    int? hull;
    bool initialized;

    public bool Update(float currentX, float currentY, int? currentHull)
    {
        float dx = currentX - x, dy = currentY - y;
        if (initialized && hull == currentHull && dx * dx + dy * dy < 40f * 40f) return false;
        initialized = true;
        x = currentX;
        y = currentY;
        hull = currentHull;
        return true;
    }
}

/// <summary>Track the body's movement, rather than the carrier's path or the lifetime number of regrabs.</summary>
public sealed class BodyDragProgress
{
    float lastX, lastY;
    int? lastHull;
    double lastProgressAt;
    bool initialized;
    public int GripLossesWithoutProgress { get; private set; }
    public double RetryAt { get; private set; }

    public void Observe(float x, float y, int? hull, double now)
    {
        float dx = x - lastX, dy = y - lastY;
        if (initialized && hull == lastHull && dx * dx + dy * dy < 40f * 40f) return;
        initialized = true;
        lastX = x;
        lastY = y;
        lastHull = hull;
        lastProgressAt = now;
        GripLossesWithoutProgress = 0;
    }

    public void LostGrip(double now)
    {
        GripLossesWithoutProgress++;
        RetryAt = now + Math.Min(2, 0.25 * GripLossesWithoutProgress);
    }

    public bool CannotKeepGrip(double now) => initialized && GripLossesWithoutProgress >= 6 && now - lastProgressAt >= 15;
    public bool Stuck(double now) => initialized && now - lastProgressAt >= 30;
}
