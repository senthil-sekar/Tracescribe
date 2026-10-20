using System.Diagnostics;
using System.Globalization;
using System.Text;
using Tracescribe.SemanticConventions;

namespace Tracescribe.Digest;

/// <summary>Options for <see cref="TriageDigest"/>.</summary>
public sealed class TriageDigestOptions
{
    /// <summary>Render subtrees that contain no issues. Off by default to save tokens.</summary>
    public bool ExpandHealthy { get; set; }

    /// <summary>Maximum characters for any rendered value.</summary>
    public int MaxValueLength { get; set; } = 160;

    /// <summary>Render <c>tracescribe.context.*</c> values.</summary>
    public bool IncludeContext { get; set; } = true;

    /// <summary>
    /// Redactor applied to every rendered value, so digests stay safe even when built from raw backend
    /// data that was never enriched. Defaults to <see cref="TracescribeRedaction.Default"/>; <c>null</c> disables.
    /// </summary>
    public IRedactor? Redactor { get; set; } = TracescribeRedaction.Default;
}

/// <summary>Health of a span as seen by triage.</summary>
public enum SpanHealth
{
    /// <summary>No problem recorded.</summary>
    Ok,
    /// <summary>Intent span that ended without an outcome.</summary>
    Unknown,
    /// <summary>Partially achieved its intent.</summary>
    Partial,
    /// <summary>Completed without error but produced the wrong result.</summary>
    Semantic,
    /// <summary>Errored or explicitly failed.</summary>
    Failed,
}

/// <summary>A span ranked as a likely root cause.</summary>
/// <param name="Rank">1 = most likely.</param>
/// <param name="SpanId">Span id.</param>
/// <param name="Name">Span name.</param>
/// <param name="Health">Failure kind.</param>
/// <param name="FailureClass">Failure class, if any.</param>
/// <param name="Inferred">Whether the class was inferred.</param>
/// <param name="Reason">Failure reason.</param>
/// <param name="Retryable">Whether retrying could succeed.</param>
/// <param name="Path">Span names from root to this span.</param>
/// <param name="ExceptionMessage">First exception, if any.</param>
/// <param name="Context">Business context.</param>
/// <param name="Service">Service that emitted the span, when known.</param>
public sealed record RootCauseCandidate(
    int Rank,
    string SpanId,
    string Name,
    SpanHealth Health,
    string? FailureClass,
    bool Inferred,
    string? Reason,
    bool? Retryable,
    string Path,
    string? ExceptionMessage,
    IReadOnlyDictionary<string, string> Context,
    string? Service = null);

/// <summary>Digest of one trace.</summary>
/// <param name="TraceId">Trace id.</param>
/// <param name="SpanCount">Spans in the trace.</param>
/// <param name="FailedCount">Spans with <see cref="SpanHealth.Failed"/>.</param>
/// <param name="SemanticCount">Spans with <see cref="SpanHealth.Semantic"/>.</param>
/// <param name="Duration">Wall-clock span of the trace.</param>
/// <param name="RootCauses">Ranked root-cause candidates.</param>
/// <param name="Text">Agent-readable rendering.</param>
public sealed record TriageReport(
    string TraceId,
    int SpanCount,
    int FailedCount,
    int SemanticCount,
    TimeSpan Duration,
    IReadOnlyList<RootCauseCandidate> RootCauses,
    string Text);

/// <summary>
/// Turns raw spans into a compact, plain-text digest an AI agent can triage from: the span tree with
/// intent, outcomes, failure classes and decisions, healthy subtrees collapsed, followed by ranked
/// root-cause candidates.
/// </summary>
public static class TriageDigest
{
    /// <summary>Builds one report per trace, ordered by trace start time.</summary>
    public static IReadOnlyList<TriageReport> Build(IEnumerable<SpanRecord> spans, TriageDigestOptions? options = null)
    {
        var o = options ?? new TriageDigestOptions();
        return spans
            .GroupBy(s => s.TraceId)
            .OrderBy(g => g.Min(s => s.StartTime))
            .Select(g => BuildTrace(g.Key, g.ToList(), o))
            .ToList();
    }

    /// <summary>Renders all traces in <paramref name="spans"/> as one text block.</summary>
    public static string Render(IEnumerable<SpanRecord> spans, TriageDigestOptions? options = null) =>
        string.Join(Environment.NewLine, Build(spans, options).Select(r => r.Text));

