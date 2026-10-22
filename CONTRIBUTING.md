# Contributing

## Build and test

You need the **.NET 10 SDK** (pinned in `global.json`) and the **.NET 8 runtime**. Docker is optional locally and
required in CI.

```bash
cd dotnet
dotnet build -c Release
dotnet test -c Release
```

| Test project | What it covers |
|---|---|
| `Tracescribe.Tests` | Core, readers, digest, query backends against fake HTTP (net8.0 and net10.0) |
| `Tracescribe.NetStandard.Tests` | The core suite against the **netstandard2.0** build, on the .NET 8 runtime |
| `Tracescribe.Integrations.Tests` | OpenTelemetry processors, chat middleware, MCP tools and CLI |
| `Tracescribe.Generators.Tests` | The `[Scribe]` source generator |
| `Tracescribe.Backends.Tests` | Real Jaeger, Tempo and Loki in containers. Skipped without Docker, unless `TRACESCRIBE_REQUIRE_BACKENDS=1` (CI sets it) |

CI also runs the sample, the CLI, a stdio MCP smoke test (`tests/mcp_smoke.py`) and installs the tool from its
package.

## Changing the conventions

Edit [`spec/attributes.yaml`](spec/attributes.yaml) and [`spec/SPEC.md`](spec/SPEC.md) together, then regenerate
the constants:

```bash
pip install pyyaml
python3 spec/tools/generate_dotnet.py
```

Every entry needs `stability: stable` or `stability: development`. Stable entries can't be renamed, removed or
retyped outside a major version ([spec section 10](spec/SPEC.md#10-versioning-and-stability)).

## Changing the public API

Each library's API is compared with the last release on nuget.org on every `dotnet pack`. A breaking change fails
the pack. If the break is intentional (only in a major version, or before 1.0), record it:

```bash
dotnet pack -c Release -p:ApiCompatGenerateSuppressionFile=true
```

and commit the updated `CompatibilitySuppressions.xml` with a note in [CHANGELOG.md](CHANGELOG.md).

## Pull requests

- Keep each PR to one change, with tests.
- Update `CHANGELOG.md` under the next version.
- Telemetry values that reach an agent must go through redaction and `TextSafety.OneLine`.

## Releasing

1. Set `VersionPrefix`/`VersionSuffix` in [`dotnet/Directory.Build.props`](dotnet/Directory.Build.props) and move
   the changelog entries under that version.
2. Merge to `main`, then create a GitHub release (or push a tag) named `v<version>`, e.g. `v1.0.0-rc.1`.
   [`release.yml`](.github/workflows/release.yml) builds, tests, packs and publishes to nuget.org.
3. After it's published, set `PackageValidationBaselineVersion` in `Directory.Build.props` to that version and
   delete the `CompatibilitySuppressions.xml` files.
