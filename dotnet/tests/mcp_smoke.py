#!/usr/bin/env python3
"""End-to-end check of the Tracescribe MCP server over stdio.

Usage: python3 tests/mcp_smoke.py <otlp-json-file> -- <command to start the server...>

Starts the server, performs the MCP handshake, lists tools and calls each one against the trace file.
Exits non-zero on any protocol error or unexpected result.
"""
import json
import queue
import subprocess
import sys
import threading

TIMEOUT_S = 60


def main() -> int:
    sep = sys.argv.index("--")
    trace_file = sys.argv[1]
    command = sys.argv[sep + 1:] + ["--path", trace_file]

    with open(trace_file, encoding="utf-8") as f:
        trace_id = json.load(f)["resourceSpans"][0]["scopeSpans"][0]["spans"][0]["traceId"]

    proc = subprocess.Popen(command, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=sys.stderr, text=True, bufsize=1)
    lines: "queue.Queue[str]" = queue.Queue()
    threading.Thread(target=lambda: [lines.put(l) for l in proc.stdout], daemon=True).start()

    def send(msg: dict) -> None:
        proc.stdin.write(json.dumps(msg) + "\n")
        proc.stdin.flush()

    def request(id_: int, method: str, params: dict | None = None) -> dict:
        send({"jsonrpc": "2.0", "id": id_, "method": method, "params": params or {}})
        while True:
            msg = json.loads(lines.get(timeout=TIMEOUT_S))
            if msg.get("id") == id_:
                if "error" in msg:
                    raise RuntimeError(f"{method} failed: {msg['error']}")
                return msg["result"]

    def call(id_: int, name: str, arguments: dict) -> str:
        result = request(id_, "tools/call", {"name": name, "arguments": arguments})
        text = "".join(c.get("text", "") for c in result.get("content", []))
        if result.get("isError"):
            raise RuntimeError(f"{name} returned an error: {text}")
        print(f"--- {name} {json.dumps(arguments)}\n{text}\n")
        return text

    try:
        init = request(1, "initialize", {
            "protocolVersion": "2025-06-18",
            "capabilities": {},
            "clientInfo": {"name": "tracescribe-smoke", "version": "1.0"},
        })
        assert init["serverInfo"]["name"] == "tracescribe", init
        send({"jsonrpc": "2.0", "method": "notifications/initialized"})

        tools = {t["name"] for t in request(2, "tools/list")["tools"]}
        assert {"get_trace_digest", "find_failures", "list_services"} <= tools, tools

        assert "claims-api" in call(3, "list_services", {})
        failures = call(4, "find_failures", {"kind": "semantic"})
        assert trace_id in failures, failures
        digest = call(5, "get_trace_digest", {"traceId": trace_id})
        assert "ROOT CAUSE CANDIDATES" in digest and "execute_tool decode_vin" in digest, digest
        print("MCP smoke test passed.")
        return 0
    finally:
        proc.stdin.close()
        proc.terminate()
        proc.wait(timeout=10)


if __name__ == "__main__":
    sys.exit(main())
