using Vixen.Raven.Binding;
using Vixen.Raven.Diagnostics;
using Vixen.Raven.Symbols;
using Vixen.Raven.Symbols.Source;
using Vixen.Raven.Syntax;
using Vixen.Core.Syntax;
using Vixen.Core.Syntax.Diagnostics;

namespace Vixen.Raven;

/// <summary>
///     Semantic answers about one syntax tree: what a name refers to, what type an
///     expression has, and what a declaration declares.
/// </summary>
/// <remarks>
///     Unlike Roslyn, which binds on demand around the node you ask about, this
///     model binds every member body in the tree the first time it is queried and
///     answers from the resulting maps. That keeps the binder chain construction in
///     one place at the cost of doing the whole tree at once.
/// </remarks>
public sealed class SemanticModel {
    readonly List<BoundBody> boundBodies = [];
    readonly BindingContext context;
    bool bound;

    public Compilation Compilation { get; }

    public SyntaxTree SyntaxTree { get; }

    /// <summary>Diagnostics produced by binding this tree's member bodies.</summary>
    public IReadOnlyList<Diagnostic> Diagnostics {
        get {
            EnsureBound();
            return [..context.Diagnostics];
        }
    }

    internal SemanticModel(Compilation compilation, SyntaxTree syntaxTree) {
        Compilation = compilation;
        SyntaxTree = syntaxTree;
        context = new(compilation, new());
    }

    /// <summary>The symbol a name or expression refers to.</summary>
    public SymbolInfo GetSymbolInfo(SyntaxNode node) {
        EnsureBound();

        if (FindBound(node) is not BoundExpression expression) {
            return SymbolInfo.None;
        }

        return expression switch {
            BoundMethodGroupExpression group => new(group.Methods.Count == 1 ? group.Methods[0] : null, group.Methods),
            { Symbol: { } symbol } => new(symbol, [symbol]),
            _ => SymbolInfo.None
        };
    }

    /// <summary>The type of an expression, and the type its context converted it to.</summary>
    public TypeInfo GetTypeInfo(ExpressionSyntax node) {
        EnsureBound();

        if (FindBound(node) is not BoundExpression expression) {
            return TypeInfo.None;
        }

        var converted = context.ConvertedTypes.GetValueOrDefault(node)
            ?? Compilation.DeclarationContext.ConvertedTypes.GetValueOrDefault(node);

        return new(expression.Type, converted);
    }

    /// <summary>The symbol a declaration introduces.</summary>
    public Symbol? GetDeclaredSymbol(SyntaxNode node) {
        EnsureBound();
        return context.DeclaredSymbols.GetValueOrDefault(node)
            ?? Compilation.DeclarationContext.DeclaredSymbols.GetValueOrDefault(node);
    }

    /// <summary>
    ///     Every member body in this tree, normalized to a block with an explicit
    ///     parameter list and return type. This is the entry point for lowering.
    /// </summary>
    public IReadOnlyList<BoundBody> GetBoundBodies() {
        EnsureBound();
        return boundBodies;
    }

    /// <summary>The bound node produced for a syntax node, for inspection and testing.</summary>
    public BoundNode? GetBoundNode(SyntaxNode node) {
        EnsureBound();
        return FindBound(node);
    }

    BoundNode? FindBound(SyntaxNode node) =>
        context.BoundNodes.GetValueOrDefault(node)
        ?? Compilation.DeclarationContext.BoundNodes.GetValueOrDefault(node);

    void EnsureBound() {
        if (bound) {
            return;
        }

        bound = true;

        foreach (var type in Compilation.GetDeclaredTypes(SyntaxTree)) {
            if (type is SourceNamedTypeSymbol source) {
                BindTypeBodies(source);
            }
        }
    }

    void BindTypeBodies(SourceNamedTypeSymbol type) {
        context.RecordDeclaration(type.DeclaringSyntax, type);
        type.EnsureSignatureResolved();

        foreach (var member in type.GetMembers()) {
            switch (member) {
                case SourceNamedTypeSymbol nested:
                    BindTypeBodies(nested);
                    break;

                case SourceFieldSymbol field:
                    BindField(type, field);
                    break;

                case SourceEnumMemberSymbol enumMember:
                    context.RecordDeclaration(enumMember.DeclaringSyntax, enumMember);
                    break;

                case SourcePropertySymbol property:
                    BindProperty(type, property);
                    break;

                case SourceMethodSymbol method:
                    BindMethod(type, method);
                    break;
            }
        }
    }

    void BindField(SourceNamedTypeSymbol type, SourceFieldSymbol field) {
        context.RecordDeclaration(field.DeclaringSyntax, field);
        context.RecordDeclaration(field.Declaration, field);
        field.EnsureSignatureResolved();

        if (field.Declaration.Initializer?.Value is not { } value) {
            return;
        }

        var binder = MemberScope(type, field, null);
        var initializer = binder.BindValue(value);

        // Only check the initializer when the type was declared; otherwise the
        // type came from the initializer and always matches.
        if (field.Declaration.Type is not null) {
            initializer = binder.Convert(initializer, field.Type, value);
        }

        Record(field, BoundBodyKind.FieldInitializer, [], field.Type, Wrap(initializer, field.Type, value));
    }

