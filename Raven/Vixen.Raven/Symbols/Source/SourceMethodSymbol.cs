// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Syntax;
using Vixen.Raven.Binding;
using Vixen.Raven.Diagnostics;
using Vixen.Raven.Syntax;

namespace Vixen.Raven.Symbols.Source;

/// <summary>
///     A callable declared in source: <c>func</c>, <c>init</c>, <c>~init</c>, an
///     operator, a conversion operator, or a local function.
/// </summary>
internal sealed class SourceMethodSymbol : MethodSymbol {
    readonly Binder binder;

    Binder? methodBinder;
    ParameterSymbol[]? parameters;
    bool resolvingReturnType;
    TypeSymbol? returnType;
    bool typeParameterConstraintsResolved;
    TypeParameterSymbol[]? typeParameters;
    Binder? typeScopedBinder;

    /// <summary>The declaration this method came from.</summary>
    public SyntaxNode Syntax { get; }

    public override Symbol? ContainingSymbol { get; }
    public override SyntaxNode DeclaringSyntax => Syntax;

    public override MethodKind MethodKind =>
        Syntax switch {
            ConstructorDeclarationSyntax => MethodKind.Constructor,
            OperatorDeclarationSyntax => MethodKind.Operator,
            _ => MethodKind.Ordinary
        };

    public override string Name =>
        Syntax switch {
            MethodDeclarationSyntax method => method.Identifier.ValueText,
            ConstructorDeclarationSyntax => ".ctor",
            OperatorDeclarationSyntax @operator => "operator" + @operator.OperatorToken.Text,
            _ => string.Empty
        };

    public override bool IsStatic => DeclarationFacts.Has(Modifiers, SyntaxKind.StaticKeyword);

    public override ShaderStage Stage => DeclarationFacts.GetShaderStage(AttributeLists);

    public override WorkgroupSize? WorkgroupSize => DeclarationFacts.GetWorkgroupSize(AttributeLists);

    public override string? SemanticName => DeclarationFacts.GetSemanticName(AttributeLists);

    public override IReadOnlyList<ParameterSymbol> Parameters => parameters ??= ResolveParameters();

    public override IReadOnlyList<TypeParameterSymbol> TypeParameters {
        get {
            EnsureTypeParameters();
            return typeParameters!;
        }
    }

    public override TypeSymbol ReturnType => returnType ??= ResolveReturnType();

    /// <summary>The block body, if the method has one.</summary>
    public BlockSyntax? Body =>
        Syntax switch {
            MethodDeclarationSyntax method => method.Body,
            ConstructorDeclarationSyntax constructor => constructor.Body,
            OperatorDeclarationSyntax @operator => @operator.Body,
            _ => null
        };

    /// <summary>The <c>=&gt; expression</c> body, if the method has one.</summary>
    public ArrowExpressionClauseSyntax? ExpressionBody =>
        Syntax switch {
            MethodDeclarationSyntax method => method.ExpressionBody,
            ConstructorDeclarationSyntax constructor => constructor.ExpressionBody,
            OperatorDeclarationSyntax @operator => @operator.ExpressionBody,
            _ => null
        };

    /// <summary>The scope a body is bound in: this method's parameters and type parameters.</summary>
    internal Binder MethodBinder =>
        methodBinder ??= new MemberBinder(binder, this, ReturnType, Parameters, TypeParameters);

    SyntaxList<SyntaxToken> Modifiers =>
        Syntax switch {
            MemberDeclarationSyntax member => member.Modifiers,
            _ => default
        };

    SyntaxList<AttributeListSyntax> AttributeLists =>
        Syntax switch {
            MemberDeclarationSyntax member => member.AttributeLists,
            _ => default
        };

    ParameterListSyntax? ParameterListSyntax =>
        Syntax switch {
            MethodDeclarationSyntax method => method.ParameterList,
            ConstructorDeclarationSyntax constructor => constructor.ParameterList,
            OperatorDeclarationSyntax @operator => @operator.ParameterList,
            _ => null
        };

