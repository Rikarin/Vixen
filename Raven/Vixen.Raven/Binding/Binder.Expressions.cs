// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Raven.Diagnostics;
using Vixen.Raven.Symbols;
using Vixen.Raven.Syntax;
using Vixen.Core.Syntax;

namespace Vixen.Raven.Binding;

/// <summary>Expression binding: names to symbols, expressions to types.</summary>
public abstract partial class Binder {
    /// <summary>
    ///     Binds an expression. The result may denote a namespace, a type or a
    ///     method group as well as a value — use <see cref="BindValue" /> where only a
    ///     value will do.
    /// </summary>
    public BoundExpression BindExpression(ExpressionSyntax syntax) {
        var bound = BindExpressionCore(syntax);
        Context.Record(syntax, bound);
        return bound;
    }

    /// <summary>Binds an expression that must produce a value.</summary>
    public BoundExpression BindValue(ExpressionSyntax syntax) {
        var bound = BindExpression(syntax);

        switch (bound) {
            case BoundTypeExpression type when !type.ReferencedType.IsErrorType:
                Report(SemanticDiagnostics.TypeUsedAsValue, syntax, type.ReferencedType.ToDisplayString());
                return new BoundErrorExpression(syntax);

            case BoundNamespaceExpression ns:
                Report(SemanticDiagnostics.TypeUsedAsValue, syntax, ns.Namespace.ToDisplayString());
                return new BoundErrorExpression(syntax);

            case BoundMethodGroupExpression group:
                Report(SemanticDiagnostics.NotInvocable, syntax, group.Methods[0].Name);
                return new BoundErrorExpression(syntax);

            default:
                return bound;
        }
    }

    BoundExpression BindExpressionCore(ExpressionSyntax syntax) {
        switch (syntax) {
            case LiteralExpressionSyntax literal: {
                // Strings exist for attributes, which are read off the syntax and
                // never bound; a string in expression position has no value.
                if (literal.Kind == SyntaxKind.StringLiteralExpression) {
                    Report(SemanticDiagnostics.StringLiteralIsNotAValue, literal);
                    return new BoundErrorExpression(literal);
                }

                var (type, value) = LiteralParser.Parse(literal);
                return new BoundLiteralExpression(literal, type, value);
            }

            case ParenthesizedExpressionSyntax parenthesized:
                return BindExpression(parenthesized.Expression);

            case PredefinedTypeSyntax or ArrayTypeSyntax or TupleTypeSyntax:
                return new BoundTypeExpression(syntax, BindType((TypeSyntax)syntax));

            case SimpleNameSyntax simple:
                return BindSimpleName(simple);

            case QualifiedNameSyntax qualified:
                return BindQualifiedNameExpression(qualified);

            case MemberAccessExpressionSyntax memberAccess:
                return BindMemberAccess(memberAccess);

            case InvocationExpressionSyntax invocation:
                return BindInvocation(invocation);

            case ElementAccessExpressionSyntax elementAccess:
                return BindElementAccess(elementAccess);

            case SelfExpressionSyntax:
                return BindSelf(syntax);

            case BaseExpressionSyntax:
                return BindBase(syntax);

            case BinaryExpressionSyntax binary:
                return BindBinary(binary);

            case AssignmentExpressionSyntax assignment:
                return BindAssignment(assignment);

            case PrefixUnaryExpressionSyntax prefix:
                return BindUnary(prefix, prefix.Operand, MapPrefixOperator(prefix.Kind));

            case PostfixUnaryExpressionSyntax postfix:
                return BindUnary(postfix, postfix.Operand, MapPostfixOperator(postfix.Kind));

            case ConditionalExpressionSyntax conditional:
                return BindConditional(conditional);

            case CastExpressionSyntax cast:
                return ConvertExplicit(BindValue(cast.Expression), BindType(cast.Type), cast);

            case DefaultExpressionSyntax @default:
                return new BoundLiteralExpression(@default, BindType(@default.Type), null);

            case SizeOfExpressionSyntax sizeOf: {
                BindType(sizeOf.Type);
                return new BoundLiteralExpression(sizeOf, BuiltInTypes.Int, null);
            }

            case RefExpressionSyntax @ref: {
                var operand = BindValue(@ref.Expression);
                return operand;
            }

            case RangeExpressionSyntax range:
                return BindRange(range);

            case TupleExpressionSyntax tuple:
                return BindTuple(tuple);

            case CollectionExpressionSyntax collection:
                return BindCollection(collection);

            case IsPatternExpressionSyntax isPattern: {
                var operand = BindValue(isPattern.Expression);
                List<BoundNode> parts = [];
                BindPattern(isPattern.Pattern, parts);
                return new BoundIsPatternExpression(isPattern, operand, parts);
            }

            case SwitchExpressionSyntax switchExpression:
                return BindSwitchExpression(switchExpression);

            case DeclarationExpressionSyntax declaration:
                return BindDeclarationExpression(declaration);

            case MemberBindingExpressionSyntax:
                // `.Name` only means something inside a conditional-access chain,
                // which the grammar does not currently produce.
                return new BoundErrorExpression(syntax);

            default:
                Report(SemanticDiagnostics.UndefinedName, syntax, syntax.ToString().Trim());
                return new BoundErrorExpression(syntax);
        }
    }

