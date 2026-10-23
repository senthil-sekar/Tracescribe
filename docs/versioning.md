# Versioning and support

Tracescribe has two things you depend on, versioned separately: the **packages** and the **conventions**.

## Packages

All packages share one version and follow [SemVer](https://semver.org). From 1.0:

| Release | May change |
|---|---|
| Major (2.0) | Anything, with a migration note in the [changelog](../CHANGELOG.md) |
| Minor (1.1) | New APIs, new backends, new MCP tools, new target frameworks, dependency updates within their major |
| Patch (1.0.1) | Fixes only |

**How it's enforced.** On every `dotnet pack`, package validation compares each library's API with the last
release on nuget.org and fails on a breaking change that isn't explicitly recorded. It also checks that the
target frameworks inside one package expose the same API.

**Not covered by API compatibility:**

- Anything `internal`, and the `Tracescribe.Mcp` assembly (it's an application; its command line and MCP tool
  names follow the same SemVer rules as the API).
- The **text** of triage digests, failure lists and log searches. It's written for language models and may be
  reworded in any release. Parse the data types (`TriageReport`, `RootCauseCandidate`, `LogEntry`), not the text.

## Conventions

The attribute names your services emit are covered by [spec section 10](../spec/SPEC.md#10-versioning-and-stability):
`stable` items never change outside a major version; `development` items (today only
`tracescribe.chat.finish_reason`) may change in a minor version. Spans carry `tracescribe.schema.version`.

## Target frameworks

| Packages | Targets |
|---|---|
| `Tracescribe.SemanticConventions`, `Tracescribe`, `Tracescribe.OpenTelemetry`, `Tracescribe.Extensions.AI` | .NET Standard 2.0 (.NET Framework 4.6.2+), .NET 8, .NET 10 |
| `Tracescribe.Query`, `Tracescribe.Mcp` | .NET 8, .NET 10 |
| `Tracescribe.Generators` | Any project built with the .NET 8 SDK or later |

A .NET version is supported while Microsoft supports it. Dropping one is a major change.

## Dependencies

| Package | Minimum |
|---|---|
| `Tracescribe.OpenTelemetry` | OpenTelemetry 1.20.1 |
| `Tracescribe.Extensions.AI` | Microsoft.Extensions.AI 10.11.0 |
| `Tracescribe` on .NET Standard 2.0 | System.Diagnostics.DiagnosticSource 8.0.1, System.Text.Json 8.0.6 |

## Support

The latest minor version of the latest major gets fixes, including security fixes ([SECURITY.md](../SECURITY.md)).
