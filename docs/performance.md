# Performance

Tracescribe runs inside your services, so its cost matters. The numbers below come from
[`dotnet/benchmarks`](../dotnet/benchmarks/Tracescribe.Benchmarks/Program.cs): every benchmark goes through a real
OpenTelemetry `TracerProvider` that samples everything and exports nothing, so they measure in-process cost, not
export.

**Bottom line:** a Tracescribe operation costs about 0.4 µs more than a plain OpenTelemetry span, or about 2 µs
with three context values, a decision and a mismatch, most of which is redaction. A single database or HTTP call
takes thousands of times longer.

## Spans

| Operation | Mean | Allocated |
|---|---:|---:|
| Plain OpenTelemetry span + 1 tag (baseline) | 0.40 µs | 856 B |
| `Scribe.Begin` with intent and expected outcome, `Succeed` | 0.81 µs | 1,224 B |
| … plus 3 context values, a decision and a mismatch | 2.16 µs | 2,672 B |
| `Scribe.Begin`, `Fail(exception)` with the failure class inferred | 1.83 µs | 1,792 B |
| Plain span with error status, enriched by `TracescribeProcessor` | 0.61 µs | 1,112 B |

## Redaction (per value)

| Value | Mean | Allocated |
|---|---:|---:|
| 40 characters, no PII | 0.35 µs | 0 B |
| 60 characters with an email address | 0.49 µs | 136 B |
| Deny-listed key | 0.01 µs | 0 B |

Redaction runs on context values, exception messages and log records. The default redactor scans for four
patterns; a custom `IRedactor` with fewer checks, or `RedactionOptions.DetectPatterns = false` with a deny-list,
is cheaper.

## Triage digest

| Trace | Mean | Allocated |
|---|---:|---:|
| 10 spans | 9.6 µs | 14 KB |
| 200 spans | 88 µs | 129 KB |

Digests are rendered by the MCP server and the CLI when an agent asks, not in your services.

## Environment and how to reproduce

BenchmarkDotNet 0.15.8, ShortRun job, .NET 10.0.13, Linux, Intel Xeon 2.10 GHz, 4 cores, shared cloud VM. Treat
the numbers as orders of magnitude; run them on your own hardware for yours:

```bash
cd dotnet
dotnet run -c Release --project benchmarks/Tracescribe.Benchmarks -- --filter '*'
```