    TypeParameterListSyntax? TypeParameterListSyntax =>
        Syntax switch {
            MethodDeclarationSyntax method => method.TypeParameterList,
            _ => null
        };

    SyntaxList<TypeParameterConstraintClauseSyntax> ConstraintClauses =>
        Syntax switch {
            MethodDeclarationSyntax method => method.ConstraintClauses,
            _ => default
        };

    TypeSyntax? ReturnTypeSyntax =>
        Syntax switch {
            MethodDeclarationSyntax method => method.ReturnType,
            OperatorDeclarationSyntax @operator => @operator.Type,
            _ => null
        };

    /// <summary>Binder that can see this method's type parameters while resolving its signature.</summary>
    Binder TypeScopedBinder =>
        typeScopedBinder ??= TypeParameters.Count == 0
            ? binder
            : new MemberBinder(binder, this, null, [], TypeParameters);

    internal SourceMethodSymbol(Symbol container, SyntaxNode syntax, Binder binder) {
        ContainingSymbol = container;
        Syntax = syntax;
        this.binder = binder;
    }

    ParameterSymbol[] ResolveParameters() {
        // Assign before resolving: a parameter's type is bound through this
        // method's own scope, which reads Parameters back.
        var list = ParameterListSyntax;
        if (list is null) {
            return parameters = [];
        }

        List<SourceParameterSymbol> built = [];
        var ordinal = 0;
        foreach (var parameter in list.Parameters) {
            // Parameter types may name this method's own type parameters.
            built.Add(new(this, parameter, ordinal++, TypeScopedBinder));
        }

        return parameters = built.ToArray<ParameterSymbol>();
    }

    void EnsureTypeParameters() {
        if (typeParameters is null) {
            List<TypeParameterSymbol> built = [];
            if (TypeParameterListSyntax is { } list) {
                var ordinal = 0;
                foreach (var parameter in list.Parameters) {
                    built.Add(new(this, parameter.Identifier.ValueText, ordinal++, parameter));
                }
            }

            typeParameters = built.ToArray();
        }

        if (typeParameterConstraintsResolved || typeParameters.Length == 0) {
            return;
        }

        typeParameterConstraintsResolved = true;
        ConstraintResolution.Apply(typeParameters, ConstraintClauses, binder);
    }

    TypeSymbol ResolveReturnType() {
        if (MethodKind is MethodKind.Constructor) {
            return BuiltInTypes.Void;
        }

        // ⚠ **The guard is around the whole resolution, not only the inferred branch** — the shape
        // SourceFieldSymbol and SourcePropertySymbol already have. An annotation is not inert: an
        // array rank folds its size, which binds an expression, which can be a call to this very
        // method and asks it for the return type being resolved. `func F(): float[F()]` took that
        // route, and with the guard sitting one branch further down it recursed to the guard page —
        // a stack overflow, which is the one failure the compiler cannot turn into a diagnostic
        // because the process is gone before anything can write one.
        if (resolvingReturnType) {
            binder.Diagnostics.Add(SemanticDiagnostics.CircularDefinition, DeclaringSyntax.GetLocation(), Name);
            return ErrorTypeSymbol.Instance;
        }

        resolvingReturnType = true;
        try {
            if (ReturnTypeSyntax is { } annotation) {
                return TypeScopedBinder.BindType(annotation);
            }

            // `func f() => expr` with no annotation takes its type from the body.
            if (ExpressionBody?.Expression is { } expression) {
                return new MemberBinder(binder, this, null, Parameters, TypeParameters).InferType(expression);
            }

            return BuiltInTypes.Void;
        } finally {
            resolvingReturnType = false;
        }
    }

    /// <summary>Resolves the whole signature, so its diagnostics appear unprompted.</summary>
    internal void EnsureSignatureResolved() {
        _ = ReturnType;
        _ = TypeParameters;
        foreach (var parameter in Parameters) {
            _ = parameter.Type;
        }
    }
}
