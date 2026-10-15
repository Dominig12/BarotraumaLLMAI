using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace LlmAgents.Llm;

public enum RouteStatus { Ok, Limited, Failed }

public sealed record RouteResult(RouteStatus Status, LlmResponse? Response, string Provider, string Detail);

/// <summary>
/// Picks a provider, applies global + per-provider limits, falls back to a second provider.
/// Slots are reserved synchronously (<see cref="TryReserve"/>, on the game thread) and then used by
/// the asynchronous request, so the scheduler always knows exactly how much capacity is left.
/// </summary>
public sealed class LlmRouter : IDisposable
{
    sealed record Entry(ProviderConfig Config, ILlmProvider Provider, LlmGate Gate);

    /// <summary>Held slots for one request. Dispose to release (CompleteAsync does it for you).</summary>
    public sealed class Reservation : IDisposable
    {
        internal Reservation(string provider, LlmGate.Lease providerLease, LlmGate.Lease globalLease)
        {
            Provider = provider;
            ProviderLease = providerLease;
            GlobalLease = globalLease;
        }

        public string Provider { get; }
        internal LlmGate.Lease ProviderLease { get; }
        internal LlmGate.Lease GlobalLease { get; }

        public void Dispose()
        {
            ProviderLease.Dispose();
            GlobalLease.Dispose();
        }
    }

    readonly Dictionary<string, Entry> providers = new(StringComparer.OrdinalIgnoreCase);
    readonly LlmGate globalGate;
    readonly ModSettings settings;
    readonly HttpClient http;

    public LlmRouter(ModSettings settings, HttpMessageHandler? handler = null, Func<DateTime>? utcNow = null)
    {
        this.settings = settings;
        globalGate = new LlmGate(
            settings.Global.MaxConcurrentRequests,
            settings.Global.RequestsPerMinute,
            tokensPerMinute: 0,
            dailyBudget: settings.Global.DailyRequestBudget,
            utcNow: utcNow);

        http = handler != null ? new HttpClient(handler, disposeHandler: false) : new HttpClient();
        http.Timeout = Timeout.InfiniteTimeSpan; // per-request timeouts are handled by the providers

        foreach (var p in settings.Providers)
        {
            ILlmProvider impl = p.Type == "anthropic"
                ? new AnthropicProvider(p, http)
                : new OpenAiCompatibleProvider(p, http);
            providers[p.Name] = new Entry(p, impl,
                new LlmGate(p.MaxConcurrent, p.RequestsPerMinute, p.TokensPerMinute, p.DailyRequestBudget, utcNow));
        }
    }

    public LlmGate GlobalGate => globalGate;
    public LlmGate? GateOf(string provider) => providers.TryGetValue(provider, out var e) ? e.Gate : null;

    List<string> Order(string preferred)
    {
        var order = new List<string>();
        if (providers.ContainsKey(preferred)) order.Add(preferred);
        var fb = settings.FallbackProvider;
        if (!string.IsNullOrEmpty(fb) && providers.ContainsKey(fb) &&
            !string.Equals(fb, preferred, StringComparison.OrdinalIgnoreCase)) order.Add(fb);
        if (order.Count == 0) order.Add(settings.DefaultProvider);
        return order;
    }

    /// <summary>Non-blocking: reserves a slot on the preferred provider, or on the fallback if the preferred one is full.</summary>
    public bool TryReserve(string preferred, out Reservation? reservation, out string reason)
    {
        reason = "no provider";
        foreach (var name in Order(preferred))
        {
            if (TryReserveExact(name, out reservation, out reason)) return true;
            if (reason.StartsWith("global:", StringComparison.Ordinal)) return false; // nothing else can succeed
        }
        reservation = null;
        return false;
    }

    bool TryReserveExact(string name, out Reservation? reservation, out string reason)
    {
        reservation = null;
        var e = providers[name];
        // Provider gate first, so a provider that is full does not burn global quota.
        var lease = e.Gate.TryEnter(out var why);
        if (lease == null) { reason = $"{name}: {why}"; return false; }
        var globalLease = globalGate.TryEnter(out var gwhy);
        if (globalLease == null)
        {
            lease.Dispose();
            reason = "global: " + gwhy;
            return false;
        }
        reservation = new Reservation(name, lease, globalLease);
        reason = "";
        return true;
    }

    /// <summary>
    /// Runs the request on the reserved provider; on error tries the fallback provider (if it has a free slot).
    /// Always releases the reservation.
    /// </summary>
    public async Task<RouteResult> CompleteAsync(
        Reservation reservation, string system, IReadOnlyList<LlmTurn> turns, IReadOnlyList<ToolSpec> tools, CancellationToken ct)
    {
        var current = reservation;
        var tried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string detail = "";
        try
        {
            while (true)
            {
                tried.Add(current.Provider);
                var e = providers[current.Provider];
                try
                {
                    var req = new LlmRequest(system, turns, tools, e.Config.MaxOutputTokens, e.Config.Temperature);
                    var res = await e.Provider.CompleteAsync(req, ct).ConfigureAwait(false);
                    e.Gate.ReportTokens(res.InputTokens + res.OutputTokens);
                    return new RouteResult(RouteStatus.Ok, res, current.Provider, "");
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    detail += (detail.Length > 0 ? "; " : "") + $"{current.Provider}: {ex.Message}";
                }

                var fb = settings.FallbackProvider;
                if (string.IsNullOrEmpty(fb) || tried.Contains(fb) || !providers.ContainsKey(fb))
                    return new RouteResult(RouteStatus.Failed, null, current.Provider, detail);

                current.Dispose();
                if (!TryReserveExact(fb, out var next, out var why))
                    return new RouteResult(RouteStatus.Failed, null, current.Provider, detail + $"; fallback {why}");
                current = next!;
            }
        }
        finally
        {
            current.Dispose();
        }
    }

    /// <summary>Convenience: reserve + complete. Returns Limited if no slot is free.</summary>
    public async Task<RouteResult> CompleteAsync(
        string preferred, string system, IReadOnlyList<LlmTurn> turns, IReadOnlyList<ToolSpec> tools, CancellationToken ct)
    {
        if (!TryReserve(preferred, out var reservation, out var reason))
            return new RouteResult(RouteStatus.Limited, null, preferred, reason);
        return await CompleteAsync(reservation!, system, turns, tools, ct).ConfigureAwait(false);
    }

    public void Dispose() => http.Dispose();
}
