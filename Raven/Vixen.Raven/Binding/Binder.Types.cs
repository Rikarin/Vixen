// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Raven.Diagnostics;
using Vixen.Raven.Symbols;
using Vixen.Raven.Syntax;
using Vixen.Core.Syntax;

namespace Vixen.Raven.Binding;

/// <summary>Resolution of type syntax to <see cref="TypeSymbol" />s.</summary>
public abstract partial class Binder {
    /// <summary>
    ///     Resolves a type annotation. Failures report once and yield
    ///     <see cref="ErrorTypeSymbol" /> so callers need no null checks.
    /// </summary>
    public TypeSymbol BindType(TypeSyntax? syntax) {
        if (syntax is null) {
            return ErrorTypeSymbol.Instance;
        }

        var type = BindTypeCore(syntax);
        Context.Record(syntax, new BoundTypeExpression(syntax, type));
        return type;
    }

    TypeSymbol BindTypeCore(TypeSyntax syntax) {
        switch (syntax) {
            case PredefinedTypeSyntax predefined: {
                var type = BuiltInTypes.FromKeyword(predefined.Keyword.Kind);
                if (type is null) {
                    Report(SemanticDiagnostics.TypeNotFound, syntax, predefined.Keyword.Text);
                    return ErrorTypeSymbol.Instance;
                }

                return type;
            }

            case IdentifierNameSyntax identifier:
                return BindNamedType(identifier.Identifier.ValueText, [], identifier);

            case GenericNameSyntax generic: {
                var arguments = generic.TypeArgumentList.Arguments.Select(BindType).ToArray();
                return BindNamedType(generic.Identifier.ValueText, arguments, generic);
            }

            case QualifiedNameSyntax qualified:
                return BindQualifiedType(qualified);

            case ArrayTypeSyntax array: {
                var element = BindType(array.ElementType);
                // `T[][]` nests: the rank specifiers read left to right, outermost last.
                foreach (var rank in array.RankSpecifiers) {
                    element = new ArrayTypeSymbol(element, rank.Commas.Count + 1);
                }

                return element;
            }

            case TupleTypeSyntax tuple: {
                List<TypeSymbol> types = [];
                List<string?> names = [];
                foreach (var element in tuple.Elements) {
                    types.Add(BindType(element.Type));
                    names.Add(element.Identifier?.ValueText);
                }

                return new TupleTypeSymbol(types, names);
            }

            default:
                Report(SemanticDiagnostics.NotAType, syntax, syntax.ToString().Trim());
                return ErrorTypeSymbol.Instance;
        }
    }

    TypeSymbol BindNamedType(string name, IReadOnlyList<TypeSymbol> typeArguments, SyntaxNode syntax) {
        var type = LookupType(name, typeArguments.Count);

        if (type is null) {
            // Found under a different arity? Say so rather than "not found".
            if (LookupAnyArity(name) is { } mismatched) {
                Report(
                    SemanticDiagnostics.WrongTypeArgumentCount,
                    syntax,
                    name,
                    mismatched.Arity,
                    typeArguments.Count
                );
                return ErrorTypeSymbol.Instance;
            }

            Report(SemanticDiagnostics.TypeNotFound, syntax, name);
            return ErrorTypeSymbol.Instance;
        }

        return Construct(type, typeArguments);
    }

    static TypeSymbol Construct(TypeSymbol type, IReadOnlyList<TypeSymbol> typeArguments) =>
        typeArguments.Count > 0 && type is NamedTypeSymbol named
            ? new ConstructedNamedTypeSymbol(named, typeArguments)
            : type;

    NamedTypeSymbol? LookupAnyArity(string name) {
        for (var binder = this; binder is not null; binder = binder.Next) {
            List<Symbol> results = [];
            binder.LookupInScope(name, results);
            foreach (var symbol in results) {
                if (symbol is NamedTypeSymbol named) {
                    return named;
                }
            }
        }

        return null;
    }

    TypeSymbol BindQualifiedType(QualifiedNameSyntax syntax) {
        var container = BindNamespaceOrTypeQualifier(syntax.Left);
        if (container is null) {
            return ErrorTypeSymbol.Instance;
        }

        var (name, typeArguments) = SplitSimpleName(syntax.Right);

        var member = container switch {
            NamespaceSymbol ns => ns.GetTypeMember(name, typeArguments.Count) as TypeSymbol,
            TypeSymbol type => LookupMembers(type, name)
                .OfType<NamedTypeSymbol>()
                .FirstOrDefault(t => t.Arity == typeArguments.Count),
            _ => null
        };

        if (member is null) {
            Report(SemanticDiagnostics.TypeNotFound, syntax, syntax.ToString().Trim());
            return ErrorTypeSymbol.Instance;
        }

        return Construct(member, typeArguments);
    }

    /// <summary>Resolves the left side of a dotted name to a namespace or a type.</summary>
    Symbol? BindNamespaceOrTypeQualifier(NameSyntax syntax) {
        switch (syntax) {
            case QualifiedNameSyntax qualified: {
                var container = BindNamespaceOrTypeQualifier(qualified.Left);
                if (container is null) {
                    return null;
                }

                var (name, typeArguments) = SplitSimpleName(qualified.Right);

                var member = container switch {
                    NamespaceSymbol ns =>
                        (Symbol?)ns.GetTypeMember(name, typeArguments.Count) ?? ns.GetNamespace(name),
                    TypeSymbol type => LookupMembers(type, name)
                        .OfType<NamedTypeSymbol>()
                        .FirstOrDefault(t => t.Arity == typeArguments.Count),
                    _ => null
                };

                if (member is null) {
                    Report(SemanticDiagnostics.TypeNotFound, qualified, qualified.ToString().Trim());
                    return null;
                }

                return member is TypeSymbol type2 ? Construct(type2, typeArguments) : member;
            }

            case SimpleNameSyntax simple: {
                var (name, typeArguments) = SplitSimpleName(simple);
                var type = LookupType(name, typeArguments.Count);
                if (type is not null) {
                    return Construct(type, typeArguments);
                }

                var ns = LookupNamespace(name);
                if (ns is not null) {
                    return ns;
                }

                Report(SemanticDiagnostics.TypeNotFound, simple, name);
                return null;
            }

            default:
                Report(SemanticDiagnostics.NotAType, syntax, syntax.ToString().Trim());
                return null;
        }
    }

    (string Name, IReadOnlyList<TypeSymbol> TypeArguments) SplitSimpleName(SimpleNameSyntax syntax) =>
        syntax is GenericNameSyntax generic
            ? (generic.Identifier.ValueText, generic.TypeArgumentList.Arguments.Select(BindType).ToArray())
            : (syntax.Identifier.ValueText, []);
}
