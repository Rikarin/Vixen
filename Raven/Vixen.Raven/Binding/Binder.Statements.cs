// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Syntax;
using Vixen.Raven.Diagnostics;
using Vixen.Raven.Symbols;
using Vixen.Raven.Symbols.Source;
using Vixen.Raven.Syntax;

namespace Vixen.Raven.Binding;

/// <summary>Statement binding.</summary>
internal abstract partial class Binder {
    public BoundStatement BindStatement(StatementSyntax syntax) {
        // Nothing downstream reads statement attributes, so `[Unroll] for (...)` would
        // otherwise be a silent no-op the author believes in.
        if (syntax.AttributeLists is { Count: > 0 } attributes && attributes[0] is { } list) {
            Report(SemanticDiagnostics.AttributesOnStatementHaveNoEffect, list);
        }

        var bound = BindStatementCore(syntax);
        Context.Record(syntax, bound);
        return bound;
    }

    /// <summary>Binds a block in its own scope.</summary>
    public BoundBlockStatement BindBlock(BlockSyntax syntax) {
        var binder = new BlockBinder(this);
        List<BoundStatement> statements = [];

        foreach (var statement in syntax.Statements) {
            statements.Add(binder.BindStatement(statement));
        }

        var bound = new BoundBlockStatement(syntax, statements);
        Context.Record(syntax, bound);
        return bound;
    }

    BoundStatement BindStatementCore(StatementSyntax syntax) {
        switch (syntax) {
            case BlockSyntax block:
                return BindBlock(block);

            case LocalDeclarationStatementSyntax declaration:
                return BindLocalDeclaration(declaration);

            case ExpressionStatementSyntax expression: {
                var value = BindExpression(expression.Expression);

                if (!HasEffect(value)) {
                    Report(SemanticDiagnostics.ExpressionStatementHasNoEffect, expression.Expression);
                }

                return new BoundExpressionStatement(expression, value);
            }

            case IfStatementSyntax ifStatement: {
                var condition = BindCondition(ifStatement.Condition);
                var consequence = new BlockBinder(this).BindStatement(ifStatement.Statement);
                var alternative = ifStatement.Else is { } elseClause
                    ? new BlockBinder(this).BindStatement(elseClause.Statement)
                    : null;
                return new BoundIfStatement(ifStatement, condition, consequence, alternative);
            }

            case WhileStatementSyntax whileStatement: {
                var condition = BindCondition(whileStatement.Condition);
                var body = new BlockBinder(this, true).BindStatement(whileStatement.Statement);
                return new BoundWhileStatement(whileStatement, condition, body);
            }

            case RepeatStatementSyntax repeat: {
                var body = new BlockBinder(this, true).BindStatement(repeat.Statement);
                return new BoundRepeatStatement(repeat, body, BindCondition(repeat.Condition));
            }

            case ForStatementSyntax forStatement:
                return BindFor(forStatement);

            case ReturnStatementSyntax returnStatement:
                return BindReturn(returnStatement);

            case BreakStatementSyntax:
                return new BoundBreakStatement(syntax);

            case ContinueStatementSyntax:
                return new BoundContinueStatement(syntax);

            // Nothing to check here: whether the stage may discard is a question about which entry
            // points reach this body, and a body does not know its callers. Lowering has the call
            // graph and answers it there (RVN3008).
            case DiscardStatementSyntax:
                return new BoundDiscardStatement(syntax);

            case SwitchStatementSyntax switchStatement:
                return BindSwitch(switchStatement);

            case EmptyStatementSyntax:
                return new BoundNoOpStatement(syntax);

            default:
                return new BoundNoOpStatement(syntax);
        }
    }

    /// <summary>
    ///     True when evaluating this expression as a statement can do something — <c>RVN2141</c>.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Three forms, and the list is closed rather than a heuristic over the subtree. An
    ///         <b>assignment</b> writes; a <b>call</b> may write a resource or an <c>inout</c>
    ///         argument, and no caller can see which from here; an <b>increment</b> is an assignment
    ///         spelled shorter. Nothing else in this language has an effect at all — there is no
    ///         allocation, no exception and no <c>await</c>, so a discarded value really is
    ///         discarded.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Deliberately not "does the subtree contain a call".</b>
    ///         <c>+ Morph.Low(word) * weight</c> contains two, and it is exactly the statement this
    ///         rule exists to refuse: the calls are pure, their result is added to nothing, and the
    ///         author believed they were the tail of the assignment on the line above. What matters
    ///         is what the <em>statement</em> does with the value, which is the root node.
    ///     </para>
    ///     <para>
    ///         An expression that failed to bind passes, on <see cref="ErrorTypeSymbol" />'s own
    ///         terms — "reports one diagnostic, and then lets it flow through the rest of the
    ///         expression so a single mistake does not cascade". <c>[]</c> as a statement is where
    ///         that matters: it is <c>RVN2140</c>'s trigger and it lands in exactly this position,
    ///         so without the guard the one program written to prove that rule fires would report
    ///         two.
    ///     </para>
    /// </remarks>
    static bool HasEffect(BoundExpression expression) =>
        expression switch {
            BoundAssignmentExpression => true,
            BoundInvocationExpression => true,
            BoundUnaryExpression unary => unary.OperatorKind
                is UnaryOperatorKind.PreIncrement or UnaryOperatorKind.PreDecrement
                or UnaryOperatorKind.PostIncrement or UnaryOperatorKind.PostDecrement,
            BoundErrorExpression => true,

            // A conversion is inserted around a value, never around the effect: unwrap it so a
            // widened call still reads as a call and a widened sum still reads as a sum.
            BoundConversionExpression conversion => HasEffect(conversion.Operand),
            _ => Erroneous(expression.Type)
        };

