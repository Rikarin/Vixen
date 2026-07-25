using Vixen.Raven.Diagnostics;
using Vixen.Raven.Symbols;
using Vixen.Raven.Symbols.Source;
using Vixen.Raven.Syntax;

namespace Vixen.Raven.Binding;

/// <summary>Statement binding.</summary>
public abstract partial class Binder {
    public BoundStatement BindStatement(StatementSyntax syntax) {
        var bound = BindStatementCore(syntax);
        Context.Record(syntax, bound);
        return bound;
    }

    BoundStatement BindStatementCore(StatementSyntax syntax) {
        switch (syntax) {
            case BlockSyntax block:
                return BindBlock(block);

            case LocalDeclarationStatementSyntax declaration:
                return BindLocalDeclaration(declaration);

            case ExpressionStatementSyntax expression:
                return new BoundExpressionStatement(expression, BindExpression(expression.Expression));

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
                var body = new BlockBinder(this, isLoopBody: true).BindStatement(whileStatement.Statement);
                return new BoundWhileStatement(whileStatement, condition, body);
            }

            case RepeatStatementSyntax repeat: {
                var body = new BlockBinder(this, isLoopBody: true).BindStatement(repeat.Statement);
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

            case LocalFunctionStatementSyntax localFunction:
                return BindLocalFunction(localFunction);

            case SwitchStatementSyntax switchStatement:
                return BindSwitch(switchStatement);

            case UsingStatementSyntax usingStatement:
                return new BoundBlockStatement(
                    usingStatement, [new BlockBinder(this).BindStatement(usingStatement.Statement)]);

            case EmptyStatementSyntax:
                return new BoundNoOpStatement(syntax);

            default:
                return new BoundNoOpStatement(syntax);
        }
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
            declaration);

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

        var binder = new BlockBinder(this, isLoopBody: true);
        var iterationVariable = new LocalSymbol(
            ContainingMember, syntax.Identifier.ValueText, elementType, isReadOnly: true, syntax);

        binder.DeclareLocal(iterationVariable, syntax);
        var body = binder.BindStatement(syntax.Statement);

        return new BoundForStatement(syntax, iterationVariable, sequence, body);
    }

    static TypeSymbol? GetElementType(TypeSymbol type) => type switch {
        ArrayTypeSymbol array => array.ElementType,
        SequenceTypeSymbol sequence => sequence.ElementType,
        { SpecialType: SpecialType.String } => BuiltInTypes.Char,
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

    BoundStatement BindLocalFunction(LocalFunctionStatementSyntax syntax) {
        var method = new SourceMethodSymbol(ContainingMember ?? Compilation.GlobalNamespace, syntax, this);

        DeclareLocal(method, syntax);
        Context.RecordDeclaration(syntax, method);

        var binder = new MemberBinder(this, method, method.ReturnType, method.Parameters, method.TypeParameters);

        BoundNode? body = syntax.Body is { } block
            ? binder.BindBlock(block)
            : syntax.ExpressionBody is { } arrow
                ? binder.BindValue(arrow.Expression)
                : null;

        return new BoundLocalFunctionStatement(syntax, method, body);
    }

    BoundStatement BindSwitch(SwitchStatementSyntax syntax) {
        var governing = BindValue(syntax.Expression);
        List<BoundStatement> statements = [];

        foreach (var section in syntax.Sections) {
            var binder = new BlockBinder(this);

            foreach (var label in section.Labels) {
                switch (label) {
                    case CaseSwitchLabelSyntax value:
                        binder.BindValue(value.Value);
                        break;
                    case CasePatternSwitchLabelSyntax pattern: {
                        List<BoundNode> parts = [];
                        binder.BindPattern(pattern.Pattern, parts);
                        if (pattern.WhenClause is { } when) {
                            binder.BindCondition(when.Condition);
                        }

                        break;
                    }
                }
            }

            foreach (var statement in section.Statements) {
                statements.Add(binder.BindStatement(statement));
            }
        }

        return new BoundSwitchStatement(syntax, governing, statements);
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
