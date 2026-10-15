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


JOBS = {
    "mechanic": ["механик", "mechanic"],
    "engineer": ["инженер", "engineer"],
    "medicaldoctor": ["медик", "врач", "доктор", "medic", "doctor"],
    "captain": ["капитан", "captain"],
    "assistant": ["ассистент", "помощник", "assistant"],
    "securityofficer": ["охран", "security"],
}


TR = dict(zip("абвгдеёзийклмнопрстуфхцыэ", "abvgdeeziiklmnoprstufhcye"))
TR.update({"ж": "zh", "ч": "ch", "ш": "sh", "щ": "sh", "ю": "yu", "я": "ya", "ъ": "", "ь": ""})


def latin(s: str) -> str:
    return "".join(TR.get(c, c) for c in s.lower())


def route(prompt: str) -> list[str]:
    """Диспетчер чата: имена из ростера, упомянутые в сообщении; иначе по профессии; иначе все."""
    message = prompt.split('"')[1].lower() if '"' in prompt else prompt.lower()
    roster = []
    for line in prompt.splitlines():
        if line.startswith("- ") and "| job:" in line:
            name = line[2:].split(" | ")[0]
            job = line.split("| job: ")[1].split(" | ")[0]
            roster.append((name, job))
    msg_latin = latin(message)
    named = [n for n, _ in roster if any(latin(part)[:3] in msg_latin for part in n.split() if len(part) >= 3)]
    if named:
        return named
    for job, words in JOBS.items():
        if any(w in message for w in words):
            return [n for n, j in roster if j == job]
    if any(w in message for w in ["все", "всем", "экипаж", "everyone", "all", "crew"]):
        return [n for n, _ in roster]
    return []


def decide(obs: dict) -> list[dict]:
    """Возвращает список {name, arguments}."""
    calls = []
    for order in obs.get("playerOrders", []):
        text = order.lower()
        calls.append({"name": "say", "arguments": {"text": "Есть, выполняю!"}})
        if "насос" in text or "pump" in text:
            calls.append({"name": "operate", "arguments": {"role": "pumps", "option": "pumpout"}})
        elif "реактор" in text or "reactor" in text:
            calls.append({"name": "operate", "arguments": {"role": "reactor"}})
        elif "течь" in text or "пробоин" in text or "leak" in text:
            calls.append({"name": "fix_leaks", "arguments": {}})
        else:
            rooms = [r["id"] for r in obs.get("rooms", [])]
            target = next((r for r in rooms if r.split(" [")[0].lower()[:5] in text), None)
            if target:
                calls.append({"name": "go_to_room", "arguments": {"room": target}})
        return calls[:3]
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
        last_user = next((m for m in reversed(body["messages"]) if m["role"] == "user"), {"content": ""})
        tool_names = [t["function"]["name"] for t in body.get("tools", [])]
        if "route_message" in tool_names:
            calls = [{"name": "route_message", "arguments": {"recipients": route(last_user["content"]), "reason": "mock"}}]
        else:
            try:
                calls = decide(json.loads(last_user["content"]))
            except Exception:
                calls = [{"name": "wait", "arguments": {}}]
        tool_calls = [
            {
                "id": "call_" + uuid.uuid4().hex[:8],
                "type": "function",
                "function": {"name": c["name"], "arguments": json.dumps(c["arguments"])},
            }
            for c in calls
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
