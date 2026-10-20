using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Tracescribe.Generators;

/// <summary>
/// Writes the implementation of <c>[Scribe]</c> partial methods: a Tracescribe operation that wraps a call
/// to the user's implementation method and records success or an inferred failure.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class ScribeGenerator : IIncrementalGenerator
{
    private const string ScribeAttributeName = "Tracescribe.ScribeAttribute";
    private const string ContextAttributeName = "Tracescribe.ScribeContextAttribute";

    private static readonly SymbolDisplayFormat TypeFormat = SymbolDisplayFormat.FullyQualifiedFormat
        .AddMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var results = context.SyntaxProvider.ForAttributeWithMetadataName(
            ScribeAttributeName,
            static (node, _) => node is MethodDeclarationSyntax,
            static (ctx, ct) => Build(ctx, ct));

        context.RegisterSourceOutput(results, static (spc, result) =>
        {
            foreach (var diagnostic in result.Diagnostics)
            {
                spc.ReportDiagnostic(diagnostic);
            }

            if (result.Source is not null)
            {
                spc.AddSource(result.HintName, result.Source);
            }
        });
    }

    private static GenerationResult Build(GeneratorAttributeSyntaxContext ctx, CancellationToken ct)
    {
        var method = (IMethodSymbol)ctx.TargetSymbol;
        var syntax = (MethodDeclarationSyntax)ctx.TargetNode;
        var location = syntax.Identifier.GetLocation();
        var attribute = ctx.Attributes[0];

        GenerationResult Fail(DiagnosticDescriptor descriptor, params object[] args) =>
            GenerationResult.Error(Diagnostic.Create(descriptor, location, args));

        if (!syntax.Modifiers.Any(SyntaxKind.PartialKeyword) || syntax.Body is not null || syntax.ExpressionBody is not null)
        {
            return Fail(Diagnostics.NotPartialDeclaration, method.Name);
        }

        if (method.PartialImplementationPart is not null)
        {
            return Fail(Diagnostics.AlreadyImplemented, method.Name);
        }

        if (method.IsGenericMethod)
        {
            return Fail(Diagnostics.GenericNotSupported, method.Name);
        }

        if (method.ReturnsByRef || method.ReturnsByRefReadonly)
        {
            return Fail(Diagnostics.UnsupportedReturn, method.Name, "ref returns");
        }

        for (var t = method.ContainingType; t is not null; t = t.ContainingType)
        {
            var isPartial = t.DeclaringSyntaxReferences
                .Select(r => r.GetSyntax(ct))
                .OfType<TypeDeclarationSyntax>()
                .All(d => d.Modifiers.Any(SyntaxKind.PartialKeyword));
            if (!isPartial)
            {
                return Fail(Diagnostics.TypeNotPartial, t.Name, method.Name);
            }
        }

        var returnKind = Classify(method.ReturnType, ctx.SemanticModel.Compilation);
        if (returnKind == ReturnKind.Unsupported)
        {
            return Fail(Diagnostics.UnsupportedReturn, method.Name, method.ReturnType.ToDisplayString());
        }

        if (attribute.ConstructorArguments.Length != 1 || attribute.ConstructorArguments[0].Value is not string intent || string.IsNullOrWhiteSpace(intent))
        {
            return Fail(Diagnostics.MissingIntent, method.Name);
        }

        string? expected = null, description = null, implementation = null;
        foreach (var named in attribute.NamedArguments)
        {
            var value = named.Value.Value as string;
            switch (named.Key)
            {
                case "Expected": expected = value; break;
                case "Description": description = value; break;
                case "Implementation": implementation = value; break;
            }
        }

        implementation ??= DefaultImplementationName(method.Name);
        var impl = method.ContainingType.GetMembers(implementation).OfType<IMethodSymbol>().FirstOrDefault(m => Matches(m, method));
        if (impl is null)
        {
            var signature = $"{method.ReturnType.ToDisplayString()} {implementation}({string.Join(", ", method.Parameters.Select(p => p.Type.ToDisplayString()))})";
            return Fail(Diagnostics.ImplementationNotFound, signature, method.ContainingType.Name, method.Name);
        }

        ct.ThrowIfCancellationRequested();
        var source = Emit(method, syntax, returnKind, intent, expected, description, implementation);
        var hint = Sanitize(method.ContainingType.ToDisplayString() + "." + method.Name + "_" + method.Parameters.Length) + "." + Hash(method.ToDisplayString()) + ".Scribe.g.cs";
        return GenerationResult.Ok(hint, source);
    }

    private enum ReturnKind
    {
        Void,
        Task,
        TaskOfT,
        Value,
        Unsupported,
    }

    private static ReturnKind Classify(ITypeSymbol type, Compilation compilation)
    {
        if (type.SpecialType == SpecialType.System_Void)
        {
            return ReturnKind.Void;
        }

        var task = compilation.GetTypeByMetadataName("System.Threading.Tasks.Task");
        var taskT = compilation.GetTypeByMetadataName("System.Threading.Tasks.Task`1");
        var valueTask = compilation.GetTypeByMetadataName("System.Threading.Tasks.ValueTask");
        var valueTaskT = compilation.GetTypeByMetadataName("System.Threading.Tasks.ValueTask`1");
        var original = type.OriginalDefinition;

        if (SymbolEqualityComparer.Default.Equals(type, task) || SymbolEqualityComparer.Default.Equals(type, valueTask))
        {
            return ReturnKind.Task;
        }

        if (SymbolEqualityComparer.Default.Equals(original, taskT) || SymbolEqualityComparer.Default.Equals(original, valueTaskT))
        {
            return ReturnKind.TaskOfT;
        }

        // Other awaitables and async streams would end the span before the work completes.
        if (type.GetMembers("GetAwaiter").Any() || type.AllInterfaces.Any(i => i.OriginalDefinition.ToDisplayString() == "System.Collections.Generic.IAsyncEnumerable<T>")
            || type.OriginalDefinition.ToDisplayString() == "System.Collections.Generic.IAsyncEnumerable<T>")
        {
            return ReturnKind.Unsupported;
        }

        return ReturnKind.Value;
    }

    private static bool Matches(IMethodSymbol candidate, IMethodSymbol method)
    {
        if (SymbolEqualityComparer.Default.Equals(candidate, method)
            || candidate.IsGenericMethod
            || candidate.Parameters.Length != method.Parameters.Length
            || candidate.IsStatic != method.IsStatic
            || !SymbolEqualityComparer.Default.Equals(candidate.ReturnType, method.ReturnType))
        {
            return false;
        }

        for (var i = 0; i < candidate.Parameters.Length; i++)
        {
            if (candidate.Parameters[i].RefKind != method.Parameters[i].RefKind
                || !SymbolEqualityComparer.Default.Equals(candidate.Parameters[i].Type, method.Parameters[i].Type))
            {
                return false;
            }
        }

        return true;
    }

    internal static string DefaultImplementationName(string name) =>
        name.EndsWith("Async", StringComparison.Ordinal) && name.Length > 5
            ? name.Substring(0, name.Length - 5) + "CoreAsync"
            : name + "Core";

    private static string Emit(
        IMethodSymbol method,
        MethodDeclarationSyntax syntax,
        ReturnKind returnKind,
        string intent,
        string? expected,
        string? description,
        string implementation)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated by Tracescribe.Generators />");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();

        var ns = method.ContainingType.ContainingNamespace;
        var indent = string.Empty;
        if (ns is { IsGlobalNamespace: false })
        {
            sb.Append("namespace ").AppendLine(ns.ToDisplayString());
            sb.AppendLine("{");
            indent = "    ";
        }

        var types = new Stack<INamedTypeSymbol>();
        for (var t = method.ContainingType; t is not null; t = t.ContainingType)
        {
            types.Push(t);
        }

        var depth = 0;
        foreach (var t in types)
        {
            var pad = indent + new string(' ', depth * 4);
            sb.Append(pad).Append("partial ").Append(TypeKeyword(t)).Append(' ').Append(t.Name);
            if (t.TypeParameters.Length > 0)
            {
                sb.Append('<').Append(string.Join(", ", t.TypeParameters.Select(p => p.Name))).Append('>');
            }

            sb.AppendLine();
            sb.Append(pad).AppendLine("{");
            depth++;
        }

        var body = indent + new string(' ', depth * 4);
        var isAsync = returnKind is ReturnKind.Task or ReturnKind.TaskOfT;

        var modifiers = syntax.Modifiers
            .Where(m => !m.IsKind(SyntaxKind.PartialKeyword) && !m.IsKind(SyntaxKind.AsyncKeyword))
            .Select(m => m.Text)
            .ToList();
        if (isAsync)
        {
            modifiers.Add("async");
        }

        modifiers.Add("partial");

        var parameters = string.Join(", ", method.Parameters.Select(p =>
            (p.Ordinal == 0 && method.IsExtensionMethod ? "this " : string.Empty)
            + (p.IsParams ? "params " : string.Empty)
            + RefPrefix(p.RefKind, declaration: true)
            + p.Type.ToDisplayString(TypeFormat) + " " + Escape(p.Name)));
        var arguments = string.Join(", ", method.Parameters.Select(p => RefPrefix(p.RefKind, declaration: false) + Escape(p.Name)));

        sb.Append(body).Append(string.Join(" ", modifiers)).Append(' ')
          .Append(method.ReturnType.ToDisplayString(TypeFormat)).Append(' ')
          .Append(Escape(method.Name)).Append('(').Append(parameters).AppendLine(")");
        sb.Append(body).AppendLine("{");

        var inner = body + "    ";
        sb.Append(inner).Append("using var __scribe = global::Tracescribe.Scribe.Begin(").Append(Literal(intent));
        if (expected is not null)
        {
            sb.Append(", expected: ").Append(Literal(expected));
        }

        if (description is not null)
        {
            sb.Append(", description: ").Append(Literal(description));
        }

        sb.AppendLine(");");

        foreach (var p in method.Parameters)
        {
            var key = p.GetAttributes()
                .FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == ContextAttributeName)?
                .ConstructorArguments.FirstOrDefault().Value as string;
            if (key is not null && p.RefKind != RefKind.Out)
            {
                sb.Append(inner).Append("__scribe.WithContext(").Append(Literal(key)).Append(", ").Append(Escape(p.Name)).AppendLine(");");
            }
        }

        var call = Escape(implementation) + "(" + arguments + ")";
        sb.Append(inner).AppendLine("try");
        sb.Append(inner).AppendLine("{");
        var tryBody = inner + "    ";
        switch (returnKind)
        {
            case ReturnKind.Void:
                sb.Append(tryBody).Append(call).AppendLine(";");
                sb.Append(tryBody).AppendLine("__scribe.Succeed();");
                break;
            case ReturnKind.Task:
                sb.Append(tryBody).Append("await ").Append(call).AppendLine(".ConfigureAwait(false);");
                sb.Append(tryBody).AppendLine("__scribe.Succeed();");
                break;
            case ReturnKind.TaskOfT:
                sb.Append(tryBody).Append("var __result = await ").Append(call).AppendLine(".ConfigureAwait(false);");
                sb.Append(tryBody).AppendLine("__scribe.Succeed();");
                sb.Append(tryBody).AppendLine("return __result;");
                break;
            default:
                sb.Append(tryBody).Append("var __result = ").Append(call).AppendLine(";");
                sb.Append(tryBody).AppendLine("__scribe.Succeed();");
                sb.Append(tryBody).AppendLine("return __result;");
                break;
        }

        sb.Append(inner).AppendLine("}");

        var token = method.Parameters.FirstOrDefault(p => p.Type.ToDisplayString() == "System.Threading.CancellationToken" && p.RefKind == RefKind.None);
        if (token is not null)
        {
            // Caller cancellation is not a failure of this operation: leave the outcome unknown.
            sb.Append(inner).Append("catch (global::System.OperationCanceledException) when (").Append(Escape(token.Name)).AppendLine(".IsCancellationRequested)");
            sb.Append(inner).AppendLine("{");
            sb.Append(inner).AppendLine("    __scribe.Hint(\"canceled by caller\");");
            sb.Append(inner).AppendLine("    throw;");
            sb.Append(inner).AppendLine("}");
        }

        sb.Append(inner).AppendLine("catch (global::System.Exception __exception)");
        sb.Append(inner).AppendLine("{");
        sb.Append(inner).AppendLine("    __scribe.Fail(__exception);");
        sb.Append(inner).AppendLine("    throw;");
        sb.Append(inner).AppendLine("}");
        sb.Append(body).AppendLine("}");

        for (var d = depth - 1; d >= 0; d--)
        {
            sb.Append(indent).Append(new string(' ', d * 4)).AppendLine("}");
        }

        if (indent.Length > 0)
        {
            sb.AppendLine("}");
        }

        return sb.ToString();
    }

    private static string TypeKeyword(INamedTypeSymbol type) => type switch
    {
        { IsRecord: true, TypeKind: TypeKind.Struct } => "record struct",
        { IsRecord: true } => "record",
        { TypeKind: TypeKind.Struct } => "struct",
        { TypeKind: TypeKind.Interface } => "interface",
        _ => "class",
    };

    private static string RefPrefix(RefKind kind, bool declaration) => kind switch
    {
        RefKind.Ref => "ref ",
        RefKind.Out => "out ",
        RefKind.In => "in ",
        (RefKind)4 => declaration ? "ref readonly " : "in ", // RefKind.RefReadOnlyParameter (C# 12)
        _ => string.Empty,
    };

    private static string Escape(string identifier) =>
        SyntaxFacts.GetKeywordKind(identifier) != SyntaxKind.None || SyntaxFacts.GetContextualKeywordKind(identifier) == SyntaxKind.AwaitKeyword
            ? "@" + identifier
            : identifier;

    private static string Literal(string value) => SymbolDisplay.FormatLiteral(value, quote: true);

    private static string Sanitize(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            sb.Append(char.IsLetterOrDigit(c) || c == '.' || c == '_' ? c : '_');
        }

        return sb.ToString();
    }

    private static string Hash(string value)
    {
        unchecked
        {
            var h = 2166136261u;
            foreach (var c in value)
            {
                h = (h ^ c) * 16777619u;
            }

            return h.ToString("x8");
        }
    }

    private sealed class GenerationResult : IEquatable<GenerationResult>
    {
        private GenerationResult(string hintName, string? source, ImmutableArray<Diagnostic> diagnostics)
        {
            HintName = hintName;
            Source = source;
            Diagnostics = diagnostics;
        }

        public string HintName { get; }

        public string? Source { get; }

        public ImmutableArray<Diagnostic> Diagnostics { get; }

        public static GenerationResult Ok(string hintName, string source) => new(hintName, source, ImmutableArray<Diagnostic>.Empty);

        public static GenerationResult Error(Diagnostic diagnostic) => new(string.Empty, null, ImmutableArray.Create(diagnostic));

        public bool Equals(GenerationResult? other) =>
            other is not null
            && HintName == other.HintName
            && Source == other.Source
            && Diagnostics.SequenceEqual(other.Diagnostics);

        public override bool Equals(object? obj) => Equals(obj as GenerationResult);

        public override int GetHashCode() => (HintName.GetHashCode() * 31) ^ (Source?.GetHashCode() ?? 0);
    }
}

