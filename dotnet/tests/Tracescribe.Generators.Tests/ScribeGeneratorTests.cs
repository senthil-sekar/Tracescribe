using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Tracescribe.Digest;
using Tracescribe.Generators;
using Tracescribe.SemanticConventions;
using Xunit;

namespace Tracescribe.Generators.Tests;

public class ScribeGeneratorTests
{
    private static (Compilation Output, ImmutableArray<Diagnostic> GeneratorDiagnostics, string Generated) Run(string source)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Select(p => MetadataReference.CreateFromFile(p))
            .Append(MetadataReference.CreateFromFile(typeof(Scribe).Assembly.Location))
            .Append(MetadataReference.CreateFromFile(typeof(TracescribeAttributes).Assembly.Location))
            .GroupBy(r => Path.GetFileName(r.Display))
            .Select(g => g.First());

        var compilation = CSharpCompilation.Create(
            "ScribeGenTest" + Guid.NewGuid().ToString("N"),
            [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest))],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        var driver = CSharpGeneratorDriver.Create(new ScribeGenerator())
            .RunGeneratorsAndUpdateCompilation(compilation, out var output, out var diagnostics);
        var generated = string.Join("\n", driver.GetRunResult().GeneratedTrees.Select(t => t.ToString()));
        return (output, diagnostics, generated);
    }

    private static void AssertCompiles(Compilation output)
    {
        var errors = output.GetDiagnostics().Where(d => d.Severity >= DiagnosticSeverity.Warning).ToList();
        Assert.True(errors.Count == 0, string.Join("\n", errors.Select(e => e.ToString())));
    }

    private const string ClaimService = """
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        using Tracescribe;

        namespace Demo.Claims;

        public partial class ClaimService
        {
            public bool FailNext { get; set; }

            [Scribe("{INTENT}.submit", Expected = "claim accepted")]
            public partial Task<string> SubmitAsync([ScribeContext("policy_id")] string policyId, CancellationToken cancellationToken);

            private async Task<string> SubmitCoreAsync(string policyId, CancellationToken cancellationToken)
            {
                await Task.Yield();
                if (FailNext) throw new TimeoutException("rating timed out");
                return "CLM-" + policyId;
            }

            [Scribe("{INTENT}.rate")]
            public partial decimal Rate(int driverAge);
            private decimal RateCore(int driverAge) => driverAge < 25 ? 1.5m : 1.0m;

            [Scribe("{INTENT}.audit", Implementation = nameof(WriteAudit))]
            internal static partial void Audit(string @event);
            private static void WriteAudit(string @event) { }

            [Scribe("{INTENT}.notify")]
            public partial ValueTask NotifyAsync();
            private ValueTask NotifyCoreAsync() => default;

            public partial class Nested<T>
            {
                [Scribe("{INTENT}.nested")]
                public partial int Count(T item);
                private int CountCore(T item) => 1;
            }
        }
        """;

    [Fact]
    public void Generates_compilable_code_for_supported_shapes()
    {
        var (output, diagnostics, generated) = Run(ClaimService.Replace("{INTENT}", "test"));

        Assert.Empty(diagnostics);
        AssertCompiles(output);
        Assert.Contains("global::Tracescribe.Scribe.Begin(\"test.submit\", expected: \"claim accepted\")", generated);
        Assert.Contains("__scribe.WithContext(\"policy_id\", policyId);", generated);
        Assert.Contains("public async partial global::System.Threading.Tasks.Task<string> SubmitAsync(", generated);
        Assert.Contains("when (cancellationToken.IsCancellationRequested)", generated);
        Assert.Contains("WriteAudit(@event)", generated);
        Assert.Contains("partial class Nested<T>", generated);
    }

    [Fact]
    public async Task Generated_methods_emit_triage_spans()
    {
        var intent = "gen" + Guid.NewGuid().ToString("N");
        var (output, diagnostics, _) = Run(ClaimService.Replace("{INTENT}", intent));
        Assert.Empty(diagnostics);

        using var stream = new MemoryStream();
        var emit = output.Emit(stream);
        Assert.True(emit.Success, string.Join("\n", emit.Diagnostics));
        stream.Position = 0;
        var assembly = new AssemblyLoadContext(intent, isCollectible: true).LoadFromStream(stream);
        var type = assembly.GetType("Demo.Claims.ClaimService")!;
        var service = Activator.CreateInstance(type)!;
        var submit = type.GetMethod("SubmitAsync")!;

        using var collector = new TraceCollector(s => s.Name == Scribe.SourceName);

        var ok = await (Task<string>)submit.Invoke(service, ["PA-1", CancellationToken.None])!;
        Assert.Equal("CLM-PA-1", ok);

        type.GetProperty("FailNext")!.SetValue(service, true);
        await Assert.ThrowsAsync<TimeoutException>(() => (Task<string>)submit.Invoke(service, ["PA-2", CancellationToken.None])!);

        var spans = collector.Spans.Where(s => s.Name == intent + ".submit").ToList();
        Assert.Equal(2, spans.Count);
        Assert.Equal(OutcomeStatusValues.Success, spans[0].Get(TracescribeAttributes.OutcomeStatus));
        Assert.Equal("PA-1", spans[0].Get(TracescribeAttributes.Context("policy_id")));
        Assert.Equal("claim accepted", spans[0].Get(TracescribeAttributes.OutcomeExpected));
        Assert.Equal(OutcomeStatusValues.Failure, spans[1].Get(TracescribeAttributes.OutcomeStatus));
        Assert.Equal(FailureClassValues.DependencyTimeout, spans[1].Get(TracescribeAttributes.FailureClass));
        Assert.Equal("rating timed out", spans[1].Get(TracescribeAttributes.FailureReason));
    }

    [Theory]
    [InlineData("public class S { [Scribe(\"a\")] public partial void M(); void MCore() {} }", "TSCR003")]
    [InlineData("public partial class S { [Scribe(\"a\")] public partial void M(); }", "TSCR006")]
    [InlineData("public partial class S { [Scribe(\"a\")] public partial T M<T>(); T MCore<T>() => default!; }", "TSCR004")]
    [InlineData("public partial class S { [Scribe(\"a\")] public partial System.Collections.Generic.IAsyncEnumerable<int> M(); System.Collections.Generic.IAsyncEnumerable<int> MCore() => null!; }", "TSCR005")]
    [InlineData("public partial class S { [Scribe(\"a\")] public void M() {} void MCore() {} }", "TSCR001")]
    [InlineData("public partial class S { [Scribe(\"\")] public partial void M(); void MCore() {} }", "TSCR007")]
    public void Reports_diagnostics(string code, string id)
    {
        var (_, diagnostics, _) = Run("using Tracescribe;\n" + code);
        Assert.Contains(diagnostics, d => d.Id == id);
    }
}