    // --- Names -------------------------------------------------------------

    BoundExpression BindSimpleName(SimpleNameSyntax syntax) {
        var name = syntax.Identifier.ValueText;
        var typeArguments = syntax is GenericNameSyntax generic
            ? generic.TypeArgumentList.Arguments.Select(BindType).ToArray()
            : [];

        var candidates = Lookup(name);

        if (candidates.Count == 0) {
            Report(SemanticDiagnostics.UndefinedName, syntax, name);
            return new BoundErrorExpression(syntax);
        }

        var methods = candidates.OfType<MethodSymbol>().ToArray();
        if (methods.Length > 0) {
            return new BoundMethodGroupExpression(syntax, ImplicitReceiver(syntax, methods[0]), methods, typeArguments);
        }

        return candidates[0] switch {
            LocalSymbol local => new BoundLocalExpression(syntax, local),
            ParameterSymbol parameter => new BoundParameterExpression(syntax, parameter),
            FieldSymbol field => new BoundFieldExpression(syntax, ImplicitReceiver(syntax, field), field),
            PropertySymbol property =>
                new BoundPropertyExpression(syntax, ImplicitReceiver(syntax, property), property, []),
            NamespaceSymbol ns => new BoundNamespaceExpression(syntax, ns),
            TypeSymbol type => new BoundTypeExpression(
                syntax,
                typeArguments.Length > 0 && type is NamedTypeSymbol named
                    ? new ConstructedNamedTypeSymbol(named, typeArguments)
                    : type
            ),
            _ => new BoundErrorExpression(syntax)
        };
    }

    /// <summary>The implied <c>self</c> for an unqualified instance member reference.</summary>
    BoundExpression? ImplicitReceiver(SyntaxNode syntax, Symbol member) {
        if (member.IsStatic || ContainingType is null) {
            return null;
        }

        // Only members of the enclosing type or one of its bases get an implicit
        // receiver; anything else reached this scope some other way.
        return member.ContainingSymbol is TypeSymbol declaring && ContainingType.IsSubtypeOf(declaring)
            ? new BoundSelfExpression(syntax, ContainingType)
            : null;
    }

    BoundExpression BindQualifiedNameExpression(QualifiedNameSyntax syntax) {
        var left = BindExpression(syntax.Left);
        return BindMemberOf(left, syntax.Right, syntax);
    }

    BoundExpression BindMemberAccess(MemberAccessExpressionSyntax syntax) {
        var receiver = BindExpression(syntax.Expression);
        return BindMemberOf(receiver, syntax.Name, syntax);
    }

