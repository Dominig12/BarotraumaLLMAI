using System.Linq;
using System;
using System.Collections.Generic;
using System.Net;
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
    sealed class Entry
    {
        public Entry(ProviderConfig config, ILlmProvider provider, LlmGate gate) { Config = config; Provider = provider; Gate = gate; }
        public ProviderConfig Config { get; }
        public ILlmProvider Provider { get; set; }
        public LlmGate Gate { get; }
        // health: guarded by `lock (this)`
        public DateTime PausedUntil;
        public int Failures;
        public string LastError = "";
        // statistics, guarded by `lock (this)`
        public int Ok, Errors;
        public double TotalSeconds;
        public string? LastErrorDetail;
        public DateTime LastErrorAt;
    }

    /// <summary>Health and usage of one provider, for !llm status and the settings window.</summary>
    public sealed record ProviderStatus(string Name, string Model, double PausedForSeconds, string? LastError, DateTime? LastErrorAt,
        int Active, int UsedToday, int Ok, int Errors, double AvgSeconds);

    public IReadOnlyList<ProviderStatus> Status()
    {
        var list = new List<ProviderStatus>();
        foreach (var e in providers.Values)
            lock (e)
                list.Add(new ProviderStatus(e.Config.Name, e.Config.Model, Math.Max(0, (e.PausedUntil - utcNow()).TotalSeconds),
                    e.LastErrorDetail, e.LastErrorDetail != null ? e.LastErrorAt : null,
                    e.Gate.Active, e.Gate.UsedToday, e.Ok, e.Errors, e.Ok > 0 ? e.TotalSeconds / e.Ok : 0));
        return list;
    }

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
    readonly List<HttpClient> ownClients = new();

    static HttpMessageHandler RealHandler(string? proxy, double connectTimeoutSeconds)
    {
        var h = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(Math.Clamp(connectTimeoutSeconds, 1, 120)),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        // empty = the system proxy settings, like before
        if (!string.IsNullOrWhiteSpace(proxy)) h.Proxy = new WebProxy(new Uri(proxy!));
        return h;
    }
    readonly Func<DateTime> utcNow;

    public LlmRouter(ModSettings settings, HttpMessageHandler? handler = null, Func<DateTime>? utcNow = null)
    {
        this.settings = settings;
        this.utcNow = utcNow ?? (() => DateTime.UtcNow);
        globalGate = new LlmGate(
            settings.Global.MaxConcurrentRequests,
            settings.Global.RequestsPerMinute,
            tokensPerMinute: 0,
            dailyBudget: settings.Global.DailyRequestBudget,
            utcNow: utcNow);

        http = handler != null ? new HttpClient(handler, disposeHandler: false) : new HttpClient(RealHandler(null, 8));
        http.Timeout = Timeout.InfiniteTimeSpan; // per-request timeouts are handled by the providers

        foreach (var p in settings.Providers)
        {
            var client = http;
            if (handler == null)
            {
                // own client: its own proxy and a short connect timeout (a blocked host otherwise hangs ~20 s per request)
                client = new HttpClient(RealHandler(p.Proxy, p.ConnectTimeoutSeconds)) { Timeout = Timeout.InfiniteTimeSpan };
                ownClients.Add(client);
            }
            ILlmProvider impl = p.Type switch
            {
                "anthropic" => new AnthropicProvider(p, client),
                "ollama" => new OllamaProvider(p, client),
                _ => new OpenAiCompatibleProvider(p, client),
            };
            providers[p.Name] = new Entry(p, impl,
                new LlmGate(p.MaxConcurrent, p.RequestsPerMinute, p.TokensPerMinute, p.DailyRequestBudget, utcNow));
        }
    }

    public LlmGate GlobalGate => globalGate;

    /// <summary>Carries the limits' counters over from the router this one replaces (same provider names).</summary>
    public void CarryOver(LlmRouter old)
    {
        globalGate.CarryOver(old.globalGate);
        foreach (var (name, e) in providers)
            if (old.providers.TryGetValue(name, out var o)) e.Gate.CarryOver(o.Gate);
    }
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
    /// <param name="priority">false = routine work (not a player's order): part of the limits stays free for orders.</param>
    public bool TryReserve(string preferred, out Reservation? reservation, out string reason, bool priority = true)
    {
        reason = "no provider";
        foreach (var name in Order(preferred))
        {
            if (TryReserveExact(name, out reservation, out reason, priority)) return true;
            if (reason.StartsWith("global:", StringComparison.Ordinal)) return false; // nothing else can succeed
        }
        reservation = null;
        return false;
    }

    /// <summary>Seconds until the provider is used again after errors (0 = healthy).</summary>
    public double PausedFor(string provider)
    {
        if (!providers.TryGetValue(provider, out var e)) return 0;
        lock (e) return Math.Max(0, (e.PausedUntil - utcNow()).TotalSeconds);
    }

    bool TryReserveExact(string name, out Reservation? reservation, out string reason, bool priority = true)
    {
        reservation = null;
        var e = providers[name];
        lock (e)
        {
            var left = (e.PausedUntil - utcNow()).TotalSeconds;
            if (left > 0) { reason = $"{name}: paused {left:0}s after errors ({e.LastError})"; return false; }
        }
        // Provider gate first, so a provider that is full does not burn global quota.
        var lease = e.Gate.TryEnter(out var why, priority);
        if (lease == null) { reason = $"{name}: {why}"; return false; }
        var globalLease = globalGate.TryEnter(out var gwhy, priority);
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

    readonly Dictionary<string, DateTime> contextWarned = new(StringComparer.OrdinalIgnoreCase);

    void WarnContext(string provider, int used, int ctx)
    {
        var now = utcNow();
        lock (contextWarned)
        {
            if (contextWarned.TryGetValue(provider, out var at) && (now - at).TotalMinutes < 5) return;
            contextWarned[provider] = now;
        }
        Log.Warn($"{provider}: the prompt used {used} of {ctx} context tokens; raise contextLength (and the server's context size) or shorten the history");
    }

    /// <summary>
    /// Runs the request on the reserved provider; on error tries the fallback provider (if it has a free slot).
    /// Always releases the reservation.
    /// </summary>
    /// <param name="minOutputTokens">At least this many output tokens (a retry after a reply that was cut off).</param>
    public async Task<RouteResult> CompleteAsync(
        Reservation reservation, string system, IReadOnlyList<LlmTurn> turns, IReadOnlyList<ToolSpec> tools, CancellationToken ct,
        int minOutputTokens = 0, IReadOnlyList<ToolSpec>? accept = null)
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
                    int maxOut = Math.Max(e.Config.MaxOutputTokens, minOutputTokens);
                    var fitted = turns.ToList();
                    int ctx = e.Config.ContextLength;
                    int dropped = ContextGuard.Fit(system, fitted, tools, ctx, maxOut);
                    if (dropped > 0) Log.Info($"{current.Provider}: prompt over contextLength {ctx}, dropped {dropped} oldest history turn(s)");
                    var req = new LlmRequest(system, fitted, tools, maxOut, e.Config.Temperature, accept);
                    var started = utcNow();
                    var res = await e.Provider.CompleteAsync(req, ct).ConfigureAwait(false);
                    e.Gate.ReportTokens(res.InputTokens + res.OutputTokens);
                    if (ctx > 0 && res.InputTokens >= ContextGuard.WarnShare * ctx)
                        WarnContext(current.Provider, res.InputTokens, ctx);
                    lock (e) { e.Ok++; e.TotalSeconds += Math.Max(0, (utcNow() - started).TotalSeconds); }
                    MarkHealthy(e);
                    return new RouteResult(RouteStatus.Ok, res, current.Provider, "");
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    detail += (detail.Length > 0 ? "; " : "") + $"{current.Provider}: {ex.Message}";
                    lock (e) { e.Errors++; e.LastErrorDetail = JsonUtil.Trim(ex.Message, 160); e.LastErrorAt = utcNow(); }
                    MarkFailed(e, ex);
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

    void MarkHealthy(Entry e)
    {
        lock (e)
        {
            if (e.Failures > 0) Log.Warn($"provider '{e.Config.Name}' is working again");
            e.Failures = 0;
            e.PausedUntil = DateTime.MinValue;
        }
    }

    void MarkFailed(Entry e, Exception ex)
    {
        var code = (ex as HttpRequestException)?.StatusCode;
        bool permanent = code is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound
                         or HttpStatusCode.PaymentRequired;
        bool transient = permanent || code is HttpStatusCode.TooManyRequests || (int?)code >= 500
                         || ex is TimeoutException || (ex is HttpRequestException && code == null);
        if (!transient) return; // e.g. a malformed answer: the provider itself is fine

        lock (e)
        {
            e.Failures++;
            e.LastError = code != null ? $"HTTP {(int)code.Value}" : ex.GetType().Name.Replace("Exception", "");
            double pause = permanent
                ? settings.Global.ProviderAuthCooldownSeconds
                : Math.Min(settings.Global.ProviderBackoffMaxSeconds, 5 * Math.Pow(2, Math.Min(e.Failures - 1, 10)));
            e.PausedUntil = utcNow().AddSeconds(pause);
            if (permanent || e.Failures == 1 || pause >= settings.Global.ProviderBackoffMaxSeconds)
            {
                var hint = code == HttpStatusCode.Forbidden
                    ? " (403: access denied: region not supported by the API, or the key has no access. Use a VPN/proxy or another provider)"
                    : code == HttpStatusCode.Unauthorized ? " (401: wrong or missing API key; check apiKey/apiKeyEnv)"
                    : code == HttpStatusCode.NotFound ? " (404: wrong baseUrl or model name)" : "";
                Log.Warn($"provider '{e.Config.Name}' paused for {pause:0}s: {JsonUtil.Trim(ex.Message, 200)}{hint}");
            }
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

    public void Dispose()
    {
        http.Dispose();
        foreach (var c in ownClients) c.Dispose();
    }
}
