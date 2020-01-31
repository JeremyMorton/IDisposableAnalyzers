﻿namespace IDisposableAnalyzers
{
    using System.Collections.Generic;
    using System.Collections.Immutable;
    using System.Diagnostics.CodeAnalysis;
    using System.Linq;
    using Gu.Roslyn.AnalyzerExtensions;
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.CSharp;
    using Microsoft.CodeAnalysis.CSharp.Syntax;
    using Microsoft.CodeAnalysis.Diagnostics;

    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    internal class DisposeCallAnalyzer : DiagnosticAnalyzer
    {
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(
            Descriptors.IDISP007DoNotDisposeInjected,
            Descriptors.IDISP016DoNotUseDisposedInstance,
            Descriptors.IDISP017PreferUsing);

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterSyntaxNodeAction(c => Handle(c), SyntaxKind.InvocationExpression);
        }

        private static void Handle(SyntaxNodeAnalysisContext context)
        {
            if (!context.IsExcludedFromAnalysis() &&
                context.Node is InvocationExpressionSyntax invocation &&
                DisposeCall.IsIDisposableDispose(invocation, context.SemanticModel, context.CancellationToken) &&
                !invocation.TryFirstAncestorOrSelf<AnonymousFunctionExpressionSyntax>(out _) &&
                DisposeCall.TryGetDisposedRootMember(invocation, context.SemanticModel, context.CancellationToken, out var root))
            {
                if (Disposable.IsCachedOrInjectedOnly(root, invocation, context.SemanticModel, context.CancellationToken))
                {
                    context.ReportDiagnostic(Diagnostic.Create(Descriptors.IDISP007DoNotDisposeInjected, invocation.FirstAncestorOrSelf<StatementSyntax>()?.GetLocation() ?? invocation.GetLocation()));
                }

                if (invocation.Expression is MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax _ } &&
                    context.SemanticModel.TryGetSymbol(root, context.CancellationToken, out ILocalSymbol? local))
                {
                    if (IsUsedAfter(local, invocation, context, out var locations))
                    {
                        context.ReportDiagnostic(Diagnostic.Create(Descriptors.IDISP016DoNotUseDisposedInstance, invocation.FirstAncestorOrSelf<StatementSyntax>()?.GetLocation() ?? invocation.GetLocation(), additionalLocations: locations));
                    }

                    if (IsPreferUsing(local, invocation, context))
                    {
                        context.ReportDiagnostic(Diagnostic.Create(Descriptors.IDISP017PreferUsing, invocation.GetLocation()));
                    }
                }
            }
        }

        private static bool IsUsedAfter(ILocalSymbol local, InvocationExpressionSyntax invocation, SyntaxNodeAnalysisContext context, [NotNullWhen(true)] out IReadOnlyList<Location>? locations)
        {
            if (local.TrySingleDeclaration(context.CancellationToken, out var declaration) &&
                declaration.TryFirstAncestor(out BlockSyntax? block))
            {
                List<Location>? temp = null;
                using var walker = IdentifierNameWalker.Borrow(block);
                foreach (var identifierName in walker.IdentifierNames)
                {
                    if (identifierName.Identifier.ValueText == local.Name &&
                        invocation.IsExecutedBefore(identifierName) == ExecutedBefore.Yes &&
                        context.SemanticModel.TryGetSymbol(identifierName, context.CancellationToken, out ILocalSymbol? candidate) &&
                        local.Equals(candidate) &&
                        !IsAssigned(identifierName) &&
                        !IsReassigned(identifierName))
                    {
                        if (temp is null)
                        {
                            temp = new List<Location>();
                        }

                        temp.Add(identifierName.GetLocation());
                    }
                }

                locations = temp;
                return locations != null;
            }

            locations = null;
            return false;

            static bool IsAssigned(IdentifierNameSyntax identifier)
            {
                return identifier.Parent switch
                {
                    AssignmentExpressionSyntax { Left: { } left } => left == identifier,
                    ArgumentSyntax { RefOrOutKeyword: { } keyword } => keyword.IsKind(SyntaxKind.OutKeyword),
                    _ => false,
                };
            }

            bool IsReassigned(ExpressionSyntax location)
            {
                using (var walker = MutationWalker.For(local, context.SemanticModel, context.CancellationToken))
                {
                    foreach (var mutation in walker.All())
                    {
                        if (mutation.TryFirstAncestorOrSelf(out ExpressionSyntax? expression) &&
                            invocation.IsExecutedBefore(expression) != ExecutedBefore.No &&
                            expression.IsExecutedBefore(location) != ExecutedBefore.No)
                        {
                            return true;
                        }
                    }
                }

                return false;
            }
        }

        private static bool IsPreferUsing(ILocalSymbol local, InvocationExpressionSyntax invocation, SyntaxNodeAnalysisContext context)
        {
            return local.TrySingleDeclaration(context.CancellationToken, out var declaration) &&
                   declaration is VariableDeclaratorSyntax declarator &&
                   declaration.TryFirstAncestor(out LocalDeclarationStatementSyntax? localDeclarationStatement) &&
                   invocation.TryFirstAncestor(out ExpressionStatementSyntax? expressionStatement) &&
                   (DeclarationIsAssignment() || IsTrivialTryFinally()) &&
                   !IsMutated();

            bool DeclarationIsAssignment()
            {
                return localDeclarationStatement!.Parent == expressionStatement!.Parent &&
                       declarator is { Initializer: { Value: { } value } } &&
                       Disposable.IsCreation(value, context.SemanticModel, context.CancellationToken) == Result.Yes;
            }

            bool IsTrivialTryFinally()
            {
                return expressionStatement!.Parent is BlockSyntax { Statements: { Count: 1 }, Parent: FinallyClauseSyntax { Parent: TryStatementSyntax tryStatement } } &&
                       !tryStatement.Catches.Any();
            }

            bool IsMutated()
            {
                using var walker = MutationWalker.For(local, context.SemanticModel, context.CancellationToken);
                if (declarator.Initializer?.Value.IsKind(SyntaxKind.NullLiteralExpression) == true &&
                    walker.TrySingle(out var mutation) &&
                    mutation.TryFirstAncestor(out ExpressionStatementSyntax? statement) &&
                    statement.Parent is BlockSyntax block &&
                    block.Statements[0] == statement &&
                    block.Parent is TryStatementSyntax)
                {
                    return false;
                }

                return walker.All().Any();
            }
        }
    }
}
