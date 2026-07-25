// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Raven.Diagnostics;
using Vixen.Raven.Symbols;
using Vixen.Raven.Syntax;
using Vixen.Core.Syntax;

namespace Vixen.Raven.Binding;

/// <summary>One argument of a call, with its name when the call site supplied one.</summary>
readonly record struct BoundArgument(string? Name, BoundExpression Expression, ExpressionSyntax Syntax);

/// <summary>Calls, constructions, overload resolution and indexing.</summary>
public abstract partial class Binder {
    BoundExpression BindInvocation(InvocationExpressionSyntax syntax) {
        var callee = BindExpression(syntax.Expression);
        var arguments = BindArguments(syntax.ArgumentList.Arguments);

        switch (callee) {
            case BoundMethodGroupExpression group:
                return BindCall(group, arguments, syntax);

            case BoundTypeExpression type:
                return BindConstruction(type.ReferencedType, arguments, syntax);

            case BoundErrorExpression:
                return new BoundErrorExpression(syntax, arguments.Select(a => a.Expression).ToArray());

            default:
                if (!callee.Type.IsErrorType) {
                    Report(SemanticDiagnostics.NotInvocable, syntax, syntax.Expression.ToString().Trim());
                }

                return new BoundErrorExpression(syntax, arguments.Select(a => a.Expression).ToArray());
        }
    }

    IReadOnlyList<BoundArgument> BindArguments(SeparatedSyntaxList<ArgumentSyntax> arguments) {
        List<BoundArgument> result = [];
        foreach (var argument in arguments) {
            result.Add(
                new(
                    argument.NameColon?.Name.Identifier.ValueText,
                    BindValue(argument.Expression),
                    argument.Expression
                )
            );
        }

        return result;
    }

    BoundExpression BindCall(
        BoundMethodGroupExpression group,
        IReadOnlyList<BoundArgument> arguments,
        ExpressionSyntax syntax
    ) {
        List<(MethodSymbol Method, BoundExpression[] Arguments, int Cost)> applicable = [];

        foreach (var candidate in group.Methods) {
            var method = candidate;

            if (group.TypeArguments.Count > 0) {
                if (candidate.Arity != group.TypeArguments.Count) {
                    continue;
                }

                // Explicit type arguments only; there is no inference yet.
                method = new SubstitutedMethodSymbol(
                    candidate,
                    candidate.ContainingSymbol,
                    new(candidate.TypeParameters, group.TypeArguments),
                    group.TypeArguments
                );
            }

            if (TryMapArguments(method, arguments, syntax, out var mapped, out var cost)) {
                applicable.Add((method, mapped, cost));
            }
        }

        var name = group.Methods[0].Name;

        if (applicable.Count == 0) {
            // Stay quiet when an argument already failed to bind.
            if (!arguments.Any(a => a.Expression.Type.IsErrorType)) {
                ReportNoOverload(group.Methods, arguments, name, syntax);
            }

            return new BoundErrorExpression(syntax, arguments.Select(a => a.Expression).ToArray());
        }

        var best = applicable.MinBy(c => c.Cost);
        var tied = applicable.Where(c => c.Cost == best.Cost).ToArray();

        if (tied.Length > 1) {
            Report(
                SemanticDiagnostics.AmbiguousInvocation,
                syntax,
                name,
                string.Join(" and ", tied.Take(2).Select(c => c.Method.ToDisplayString()))
            );
            return new BoundErrorExpression(syntax, arguments.Select(a => a.Expression).ToArray());
        }

        return new BoundInvocationExpression(syntax, group.Receiver, best.Method, best.Arguments);
    }

    void ReportNoOverload(
        IReadOnlyList<MethodSymbol> candidates,
        IReadOnlyList<BoundArgument> arguments,
        string name,
        SyntaxNode syntax
    ) {
        // A single candidate with the wrong arity gets the more specific message.
        if (candidates.Count == 1
            && (arguments.Count > candidates[0].Parameters.Count
                || arguments.Count < candidates[0].MinimumArgumentCount)) {
            Report(
                SemanticDiagnostics.WrongArgumentCount,
                syntax,
                candidates[0].ToDisplayString(),
                candidates[0].Parameters.Count,
                arguments.Count
            );
            return;
        }

        var signature = string.Join(", ", arguments.Select(a => a.Expression.Type.ToDisplayString()));
        Report(SemanticDiagnostics.NoApplicableOverload, syntax, name, signature);
    }

