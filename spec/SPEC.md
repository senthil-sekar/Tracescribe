# Tracescribe Semantic Conventions v0.4.0

**Status:** Release candidate for 1.0. Every attribute, event and metric is marked *stable* or *development*
(section 10); the stable set is what 1.0 will freeze.
Machine-readable registry: [`attributes.yaml`](attributes.yaml). SDK constants are generated from it
(`python3 spec/tools/generate_dotnet.py`), and CI fails if they drift.

## 1. Purpose

OpenTelemetry records *what happened*: spans, durations, status codes,
exceptions. An AI agent triaging an incident also needs to know:

1. **What was the code trying to do?** (intent)
2. **What should have happened, and did it?** (expected vs. actual outcome)
3. **What kind of failure is this?** (failure class)
4. **Which choices did the code make along the way?** (decisions)
5. **Which business entities are involved?** (context)

Tracescribe adds these as a small set of `tracescribe.*` attributes, events
and links. It **extends** OTel and the
[GenAI semantic conventions](https://opentelemetry.io/docs/specs/semconv/gen-ai/);
it never replaces or renames an existing OTel attribute.

## 2. Design principles

| Principle | Rule |
|---|---|
| OTel-native | Plain span attributes, events and links. Works with any OTLP backend. |
| Additive | Never redefine `gen_ai.*`, `http.*`, `db.*`, `exception.*`. |
| Low cardinality where it matters | `tracescribe.intent` and `failure.class` are enums or stable names; IDs go in `tracescribe.context.*`. |
| Safe by default | Context values are redacted before export unless explicitly allowed. |
| Token-efficient | Values are short, single-line and machine-parseable, so an agent can read a whole trace cheaply. |
| No hidden reasoning capture | Decisions are emitted *explicitly* by application code; we do not scrape model chain-of-thought. |

## 3. Attributes

### 3.1 Intent

| Attribute | Type | Requirement | Description |
|---|---|---|---|
| `tracescribe.intent` | string | Recommended on any span that represents a business operation | Stable dotted name, e.g. `claim.fnol.submit`. |
| `tracescribe.intent.description` | string | Opt-in | One-sentence description. |

**Naming.** An intent is a lower-case, dot-separated name, each segment matching `[a-z0-9_]+`, usually
`<domain>.<entity>.<action>`: `claim.fnol.submit`, `policy.quote.rate`, `payment.refund.issue`.
It names *what kind* of operation runs, never *which* one, so it stays low-cardinality and safe as a metric
dimension. Identifiers belong in `tracescribe.context.*`. Renaming an intent breaks every query and alert
built on it, so treat intents like API names.

### 3.2 Outcome

| Attribute | Type | Description |
|---|---|---|
| `tracescribe.outcome.expected` | string | Expected result, stated before work runs. |
| `tracescribe.outcome.actual` | string | What actually happened. |
| `tracescribe.outcome.status` | `success` \| `failure` \| `partial` \| `unknown` | Business-level result. |
| `tracescribe.outcome.matched` | bool | Did actual satisfy expected? |

**Semantic failure:** a span with OTel status `Unset`/`Ok` and
`tracescribe.outcome.matched = false`. These are the failures traditional
alerting misses; triage tooling must surface them.

If an intent span ends without an outcome, SDKs set
`tracescribe.outcome.status = unknown`.

### 3.3 Failure

| Attribute | Type | Description |
|---|---|---|
| `tracescribe.failure.class` | enum (see below) | Coarse category to route triage. |
| `tracescribe.failure.reason` | string | One-line, PII-free explanation. |
| `tracescribe.failure.retryable` | bool | Could a retry succeed? |
| `tracescribe.failure.inferred` | bool | `true` if the SDK inferred the class. |

`failure.class` values: `dependency`, `dependency.timeout`, `data`,
`validation`, `policy`, `semantic`, `configuration`, `internal`, `unknown`.

**Extending.** The set is closed: applications put detail in `tracescribe.failure.reason`, not in new
classes. A future minor version MAY add sub-values of the form `<class>.<detail>`, as `dependency.timeout`
refines `dependency`. Consumers MUST treat a value they don't recognise by its first segment
(`dependency.rate_limited` is a `dependency` failure, `semantic.x` is a semantic failure) and a first segment
they don't recognise as `unknown`. This keeps older tools correct when the set grows.

**Inference.** When a span ends with error status and no class, SDKs MAY
infer one from the exception type (for example a timeout exception maps to
`dependency.timeout`, and a failing `execute_tool` span maps to
`dependency`). Inferred values MUST set `tracescribe.failure.inferred = true`.

### 3.4 Context and redaction

`tracescribe.context.<key>` carries business identifiers
(`tracescribe.context.claim_id`, `tracescribe.context.policy_id`).

- Keys are `snake_case`.
- Values pass through a redaction policy before export. The default policy
  masks values that look like email addresses, phone numbers, payment card
  numbers and US SSNs, and masks any key on a configured deny-list.
- Redacted spans set `tracescribe.redacted = true` and list keys in
  `tracescribe.redacted.keys`.

### 3.5 Chat calls

| Attribute | Type | Description |
|---|---|---|
| `tracescribe.chat.finish_reason` | string | The model's finish reason on an intent span wrapping a chat call (`stop`, `length`, `tool_calls`, `content_filter`). |

*Development:* this attribute complements the OpenTelemetry GenAI conventions, which are not yet stable
upstream, so it may change in a minor version.

SDK middleware for chat clients SHOULD map finish reasons to outcomes:

| Finish reason | Outcome |
|---|---|
| `content_filter` | `failure`, `failure.class = policy` |
| `length` | `partial` (truncated) |
| anything else | the result of the configured outcome check (`success`, or a semantic failure) |

## 4. Events

### `tracescribe.decision`

Emitted when code takes a branch that changes downstream behavior.

| Attribute | Type |
|---|---|
| `tracescribe.decision.name` | string |
| `tracescribe.decision.chosen` | string |
| `tracescribe.decision.alternatives` | string[] |
| `tracescribe.decision.rationale` | string |

### `tracescribe.hint`

An author-supplied pointer for a future triager:
`tracescribe.hint.text = "If this fails in CA only, check flag rating.ca.v2"`.

## 5. Links

A span link MAY carry `tracescribe.link.reason` with one of `caused_by`,
`retry_of`, `compensates` or `follows_from`. This expresses causality that
parent/child nesting can't, such as a queue consumer failure caused by an
earlier producer span.

## 6. Relationship to GenAI conventions

Tracescribe attributes sit **alongside** `gen_ai.*` on the same spans:

```
invoke_agent claims-assistant        tracescribe.intent=claim.fnol.assist
├── chat gpt-x
├── execute_tool lookup_policy       tracescribe.outcome.status=success
└── execute_tool decode_vin          tracescribe.failure.class=dependency.timeout
```

## 7. Logs

Log records emitted while an intent span is active SHOULD carry `tracescribe.intent` with the value from
the nearest enclosing intent span. This lets an agent fetch every log line for a failing business
operation without knowing trace ids.

Log bodies and attribute values SHOULD pass through the same redaction policy as
`tracescribe.context.*`. Redacted records set `tracescribe.redacted = true`.

## 8. Metrics

Emitted on the `Tracescribe` meter when an intent span ends.

| Metric | Instrument | Unit | Attributes |
|---|---|---|---|
| `tracescribe.operation.count` | Counter | `{operation}` | `tracescribe.intent`, `tracescribe.outcome.status`, `tracescribe.failure.class` (when set) |
| `tracescribe.operation.duration` | Histogram | `s` | `tracescribe.intent`, `tracescribe.outcome.status` |

`tracescribe.outcome.status = failure` with `tracescribe.failure.class = semantic` on the counter is a
ready-made alert for semantic failures, which never show up in error-rate dashboards.

## 9. Triage digest (informative)

SDKs SHOULD provide a *digest*: a compact, plain-text rendering of a trace
for agents. It shows the span tree with intent, outcome and failure class,
followed by a ranked list of root-cause candidates (deepest failing spans
first, then semantic failures). This keeps a 200-span trace within a few
hundred tokens.

The digest is written for language models, not parsers. Its layout and wording are **not** covered by the
stability guarantees in section 10 and may change in any release.

## 10. Versioning and stability

Emitters set `tracescribe.schema.version` (semver) on intent spans, so consumers can tell which rules a span
follows.

Each attribute, event and metric in [`attributes.yaml`](attributes.yaml) has a stability:

| Stability | Guarantee |
|---|---|
| `stable` | Never renamed, removed, retyped or given a different meaning without a new **major** version. |
| `development` | May change in a **minor** version. Used where Tracescribe depends on conventions that are not stable upstream. |

From 1.0:

- **Major** versions are the only place stable items can break.
- **Minor** versions may add attributes, events, metrics, enum sub-values (section 3.3) and `development` items,
  and may promote `development` items to `stable`.
- **Patch** versions only clarify wording.
- `gen_ai.*`, `http.*`, `exception.*` and other OpenTelemetry names follow OpenTelemetry's own stability;
  Tracescribe never redefines them.

Before 1.0, breaking changes bump the minor version.

### Changes in 0.4.0

- Added a stability to every attribute, event and metric. All are `stable` except
  `tracescribe.chat.finish_reason` (`development`).
- Added intent naming rules (3.1), `failure.class` extension rules (3.3) and the versioning rules above.
- The triage digest format is explicitly outside the stability guarantees (9).
- No attribute was renamed or removed.

### Changes in 0.2.0

- Added `tracescribe.chat.finish_reason` and chat outcome mapping (3.5).
- Added log conventions (7) and metrics (8).
- `tracescribe.redacted` also applies to log records.