    /// <summary>Classifies a single span.</summary>
    public static SpanHealth HealthOf(SpanRecord span)
    {
        var status = span.Get(TracescribeAttributes.OutcomeStatus);
        var failureClass = span.Get(TracescribeAttributes.FailureClass);
        var matched = span.Get(TracescribeAttributes.OutcomeMatched);

        if (span.Status == ActivityStatusCode.Error)
        {
            return SpanHealth.Failed;
        }

        if (status == OutcomeStatusValues.Partial)
        {
            return SpanHealth.Partial;
        }

        if (failureClass == FailureClassValues.Semantic || matched == "false")
        {
            return SpanHealth.Semantic;
        }

        if (status == OutcomeStatusValues.Failure)
        {
            return SpanHealth.Failed;
        }

        return status == OutcomeStatusValues.Unknown ? SpanHealth.Unknown : SpanHealth.Ok;
    }

    private sealed class Node(SpanRecord span)
    {
        public SpanRecord Span { get; } = span;
        public SpanHealth Health { get; } = HealthOf(span);
        public List<Node> Children { get; } = new();
        public Node? Parent { get; set; }
        public bool HasIssue { get; set; }
        public bool HasFailedDescendant { get; set; }
        public int DescendantCount { get; set; }
    }

    private static TriageReport BuildTrace(string traceId, List<SpanRecord> spans, TriageDigestOptions o)
    {
        var nodes = spans.ToDictionary(s => s.SpanId, s => new Node(s));
        var roots = new List<Node>();
        foreach (var n in nodes.Values)
        {
            if (n.Span.ParentSpanId is { } p && nodes.TryGetValue(p, out var parent))
            {
                n.Parent = parent;
                parent.Children.Add(n);
            }
            else
            {
                roots.Add(n);
            }
        }

        foreach (var n in nodes.Values)
        {
            n.Children.Sort((a, b) => a.Span.StartTime.CompareTo(b.Span.StartTime));
        }

        roots.Sort((a, b) => a.Span.StartTime.CompareTo(b.Span.StartTime));
        foreach (var r in roots)
        {
            Annotate(r);
        }

        var start = spans.Min(s => s.StartTime);
        var end = spans.Max(s => s.StartTime + s.Duration);
        var failed = nodes.Values.Count(n => n.Health == SpanHealth.Failed);
        var semantic = nodes.Values.Count(n => n.Health == SpanHealth.Semantic);
        var rootCauses = RankRootCauses(nodes.Values, o);

        var sb = new StringBuilder();
        sb.Append("TRACE ").Append(traceId)
          .Append(" | ").Append(spans.Count).Append(" spans")
          .Append(" | ").Append(failed).Append(" failed, ").Append(semantic).Append(" semantic")
          .Append(" | ").Append(FormatDuration(end - start))
          .Append(" | root: ").Append(string.Join(", ", roots.Select(r => r.Span.Name)))
          .AppendLine();

        foreach (var r in roots)
        {
            RenderNode(sb, r, prefix: string.Empty, isLast: true, isRoot: true, o);
        }

        sb.AppendLine();
        if (rootCauses.Count == 0)
        {
            sb.AppendLine("ROOT CAUSE CANDIDATES: none (no failed, semantic or partial spans)");
        }
        else
        {
            sb.AppendLine("ROOT CAUSE CANDIDATES (most likely first):");
            foreach (var c in rootCauses)
            {
                sb.Append(c.Rank).Append(". ").Append(c.Name)
                  .Append(" [").Append(HealthLabel(c.Health)).Append(']');
                if (c.Service is not null)
                {
                    sb.Append(" svc=").Append(c.Service);
                }

                if (c.FailureClass is not null)
                {
                    sb.Append(" class=").Append(c.FailureClass).Append(c.Inferred ? "(inferred)" : string.Empty);
                }

                if (c.Retryable is { } rt)
                {
                    sb.Append(" retryable=").Append(rt ? "true" : "false");
                }

                sb.AppendLine();
                if (c.Reason is not null)
                {
                    sb.Append("   reason: ").AppendLine(c.Reason);
                }

                if (c.ExceptionMessage is not null)
                {
                    sb.Append("   exception: ").AppendLine(c.ExceptionMessage);
                }

                sb.Append("   path: ").AppendLine(c.Path);
                if (c.Context.Count > 0)
                {
                    sb.Append("   context: ").AppendLine(string.Join(", ", c.Context.Select(kv => $"{kv.Key}={kv.Value}")));
                }
            }
        }

        return new TriageReport(traceId, spans.Count, failed, semantic, end - start, rootCauses, sb.ToString());
    }

    private static void Annotate(Node n)
    {
        var issue = n.Health is SpanHealth.Failed or SpanHealth.Semantic or SpanHealth.Partial;
        var count = 0;
        var failedBelow = false;
        foreach (var c in n.Children)
        {
            Annotate(c);
            issue |= c.HasIssue;
            failedBelow |= c.Health == SpanHealth.Failed || c.HasFailedDescendant;
            count += 1 + c.DescendantCount;
        }

        n.HasIssue = issue;
        n.HasFailedDescendant = failedBelow;
        n.DescendantCount = count;
    }

