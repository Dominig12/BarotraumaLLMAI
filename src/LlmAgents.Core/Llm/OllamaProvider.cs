using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace LlmAgents.Llm;

/// <summary>
/// Ollama's native API (/api/chat). Unlike its OpenAI-compatible /v1 endpoint it honours
/// "think": false (Qwen3 & co. otherwise spend most of the time "thinking") and lets us set the context
/// length per request (options.num_ctx) — Ollama's default window is small and silently cuts long prompts,
/// so a bot may never see the order at the start of its observation.
/// </summary>
public sealed class OllamaProvider : ILlmProvider
{
    readonly ProviderConfig cfg;
    readonly HttpClient http;

    public string Name => cfg.Name;

    public OllamaProvider(ProviderConfig cfg, HttpClient http)
    {
        this.cfg = cfg;
        this.http = http;
    }

    /// <summary>"http://127.0.0.1:11434/v1" and "http://127.0.0.1:11434" both work.</summary>
    public static string ChatUrl(ProviderConfig cfg)
    {
        var b = cfg.BaseUrl.TrimEnd('/');
        if (b.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) b = b.Substring(0, b.Length - 3);
        if (b.EndsWith("/api", StringComparison.OrdinalIgnoreCase)) b = b.Substring(0, b.Length - 4);
        return b + "/api/chat";
    }

    public static JsonObject BuildBody(ProviderConfig cfg, LlmRequest req)
    {
        bool promptMode = cfg.ToolMode == "prompt" && req.Tools.Count > 0;
        var system = promptMode ? ToolPrompt.Build(req.System, req.Tools) : req.System;
        var messages = new JsonArray { new JsonObject { ["role"] = "system", ["content"] = system } };
        foreach (var t in req.Turns)
            messages.Add(new JsonObject { ["role"] = t.Role, ["content"] = t.Text });

        var options = new JsonObject
        {
            ["temperature"] = req.Temperature,
            ["num_predict"] = req.MaxOutputTokens,
        };
        if (cfg.ContextLength > 0) options["num_ctx"] = cfg.ContextLength;

        var body = new JsonObject
        {
            ["model"] = cfg.Model,
            ["messages"] = messages,
            ["stream"] = false,
            ["options"] = options,
        };
        if (cfg.Thinking == "off") body["think"] = false;
        else if (cfg.Thinking == "on") body["think"] = true;
        if (cfg.KeepAliveMinutes > 0) body["keep_alive"] = $"{cfg.KeepAliveMinutes}m";

        if (promptMode)
        {
            if (cfg.JsonResponseFormat) body["format"] = "json";
        }
        else if (req.Tools.Count > 0)
        {
            var tools = new JsonArray();
            foreach (var t in req.Tools)
                tools.Add(new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = t.Name,
                        ["description"] = t.Description,
                        ["parameters"] = t.Parameters.DeepClone(),
                    },
                });
            body["tools"] = tools;
        }
        JsonUtil.Merge(body, cfg.ExtraBody);
        return body;
    }

    public static LlmResponse ParseResponse(string raw, IReadOnlyList<ToolSpec> tools)
    {
        var root = JsonNode.Parse(raw) as JsonObject ?? throw new FormatException("response is not a JSON object");
        if (root["error"] is JsonNode err) throw new FormatException("provider error: " + JsonUtil.Trim(err.ToJsonString()));

        var message = root["message"];
        var text = Thinking.Strip(JsonUtil.Str(message?["content"]) ?? "");
        var calls = new List<ToolCall>();
        if (message?["tool_calls"] is JsonArray arr)
        {
            foreach (var c in arr)
            {
                var fn = c?["function"];
                var name = JsonUtil.Str(fn?["name"]);
                if (string.IsNullOrEmpty(name)) throw new FormatException("tool call is missing its name");
                calls.Add(new ToolCall(name!, NativeToolArguments.Read(fn?["arguments"], allowJsonString: true)));
            }
        }
        if (calls.Count == 0) calls = TextToolCallParser.Parse(text, tools);
        return new LlmResponse(text, calls, JsonUtil.Int(root["prompt_eval_count"]), JsonUtil.Int(root["eval_count"]),
            Truncated: JsonUtil.Str(root["done_reason"]) == "length");
    }


    public async Task<LlmResponse> CompleteAsync(LlmRequest req, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(cfg.TimeoutSeconds));
        string raw;
        try
        {
            using var msg = new HttpRequestMessage(HttpMethod.Post, ChatUrl(cfg))
            {
                Content = new StringContent(BuildBody(cfg, req).ToJsonString(), Encoding.UTF8, "application/json"),
            };
            foreach (var kv in cfg.ExtraHeaders) msg.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
            using var resp = await http.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            raw = await resp.Content.ReadCappedAsync(cts.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                var hint = resp.StatusCode == HttpStatusCode.NotFound && raw.Contains("model", StringComparison.OrdinalIgnoreCase)
                    ? $" (model not downloaded? run: ollama pull {cfg.Model})" : "";
                throw new HttpRequestException($"HTTP {(int)resp.StatusCode}: {JsonUtil.Trim(JsonUtil.OneLine(raw))}{hint}", null, resp.StatusCode);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"timed out after {cfg.TimeoutSeconds}s");
        }

        try { return ParseResponse(raw, req.Understood); }
        catch (JsonException e) { throw new FormatException("invalid JSON response: " + e.Message); }
    }
}

/// <summary>Reasoning models may put their thoughts into the answer text; tool calls are parsed without them.</summary>
public static class Thinking
{
    static readonly System.Text.RegularExpressions.Regex Block =
        new(@"<think(ing)?>.*?(</think(ing)?>|$)", System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    public static string Strip(string text) => Block.Replace(text, "").Trim();

    /// <summary>Qwen3's soft switch: works on any server that runs the model with its own chat template.</summary>
    public const string NoThinkSwitch = "/no_think";
}
