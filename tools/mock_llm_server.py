#!/usr/bin/env python3
"""
Мок OpenAI-совместимого сервера для отладки мода без реальной модели.
Читает JSON-наблюдение из последнего user-сообщения и возвращает tool call по простым правилам.

    python3 tools/mock_llm_server.py [port]      # по умолчанию 11434 (порт Ollama)

Провайдер "local" в Content/llm_config.json уже смотрит на этот адрес.
Проверка без игры: dotnet run --project tools/LlmAgents.Sim -- --seconds 60 --speed 3
"""
import json
import sys
import uuid
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer


def decide(obs: dict) -> list[dict]:
    """Возвращает список {name, arguments}."""
    calls = []
    if obs.get("inbox"):
        calls.append({"name": "say", "arguments": {"text": "Принял, " + obs["inbox"][0].split(":")[0] + "."}})
    if obs.get("threats"):
        calls.append({"name": "fight_intruders", "arguments": {}})
        crew = [c for c in obs.get("crew", []) if c.get("healthPercent", 100) < 100]
        if crew:
            calls.append({"name": "message_crew", "arguments": {"to": "all", "text": "Враг на борту, нужна помощь!"}})
    elif obs.get("leaks"):
        calls.append({"name": "fix_leaks", "arguments": {}})
    elif obs.get("roomFires", 0) > 0:
        calls.append({"name": "extinguish_fires", "arguments": {}})
    elif obs.get("healthPercent", 100) < 40:
        calls.append({"name": "say", "arguments": {"text": "Мне нужен медик!"}})
    if not calls:
        calls.append({"name": "return_to_default", "arguments": {}})
    return calls[:3]


class Handler(BaseHTTPRequestHandler):
    def log_message(self, fmt, *args):
        sys.stderr.write("[mock] " + fmt % args + "\n")

    def _send(self, code, payload):
        data = json.dumps(payload).encode()
        self.send_response(code)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def do_POST(self):
        if not self.path.endswith("/chat/completions"):
            return self._send(404, {"error": "not found"})
        body = json.loads(self.rfile.read(int(self.headers.get("Content-Length", 0))) or b"{}")
        try:
            last_user = next(m for m in reversed(body["messages"]) if m["role"] == "user")
            obs = json.loads(last_user["content"])
        except Exception:
            obs = {}
        tool_calls = [
            {
                "id": "call_" + uuid.uuid4().hex[:8],
                "type": "function",
                "function": {"name": c["name"], "arguments": json.dumps(c["arguments"])},
            }
            for c in decide(obs)
        ]
        self._send(200, {
            "id": "mock-" + uuid.uuid4().hex[:8],
            "object": "chat.completion",
            "choices": [{"index": 0, "finish_reason": "tool_calls",
                         "message": {"role": "assistant", "content": None, "tool_calls": tool_calls}}],
            "usage": {"prompt_tokens": 100, "completion_tokens": 20, "total_tokens": 120},
        })


if __name__ == "__main__":
    port = int(sys.argv[1]) if len(sys.argv) > 1 else 11434
    print(f"mock LLM on http://127.0.0.1:{port}/v1")
    ThreadingHTTPServer(("127.0.0.1", port), Handler).serve_forever()