    static bool TryMapArguments(
        MethodSymbol method,
        IReadOnlyList<BoundArgument> arguments,
        SyntaxNode syntax,
        out BoundExpression[] mapped,
        out int cost
    ) {
        var parameters = method.Parameters;
        mapped = [];
        cost = 0;

        var slots = new BoundArgument?[parameters.Count];
        var positional = 0;

        foreach (var argument in arguments) {
            if (argument.Name is null) {
                if (positional >= parameters.Count) {
                    return false;
                }

                slots[positional++] = argument;
                continue;
            }

            var index = -1;
            for (var i = 0; i < parameters.Count; i++) {
                if (parameters[i].Name == argument.Name) {
                    index = i;
                    break;
                }
            }

            if (index < 0 || slots[index] is not null) {
                return false;
            }

            slots[index] = argument;
        }

        var result = new BoundExpression[parameters.Count];

        for (var i = 0; i < parameters.Count; i++) {
            var parameter = parameters[i];

            if (slots[i] is { } supplied) {
                var conversion = ClassifyConversion(supplied.Expression, parameter.Type);
                if (!conversion.Exists || !conversion.IsImplicit) {
                    return false;
                }

                cost += conversion.Cost;
                result[i] = conversion.IsIdentity
                    ? supplied.Expression
                    : new BoundConversionExpression(supplied.Syntax, supplied.Expression, parameter.Type, conversion);
                continue;
            }

            if (!parameter.HasDefaultValue) {
                return false;
            }

            result[i] = new BoundLiteralExpression(syntax, parameter.Type, parameter.DefaultValue);
        }

        mapped = result;
        return true;
    }

    // --- Construction ------------------------------------------------------

    /// <summary>
    ///     Binds <c>T(args)</c>. For scalars that is a conversion, for vectors and
    ///     matrices a componentwise build, and for named types a constructor call.
    /// </summary>
    BoundExpression BindConstruction(
        TypeSymbol type,
        IReadOnlyList<BoundArgument> arguments,
        ExpressionSyntax syntax
    ) {
        var values = arguments.Select(a => a.Expression).ToArray();

        if (type.IsErrorType) {
            return new BoundErrorExpression(syntax, values);
        }

        if (type is PrimitiveTypeSymbol primitive) {
            return BindPrimitiveConstruction(primitive, arguments, syntax);
        }

        if (type is not NamedTypeSymbol named) {
            Report(SemanticDiagnostics.NoConstructor, syntax, type.ToDisplayString());
            return new BoundErrorExpression(syntax, values);
        }

        var constructors = named.Constructors;

        if (constructors.Count == 0) {
            if (arguments.Count == 0) {
                return new BoundObjectCreationExpression(syntax, named, null, []);
            }

            return BindPositionalConstruction(named, arguments, syntax);
        }

        List<(MethodSymbol Method, BoundExpression[] Arguments, int Cost)> applicable = [];
        foreach (var constructor in constructors) {
            if (TryMapArguments(constructor, arguments, syntax, out var mapped, out var cost)) {
                applicable.Add((constructor, mapped, cost));
            }
        }

        if (applicable.Count == 0) {
            Report(SemanticDiagnostics.NoConstructor, syntax, named.ToDisplayString());
            return new BoundErrorExpression(syntax, values);
        }

        var best = applicable.MinBy(c => c.Cost);
        return new BoundObjectCreationExpression(syntax, named, best.Method, best.Arguments);
    }