    BoundExpression BindMemberOf(BoundExpression receiver, SimpleNameSyntax nameSyntax, ExpressionSyntax syntax) {
        var name = nameSyntax.Identifier.ValueText;
        var typeArguments = nameSyntax is GenericNameSyntax generic
            ? generic.TypeArgumentList.Arguments.Select(BindType).ToArray()
            : [];

        switch (receiver) {
            case BoundErrorExpression:
                return new BoundErrorExpression(syntax);

            case BoundNamespaceExpression ns: {
                var members = ns.Namespace.GetMembers(name);
                foreach (var member in members) {
                    switch (member) {
                        case NamespaceSymbol nested:
                            return new BoundNamespaceExpression(syntax, nested);
                        case NamedTypeSymbol type when type.Arity == typeArguments.Length:
                            return new BoundTypeExpression(
                                syntax,
                                typeArguments.Length > 0 ? new ConstructedNamedTypeSymbol(type, typeArguments) : type
                            );
                    }
                }

                Report(SemanticDiagnostics.MemberNotFound, syntax, ns.Namespace.ToDisplayString(), name);
                return new BoundErrorExpression(syntax);
            }

            case BoundTypeExpression typeExpression:
                return BindMemberOfType(null, typeExpression.ReferencedType, name, typeArguments, syntax);

            default:
                return receiver.Type.IsErrorType
                    ? new BoundErrorExpression(syntax)
                    : BindMemberOfType(receiver, receiver.Type, name, typeArguments, syntax);
        }
    }

    BoundExpression BindMemberOfType(
        BoundExpression? receiver,
        TypeSymbol type,
        string name,
        IReadOnlyList<TypeSymbol> typeArguments,
        ExpressionSyntax syntax
    ) {
        var members = LookupMembers(type, name);

        if (members.Count == 0) {
            Report(SemanticDiagnostics.MemberNotFound, syntax, type.ToDisplayString(), name);
            return new BoundErrorExpression(syntax);
        }

        var methods = members.OfType<MethodSymbol>().ToArray();
        if (methods.Length > 0) {
            return new BoundMethodGroupExpression(syntax, receiver, methods, typeArguments);
        }

        return members[0] switch {
            FieldSymbol field => new BoundFieldExpression(syntax, field.IsStatic ? null : receiver, field),
            PropertySymbol property =>
                new BoundPropertyExpression(syntax, property.IsStatic ? null : receiver, property, []),
            NamedTypeSymbol nested => new BoundTypeExpression(
                syntax,
                typeArguments.Count > 0 ? new ConstructedNamedTypeSymbol(nested, typeArguments) : nested
            ),
            _ => new BoundErrorExpression(syntax)
        };
    }

    BoundExpression BindSelf(ExpressionSyntax syntax) {
        if (ContainingType is null) {
            Report(SemanticDiagnostics.SelfOutsideType, syntax, "self");
            return new BoundErrorExpression(syntax);
        }

        return new BoundSelfExpression(syntax, ContainingType);
    }

    BoundExpression BindBase(ExpressionSyntax syntax) {
        if (ContainingType is null) {
            Report(SemanticDiagnostics.SelfOutsideType, syntax, "base");
            return new BoundErrorExpression(syntax);
        }

        if (ContainingType.BaseType is not { } baseType) {
            Report(SemanticDiagnostics.NoBaseType, syntax, ContainingType.ToDisplayString());
            return new BoundErrorExpression(syntax);
        }

        return new BoundBaseExpression(syntax, baseType);
    }

    // --- Operators ---------------------------------------------------------

    BoundExpression BindBinary(BinaryExpressionSyntax syntax) {
        var operatorText = syntax.OperatorToken.Text;

        switch (operatorText) {
            case "is": {
                var operand = BindValue(syntax.Left);
                BindType(syntax.Right as TypeSyntax);
                return new BoundIsPatternExpression(syntax, operand, []);
            }

            case "as": {
                var operand = BindValue(syntax.Left);
                var target = BindType(syntax.Right as TypeSyntax);
                return new BoundConversionExpression(syntax, operand, target, new(ConversionKind.ExplicitReference));
            }
        }

        var left = BindValue(syntax.Left);
        var right = BindValue(syntax.Right);

        if (MapBinaryOperator(operatorText) is not { } kind) {
            Report(
                SemanticDiagnostics.BinaryOperatorNotDefined,
                syntax,
                operatorText,
                left.Type.ToDisplayString(),
                right.Type.ToDisplayString()
            );
            return new BoundErrorExpression(syntax, [left, right]);
        }

        if (ResolveBinaryOperator(kind, left.Type, right.Type) is not { } signature) {
            if (!left.Type.IsErrorType && !right.Type.IsErrorType) {
                Report(
                    SemanticDiagnostics.BinaryOperatorNotDefined,
                    syntax,
                    operatorText,
                    left.Type.ToDisplayString(),
                    right.Type.ToDisplayString()
                );
            }

            return new BoundErrorExpression(syntax, [left, right]);
        }

        return new BoundBinaryExpression(
            syntax,
            kind,
            Convert(left, signature.LeftType, syntax.Left),
            Convert(right, signature.RightType, syntax.Right),
            signature.ResultType
        );
    }

