// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Raven.Artefacts;

namespace Vixen.Raven.Symbols.Metadata;

/// <summary>
///     A type read out of a compiled library rather than out of source.
/// </summary>
/// <remarks>
///     <para>
///         The point of this class is that it is a <see cref="NamedTypeSymbol" /> and nothing more
///         specific. Member lookup, overload resolution, conversions, protocol conformance and
///         <c>compose</c> resolution all work against the abstract symbol classes, so a library's
///         declarations take part in binding on exactly the same terms as a source file's —
///         which is what "referenced without reparsing source" has to mean to be worth anything.
///         This is Roslyn's split between source symbols and PE symbols, at Raven's scale.
///     </para>
///     <para>
///         <see cref="Symbol.DeclaringSyntax" /> stays null, so a diagnostic about a library type
///         has no source span to point at. That is honest rather than unfortunate: the source is
///         not in this compilation, and the alternative — a span into a file that may not exist on
///         this machine — is worse than none.
///     </para>
/// </remarks>
public sealed class MetadataNamedTypeSymbol : NamedTypeSymbol {
    readonly MetadataLoader loader;
    readonly List<MetadataNamedTypeSymbol> nestedTypes = [];

    NamedTypeSymbol? baseType;
    bool basesResolved;
    bool constraintsResolved;
    NamedTypeSymbol[] interfaces = [];
    Symbol[]? members;
    Dictionary<string, Symbol[]>? membersByName;
    NamespaceSymbol? containingNamespace;
    readonly TypeParameterSymbol[] typeParameters;

    /// <summary>The declaration as the artefact recorded it.</summary>
    public LibraryType Declaration { get; }

    /// <summary>Which library this type came from.</summary>
    public string LibraryName { get; }

    public override string Name => Declaration.Name;
    public override TypeKind TypeKind => Declaration.Kind;
    public override Symbol? ContainingSymbol => (Symbol?)DeclaringType ?? containingNamespace;
    public override IReadOnlyList<TypeParameterSymbol> TypeParameters => ResolvedTypeParameters();

    /// <summary>The type this one is nested in, when it is nested.</summary>
    public MetadataNamedTypeSymbol? DeclaringType { get; private set; }

    /// <summary>
    ///     The IR struct this type lowered to when the library was built, or null when it has no
    ///     storage. The link the lowerer uses to give a library struct the same identity in a
    ///     consumer's module that it had in its own.
    /// </summary>
    public string? IrStructName => Declaration.IrStruct;

    public override NamedTypeSymbol? BaseType {
        get {
            EnsureBases();
            return baseType;
        }
    }

    public override IReadOnlyList<NamedTypeSymbol> Interfaces {
        get {
            EnsureBases();
            return interfaces;
        }
    }

    /// <summary>Types declared inside this one.</summary>
    public IReadOnlyList<MetadataNamedTypeSymbol> NestedTypes => nestedTypes;

    internal MetadataNamedTypeSymbol(MetadataLoader loader, string libraryName, LibraryType declaration) {
        this.loader = loader;
        LibraryName = libraryName;
        Declaration = declaration;

        // Eager, and only the names: a constraint may name this very type, so nothing that needs
        // a lookup can happen while the symbol is still being constructed.
        typeParameters = declaration.TypeParameters
            .OrderBy(p => p.Ordinal)
            .Select(p => new TypeParameterSymbol(this, p.Name, p.Ordinal))
            .ToArray();
    }

    public override IReadOnlyList<Symbol> GetMembers() {
        EnsureMembers();
        return members!;
    }

    public override IReadOnlyList<Symbol> GetMembers(string name) {
        EnsureMembers();
        return membersByName!.GetValueOrDefault(name) ?? (IReadOnlyList<Symbol>)[];
    }

    /// <summary>The scope a reference in this type's own declarations resolves against.</summary>
    internal MetadataScope Scope => new(this, null);

    internal TypeSymbol Resolve(LibraryTypeReference? reference, MetadataMethodSymbol? method = null) =>
        loader.Resolve(reference, new(this, method), LibraryName);

    internal void Attach(NamespaceSymbol package) => containingNamespace = package;

    internal void AddNestedType(MetadataNamedTypeSymbol nested) {
        nested.DeclaringType = this;
        nestedTypes.Add(nested);
    }

    TypeParameterSymbol[] ResolvedTypeParameters() {
        if (constraintsResolved || typeParameters.Length == 0) {
            return typeParameters;
        }

        // Set before resolving: a constraint may name a type whose own signature reads these
        // parameters back, and the guard is what stops that from recursing.
        constraintsResolved = true;

        foreach (var parameter in typeParameters) {
            var model = Declaration.TypeParameters.FirstOrDefault(p => p.Ordinal == parameter.Ordinal);
            if (model is null || model.Constraints.IsDefaultOrEmpty) {
                continue;
            }

            parameter.SetConstraintTypes([.. model.Constraints.Select(c => Resolve(c))]);
        }

        return typeParameters;
    }

    void EnsureBases() {
        if (basesResolved) {
            return;
        }

        basesResolved = true;

        if (Declaration.BaseType is { } declaredBase && Resolve(declaredBase) is NamedTypeSymbol resolved
            && !resolved.IsErrorType) {
            baseType = resolved;
        }

        interfaces = Declaration.Interfaces
            .Select(declared => Resolve(declared))
            .OfType<NamedTypeSymbol>()
            .Where(type => !type.IsErrorType)
            .ToArray();
    }

    void EnsureMembers() {
        if (members is not null) {
            return;
        }

        List<Symbol> built = [];

        // Fields before everything else, in the order the artefact recorded them. A
        // constructor-less struct is built from its fields positionally, so this order is part of
        // the type's surface rather than a presentation choice.
        foreach (var field in Declaration.Fields) {
            built.Add(new MetadataFieldSymbol(this, field));
        }

        foreach (var property in Declaration.Properties) {
            built.Add(new MetadataPropertySymbol(this, property));
        }

        foreach (var method in Declaration.Methods) {
            built.Add(new MetadataMethodSymbol(this, method));
        }

        built.AddRange(nestedTypes);

        members = built.ToArray();
        membersByName = built
            .GroupBy(m => m.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
    }
}
