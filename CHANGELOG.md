# Changelog

All notable changes to the Tracescribe packages and conventions. Versions follow [SemVer](https://semver.org);
see [docs/versioning.md](docs/versioning.md) for what each kind of release may change.

## 1.0.0-rc.1

Release candidate. The API and the stable conventions are what 1.0.0 will ship unless the release candidate
turns up a problem.

### Added
- Integration tests against real **Jaeger 1.76, Grafana Tempo 2.9 and Grafana Loki 3.5** (Testcontainers),
  run in CI on every change.
- Benchmarks (`dotnet/benchmarks`) and published overhead numbers ([docs/performance.md](docs/performance.md)).
- Documentation: [getting started](docs/getting-started.md), [backends](docs/backends.md),
  [versioning](docs/versioning.md), [performance](docs/performance.md), [SECURITY.md](SECURITY.md),
  [CONTRIBUTING.md](CONTRIBUTING.md).

### Changed
- **MCP tool output is marked as untrusted data.** Every tool result is wrapped in `<telemetry>` … `</telemetry>`
  with a one-line reminder not to follow instructions inside it.
- **Telemetry text is reduced to single, safe lines** in digests, failure lists, log searches and service lists:
  line breaks, Unicode line separators and control characters (such as terminal escapes) become spaces. A span
  name or log line can no longer forge digest lines.

### Fixed
- Jaeger: `tracescribe.decision.alternatives` and `tracescribe.redacted.keys` came back as a JSON string
  (`[["a","b"]]`) because Jaeger has no array type. They are arrays again. Found by the new Jaeger tests.

## 0.4.0

Groundwork for 1.0: every breaking cleanup in one release.

### Added
- Target frameworks: `netstandard2.0` (for .NET Framework 4.6.2+ services), `net8.0` and `net10.0` for
  `Tracescribe.SemanticConventions`, `Tracescribe`, `Tracescribe.OpenTelemetry` and `Tracescribe.Extensions.AI`;
  `net8.0` and `net10.0` for `Tracescribe.Query` and `Tracescribe.Mcp`.
- Package validation against the previous release on every pack.
- Spec 0.4.0: a stability (`stable` or `development`) for every attribute, event and metric; intent naming
  rules; the `failure.class` extension rule; the versioning policy.

### Changed
- OpenTelemetry 1.9 → 1.20.1; ModelContextProtocol 1.4 → 2.3. Versions are pinned centrally.
- Building from source needs the .NET 10 SDK (`global.json`).
- `failure.class` values such as `semantic.<detail>` count as their first segment, in the SDK and the Azure
  Monitor search.

### Breaking
- `HttpBackend` and `HttpTraceBackend` are gone; backends implement `ITraceBackend` / `ILogBackend` directly.
  Custom backends should do the same.
- `TempoBackend.TraceQl`, `LokiLogBackend.LogQl`/`Label`/`Read` and `AzureMonitorBackend.SearchKql`/`TraceKql`/
  `LogsKql` are internal.
- The `Tracescribe.Mcp` assembly's types are internal (it is an application, not a library).

## 0.3.0-alpha

First release on nuget.org.

### Added
- `find_logs` MCP tool and `logs` CLI command keyed by `tracescribe.intent`, over Loki, Azure Monitor and
  OTLP/JSON log files.
- Azure Monitor / Application Insights backend (KQL over a Log Analytics workspace, Entra ID sign-in).
- Loki log backend; `LogEntry`, OTLP/JSON log reader and writer, `LogRecord.ToLogEntry()`.
- NuGet packaging with READMEs, symbols and SourceLink; tag-driven release with trusted publishing;
  `Tracescribe.Mcp` as a .NET tool.

## 0.2.0

Not published to nuget.org.

- MCP server and CLI over Jaeger, Tempo and OTLP/JSON files.
- `IChatClient` middleware, `[Scribe]` source generator, log and metric conventions, redaction.