    BoundExpression BindUnary(ExpressionSyntax syntax, ExpressionSyntax operandSyntax, UnaryOperatorKind? kind) {
        var operand = BindValue(operandSyntax);

        if (kind is not { } operatorKind) {
            Report(
                SemanticDiagnostics.UnaryOperatorNotDefined,
                syntax,
                syntax.ToString().Trim(),
                operand.Type.ToDisplayString()
            );
            return new BoundErrorExpression(syntax, [operand]);
        }

        if (ResolveUnaryOperator(operatorKind, operand.Type) is not { } resultType) {
            if (!operand.Type.IsErrorType) {
                Report(
                    SemanticDiagnostics.UnaryOperatorNotDefined,
                    syntax,
                    OperatorText(operatorKind),
                    operand.Type.ToDisplayString()
                );
            }

            return new BoundErrorExpression(syntax, [operand]);
        }

        if (operatorKind is UnaryOperatorKind.PreIncrement
            or UnaryOperatorKind.PreDecrement
            or UnaryOperatorKind.PostIncrement
            or UnaryOperatorKind.PostDecrement) {
            CheckAssignable(operand, operandSyntax);
        }

        return new BoundUnaryExpression(syntax, operatorKind, operand, resultType);
    }

    static UnaryOperatorKind? MapPrefixOperator(SyntaxKind kind) =>
        kind switch {
            SyntaxKind.UnaryPlusExpression => UnaryOperatorKind.Plus,
            SyntaxKind.UnaryMinusExpression => UnaryOperatorKind.Minus,
            SyntaxKind.BitwiseNotExpression => UnaryOperatorKind.BitwiseNot,
            SyntaxKind.LogicalNotExpression => UnaryOperatorKind.LogicalNot,
            SyntaxKind.PreIncrementExpression => UnaryOperatorKind.PreIncrement,
            SyntaxKind.PreDecrementExpression => UnaryOperatorKind.PreDecrement,
            SyntaxKind.IndexExpression => UnaryOperatorKind.IndexFromEnd,
            _ => null
        };

    static UnaryOperatorKind? MapPostfixOperator(SyntaxKind kind) =>
        kind switch {
            SyntaxKind.PostIncrementExpression => UnaryOperatorKind.PostIncrement,
            SyntaxKind.PostDecrementExpression => UnaryOperatorKind.PostDecrement,
            _ => null
        };

    static string OperatorText(UnaryOperatorKind kind) =>
        kind switch {
            UnaryOperatorKind.Plus => "+",
            UnaryOperatorKind.Minus => "-",
            UnaryOperatorKind.BitwiseNot => "~",
            UnaryOperatorKind.LogicalNot => "!",
            UnaryOperatorKind.PreIncrement or UnaryOperatorKind.PostIncrement => "++",
            UnaryOperatorKind.PreDecrement or UnaryOperatorKind.PostDecrement => "--",
            UnaryOperatorKind.IndexFromEnd => "^",
            _ => "!"
        };

    // --- Assignment --------------------------------------------------------

    BoundExpression BindAssignment(AssignmentExpressionSyntax syntax) {
        var target = BindValue(syntax.Left);
        var value = BindValue(syntax.Right);
        var operatorText = syntax.OperatorToken.Text;

        CheckAssignable(target, syntax.Left);

        if (operatorText == "=") {
            return new BoundAssignmentExpression(syntax, target, Convert(value, target.Type, syntax.Right), null);
        }

        if (MapCompoundAssignment(operatorText) is not { } kind
            || ResolveBinaryOperator(kind, target.Type, value.Type) is not { } signature) {
            if (!target.Type.IsErrorType && !value.Type.IsErrorType) {
                Report(
                    SemanticDiagnostics.BinaryOperatorNotDefined,
                    syntax,
                    operatorText,
                    target.Type.ToDisplayString(),
                    value.Type.ToDisplayString()
                );
            }

            return new BoundErrorExpression(syntax, [target, value]);
        }

        // The result of the operation has to fit back into the target.
        var converted = Convert(value, signature.RightType, syntax.Right);
        if (!signature.ResultType.IsErrorType
            && !Conversions.HasImplicitConversion(signature.ResultType, target.Type)) {
            Report(
                SemanticDiagnostics.CannotConvert,
                syntax,
                signature.ResultType.ToDisplayString(),
                target.Type.ToDisplayString()
            );
        }

        return new BoundAssignmentExpression(syntax, target, converted, kind);
    }