    /// <summary>True when this type is the stand-in for one that could not be resolved.</summary>
    /// <remarks>
    ///     Through an array's element, because that is the shape the failure takes: an empty
    ///     collection literal is bound as <c>?[0]</c> rather than as <c>?</c>, so asking only about
    ///     the outermost type would answer "array" and report a second time.
    /// </remarks>
    static bool Erroneous(TypeSymbol type) =>
        type.TypeKind == TypeKind.Error || (type is ArrayTypeSymbol array && Erroneous(array.ElementType));

    BoundStatement BindLocalDeclaration(LocalDeclarationStatementSyntax syntax) {
        var declaration = syntax.Declaration;
        var declaredType = declaration.Type is null ? null : BindType(declaration.Type);

        // The initializer is bound before the local exists, so `val x = x` cannot
        // resolve to the variable being declared.
        var initializer = declaration.Initializer?.Value is { } value ? BindValue(value) : null;

        var type = declaredType ?? initializer?.Type;
        if (type is null) {
            Report(SemanticDiagnostics.MissingTypeOrInitializer, declaration, declaration.Identifier.ValueText);
            type = ErrorTypeSymbol.Instance;
        }

        if (declaredType is not null && initializer is not null) {
            initializer = Convert(initializer, declaredType, declaration.Initializer!.Value);
        }

        var local = new LocalSymbol(
            ContainingMember,
            declaration.Identifier.ValueText,
            type,
            declaration.Keyword.Kind == SyntaxKind.ValKeyword,
            declaration
        );

        DeclareLocal(local, declaration);
        Context.RecordDeclaration(syntax, local);

        return new BoundLocalDeclarationStatement(syntax, local, initializer);
    }

    BoundStatement BindFor(ForStatementSyntax syntax) {
        var sequence = BindValue(syntax.Expression);
        var elementType = GetElementType(sequence.Type);

        if (elementType is null) {
            if (!sequence.Type.IsErrorType) {
                Report(SemanticDiagnostics.NotIterable, syntax.Expression, sequence.Type.ToDisplayString());
            }

            elementType = ErrorTypeSymbol.Instance;
        }

        var binder = new BlockBinder(this, true);
        var iterationVariable = new LocalSymbol(
            ContainingMember,
            syntax.Identifier.ValueText,
            elementType,
            true,
            syntax
        );

        binder.DeclareLocal(iterationVariable, syntax);
        var body = binder.BindStatement(syntax.Statement);

        return new BoundForStatement(syntax, iterationVariable, sequence, body);
    }

    static TypeSymbol? GetElementType(TypeSymbol type) =>
        type switch {
            ArrayTypeSymbol array => array.ElementType,
            SequenceTypeSymbol sequence => sequence.ElementType,
            { IsErrorType: true } => ErrorTypeSymbol.Instance,
            _ => null
        };

    BoundStatement BindReturn(ReturnStatementSyntax syntax) {
        var returnType = ReturnType;
        var memberName = ContainingMember?.Name ?? "<expression>";

        if (syntax.Expression is not { } expressionSyntax) {
            if (returnType is not null && !returnType.IsVoid && !returnType.IsErrorType) {
                Report(SemanticDiagnostics.MissingReturnValue, syntax, memberName, returnType.ToDisplayString());
            }

            return new BoundReturnStatement(syntax, null);
        }

        var value = BindValue(expressionSyntax);

        if (returnType is null || returnType.IsErrorType) {
            return new BoundReturnStatement(syntax, value);
        }

        if (returnType.IsVoid) {
            Report(SemanticDiagnostics.ReturnValueInVoidMethod, syntax, memberName);
            return new BoundReturnStatement(syntax, value);
        }

        return new BoundReturnStatement(syntax, Convert(value, returnType, expressionSyntax));
    }

    BoundStatement BindSwitch(SwitchStatementSyntax syntax) {
        var governing = BindValue(syntax.Expression);
        List<BoundSwitchSection> sections = [];

        foreach (var section in syntax.Sections) {
            // Each section is its own scope: a local declared in one case is not in scope in
            // the next, which is what makes the sections independent blocks.
            var binder = new BlockBinder(this);

            List<BoundExpression> labels = [];
            var isDefault = false;

            foreach (var label in section.Labels) {
                switch (label) {
                    case CaseSwitchLabelSyntax value:
                        // Converted to the governing type so the comparison lowering emits is
                        // well-typed without the backend having to widen anything.
                        labels.Add(binder.Convert(binder.BindValue(value.Value), governing.Type, value.Value));
                        break;

                    case DefaultSwitchLabelSyntax:
                        isDefault = true;
                        break;
                }
            }

            List<BoundStatement> statements = [];
            foreach (var statement in section.Statements) {
                statements.Add(binder.BindStatement(statement));
            }

            sections.Add(new(labels, isDefault, statements));
        }

        return new BoundSwitchStatement(syntax, governing, sections);
    }

    /// <summary>Declares a local or local function in the innermost block scope.</summary>
    private protected void DeclareLocal(Symbol symbol, SyntaxNode syntax) {
        for (var binder = this; binder is not null; binder = binder.Next) {
            if (binder is not BlockBinder block) {
                continue;
            }

            if (!block.TryDeclare(symbol)) {
                Report(SemanticDiagnostics.DuplicateDeclaration, syntax, symbol.Name);
            }

            Context.RecordDeclaration(syntax, symbol);
            return;
        }
    }
}
