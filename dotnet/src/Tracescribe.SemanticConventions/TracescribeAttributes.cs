namespace Tracescribe.SemanticConventions;

/// <summary>
/// Attribute names defined by the Tracescribe semantic conventions.
/// Mirrors <c>spec/attributes.yaml</c>.
/// </summary>
public static class TracescribeAttributes
{
    /// <summary>Version of the conventions this SDK emits.</summary>
    public const string SchemaVersionValue = "0.1.0";

    /// <summary>Prefix shared by every Tracescribe attribute.</summary>
    public const string Prefix = "tracescribe.";

    /// <summary><c>tracescribe.schema.version</c></summary>
    public const string SchemaVersion = "tracescribe.schema.version";

    /// <summary><c>tracescribe.intent</c>: stable dotted name of the business operation.</summary>
    public const string Intent = "tracescribe.intent";

    /// <summary><c>tracescribe.intent.description</c></summary>
    public const string IntentDescription = "tracescribe.intent.description";

    /// <summary><c>tracescribe.outcome.expected</c></summary>
    public const string OutcomeExpected = "tracescribe.outcome.expected";

    /// <summary><c>tracescribe.outcome.actual</c></summary>
    public const string OutcomeActual = "tracescribe.outcome.actual";

    /// <summary><c>tracescribe.outcome.status</c>; see <see cref="OutcomeStatusValues"/>.</summary>
    public const string OutcomeStatus = "tracescribe.outcome.status";

    /// <summary><c>tracescribe.outcome.matched</c></summary>
    public const string OutcomeMatched = "tracescribe.outcome.matched";

    /// <summary><c>tracescribe.failure.class</c>; see <see cref="FailureClassValues"/>.</summary>
    public const string FailureClass = "tracescribe.failure.class";

    /// <summary><c>tracescribe.failure.reason</c></summary>
    public const string FailureReason = "tracescribe.failure.reason";

    /// <summary><c>tracescribe.failure.retryable</c></summary>
    public const string FailureRetryable = "tracescribe.failure.retryable";

    /// <summary><c>tracescribe.failure.inferred</c></summary>
    public const string FailureInferred = "tracescribe.failure.inferred";

    /// <summary>Prefix for business context attributes, e.g. <c>tracescribe.context.claim_id</c>.</summary>
    public const string ContextPrefix = "tracescribe.context.";

    /// <summary><c>tracescribe.redacted</c></summary>
    public const string Redacted = "tracescribe.redacted";

    /// <summary><c>tracescribe.redacted.keys</c></summary>
    public const string RedactedKeys = "tracescribe.redacted.keys";

    /// <summary><c>tracescribe.link.reason</c>; see <see cref="LinkReasonValues"/>.</summary>
    public const string LinkReason = "tracescribe.link.reason";

    /// <summary>Builds a <c>tracescribe.context.&lt;key&gt;</c> attribute name.</summary>
    public static string Context(string key) => ContextPrefix + key;
}

/// <summary>Event names and their attributes.</summary>
public static class TracescribeEvents
{
    /// <summary><c>tracescribe.decision</c></summary>
    public const string Decision = "tracescribe.decision";

    /// <summary><c>tracescribe.decision.name</c></summary>
    public const string DecisionName = "tracescribe.decision.name";

    /// <summary><c>tracescribe.decision.chosen</c></summary>
    public const string DecisionChosen = "tracescribe.decision.chosen";

    /// <summary><c>tracescribe.decision.alternatives</c></summary>
    public const string DecisionAlternatives = "tracescribe.decision.alternatives";

    /// <summary><c>tracescribe.decision.rationale</c></summary>
    public const string DecisionRationale = "tracescribe.decision.rationale";

    /// <summary><c>tracescribe.hint</c></summary>
    public const string Hint = "tracescribe.hint";

    /// <summary><c>tracescribe.hint.text</c></summary>
    public const string HintText = "tracescribe.hint.text";
}

/// <summary>Allowed values of <c>tracescribe.outcome.status</c>.</summary>
public static class OutcomeStatusValues
{
    /// <summary>The operation achieved its intent.</summary>
    public const string Success = "success";
    /// <summary>The operation did not achieve its intent.</summary>
    public const string Failure = "failure";
    /// <summary>Some of the intent was achieved.</summary>
    public const string Partial = "partial";
    /// <summary>No outcome was recorded.</summary>
    public const string Unknown = "unknown";
}

/// <summary>Allowed values of <c>tracescribe.failure.class</c>.</summary>
public static class FailureClassValues
{
    /// <summary>A downstream service, database or queue failed.</summary>
    public const string Dependency = "dependency";
    /// <summary>A downstream call did not answer within its budget.</summary>
    public const string DependencyTimeout = "dependency.timeout";
    /// <summary>Input or state data was missing, malformed or inconsistent.</summary>
    public const string Data = "data";
    /// <summary>A business rule rejected the request.</summary>
    public const string Validation = "validation";
    /// <summary>Authorization, quota, compliance or guardrail block.</summary>
    public const string Policy = "policy";
    /// <summary>Ran without error but produced the wrong result.</summary>
    public const string Semantic = "semantic";
    /// <summary>Feature flag, setting, secret or deployment problem.</summary>
    public const string Configuration = "configuration";
    /// <summary>Bug in this component.</summary>
    public const string Internal = "internal";
    /// <summary>Unclassified.</summary>
    public const string Unknown = "unknown";
}

/// <summary>Allowed values of <c>tracescribe.link.reason</c>.</summary>
public static class LinkReasonValues
{
    /// <summary>The linked span caused this one.</summary>
    public const string CausedBy = "caused_by";
    /// <summary>This span retries the linked span.</summary>
    public const string RetryOf = "retry_of";
    /// <summary>This span undoes the linked span's effects.</summary>
    public const string Compensates = "compensates";
    /// <summary>This span logically follows the linked span.</summary>
    public const string FollowsFrom = "follows_from";
}

/// <summary>Selected OTel GenAI attribute names Tracescribe reads (never writes).</summary>
public static class GenAiAttributes
{
    /// <summary><c>gen_ai.operation.name</c></summary>
    public const string OperationName = "gen_ai.operation.name";
    /// <summary><c>gen_ai.tool.name</c></summary>
    public const string ToolName = "gen_ai.tool.name";
    /// <summary><c>gen_ai.agent.name</c></summary>
    public const string AgentName = "gen_ai.agent.name";
}