    private static List<RootCauseCandidate> RankRootCauses(IEnumerable<Node> nodes, TriageDigestOptions o)
    {
        // Deepest failures first: a failed span with no failed descendants is where the failure originated.
        var ordered = nodes
            .Where(n => n.Health is SpanHealth.Failed && !n.HasFailedDescendant)
            .OrderBy(n => n.Span.StartTime)
            .Concat(nodes.Where(n => n.Health is SpanHealth.Semantic).OrderBy(n => n.Span.StartTime))
            .Concat(nodes.Where(n => n.Health is SpanHealth.Partial && !n.HasFailedDescendant).OrderBy(n => n.Span.StartTime));

        var rank = 0;
        return ordered.Select(n => new RootCauseCandidate(
            ++rank,
            n.Span.SpanId,
            n.Span.Name,
            n.Health,
            n.Span.Get(TracescribeAttributes.FailureClass),
            n.Span.Get(TracescribeAttributes.FailureInferred) == "true",
            Truncate(n.Span.Get(TracescribeAttributes.FailureReason) ?? n.Span.StatusDescription, o),
            ParseBool(n.Span.Get(TracescribeAttributes.FailureRetryable)),
            PathOf(n),
            Truncate(FirstException(n.Span), o),
            o.IncludeContext ? ContextOf(n, o) : new Dictionary<string, string>(),
            n.Span.Service)).ToList();
    }

    private static void RenderNode(StringBuilder sb, Node n, string prefix, bool isLast, bool isRoot, TriageDigestOptions o)
    {
        var branch = isRoot ? string.Empty : isLast ? "└─ " : "├─ ";
        var childPrefix = isRoot ? string.Empty : prefix + (isLast ? "   " : "│  ");
        var s = n.Span;

        sb.Append(prefix).Append(branch).Append('[').Append(HealthLabel(n.Health)).Append("] ").Append(s.Name)
          .Append(' ').Append(FormatDuration(s.Duration));

        if (s.Service is not null && (n.Parent is null || n.Parent.Span.Service != s.Service))
        {
            sb.Append(" svc=").Append(s.Service);
        }

        var intent = s.Get(TracescribeAttributes.Intent);
        if (intent is not null && intent != s.Name)
        {
            sb.Append(" intent=").Append(intent);
        }

        if (s.Get(GenAiAttributes.OperationName) is { } op)
        {
            sb.Append(" gen_ai=").Append(op);
            if (s.Get(GenAiAttributes.ToolName) is { } tool)
            {
                sb.Append(':').Append(tool);
            }
        }

        if (s.Get(TracescribeAttributes.FailureClass) is { } fc)
        {
            sb.Append(" class=").Append(fc);
            if (s.Get(TracescribeAttributes.FailureInferred) == "true")
            {
                sb.Append("(inferred)");
            }
        }

        var collapse = !o.ExpandHealthy && !n.HasIssue && n.Children.Count > 0;
        if (collapse)
        {
            sb.Append(" (+").Append(n.DescendantCount).Append(" healthy spans)");
        }

        if (n.Parent is null && s.ParentSpanId is not null)
        {
            sb.Append(" (parent not captured)");
        }

        sb.AppendLine();

        var detail = childPrefix + (n.Children.Count > 0 && !collapse ? "│    " : "     ");
        if (n.Health != SpanHealth.Ok || o.ExpandHealthy)
        {
            AppendDetail(sb, detail, "expected", s.Get(TracescribeAttributes.OutcomeExpected), o);
            AppendDetail(sb, detail, "actual", s.Get(TracescribeAttributes.OutcomeActual), o);
            AppendDetail(sb, detail, "reason", s.Get(TracescribeAttributes.FailureReason), o);
            AppendDetail(sb, detail, "exception", FirstException(s), o);
        }

        foreach (var e in s.Events)
        {
            if (e.Name == TracescribeEvents.Decision)
            {
                var name = SpanRecord.Format(e.Attributes.GetValueOrDefault(TracescribeEvents.DecisionName));
                var chosen = SpanRecord.Format(e.Attributes.GetValueOrDefault(TracescribeEvents.DecisionChosen));
                var alts = SpanRecord.Format(e.Attributes.GetValueOrDefault(TracescribeEvents.DecisionAlternatives));
                var why = SpanRecord.Format(e.Attributes.GetValueOrDefault(TracescribeEvents.DecisionRationale));
                var text = $"{name} chose={chosen}" + (alts is null ? string.Empty : $" alts=[{alts}]") + (why is null ? string.Empty : $" why: {why}");
                AppendDetail(sb, detail, "decision", text, o);
            }
            else if (e.Name == TracescribeEvents.Hint)
            {
                AppendDetail(sb, detail, "hint", SpanRecord.Format(e.Attributes.GetValueOrDefault(TracescribeEvents.HintText)), o);
            }
        }

        if (o.IncludeContext && n.Parent is null)
        {
            var ctx = OwnContext(s, o);
            if (ctx.Count > 0)
            {
                AppendDetail(sb, detail, "context", string.Join(", ", ctx.Select(kv => $"{kv.Key}={kv.Value}")), o);
            }
        }

        if (collapse)
        {
            return;
        }

        RenderChildren(sb, n.Children, childPrefix, o);
    }

