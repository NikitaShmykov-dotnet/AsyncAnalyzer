using System.Threading;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace AsyncAnalyzer;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class AsyncCodeAnalyzer : DiagnosticAnalyzer
{
    internal const string BlockingId = "AG001";
    internal const string AsyncVoidId = "AG002";
    internal const string MissingTokenId = "AG003";

    private static readonly DiagnosticDescriptor Blocking = new(
        BlockingId, "Blocking task access",
        "'{0}' blocks inside an async function", "Async", DiagnosticSeverity.Warning, true);

    private static readonly DiagnosticDescriptor AsyncVoid = new(
        AsyncVoidId, "Async void function",
        "Use an awaitable return type for async functions outside event handlers",
        "Async", DiagnosticSeverity.Warning, true);

    private static readonly DiagnosticDescriptor MissingToken = new(
        MissingTokenId, "Cancellation token omitted",
        "Pass '{0}' to optional parameter '{1}'", "Async", DiagnosticSeverity.Warning, true);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(Blocking, AsyncVoid, MissingToken);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterOperationAction(AnalyzeProperty, OperationKind.PropertyReference);
        context.RegisterOperationAction(AnalyzeInvocation, OperationKind.Invocation);
        context.RegisterSyntaxNodeAction(AnalyzeAsyncVoid, SyntaxKind.MethodDeclaration,
            SyntaxKind.LocalFunctionStatement, SyntaxKind.SimpleLambdaExpression,
            SyntaxKind.ParenthesizedLambdaExpression, SyntaxKind.AnonymousMethodExpression);
    }

    private static void AnalyzeProperty(OperationAnalysisContext context)
    {
        var property = (IPropertyReferenceOperation)context.Operation;
        if (property.Property.Name == "Result"
            && IsFrameworkType(property.Property.ContainingType, context.Compilation, "System.Threading.Tasks.Task`1")
            && IsInAsyncFunction(context))
        {
            context.ReportDiagnostic(Diagnostic.Create(Blocking, property.Syntax.GetLocation(), "Task.Result"));
        }
    }

    private static void AnalyzeInvocation(OperationAnalysisContext context)
    {
        var call = (IInvocationOperation)context.Operation;
        var method = call.TargetMethod;
        var compilation = context.Compilation;
        string? blockingMember = null;
        if (method.Name == "Wait" && IsFrameworkType(method.ContainingType, compilation, "System.Threading.Tasks.Task"))
        {
            blockingMember = "Task.Wait";
        }
        else if (method.Name == "GetResult" && call.Instance is IInvocationOperation getAwaiter
            && getAwaiter.TargetMethod.Name == "GetAwaiter"
            && (IsFrameworkType(getAwaiter.TargetMethod.ContainingType, compilation, "System.Threading.Tasks.Task")
                || IsFrameworkType(getAwaiter.TargetMethod.ContainingType, compilation, "System.Threading.Tasks.Task`1")))
        {
            blockingMember = "Task.GetAwaiter().GetResult()";
        }

        if (blockingMember is not null && IsInAsyncFunction(context))
        {
            context.ReportDiagnostic(Diagnostic.Create(Blocking, call.Syntax.GetLocation(), blockingMember));
        }

        if (call.Syntax is InvocationExpressionSyntax syntax && !syntax.ContainsDiagnostics
            && call.SemanticModel is { } model
            && TryFindToken(call, model, context.CancellationToken, out var token, out var parameter))
        {
            context.ReportDiagnostic(Diagnostic.Create(MissingToken, syntax.Expression.GetLocation(),
                token.Name, parameter.Name));
        }
    }

    private static bool IsInAsyncFunction(OperationAnalysisContext context) =>
        context.Operation.SemanticModel?.GetEnclosingSymbol(context.Operation.Syntax.SpanStart,
            context.CancellationToken) is IMethodSymbol { IsAsync: true };

    private static void AnalyzeAsyncVoid(SyntaxNodeAnalysisContext context)
    {
        var node = context.Node;
        var (method, identifier) = node switch
        {
            MethodDeclarationSyntax declaration => (
                context.SemanticModel.GetDeclaredSymbol(declaration, context.CancellationToken), declaration.Identifier),
            LocalFunctionStatementSyntax local => (
                context.SemanticModel.GetDeclaredSymbol(local, context.CancellationToken), local.Identifier),
            AnonymousFunctionExpressionSyntax anonymous => (
                (context.SemanticModel.GetOperation(anonymous, context.CancellationToken) as IAnonymousFunctionOperation)?.Symbol,
                anonymous.AsyncKeyword),
            _ => default
        };

        if (method is IMethodSymbol { IsAsync: true, ReturnsVoid: true } asyncMethod && !identifier.IsMissing
            && !IsEventHandler(asyncMethod, context.Compilation))
        {
            context.ReportDiagnostic(Diagnostic.Create(AsyncVoid, identifier.GetLocation()));
        }
    }

    private static bool IsEventHandler(IMethodSymbol method, Compilation compilation)
    {
        if (method.Parameters.Length != 2
            || method.Parameters[0].Type.SpecialType != SpecialType.System_Object
            || method.Parameters.Any(parameter => parameter.RefKind != RefKind.None))
        {
            return false;
        }

        for (var type = method.Parameters[1].Type; type is not null; type = type.BaseType)
        {
            if (IsFrameworkType(type, compilation, "System.EventArgs"))
            {
                return true;
            }
        }

        return false;
    }

    internal static bool TryFindToken(IInvocationOperation call, SemanticModel model,
        CancellationToken cancellationToken, out IParameterSymbol token, out IParameterSymbol parameter)
    {
        token = null!;
        parameter = null!;
        var omittedTokens = call.Arguments.Where(a => a.ArgumentKind == ArgumentKind.DefaultValue
            && a.Parameter is { IsOptional: true } p
            && IsFrameworkType(p.Type, model.Compilation, "System.Threading.CancellationToken")).ToArray();
        if (omittedTokens.Length != 1
            || model.GetSymbolInfo(call.Syntax, cancellationToken).Symbol is not IMethodSymbol
            || model.GetEnclosingSymbol(call.Syntax.SpanStart, cancellationToken) is not IMethodSymbol caller)
        {
            return false;
        }

        var callerTokens = caller.Parameters.Where(p => p.RefKind == RefKind.None
            && IsFrameworkType(p.Type, model.Compilation, "System.Threading.CancellationToken")).ToArray();
        if (callerTokens.Length != 1)
        {
            return false;
        }

        token = callerTokens[0];
        parameter = omittedTokens[0].Parameter!;
        var visible = model.LookupSymbols(call.Syntax.SpanStart, name: token.Name);
        return visible.Length == 1 && SymbolEqualityComparer.Default.Equals(visible[0], token);
    }

    internal static bool IsFrameworkType(ITypeSymbol? type, Compilation compilation, string metadataName) =>
        type is not null && !SymbolEqualityComparer.Default.Equals(type.ContainingAssembly, compilation.Assembly)
        && SymbolEqualityComparer.Default.Equals(type.OriginalDefinition,
            compilation.GetTypeByMetadataName(metadataName));
}
