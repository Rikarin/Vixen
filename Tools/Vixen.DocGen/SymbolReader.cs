// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Microsoft.CodeAnalysis;

namespace Vixen.DocGen;

/// <summary>Turns a compilation's public surface into <see cref="DocNode" />s — docs/plan/25 § 2.</summary>
sealed class SymbolReader(SourceLinks links) {
    Compilation? subject;
    static readonly SymbolDisplayFormat SignatureFormat = new(
        SymbolDisplayGlobalNamespaceStyle.Omitted,
        SymbolDisplayTypeQualificationStyle.NameAndContainingTypes,
        SymbolDisplayGenericsOptions.IncludeTypeParameters
        | SymbolDisplayGenericsOptions.IncludeTypeConstraints
        | SymbolDisplayGenericsOptions.IncludeVariance,
        SymbolDisplayMemberOptions.IncludeParameters
        | SymbolDisplayMemberOptions.IncludeType
        | SymbolDisplayMemberOptions.IncludeModifiers
        | SymbolDisplayMemberOptions.IncludeAccessibility
        | SymbolDisplayMemberOptions.IncludeRef,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType
        | SymbolDisplayParameterOptions.IncludeName
        | SymbolDisplayParameterOptions.IncludeDefaultValue
        | SymbolDisplayParameterOptions.IncludeParamsRefOut
        | SymbolDisplayParameterOptions.IncludeExtensionThis,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes
        | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier
    );

    /// <summary>
    ///     The name the baselines write, so the two can be compared: namespace-qualified, type
    ///     parameters by name, and <b>no variance</b> — <c>IReadOnlySignal&lt;T&gt;</c>, not
    ///     <c>IReadOnlySignal&lt;out T&gt;</c>, which is what the default display would give.
    /// </summary>
    static readonly SymbolDisplayFormat QualifiedName = new(
        SymbolDisplayGlobalNamespaceStyle.Omitted,
        SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        SymbolDisplayGenericsOptions.IncludeTypeParameters
    );

    /// <summary>Reads every publicly reachable type declared by the assembly.</summary>
    /// <param name="assembly">The compilation's own assembly. References are not walked.</param>
    /// <param name="area">Top-level folder the project lives in.</param>
    /// <param name="isPackable">Whether the assembly carries a <c>PublicAPI.*.txt</c>.</param>
    public IEnumerable<DocNode> Read(IAssemblySymbol assembly, string area, bool isPackable) =>
        Types(assembly.GlobalNamespace).Select(type => Describe(type, assembly.Name, area, isPackable));

    /// <summary>
    ///     Reads a whole compilation. The compilation is kept because some facets are declared in
    ///     code rather than in metadata — a system's access is a property initialiser, and reading it
    ///     needs the semantic model that produced the symbol.
    /// </summary>
    public IEnumerable<DocNode> Read(Compilation compilation, string area, bool isPackable) {
        subject = compilation;

        return Read(compilation.Assembly, area, isPackable);
    }

    static IEnumerable<INamedTypeSymbol> Types(INamespaceOrTypeSymbol container) {
        foreach (var member in container.GetMembers()) {
            switch (member) {
                case INamespaceSymbol child:
                    foreach (var type in Types(child)) {
                        yield return type;
                    }

                    break;

                // C# 14's extension blocks compile to a container type with an unspeakable name —
                // `<G>$7B1E…`, displayed as `extension(…)`. It is the implementation of the
                // declaration rather than API a reader can name, and the extension members
                // themselves stay on the static class that declares them.
                case INamedTypeSymbol type when IsVisible(type) && !IsExtensionContainer(type):
                    yield return type;

                    // Nested types are types. They get their own page, because a reader searching
                    // for `Builder` should not have to know which type it hangs off.
                    foreach (var nested in Types(type)) {
                        yield return nested;
                    }

                    break;
            }
        }
    }

    static bool IsExtensionContainer(INamedTypeSymbol type) =>
        type.Name.Contains('<') || type.ToDisplayString().Contains("extension(", StringComparison.Ordinal);

    /// <summary>
    ///     Public or protected, and reachable — a public type nested in an internal one is not
    ///     surface, whatever its own modifier says.
    /// </summary>
    internal static bool IsVisible(ISymbol symbol) {
        for (var current = symbol; current is not null; current = current.ContainingType) {
            if (current.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Protected
                or Accessibility.ProtectedOrInternal)) {
                return false;
            }
        }

