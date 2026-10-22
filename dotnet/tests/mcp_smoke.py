#!/usr/bin/env python3
"""End-to-end check of the Tracescribe MCP server over stdio.

Usage: python3 tests/mcp_smoke.py <otlp-json-file> -- <command to start the server...>

Starts the server, performs the MCP handshake, lists tools and calls each one against the trace file plus a
small OTLP/JSON logs file written next to it. Exits non-zero on any protocol error or unexpected result.
"""
import json
import os
import queue
import shutil
import subprocess
import sys
import tempfile
import threading

TIMEOUT_S = 60


def main() -> int:
    sep = sys.argv.index("--")
    trace_file = sys.argv[1]

    with open(trace_file, encoding="utf-8") as f:
        trace_id = json.load(f)["resourceSpans"][0]["scopeSpans"][0]["spans"][0]["traceId"]

    data_dir = tempfile.mkdtemp(prefix="tracescribe-smoke-")
    shutil.copy(trace_file, os.path.join(data_dir, "traces.json"))
    with open(os.path.join(data_dir, "logs.json"), "w", encoding="utf-8") as f:
        json.dump(logs_for(trace_id), f)
    command = sys.argv[sep + 1:] + ["--path", data_dir]

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
        assert {"get_trace_digest", "find_failures", "find_logs", "list_services"} <= tools, tools

        assert "claims-api" in call(3, "list_services", {})
        failures = call(4, "find_failures", {"kind": "semantic"})
        assert trace_id in failures, failures
        digest = call(5, "get_trace_digest", {"traceId": trace_id})
        assert "ROOT CAUSE CANDIDATES" in digest and "execute_tool decode_vin" in digest, digest
        logs = call(6, "find_logs", {"intent": "claim.fnol.submit", "minSeverity": "warn"})
        assert "1 log record for intent=claim.fnol.submit" in logs and "VIN decoder timed out" in logs, logs
        assert "[REDACTED:email]" in logs and "jane.doe" not in logs, logs
        print("MCP smoke test passed.")
        return 0
    finally:
        proc.stdin.close()
        proc.terminate()
        proc.wait(timeout=10)
        shutil.rmtree(data_dir, ignore_errors=True)


def logs_for(trace_id: str) -> dict:
    """Two OTLP log records in the claim.fnol.submit operation: one INFO, one WARN that mentions an email."""
    def record(time_ns: int, severity: int, body: str) -> dict:
        return {
            "timeUnixNano": str(time_ns),
            "severityNumber": severity,
            "body": {"stringValue": body},
            "traceId": trace_id,
            "attributes": [{"key": "tracescribe.intent", "value": {"stringValue": "claim.fnol.submit"}}],
        }

    return {"resourceLogs": [{
        "resource": {"attributes": [{"key": "service.name", "value": {"stringValue": "claims-api"}}]},
        "scopeLogs": [{"logRecords": [
            record(1760000000000000000, 9, "Claim received"),
            record(1760000000050000000, 13, "VIN decoder timed out; notifying jane.doe@example.com"),
        ]}],
    }]}


if __name__ == "__main__":
    sys.exit(main())