    void CheckAssignable(BoundExpression target, ExpressionSyntax syntax) {
        switch (target) {
            case BoundLocalExpression { Local.IsReadOnly: true } local:
                Report(SemanticDiagnostics.NotAssignable, syntax, local.Local.Name);
                break;

            case BoundFieldExpression { Field.IsReadOnly: true } field
                when !IsInsideInitializerOf(field.Field):
                Report(SemanticDiagnostics.NotAssignable, syntax, field.Field.Name);
                break;

            case BoundPropertyExpression { Property.HasSetter: false } property:
                Report(SemanticDiagnostics.NotAssignable, syntax, property.Property.Name);
                break;

            case BoundLocalExpression
                or BoundFieldExpression
                or BoundPropertyExpression
                or BoundParameterExpression
                or BoundArrayAccessExpression
                or BoundErrorExpression:
                break;

            default:
                Report(SemanticDiagnostics.NotAnLValue, syntax);
                break;
        }
    }

    /// <summary>A <c>val</c> field may still be assigned from its type's constructor.</summary>
    bool IsInsideInitializerOf(FieldSymbol field) =>
        ContainingMember is MethodSymbol { MethodKind: MethodKind.Constructor } method
        && ReferenceEquals(method.ContainingSymbol, field.ContainingSymbol);

    // --- Composite expressions ---------------------------------------------

    BoundExpression BindConditional(ConditionalExpressionSyntax syntax) {
        var condition = BindCondition(syntax.Condition);
        var whenTrue = BindValue(syntax.WhenTrue);
        var whenFalse = BindValue(syntax.WhenFalse);

        var common = Conversions.FindCommonType(whenTrue.Type, whenFalse.Type);
        if (common is null) {
            if (!whenTrue.Type.IsErrorType && !whenFalse.Type.IsErrorType) {
                Report(
                    SemanticDiagnostics.CannotConvert,
                    syntax,
                    whenFalse.Type.ToDisplayString(),
                    whenTrue.Type.ToDisplayString()
                );
            }

            common = ErrorTypeSymbol.Instance;
        }

        return new BoundConditionalExpression(
            syntax,
            condition,
            Convert(whenTrue, common, syntax.WhenTrue),
            Convert(whenFalse, common, syntax.WhenFalse),
            common
        );
    }

    BoundExpression BindRange(RangeExpressionSyntax syntax) {
        var left = BindValue(syntax.Left);
        var right = BindValue(syntax.Right);

        var element = Conversions.FindCommonType(left.Type, right.Type) ?? BuiltInTypes.Int;
        return new BoundRangeExpression(syntax, left, right, new SequenceTypeSymbol(element));
    }

    BoundExpression BindTuple(TupleExpressionSyntax syntax) {
        List<BoundExpression> elements = [];
        List<TypeSymbol> types = [];
        List<string?> names = [];

        foreach (var argument in syntax.Arguments) {
            var bound = BindValue(argument.Expression);
            elements.Add(bound);
            types.Add(bound.Type);
            names.Add(argument.NameColon?.Name.Identifier.ValueText);
        }

        return new BoundTupleExpression(syntax, elements, new TupleTypeSymbol(types, names));
    }

