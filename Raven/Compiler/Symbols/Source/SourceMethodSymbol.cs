using Vixen.Raven.Binding;
using Vixen.Raven.Diagnostics;
using Vixen.Raven.Syntax;

namespace Vixen.Raven.Symbols.Source;

/// <summary>
/// A callable declared in source: <c>func</c>, <c>init</c>, <c>~init</c>, an
/// operator, a conversion operator, or a local function.
/// </summary>
public sealed class SourceMethodSymbol : MethodSymbol {
    readonly Binder binder;

    Binder? methodBinder;
    ParameterSymbol[]? parameters;
    bool resolvingReturnType;
    TypeSymbol? returnType;
    bool typeParameterConstraintsResolved;
    TypeParameterSymbol[]? typeParameters;
    Binder? typeScopedBinder;

    internal SourceMethodSymbol(Symbol container, SyntaxNode syntax, Binder binder) {
        ContainingSymbol = container;
        Syntax = syntax;
        this.binder = binder;
    }

    /// <summary>The declaration this method came from.</summary>
    public SyntaxNode Syntax { get; }

    public override Symbol? ContainingSymbol { get; }
    public override SyntaxNode DeclaringSyntax => Syntax;

    public override MethodKind MethodKind => Syntax switch {
        ConstructorDeclarationSyntax => MethodKind.Constructor,
        DestructorDeclarationSyntax => MethodKind.Destructor,
        OperatorDeclarationSyntax => MethodKind.Operator,
        ConversionOperatorDeclarationSyntax => MethodKind.Conversion,
        LocalFunctionStatementSyntax => MethodKind.LocalFunction,
        _ => MethodKind.Ordinary
    };

    public override string Name => Syntax switch {
        MethodDeclarationSyntax method => method.Identifier.ValueText,
        LocalFunctionStatementSyntax local => local.Identifier.ValueText,
        ConstructorDeclarationSyntax => ".ctor",
        DestructorDeclarationSyntax => ".dtor",
        OperatorDeclarationSyntax @operator => "operator" + @operator.OperatorToken.Text,
        ConversionOperatorDeclarationSyntax conversion => "op_" + conversion.ImplicitOrExplicitKeyword.Text,
        _ => string.Empty
    };

    public override bool IsStatic => DeclarationFacts.Has(Modifiers, SyntaxKind.StaticKeyword);
    public override bool IsAbstract => DeclarationFacts.Has(Modifiers, SyntaxKind.AbstractKeyword);

    public override Accessibility DeclaredAccessibility =>
        DeclarationFacts.GetAccessibility(Modifiers, Accessibility.Private);

    public override ShaderStage Stage => DeclarationFacts.GetShaderStage(AttributeLists);

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
    public BlockSyntax? Body => Syntax switch {
        MethodDeclarationSyntax method => method.Body,
        LocalFunctionStatementSyntax local => local.Body,
        ConstructorDeclarationSyntax constructor => constructor.Body,
        DestructorDeclarationSyntax destructor => destructor.Body,
        OperatorDeclarationSyntax @operator => @operator.Body,
        ConversionOperatorDeclarationSyntax conversion => conversion.Body,
        _ => null
    };

    /// <summary>The <c>=&gt; expression</c> body, if the method has one.</summary>
    public ArrowExpressionClauseSyntax? ExpressionBody => Syntax switch {
        MethodDeclarationSyntax method => method.ExpressionBody,
        LocalFunctionStatementSyntax local => local.ExpressionBody,
        ConstructorDeclarationSyntax constructor => constructor.ExpressionBody,
        DestructorDeclarationSyntax destructor => destructor.ExpressionBody,
        OperatorDeclarationSyntax @operator => @operator.ExpressionBody,
        ConversionOperatorDeclarationSyntax conversion => conversion.ExpressionBody,
        _ => null
    };