    /// <summary>
    ///     Binds <c>S(a, b)</c> for a struct that declares no <c>init</c>: one argument per
    ///     field, in declaration order.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This is what every target already does. GLSL generates a positional constructor for
    ///         each struct; HLSL and WGSL spell the same thing as an aggregate initialiser. Without
    ///         it Raven was stricter than all of them — a plain data struct needed a hand-written
    ///         <c>init</c> that assigned each field to the parameter of the same name, which is
    ///         boilerplate for a library full of small types like <c>Surface</c> or <c>BrdfSample</c>.
    ///     </para>
    ///     <para>
    ///         No synthesized symbol and no generated function: the result is the same
    ///         constructor-less <see cref="BoundObjectCreationExpression" /> that a vector or matrix
    ///         build produces, which lowering already turns into one
    ///         <c>IrConstructInstruction</c>. That is why the field filter here has to mirror
    ///         <c>Lowerer.LowerStruct</c>'s exactly — the arguments are matched to IR fields by
    ///         position.
    ///     </para>
    /// </remarks>
    BoundExpression BindPositionalConstruction(
        NamedTypeSymbol type,
        IReadOnlyList<BoundArgument> arguments,
        ExpressionSyntax syntax
    ) {
        var values = arguments.Select(a => a.Expression).ToArray();

        // Only a struct is data. A shader is a pipeline, a protocol has no storage, and an
        // enum's members are constants.
        if (type.TypeKind != TypeKind.Struct) {
            Report(SemanticDiagnostics.NoConstructor, syntax, type.ToDisplayString());
            return new BoundErrorExpression(syntax, values);
        }

        var fields = type.GetMembers().OfType<FieldSymbol>().Where(f => !f.IsConst && !f.IsCompose).ToArray();

        if (fields.Length != arguments.Count) {
            Report(
                SemanticDiagnostics.WrongArgumentCount,
                syntax,
                type.ToDisplayString(),
                fields.Length,
                arguments.Count
            );
            return new BoundErrorExpression(syntax, values);
        }

        // Positional only. A named form would have to agree with the field order the IR uses,
        // and there is no reason to offer two spellings of the same build.
        List<BoundExpression> converted = [];

        for (var i = 0; i < fields.Length; i++) {
            if (arguments[i].Name is not null) {
                Report(SemanticDiagnostics.NoConstructor, syntax, type.ToDisplayString());
                return new BoundErrorExpression(syntax, values);
            }

            var conversion = ClassifyConversion(values[i], fields[i].Type);
            if (!conversion.Exists || !conversion.IsImplicit) {
                Report(
                    SemanticDiagnostics.CannotConvert,
                    arguments[i].Syntax,
                    values[i].Type.ToDisplayString(),
                    fields[i].Type.ToDisplayString()
                );
                return new BoundErrorExpression(syntax, values);
            }

            converted.Add(Convert(values[i], fields[i].Type, arguments[i].Syntax));
        }

        return new BoundObjectCreationExpression(syntax, type, null, [.. converted]);
    }

    BoundExpression BindPrimitiveConstruction(
        PrimitiveTypeSymbol type,
        IReadOnlyList<BoundArgument> arguments,
        ExpressionSyntax syntax
    ) {
        var values = arguments.Select(a => a.Expression).ToArray();

        // A single scalar broadcasts across every lane: `float3(0)`, `mat3(1)`.
        if (values.Length == 1
            && type.TypeKind is TypeKind.Vector or TypeKind.Matrix
            && values[0].Type is PrimitiveTypeSymbol { TypeKind: TypeKind.Scalar }) {
            return new BoundObjectCreationExpression(
                syntax,
                type,
                null,
                [Convert(values[0], type.ComponentType, arguments[0].Syntax)]
            );
        }

        // Any other single argument is a conversion.
        if (values.Length == 1) {
            return ConvertExplicit(values[0], type, syntax);
        }

        if (type.TypeKind == TypeKind.Scalar) {
            Report(SemanticDiagnostics.WrongArgumentCount, syntax, type.Name, 1, values.Length);
            return new BoundErrorExpression(syntax, values);
        }

        var component = type.ComponentType;
        var supplied = 0;
        List<BoundExpression> converted = [];

        foreach (var argument in arguments) {
            var argumentType = argument.Expression.Type;

            var lanes = argumentType is PrimitiveTypeSymbol { TypeKind: TypeKind.Vector or TypeKind.Matrix } vector
                ? vector.ComponentCount
                : 1;

            supplied += lanes;

            // Each argument contributes its lanes verbatim; only the component
            // scalar type has to line up.
            var target = lanes == 1 ? component : argumentType;
            converted.Add(lanes == 1 ? Convert(argument.Expression, target, argument.Syntax) : argument.Expression);
        }

        if (supplied != type.ComponentCount) {
            Report(SemanticDiagnostics.WrongArgumentCount, syntax, type.Name, type.ComponentCount, supplied);
            return new BoundErrorExpression(syntax, values);
        }

        return new BoundObjectCreationExpression(syntax, type, null, converted);
    }