internal static class Diagnostics
{
    private const string Category = "Tracescribe";

    public static readonly DiagnosticDescriptor NotPartialDeclaration = new(
        "TSCR001", "[Scribe] needs a partial method declaration",
        "[Scribe] method '{0}' must be a partial method declaration without a body (e.g. 'public partial Task<T> {0}(...);')",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor AlreadyImplemented = new(
        "TSCR002", "[Scribe] method already implemented",
        "[Scribe] method '{0}' already has an implementation; Tracescribe generates it. Move your code into the implementation method.",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor TypeNotPartial = new(
        "TSCR003", "Containing type must be partial",
        "Type '{0}' containing [Scribe] method '{1}' must be declared partial",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor GenericNotSupported = new(
        "TSCR004", "Generic [Scribe] methods are not supported",
        "[Scribe] method '{0}' is generic; generic methods are not supported yet. Wrap it with Scribe.Begin instead.",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor UnsupportedReturn = new(
        "TSCR005", "Unsupported return type",
        "[Scribe] method '{0}' returns {1}, which is not supported. Use void, a value, Task, Task<T>, ValueTask or ValueTask<T>.",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor ImplementationNotFound = new(
        "TSCR006", "Implementation method not found",
        "Tracescribe could not find '{0}' on '{1}' to implement [Scribe] method '{2}'. Add it, or set Implementation = \"...\" on the attribute.",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor MissingIntent = new(
        "TSCR007", "Missing intent",
        "[Scribe] on '{0}' needs a non-empty intent, e.g. [Scribe(\"claim.fnol.submit\")]",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true);
}
