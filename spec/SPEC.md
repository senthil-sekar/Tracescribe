# Tracescribe Semantic Conventions v0.1.0

**Status:** Development. Names and semantics may change before 1.0.
Machine-readable registry: [`attributes.yaml`](attributes.yaml).

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

## 7. Triage digest (informative)

SDKs SHOULD provide a *digest*: a compact, plain-text rendering of a trace
for agents. It shows the span tree with intent, outcome and failure class,
followed by a ranked list of root-cause candidates (deepest failing spans
first, then semantic failures). This keeps a 200-span trace within a few
hundred tokens.

## 8. Versioning

Emitters set `tracescribe.schema.version` on intent spans. Breaking changes
bump the minor version while in Development, and the major version after 1.0.