    /// <summary>The scope a body is bound in: this method's parameters and type parameters.</summary>
    internal Binder MethodBinder =>
        methodBinder ??= new MemberBinder(binder, this, ReturnType, Parameters, TypeParameters);

    SyntaxList<SyntaxToken> Modifiers => Syntax switch {
        MemberDeclarationSyntax member => member.Modifiers,
        LocalFunctionStatementSyntax local => local.Modifiers,
        _ => default
    };

    SyntaxList<AttributeListSyntax> AttributeLists => Syntax switch {
        MemberDeclarationSyntax member => member.AttributeLists,
        LocalFunctionStatementSyntax local => local.AttributeLists,
        _ => default
    };

    BaseParameterListSyntax? ParameterListSyntax => Syntax switch {
        MethodDeclarationSyntax method => method.ParameterList,
        LocalFunctionStatementSyntax local => local.ParameterList,
        ConstructorDeclarationSyntax constructor => constructor.ParameterList,
        DestructorDeclarationSyntax destructor => destructor.ParameterList,
        OperatorDeclarationSyntax @operator => @operator.ParameterList,
        ConversionOperatorDeclarationSyntax conversion => conversion.ParameterList,
        _ => null
    };

    TypeParameterListSyntax? TypeParameterListSyntax => Syntax switch {
        MethodDeclarationSyntax method => method.TypeParameterList,
        LocalFunctionStatementSyntax local => local.TypeParameterList,
        _ => null
    };

    SyntaxList<TypeParameterConstraintClauseSyntax> ConstraintClauses => Syntax switch {
        MethodDeclarationSyntax method => method.ConstraintClauses,
        LocalFunctionStatementSyntax local => local.ConstraintClauses,
        _ => default
    };

    TypeSyntax? ReturnTypeSyntax => Syntax switch {
        MethodDeclarationSyntax method => method.ReturnType,
        LocalFunctionStatementSyntax local => local.ReturnType,
        OperatorDeclarationSyntax @operator => @operator.Type,
        ConversionOperatorDeclarationSyntax conversion => conversion.Type,
        _ => null
    };

    /// <summary>Resolves the whole signature, so its diagnostics appear unprompted.</summary>
    internal void EnsureSignatureResolved() {
        _ = ReturnType;
        _ = TypeParameters;
        foreach (var parameter in Parameters) {
            _ = parameter.Type;
        }
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
            built.Add(new SourceParameterSymbol(this, parameter, ordinal++, TypeScopedBinder));
        }

        return parameters = built.ToArray<ParameterSymbol>();
    }

    void EnsureTypeParameters() {
        if (typeParameters is null) {
            List<TypeParameterSymbol> built = [];
            if (TypeParameterListSyntax is { } list) {
                var ordinal = 0;
                foreach (var parameter in list.Parameters) {
                    built.Add(new TypeParameterSymbol(this, parameter.Identifier.ValueText, ordinal++, parameter));
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
        if (MethodKind is MethodKind.Constructor or MethodKind.Destructor) {
            return BuiltInTypes.Void;
        }

        if (ReturnTypeSyntax is { } annotation) {
            return TypeScopedBinder.BindType(annotation);
        }

        // `func f() => expr` with no annotation takes its type from the body.
        if (ExpressionBody?.Expression is { } expression) {
            if (resolvingReturnType) {
                binder.Diagnostics.Add(SemanticDiagnostics.CircularDefinition, DeclaringSyntax.GetLocation(), Name);
                return ErrorTypeSymbol.Instance;
            }

            resolvingReturnType = true;
            try {
                return new MemberBinder(binder, this, null, Parameters, TypeParameters).InferType(expression);
            }
            finally {
                resolvingReturnType = false;
            }
        }

        return BuiltInTypes.Void;
    }

    /// <summary>Binder that can see this method's type parameters while resolving its signature.</summary>
    Binder TypeScopedBinder => typeScopedBinder ??= TypeParameters.Count == 0
        ? binder
        : new MemberBinder(binder, this, null, [], TypeParameters);
}
