namespace Tracescribe.SemanticConventions;

/// <summary>
/// Selected OpenTelemetry GenAI attribute names that Tracescribe reads. Tracescribe never writes
/// <c>gen_ai.*</c> attributes; those belong to the GenAI semantic conventions.
/// </summary>
public static class GenAiAttributes
{
    /// <summary><c>gen_ai.operation.name</c></summary>
    public const string OperationName = "gen_ai.operation.name";

    /// <summary><c>gen_ai.tool.name</c></summary>
    public const string ToolName = "gen_ai.tool.name";

    /// <summary><c>gen_ai.agent.name</c></summary>
    public const string AgentName = "gen_ai.agent.name";
}
