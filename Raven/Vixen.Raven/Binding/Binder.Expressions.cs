// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Syntax;
using Vixen.Raven.Diagnostics;
using Vixen.Raven.Symbols;
using Vixen.Raven.Syntax;

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

            case RangeExpressionSyntax range:
                return BindRange(range);

            case TupleExpressionSyntax tuple:
                return BindTuple(tuple);

            case CollectionExpressionSyntax collection:
                return BindCollection(collection);

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
            TypeSymbol type => new BoundTypeExpression(syntax, Construct(type, typeArguments, syntax)),
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
            // A user-defined operator is looked for only once the built-ins have failed, so a
            // declaration can never change what `float + float` means.
            if (BindUserDefinedOperator(syntax, operatorText, [left, right], [syntax.Left, syntax.Right]) is { } call) {
                return call;
            }

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

    /// <summary>
    ///     Resolves <c>a + b</c> or <c>-a</c> against an <c>operator</c> declared on one of the
    ///     operand types, or null when there is none.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Vector maths on a user type is the reason this exists: a <c>Spectrum</c> or a
    ///         <c>Complex</c> wants <c>a + b</c> to read as addition, and writing
    ///         <c>Spectrum.Add(a, b)</c> everywhere is what the operator is for. Nothing about it
    ///         needs anything a GPU lacks — it resolves statically to one function call.
    ///     </para>
    ///     <para>
    ///         Only reached after the built-in operators have failed, so a declaration can never
    ///         change what the primitives mean. Candidates are gathered from every operand's type,
    ///         which is how <c>Spectrum * float</c> finds an operator declared on <c>Spectrum</c>,
    ///         and ranked by the same conversion cost as an ordinary overload — so a declaration
    ///         taking the operands exactly beats one that needs a widening.
    ///     </para>
    /// </remarks>
    static BoundExpression? BindUserDefinedOperator(
        ExpressionSyntax syntax,
        string operatorText,
        BoundExpression[] operands,
        ExpressionSyntax[] operandSyntax
    ) {
        if (operands.Any(o => o.Type.IsErrorType)) {
            return null;
        }

        var name = "operator" + operatorText;
        var arguments = operands
            .Select((operand, i) => new BoundArgument(null, operand, operandSyntax[i]))
            .ToArray();

        List<(MethodSymbol Method, BoundExpression[] Arguments, int Cost)> applicable = [];
        HashSet<MethodSymbol> seen = [];

        foreach (var type in operands.Select(o => o.Type).Distinct()) {
            foreach (var member in LookupMembers(type, name)) {
                if (member is not MethodSymbol { MethodKind: MethodKind.Operator } candidate
                    || candidate.Parameters.Count != operands.Length
                    || !seen.Add(candidate)) {
                    continue;
                }

                if (TryMapArguments(candidate, arguments, syntax, out var mapped, out var cost)) {
                    applicable.Add((candidate, mapped, cost));
                }
            }
        }

        if (applicable.Count == 0) {
            return null;
        }

        var best = applicable.MinBy(c => c.Cost);

        // No receiver: an operator takes every operand as an explicit parameter.
        return new BoundInvocationExpression(syntax, null, best.Method, best.Arguments);
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
            if (BindUserDefinedOperator(syntax, OperatorText(operatorKind), [operand], [operandSyntax]) is { } call) {
                return call;
            }

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
        CheckBindingIsWritable(target, syntax);

        switch (target) {
            case BoundLocalExpression { Local.IsReadOnly: true } local:
                Report(SemanticDiagnostics.NotAssignable, syntax, local.Local.Name);
                break;

            // Before the read-only case, and with no initializer exemption: a permutation
            // key's value is fixed when the shader is compiled, so even a constructor
            // cannot set it. The dedicated message says why; "not assignable" would not.
            case BoundFieldExpression { Field.IsPermutation: true } permutation:
                Report(SemanticDiagnostics.PermutationCannotBeAssigned, syntax, permutation.Field.Name);
                break;

            case BoundFieldExpression { Field.IsCompose: true } slot:
                Report(SemanticDiagnostics.ComposeCannotBeAssigned, syntax, slot.Field.Name);
                break;

            case BoundFieldExpression { Field: Symbols.Source.SourceValueParameterSymbol } parameter:
                Report(SemanticDiagnostics.ValueParameterCannotBeAssigned, syntax, parameter.Field.Name);
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

    /// <summary>
    ///     Refuses a write that lands inside a binding the host uploads.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Checked at the <em>root</em> of the access chain, not at the target, because
    ///         <c>tint.rgb</c>, <c>lights[i].color</c> and <c>tint</c> are all writes to the same
    ///         binding and only the innermost expression says which binding that is.
    ///     </para>
    ///     <para>
    ///         This was refused by nobody until there was something writable to suggest instead —
    ///         both reference compilers rejected the store, and Raven emitted it in silence. A
    ///         <c>RWBuffer&lt;T&gt;</c> is what makes the diagnostic actionable rather than a dead end.
    ///     </para>
    /// </remarks>
    void CheckBindingIsWritable(BoundExpression target, ExpressionSyntax syntax) {
        if (RootBinding(target) is not { } field) {
            return;
        }

        // Assigning the *descriptor* is never a write into what it points at. A writable resource
        // is written through — `data[i] = x`, `image.Store(…)` — and neither target has an
        // assignment for the handle itself, so this is refused even for the writable forms.
        if (target is BoundFieldExpression whole
            && ReferenceEquals(whole.Field, field)
            && field.Type.ResourceKind is not (ResourceKind.None or ResourceKind.Uniform)) {
            Report(
                SemanticDiagnostics.CannotWriteToBinding,
                syntax,
                field.Name,
                $"a '{field.Type.ToDisplayString()}' is a descriptor rather than a value, so it is "
                + "written through rather than assigned to"
            );

            return;
        }

        if (field.Type.IsWritableResource) {
            return;
        }

        // A read-only buffer is a one-character fix, so it gets its own reason; anything else is
        // host-uploaded state with no writable counterpart to point at.
        var reason = field.Type is BufferTypeSymbol
            ? $"a '{BufferTypeSymbol.ReadOnlyName}' is read-only — declare it "
            + $"'{BufferTypeSymbol.ReadWriteName}' to store into it"
            : "it is a binding the host supplies, and a shader cannot write back to one";

        Report(SemanticDiagnostics.CannotWriteToBinding, syntax, field.Name, reason);
    }

    /// <summary>The binding an access chain bottoms out in, or null when it reaches local storage.</summary>
    static FieldSymbol? RootBinding(BoundExpression expression) =>
        expression switch {
            BoundFieldExpression { Field.IsBinding: true } field => field.Field,
            BoundFieldExpression { Receiver: { } receiver } => RootBinding(receiver),
            BoundArrayAccessExpression access => RootBinding(access.Receiver),
            _ => null
        };

    /// <summary>
    ///     The <c>groupshared</c> declaration an access chain bottoms out in, or null.
    /// </summary>
    /// <remarks>
    ///     Separate from <see cref="RootBinding" /> rather than folded into it, because the two
    ///     answer different questions. A binding is host-supplied state, which is why writing one is
    ///     refused; workgroup storage is the shader's own, which is why writing one is the point.
    ///     What they have in common is only what an atomic needs — more than one invocation reaches
    ///     it — so that is the one place both are asked.
    /// </remarks>
    static FieldSymbol? RootGroupShared(BoundExpression expression) =>
        expression switch {
            BoundFieldExpression { Field.IsGroupShared: true } field => field.Field,
            BoundFieldExpression { Receiver: { } receiver } => RootGroupShared(receiver),
            BoundArrayAccessExpression access => RootGroupShared(access.Receiver),
            _ => null
        };

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
        List<BoundCollectionElement> elements = [];
        TypeSymbol? elementType = null;

        // The literal's own length, which is what makes it a *sized* array — and what lets a
        // spread be flattened at all. It goes to null the moment one contribution is unknown,
        // because a length that is right for all but one element is not a length.
        int? length = 0;

        foreach (var element in syntax.Elements) {
            var isSpread = element is SpreadElementSyntax;
            var expression = element switch {
                ExpressionElementSyntax value => value.Expression,
                SpreadElementSyntax spread => spread.Expression,
                _ => null
            };

            if (expression is null) {
                continue;
            }

            var bound = BindValue(expression);

            // A spread contributes its element type and its own count, not itself and one.
            var spreadOf = isSpread ? bound.Type as ArrayTypeSymbol : null;
            var contributed = spreadOf?.ElementType ?? bound.Type;

            elements.Add(new(bound, isSpread));
            length = isSpread ? Add(length, spreadOf?.Length) : Add(length, 1);

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
            new ArrayTypeSymbol(elementType ?? ErrorTypeSymbol.Instance, 1, length)
        );

        static int? Add(int? total, int? contribution) =>
            total is { } a && contribution is { } b ? a + b : null;
    }

    /// <summary>Binds an expression only to learn its type; its diagnostics are discarded.</summary>
    internal TypeSymbol InferType(ExpressionSyntax syntax) {
        var speculative = new BindingContext(Compilation, new());
        return new ContextBinder(this, speculative).BindExpression(syntax).Type;
    }
}
