using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Operations;

namespace AsyncAnalyzer;

[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(AsyncCodeFixes)), Shared]
public sealed class AsyncCodeFixes : CodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds =>
        ImmutableArray.Create(AsyncCodeAnalyzer.BlockingId, AsyncCodeAnalyzer.MissingTokenId);

    public override FixAllProvider? GetFixAllProvider() => null;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        var model = await context.Document.GetSemanticModelAsync(context.CancellationToken).ConfigureAwait(false);
        if (root is null || model is null)
        {
            return;
        }

        foreach (var diagnostic in context.Diagnostics)
        {
            var node = root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true);
            var edit = diagnostic.Id == AsyncCodeAnalyzer.BlockingId
                ? ReplaceResult(node, model, context.CancellationToken)
                : AddToken(node, model, context.CancellationToken);
            if (edit is not { } replacement
                || CapturesArgumentText(model.GetOperation(replacement.Original, context.CancellationToken), model.Compilation))
            {
                continue;
            }

            var changed = context.Document.WithSyntaxRoot(root.ReplaceNode(replacement.Original,
                replacement.Updated.WithAdditionalAnnotations(Formatter.Annotation)));
            var changedModel = await changed.GetSemanticModelAsync(context.CancellationToken).ConfigureAwait(false);
            // Await can be illegal because of an enclosing lock or a ref local used later.
            if (changedModel is null || changedModel.GetDiagnostics(cancellationToken: context.CancellationToken)
                .Any(d => d.Severity == DiagnosticSeverity.Error))
            {
                continue;
            }

            context.RegisterCodeFix(CodeAction.Create(replacement.Title, _ => Task.FromResult(changed),
                equivalenceKey: diagnostic.Id), diagnostic);
        }
    }

    private static (SyntaxNode Original, SyntaxNode Updated, string Title)? ReplaceResult(
        SyntaxNode node, SemanticModel model, CancellationToken cancellationToken)
    {
        if (node is not MemberAccessExpressionSyntax member
            || member.Parent is not EqualsValueClauseSyntax
            {
                Parent: VariableDeclaratorSyntax { Parent.Parent: LocalDeclarationStatementSyntax }
            }
            || model.GetOperation(member, cancellationToken) is not IPropertyReferenceOperation property
            || property.Property.Name != "Result"
            || !AsyncCodeAnalyzer.IsFrameworkType(property.Property.ContainingType, model.Compilation,
                "System.Threading.Tasks.Task`1")
            || !AsyncCodeAnalyzer.IsFrameworkType(model.GetTypeInfo(member.Expression, cancellationToken).Type, model.Compilation,
                "System.Threading.Tasks.Task`1")
            || member.WithoutTrivia().DescendantTrivia().Any(t =>
                !t.IsKind(SyntaxKind.WhitespaceTrivia) && !t.IsKind(SyntaxKind.EndOfLineTrivia)))
        {
            return null;
        }

        var awaitExpression = SyntaxFactory.AwaitExpression(member.Expression.WithoutTrivia()).WithTriviaFrom(member);
        return (member, awaitExpression, "Await the task");
    }

    private static (SyntaxNode Original, SyntaxNode Updated, string Title)? AddToken(
        SyntaxNode node, SemanticModel model, CancellationToken cancellationToken)
    {
        // An enclosing overload can change the inferred parameter types of a lambda.
        if (node.Parent is not InvocationExpressionSyntax call || call.Expression != node
            || call.Ancestors().OfType<AnonymousFunctionExpressionSyntax>().Any()
            || model.GetOperation(call, cancellationToken) is not IInvocationOperation invocation
            || !AsyncCodeAnalyzer.TryFindToken(invocation, model, cancellationToken, out var token, out var parameter))
        {
            return null;
        }

        var argument = SyntaxFactory.Argument(Identifier(token.Name))
            .WithNameColon(SyntaxFactory.NameColon(Identifier(parameter.Name)));
        var updated = call.AddArgumentListArguments(argument);
        var binding = model.GetSpeculativeSymbolInfo(call.SpanStart, updated, SpeculativeBindingOption.BindAsExpression);
        if (!SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(call, cancellationToken).Symbol, binding.Symbol))
        {
            return null;
        }

        return (call, updated, "Pass the cancellation token");
    }

    private static bool CapturesArgumentText(IOperation? operation, Compilation compilation)
    {
        // The edit can change the string captured by an enclosing call as well as the edited call.
        for (var current = operation; current is not null; current = current.Parent)
        {
            var arguments = current switch
            {
                IInvocationOperation invocation => invocation.Arguments,
                IObjectCreationOperation creation => creation.Arguments,
                IPropertyReferenceOperation property => property.Arguments,
                _ => ImmutableArray<IArgumentOperation>.Empty
            };

            if (arguments.Any(argument => argument.ArgumentKind == ArgumentKind.DefaultValue
                && argument.Parameter!.GetAttributes().Any(attribute =>
                    AsyncCodeAnalyzer.IsFrameworkType(attribute.AttributeClass, compilation,
                        "System.Runtime.CompilerServices.CallerArgumentExpressionAttribute"))))
            {
                return true;
            }
        }

        return false;
    }

    private static IdentifierNameSyntax Identifier(string name) =>
        SyntaxFactory.IdentifierName(SyntaxFacts.GetKeywordKind(name) == SyntaxKind.None
            && SyntaxFacts.GetContextualKeywordKind(name) == SyntaxKind.None
            ? SyntaxFactory.Identifier(name)
            : SyntaxFactory.Identifier(default, SyntaxKind.IdentifierToken, "@" + name, name, default));
}