    BoundExpression BindCollection(CollectionExpressionSyntax syntax) {
        List<BoundExpression> elements = [];
        TypeSymbol? elementType = null;

        foreach (var element in syntax.Elements) {
            var expression = element switch {
                ExpressionElementSyntax value => value.Expression,
                SpreadElementSyntax spread => spread.Expression,
                _ => null
            };

            if (expression is null) {
                continue;
            }

            var bound = BindValue(expression);

            // A spread contributes its element type, not its own.
            var contributed = element is SpreadElementSyntax && bound.Type is ArrayTypeSymbol array
                ? array.ElementType
                : bound.Type;

            elements.Add(bound);
            elementType = elementType is null ? contributed : Conversions.FindCommonType(elementType, contributed);

            if (elementType is null) {
                Report(
                    SemanticDiagnostics.CannotConvert,
                    expression,
                    contributed.ToDisplayString(),
                    "the element type"
                );
                elementType = ErrorTypeSymbol.Instance;
            }
        }

        return new BoundCollectionExpression(
            syntax,
            elements,
            new ArrayTypeSymbol(elementType ?? ErrorTypeSymbol.Instance)
        );
    }

    BoundExpression BindDeclarationExpression(DeclarationExpressionSyntax syntax) {
        var type = BindType(syntax.Type);
        DeclarePatternVariables(syntax.Designation, type);
        return new BoundTypeExpression(syntax, type);
    }

    // --- Lambdas -----------------------------------------------------------

    // --- Patterns (shallow) ------------------------------------------------

    /// <summary>
    ///     Walks a pattern, binding the expressions inside it and declaring any
    ///     variables it introduces. Type-test narrowing and exhaustiveness are not
    ///     modelled in this phase.
    /// </summary>
    void BindPattern(PatternSyntax? syntax, List<BoundNode> parts) {
        switch (syntax) {
            case null or DiscardPatternSyntax:
                return;

            case ConstantPatternSyntax constant:
                parts.Add(BindValue(constant.Expression));
                return;

            case RelationalPatternSyntax relational:
                parts.Add(BindValue(relational.Expression));
                return;

            case ParenthesizedPatternSyntax parenthesized:
                BindPattern(parenthesized.Pattern, parts);
                return;

            case UnaryPatternSyntax unary:
                BindPattern(unary.Pattern, parts);
                return;

            case BinaryPatternSyntax binary:
                BindPattern(binary.Left, parts);
                BindPattern(binary.Right, parts);
                return;

            case VarPatternSyntax var:
                DeclarePatternVariables(var.Designation, ErrorTypeSymbol.Instance);
                return;

            case ListPatternSyntax list: {
                foreach (var pattern in list.Patterns) {
                    BindPattern(pattern, parts);
                }

                DeclarePatternVariables(list.Designation, ErrorTypeSymbol.Instance);
                return;
            }

            case SlicePatternSyntax slice:
                BindPattern(slice.Pattern, parts);
                return;
        }
    }

    void DeclarePatternVariables(VariableDesignationSyntax? designation, TypeSymbol type) {
        switch (designation) {
            case SimpleVariableDesignationSyntax simple: {
                var local = new LocalSymbol(ContainingMember, simple.Identifier.ValueText, type, false, simple);
                DeclareLocal(local, simple);
                break;
            }

            case ParenthesizedVariableDesignationSyntax parenthesized: {
                foreach (var nested in parenthesized.Variables) {
                    DeclarePatternVariables(nested, type);
                }

                break;
            }
        }
    }

    BoundExpression BindSwitchExpression(SwitchExpressionSyntax syntax) {
        var governing = BindValue(syntax.GoverningExpression);

        List<BoundExpression> arms = [];
        TypeSymbol? common = null;

        foreach (var arm in syntax.Arms) {
            var armBinder = new BlockBinder(this);
            List<BoundNode> parts = [];
            armBinder.BindPattern(arm.Pattern, parts);

            if (arm.WhenClause is { } when) {
                armBinder.BindCondition(when.Condition);
            }

            var value = armBinder.BindValue(arm.Expression);
            arms.Add(value);
            common = common is null ? value.Type : Conversions.FindCommonType(common, value.Type);
        }

        return new BoundSwitchExpression(syntax, governing, arms, common ?? ErrorTypeSymbol.Instance);
    }

    /// <summary>Binds an expression only to learn its type; its diagnostics are discarded.</summary>
    internal TypeSymbol InferType(ExpressionSyntax syntax) {
        var speculative = new BindingContext(Compilation, new());
        return new ContextBinder(this, speculative).BindExpression(syntax).Type;
    }
}
