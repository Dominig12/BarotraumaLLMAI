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


ORDER_WORDS = [
    (["пожар", "туш", "fire"], "extinguish_fires"),
    (["течь", "течи", "пробоин", "leak"], "fix_leaks"),
    (["реактор", "reactor"], "operate"),
    (["насос", "pump"], "operate"),
]


def prioritized(text: str) -> list[dict]:
    """'сначала X, потом Y' -> X с приоритетом 3, Y с приоритетом 2 (выдаются в этом порядке)."""
    parts = [p for p in text.replace("затем", "потом").replace("then", "потом").split("потом")]
    calls, prio = [], 3
    for part in parts:
        for words, tool in ORDER_WORDS:
            if any(w in part for w in words):
                args = {"priority": prio}
                if tool == "operate":
                    args["role"] = "reactor" if "реактор" in part or "reactor" in part else "pumps"
                calls.append({"name": tool, "arguments": args})
                prio -= 1
                break
    return calls


def crew_names(obs: dict) -> list[str]:
    """crew: в компактном наблюдении строки "Имя (job) in ...", в полном — объекты."""
    return [c["name"] if isinstance(c, dict) else c.split(" (")[0] for c in obs.get("crew", [])]


def room_ids(obs: dict) -> list[str]:
    return [r["id"] if isinstance(r, dict) else r for r in obs.get("rooms", [])]