        return true;
    }

    DocNode Describe(INamedTypeSymbol type, string assembly, string area, bool isPackable) {
        var docs = DocumentationComment.For(type);
        var id = type.GetDocumentationCommentId() ?? "T:" + type.ToDisplayString();
        var source = links.For(type);
        var kind = Taxonomy.Of(type);

        return new DocNode {
            Id = id,
            Kind = kind,
            Name = type.Name,
            QualifiedName = type.ToDisplayString(QualifiedName),
            Namespace = type.ContainingNamespace.ToDisplayString(),
            Assembly = assembly,
            Area = area,
            Slug = Slugs.ForType(id, type.ContainingNamespace.ToDisplayString()),
            Signature = Signatures.OfType(type, SignatureFormat),
            Summary = docs.Summary,
            Remarks = docs.Remarks,
            BaseType = type.BaseType is { SpecialType: not SpecialType.System_Object } baseType
                ? baseType.GetDocumentationCommentId()
                : null,
            Interfaces = [.. type.Interfaces
                .Select(candidate => candidate.GetDocumentationCommentId())
                .Where(candidate => candidate is not null)!],
            Attributes = Attributes(type),
            Members = [.. Members(type)],
            SeeAlso = docs.SeeAlso,
            Obsolete = Obsolete(type),
            Facets = Vixen.DocGen.Facets.For(type, kind, subject),
            IsGenerated = source is not null && links.IsGenerated(source.Path),
            IsPackable = isPackable,
            Source = source
        };
    }

    IEnumerable<DocMember> Members(INamedTypeSymbol type) {
        foreach (var member in type.GetMembers()) {
            if (member.IsImplicitlyDeclared || !IsVisible(member) || member is INamedTypeSymbol) {
                continue;
            }

            // A property contributes its accessors as part of its own signature; listing them again
            // as methods is noise the ApiCheck baselines leave out for the same reason.
            if (member is IMethodSymbol { AssociatedSymbol: not null }) {
                continue;
            }

            if (member is IMethodSymbol { MethodKind: MethodKind.StaticConstructor or MethodKind.Destructor }) {
                continue;
            }

            var docs = DocumentationComment.For(member);

            yield return new DocMember {
                Id = member.GetDocumentationCommentId() ?? member.ToDisplayString(),
                Name = member.Name,
                MemberKind = Kind(member),
                Signature = Signatures.Of(member, SignatureFormat),
                Summary = docs.Summary,
                Returns = docs.Returns,
                IsStatic = member.IsStatic,
                Obsolete = Obsolete(member),
                Attributes = Attributes(member),
                Source = links.For(member)
            };
        }
    }

    static string Kind(ISymbol member) => member switch {
        IFieldSymbol { IsConst: true } => "constant",
        IFieldSymbol => "field",
        IPropertySymbol { IsIndexer: true } => "indexer",
        IPropertySymbol => "property",
        IEventSymbol => "event",
        IMethodSymbol { MethodKind: MethodKind.Constructor } => "constructor",
        IMethodSymbol { MethodKind: MethodKind.UserDefinedOperator or MethodKind.Conversion } => "operator",
        IMethodSymbol => "method",
        _ => "member"
    };

    static IReadOnlyList<DocAttribute> Attributes(ISymbol symbol) => [.. symbol.GetAttributes()
        .Where(attribute => attribute.AttributeClass is not null)
        .Select(attribute => new DocAttribute(
            attribute.AttributeClass!.GetDocumentationCommentId() ?? attribute.AttributeClass.ToDisplayString(),
            Trim(attribute.AttributeClass.Name),
            [
                .. attribute.ConstructorArguments.Select(Format),
                .. attribute.NamedArguments.Select(argument => $"{argument.Key} = {Format(argument.Value)}")
            ]))];

    /// <summary>
    ///     An attribute argument as it reads in source: <c>".fbx"</c>, <c>16</c>, <c>Channel.Unreliable</c>.
    /// </summary>
    /// <remarks>
    ///     Hand-written because Roslyn's own formatter for this is internal, and because the values
    ///     that matter here — the extensions an importer claims, the bits a quantised float costs —
    ///     are strings, numbers, enums and arrays of them.
    /// </remarks>
    static string Format(TypedConstant constant) => constant.Kind switch {
        // ⚠ `Values` is an uninitialised ImmutableArray when the array itself is null — `[Foo(null)]`
        // on a `params` parameter — and touching it throws rather than returning empty.
        TypedConstantKind.Array when constant.IsNull || constant.Values.IsDefault => "null",
        TypedConstantKind.Array => "[" + string.Join(", ", constant.Values.Select(Format)) + "]",
        TypedConstantKind.Type => constant.Value is ITypeSymbol type ? type.ToDisplayString() : "null",
        TypedConstantKind.Enum when constant.Type is not null =>
            $"{constant.Type.Name}.{constant.Value}",
        _ => constant.Value switch {
            null => "null",
            string text => $"\"{text}\"",
            bool flag => flag ? "true" : "false",
            var value => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "null"
        }
    };

    /// <summary>The `[Obsolete]` message, or null. Deprecation is a fact the release diff reads.</summary>
    static string? Obsolete(ISymbol symbol) {
        var obsolete = symbol.GetAttributes().FirstOrDefault(attribute =>
            attribute.AttributeClass?.ToDisplayString() == "System.ObsoleteAttribute");

        if (obsolete is null) {
            return null;
        }

        return obsolete.ConstructorArguments.FirstOrDefault().Value as string ?? string.Empty;
    }

    static string Trim(string name) =>
        name.EndsWith("Attribute", StringComparison.Ordinal) ? name[..^"Attribute".Length] : name;
}