    // --- Indexing ----------------------------------------------------------

    BoundExpression BindElementAccess(ElementAccessExpressionSyntax syntax) {
        var receiver = BindValue(syntax.Expression);
        var arguments = BindArguments(syntax.ArgumentList.Arguments);
        var indices = arguments.Select(a => a.Expression).ToArray();

        if (receiver.Type.IsErrorType) {
            return new BoundErrorExpression(syntax, indices);
        }

        // `a[1..3]` slices: the result keeps the container's shape.
        var isSlice = indices.Length == 1 && indices[0].Type is SequenceTypeSymbol;

        switch (receiver.Type) {
            case ArrayTypeSymbol array:
                return new BoundArrayAccessExpression(
                    syntax,
                    receiver,
                    ConvertIndices(indices, arguments),
                    isSlice ? array : array.ElementType
                );

            case SequenceTypeSymbol sequence:
                return new BoundArrayAccessExpression(
                    syntax,
                    receiver,
                    ConvertIndices(indices, arguments),
                    isSlice ? sequence : sequence.ElementType
                );

            case PrimitiveTypeSymbol { TypeKind: TypeKind.Vector } vector:
                return new BoundArrayAccessExpression(
                    syntax,
                    receiver,
                    ConvertIndices(indices, arguments),
                    vector.ComponentType
                );

            case PrimitiveTypeSymbol { TypeKind: TypeKind.Matrix } matrix: {
                // A column, not a row: as many lanes as the matrix has rows. Both targets index a
                // matrix by column, and storage makes that column the host matrix's row — see
                // docs/plan/07 § E.
                var column = BuiltInTypes.Vector(matrix.ComponentSpecialType, matrix.Rows);
                return new BoundArrayAccessExpression(
                    syntax,
                    receiver,
                    ConvertIndices(indices, arguments),
                    column ?? (TypeSymbol)ErrorTypeSymbol.Instance
                );
            }
        }

        // A user-defined indexer, declared as `self[…]`.
        foreach (var member in LookupMembers(receiver.Type, "self[]")) {
            if (member is PropertySymbol indexer && TryMapIndexerArguments(indexer, arguments, out var mapped)) {
                return new BoundPropertyExpression(syntax, receiver, indexer, mapped);
            }
        }

        Report(SemanticDiagnostics.CannotIndex, syntax, receiver.Type.ToDisplayString());
        return new BoundErrorExpression(syntax, indices);
    }

    BoundExpression[] ConvertIndices(IReadOnlyList<BoundExpression> indices, IReadOnlyList<BoundArgument> arguments) {
        var result = new BoundExpression[indices.Count];
        for (var i = 0; i < indices.Count; i++) {
            result[i] = indices[i].Type is SequenceTypeSymbol
                ? indices[i]
                : Convert(indices[i], BuiltInTypes.Int, arguments[i].Syntax);
        }

        return result;
    }

    static bool TryMapIndexerArguments(
        PropertySymbol indexer,
        IReadOnlyList<BoundArgument> arguments,
        out BoundExpression[] mapped
    ) {
        mapped = [];
        if (indexer.Parameters.Count != arguments.Count) {
            return false;
        }

        var result = new BoundExpression[arguments.Count];
        for (var i = 0; i < arguments.Count; i++) {
            var conversion = ClassifyConversion(arguments[i].Expression, indexer.Parameters[i].Type);
            if (!conversion.Exists || !conversion.IsImplicit) {
                return false;
            }

            result[i] = conversion.IsIdentity
                ? arguments[i].Expression
                : new BoundConversionExpression(
                    arguments[i].Syntax,
                    arguments[i].Expression,
                    indexer.Parameters[i].Type,
                    conversion
                );
        }

        mapped = result;
        return true;
    }
}