    private static void RenderChildren(StringBuilder sb, List<Node> children, string prefix, TriageDigestOptions o)
    {
        // Collapse runs of identical healthy leaves (e.g. 12 cache lookups) into one line.
        var i = 0;
        while (i < children.Count)
        {
            var c = children[i];
            var j = i + 1;
            if (!o.ExpandHealthy && IsQuietLeaf(c))
            {
                while (j < children.Count && IsQuietLeaf(children[j]) && children[j].Span.Name == c.Span.Name)
                {
                    j++;
                }
            }

            var isLast = j >= children.Count;
            if (j - i > 1)
            {
                var total = children.Skip(i).Take(j - i).Aggregate(TimeSpan.Zero, (t, x) => t + x.Span.Duration);
                sb.Append(prefix).Append(isLast ? "└─ " : "├─ ").Append("[OK] ").Append(c.Span.Name)
                  .Append(" x").Append(j - i).Append(" total ").AppendLine(FormatDuration(total));
            }
            else
            {
                RenderNode(sb, c, prefix, isLast, isRoot: false, o);
            }

            i = j;
        }
    }

    private static bool IsQuietLeaf(Node n) => n.Health == SpanHealth.Ok && n.Children.Count == 0 && n.Span.Events.Count == 0;

    private static void AppendDetail(StringBuilder sb, string prefix, string label, string? value, TriageDigestOptions o)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        sb.Append(prefix).Append(label).Append(": ").AppendLine(Truncate(value, o));
    }

    private static string HealthLabel(SpanHealth h) => h switch
    {
        SpanHealth.Failed => "FAIL",
        SpanHealth.Semantic => "WRONG",
        SpanHealth.Partial => "PARTIAL",
        SpanHealth.Unknown => "??",
        _ => "OK",
    };

    private static string PathOf(Node n)
    {
        var names = new Stack<string>();
        for (var cur = n; cur is not null; cur = cur.Parent)
        {
            names.Push(cur.Span.Name);
        }

        return string.Join(" > ", names);
    }

    private static Dictionary<string, string> ContextOf(Node n, TriageDigestOptions o)
    {
        // Nearest value wins: walk from the span up to the root.
        var ctx = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var cur = n; cur is not null; cur = cur.Parent)
        {
            foreach (var kv in OwnContext(cur.Span, o))
            {
                ctx.TryAdd(kv.Key, kv.Value);
            }
        }

        return ctx;
    }

    private static Dictionary<string, string> OwnContext(SpanRecord s, TriageDigestOptions o)
    {
        var ctx = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in s.Attributes.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            if (kv.Key.StartsWith(TracescribeAttributes.ContextPrefix, StringComparison.Ordinal)
                && SpanRecord.Format(kv.Value) is { } v)
            {
                var key = kv.Key[TracescribeAttributes.ContextPrefix.Length..];
                var safe = o.Redactor is null ? v : o.Redactor.Redact(key, v, out _);
                ctx[key] = Truncate(safe, o)!;
            }
        }

        return ctx;
    }

    private static string? FirstException(SpanRecord s)
    {
        var e = s.Events.FirstOrDefault(x => x.Name == "exception");
        if (e is null)
        {
            return null;
        }

        var type = SpanRecord.Format(e.Attributes.GetValueOrDefault("exception.type"));
        var msg = SpanRecord.Format(e.Attributes.GetValueOrDefault("exception.message"));
        return type is null ? msg : $"{type}: {msg}";
    }

    private static bool? ParseBool(string? v) => v switch
    {
        "true" => true,
        "false" => false,
        _ => null,
    };

    private static string? Truncate(string? value, TriageDigestOptions o)
    {
        if (value is null)
        {
            return null;
        }

        var single = (o.Redactor is null ? value : o.Redactor.Redact("text", value, out _)).ReplaceLineEndings(" ");
        return single.Length <= o.MaxValueLength ? single : string.Concat(single.AsSpan(0, o.MaxValueLength), "…");
    }

    private static string FormatDuration(TimeSpan d) => d.TotalSeconds >= 1
        ? d.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture) + "s"
        : ((int)Math.Round(d.TotalMilliseconds)).ToString(CultureInfo.InvariantCulture) + "ms";
}
