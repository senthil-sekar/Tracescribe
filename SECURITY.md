# Security

## Reporting a vulnerability

Please report security issues privately through
[GitHub security advisories](https://github.com/senthil-sekar/Tracescribe/security/advisories/new), not in a
public issue. You should get a reply within five working days. Include the package and version, what an attacker
can do, and steps to reproduce.

Fixes ship in a patch release of the latest minor version.

## What Tracescribe protects, and what it doesn't

### Redaction is a safety net, not a compliance control

The default redactor masks values that *look like* email addresses, phone numbers, payment card numbers (Luhn
checked) and US social security numbers, and masks every value under a deny-listed key (`ssn`, `email`,
`date_of_birth`, `driver_license`, `card_number`, `password`, …). It runs on:

- `tracescribe.context.*` values and exception messages recorded by `Scribe`,
- log bodies and attributes, through `TracescribeLogProcessor`,
- everything the triage digest and `find_logs` render, even when the data was never enriched.

It will **not** catch PII it has no pattern for: names, street addresses, VINs, policy numbers in free text,
non-US identifiers. Keep PII out of span names, intents and free-text fields, add your own keys to
`RedactionOptions.DenyKeys`, or plug in your own `IRedactor`. For telemetry Tracescribe doesn't record itself,
use the redaction processors in [`collector/tracescribe-collector.yaml`](collector/tracescribe-collector.yaml).

### The MCP server

- **Read-only.** Every tool only queries the configured backend. Nothing is written, deleted or executed.
- **Credentials** come from the environment (`TRACESCRIBE_HEADER`, `TRACESCRIBE_LOGS_HEADER`) or, for Azure
  Monitor, from `DefaultAzureCredential`. They are sent only to the configured backend URL and never appear in
  tool output. Grant the identity read access only (for Azure, *Log Analytics Reader* on one workspace).
- **Telemetry is untrusted input to the model.** Span names, exception messages and log lines can contain text
  written by end users or attackers, and the tool results go straight into an AI agent's context. Tracescribe
  reduces every value to a single line without control characters, so content can't forge digest lines, and
  wraps every tool result in `<telemetry>` … `</telemetry>` with a reminder not to follow instructions inside it.
  This lowers the risk of prompt injection through telemetry; it can't remove it. Don't give an agent that reads
  production telemetry tools that act on production without human review.
- **Network.** The server talks to the configured backend only. It listens on stdio, not on a network port.

## Supported versions

| Version | Security fixes |
|---|---|
| Latest minor of the latest major | Yes |
| Anything older | No; upgrade |