    void BindProperty(SourceNamedTypeSymbol type, SourcePropertySymbol property) {
        context.RecordDeclaration(property.DeclaringSyntax, property);
        property.EnsureSignatureResolved();

        foreach (var parameter in property.Parameters) {
            if (parameter.DeclaringSyntax is { } syntax) {
                context.RecordDeclaration(syntax, parameter);
            }
        }

        if (property.ExpressionBody?.Expression is { } expressionBody) {
            var binder = MemberScope(type, property, property.Type, property.Parameters);
            binder.Convert(binder.BindValue(expressionBody), property.Type, expressionBody);
        }

        if (property.Initializer?.Value is { } initializer) {
            var binder = MemberScope(type, property, null, property.Parameters);
            binder.Convert(binder.BindValue(initializer), property.Type, initializer);
        }

        if (property.AccessorList is not { } accessors) {
            return;
        }

        foreach (var accessor in accessors.Accessors) {
            BindAccessor(type, property, accessor);
        }
    }

    void BindAccessor(SourceNamedTypeSymbol type, SourcePropertySymbol property, AccessorDeclarationSyntax accessor) {
        var isGetter = accessor.Kind == SyntaxKind.GetAccessorDeclaration;

        // A setter and the two observers all see the incoming value as `value`.
        List<ParameterSymbol> parameters = [.. property.Parameters];
        if (!isGetter) {
            parameters.Add(new SynthesizedParameterSymbol(property, "value", property.Type, parameters.Count));
        }

        var returnType = isGetter ? property.Type : BuiltInTypes.Void;
        var binder = MemberScope(type, property, returnType, parameters);
        var kind = isGetter ? BoundBodyKind.PropertyGetter : BoundBodyKind.PropertySetter;

        if (accessor.Body is { } block) {
            Record(property, kind, parameters, returnType, binder.BindBlock(block));
            return;
        }

        if (accessor.ExpressionBody?.Expression is not { } expression) {
            return;
        }

        var bound = binder.BindValue(expression);
        if (isGetter) {
            bound = binder.Convert(bound, property.Type, expression);
        }

        Record(property, kind, parameters, returnType, Wrap(bound, returnType, expression));
    }

    void BindMethod(SourceNamedTypeSymbol type, SourceMethodSymbol method) {
        context.RecordDeclaration(method.DeclaringSyntax, method);
        method.EnsureSignatureResolved();

        foreach (var parameter in method.Parameters) {
            if (parameter.DeclaringSyntax is { } syntax) {
                context.RecordDeclaration(syntax, parameter);
            }
        }

        var binder = MemberScope(type, method, method.ReturnType, method.Parameters, method.TypeParameters);
        var kind = method.IsConstructor ? BoundBodyKind.Constructor : BoundBodyKind.Method;

        if (method.Body is { } body) {
            Record(method, kind, method.Parameters, method.ReturnType, binder.BindBlock(body));
            return;
        }

        if (method.ExpressionBody?.Expression is not { } expression) {
            return;
        }

        var bound = binder.BindValue(expression);
        if (!method.ReturnType.IsVoid && !method.ReturnType.IsErrorType) {
            bound = binder.Convert(bound, method.ReturnType, expression);
        }

        Record(method, kind, method.Parameters, method.ReturnType, Wrap(bound, method.ReturnType, expression));
    }

    void Record(
        Symbol member,
        BoundBodyKind kind,
        IReadOnlyList<ParameterSymbol> parameters,
        TypeSymbol returnType,
        BoundBlockStatement body
    ) =>
        boundBodies.Add(new(member, kind, parameters, returnType, body));

    /// <summary>
    ///     Wraps a single expression body in a block, so an arrow body and a braced
    ///     body reach lowering in the same shape.
    /// </summary>
    static BoundBlockStatement Wrap(BoundExpression expression, TypeSymbol returnType, SyntaxNode syntax) {
        BoundStatement statement = returnType.IsVoid
            ? new BoundExpressionStatement(syntax, expression)
            : new BoundReturnStatement(syntax, expression);

        return new(syntax, [statement]);
    }

    /// <summary>
    ///     The scope a member body binds in: the declaring type's scope, redirected
    ///     to this model's context so bodies' diagnostics stay here.
    /// </summary>
    Binder MemberScope(
        SourceNamedTypeSymbol type,
        Symbol member,
        TypeSymbol? returnType,
        IReadOnlyList<ParameterSymbol>? parameters = null,
        IReadOnlyList<TypeParameterSymbol>? typeParameters = null
    ) =>
        new MemberBinder(new ContextBinder(type.TypeBinder, context), member, returnType, parameters, typeParameters);
}