def decide(obs: dict) -> list[dict]:
    """Возвращает список {name, arguments}."""
    calls = []
    why = " ".join(obs.get("why", [])).lower()
    if "[control]" in why:
        granted = "handed you full control" in why
        calls.append({"name": "say", "arguments": {"text": "Принял командование." if granted else "Возвращаюсь к обычной службе."}})
        if granted:
            calls.append({"name": "order_crew", "arguments": {"to": "mechanic", "order": "Проверить и заварить все течи, приоритет 3"}})
        return calls
    if not obs.get("playerOrders"):
        if "[menu order]" in why:
            return [{"name": "carry_on", "arguments": {}}]
        if "[calm]" in why:
            return [{"name": "keep_gear", "arguments": {"on": False}}]
    for order in obs.get("playerOrders", []):
        text = order.lower()
        if "full control" in text:
            granted = "handed you full control" in text
            calls.append({"name": "say", "arguments": {"text": "Принял командование." if granted else "Возвращаюсь к обычной службе."}})
            if granted:
                calls.append({"name": "order_crew", "arguments": {"to": "mechanic", "order": "Проверить и заварить все течи, приоритет 3"}})
            return calls
        calls.append({"name": "say", "arguments": {"text": "Есть, выполняю!"}})
        if "приоритет" in text or "сначала" in text or "first" in text:
            return (calls + prioritized(text))[:3]
        crew = crew_names(obs)
        if "ко мне" in text or "сюда" in text or "come to me" in text:
            calls.append({"name": "come_to", "arguments": {"target": "Daniil"}})
        elif any(w in text for w in ["леч", "heal", "treat", "перевяж"]):
            target = next((n for n in crew if latin(n.split()[0])[:3] in latin(text)), obs.get("name"))
            item = "bandage" if ("бинт" in text or "bandage" in text) else None
            calls.append({"name": "treat", "arguments": {"target": target, **({"item": item} if item else {})}})
            if item:
                calls.append({"name": "note_preference", "arguments": {"player": "Daniil", "preference": "бережёт медикаменты: лечить бинтами, если хватает"}})
        elif any(w in text for w in ["топлив", "заряд", "refuel", "fuel"]):
            calls.append({"name": "refuel_reactor", "arguments": {}})
        elif any(w in text for w in ["скрафт", "сделай", "изготов", "craft"]):
            calls.append({"name": "craft", "arguments": {"item": "bandage", "amount": 2}})
        elif any(w in text for w in ["надень", "надеть", "экипир", "equip", "wear", "костюм"]):
            calls.append({"name": "equip", "arguments": {"item": "diving suit"}})
            calls.append({"name": "keep_gear", "arguments": {"on": True}})
        elif "насос" in text or "pump" in text:
            calls.append({"name": "operate", "arguments": {"role": "pumps", "option": "pumpout"}})
        elif "реактор" in text or "reactor" in text:
            calls.append({"name": "operate", "arguments": {"role": "reactor"}})
        elif "течь" in text or "пробоин" in text or "leak" in text:
            calls.append({"name": "fix_leaks", "arguments": {}})
        else:
            rooms = room_ids(obs)
            target = next((r for r in rooms if r.split(" [")[0].lower()[:5] in text), None)
            if target:
                calls.append({"name": "go_to_room", "arguments": {"room": target}})
        return calls[:3]
    if obs.get("inbox"):
        calls.append({"name": "say", "arguments": {"text": "Принял, " + obs["inbox"][0].split(":")[0] + "."}})
    if obs.get("threats"):
        calls.append({"name": "fight_intruders", "arguments": {}})
        crew = [c for c in obs.get("crew", []) if (c.get("healthPercent", 100) < 100 if isinstance(c, dict) else "health " in c)]
        if crew:
            calls.append({"name": "message_crew", "arguments": {"to": "all", "text": "Враг на борту, нужна помощь!"}})
    elif obs.get("leaks"):
        calls.append({"name": "fix_leaks", "arguments": {}})
    elif obs.get("roomFires", 0) > 0:
        calls.append({"name": "extinguish_fires", "arguments": {}})
    elif obs.get("healthPercent", 100) < 40:
        calls.append({"name": "say", "arguments": {"text": "Мне нужен медик!"}})
    if not calls:
        calls.append({"name": "carry_on", "arguments": {}})
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
        ollama = self.path.endswith("/api/chat")  # Ollama's native API (provider type "ollama")
        if not (self.path.endswith("/chat/completions") or ollama):
            return self._send(404, {"error": "not found"})
        body = json.loads(self.rfile.read(int(self.headers.get("Content-Length", 0))) or b"{}")
        last_user = next((m for m in reversed(body["messages"]) if m["role"] == "user"), {"content": ""})
        tool_names = [t["function"]["name"] for t in body.get("tools", [])]
        prompt_mode = not tool_names  # toolMode=prompt: tools are described in the system prompt, reply is JSON text
        system = body["messages"][0]["content"] if body.get("messages") else ""
        if "The round just ended" in system:
            events = [l[2:] for l in last_user["content"].splitlines() if l.startswith("- ")]
            text = "Раунд позади: " + "; ".join(events[-3:])[:250]
            if ollama:
                return self._send(200, {"message": {"role": "assistant", "content": text}, "prompt_eval_count": 100, "eval_count": 40})
            return self._send(200, {
                "id": "mock-" + uuid.uuid4().hex[:8], "object": "chat.completion",
                "choices": [{"index": 0, "finish_reason": "stop", "message": {"role": "assistant", "content": text}}],
                "usage": {"prompt_tokens": 100, "completion_tokens": 40, "total_tokens": 140},
            })
        if "route_message" in tool_names or (prompt_mode and "route_message" in system):
            calls = [{"name": "route_message", "arguments": {"recipients": route(last_user["content"]), "reason": "mock"}}]
        else:
            try:
                content = last_user["content"]
                if "Observation:\n" in content:  # an order header comes first
                    content = content.split("Observation:\n", 1)[1]
                calls = decide(json.loads(content))
            except Exception:
                calls = [{"name": "wait", "arguments": {}}]
        if ollama:
            msg = {"role": "assistant", "content": ""}
            if prompt_mode:
                msg["content"] = json.dumps({"calls": calls}, ensure_ascii=False)
            else:
                msg["tool_calls"] = [{"function": {"name": c["name"], "arguments": c["arguments"]}} for c in calls]
            return self._send(200, {"model": body.get("model"), "message": msg, "done": True,
                                    "prompt_eval_count": 100, "eval_count": 20})
        if prompt_mode:
            content = json.dumps({"calls": calls}, ensure_ascii=False)
            return self._send(200, {
                "id": "mock-" + uuid.uuid4().hex[:8], "object": "chat.completion",
                "choices": [{"index": 0, "finish_reason": "stop", "message": {"role": "assistant", "content": content}}],
                "usage": {"prompt_tokens": 100, "completion_tokens": 20, "total_tokens": 120},
            })
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
