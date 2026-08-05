// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Syntax.Diagnostics;
using Vixen.Raven.Artefacts;
using Vixen.Raven.Diagnostics;

namespace Vixen.Raven.Symbols.Metadata;

/// <summary>
///     Turns the declarations in a <see cref="CompiledLibrary" /> into symbols, and resolves the
///     type references between them.
/// </summary>
/// <remarks>
///     <para>
///         One loader per compilation, not per library: a type reference resolves against every
///         library the compilation was given, so <c>Shading/Brdf.rvnlib</c> can take a parameter
///         of a struct declared in <c>Core/Math.rvnlib</c>. The shared table keyed by qualified
///         name is what makes that work, and it is why the reference model is a set rather than a
///         list of independent loads.
///     </para>
///     <para>
///         Resolution is lazy throughout, for the same reason the source symbols' is: a type's
///         base may be declared after it, its members' signatures name the type itself, and a
///         generic parameter's constraint may name its own owner. Names go in eagerly; everything
///         that needs a lookup happens on first read.
///     </para>
/// </remarks>
internal sealed class MetadataLoader {
    readonly DiagnosticBag diagnostics;
    readonly Dictionary<string, MetadataNamedTypeSymbol> byQualifiedName = new(StringComparer.Ordinal);
    readonly List<CompiledLibrary> libraries = [];
    readonly List<MetadataNamedTypeSymbol> topLevel = [];

    /// <summary>Every top-level type loaded, in the order the libraries supplied them.</summary>
    public IReadOnlyList<MetadataNamedTypeSymbol> TopLevelTypes => topLevel;

    /// <summary>
    ///     The libraries that were loaded, deduplicated, in order. What the lowerer links their IR
    ///     from.
    /// </summary>
    public IReadOnlyList<CompiledLibrary> Libraries => libraries;

    internal MetadataLoader(DiagnosticBag diagnostics) {
        this.diagnostics = diagnostics;
    }

    /// <summary>
    ///     Creates a symbol for every type <paramref name="library" /> declares and files each
    ///     top-level one in its package namespace under <paramref name="globalNamespace" />.
    /// </summary>
    /// <returns>The top-level types this library contributed.</returns>
    public IReadOnlyList<MetadataNamedTypeSymbol> Load(CompiledLibrary library, NamespaceSymbol globalNamespace) {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(globalNamespace);

        libraries.Add(library);

        // Two passes over a flat list. Nested types name their declaring type, which may appear
        // in any order, so every symbol has to exist before any of them is attached.
        List<(LibraryType Model, MetadataNamedTypeSymbol Symbol)> loaded = [];

        foreach (var model in library.Types) {
            var symbol = new MetadataNamedTypeSymbol(this, library.Name, model);
            loaded.Add((model, symbol));

            // A duplicate qualified name across two libraries: the first wins, which is what the
            // duplicate-reference warning has already told the caller about.
            byQualifiedName.TryAdd(model.QualifiedName, symbol);
        }

        List<MetadataNamedTypeSymbol> contributed = [];

        foreach (var (model, symbol) in loaded) {
            if (model.ContainingType is { Length: > 0 } outer) {
                if (byQualifiedName.GetValueOrDefault(outer) is { } declaring) {
                    declaring.AddNestedType(symbol);
                }

                continue;
            }

            var package = globalNamespace.GetOrAddNamespace(SplitNamespace(model.Namespace));
            symbol.Attach(package);
            package.AddType(symbol);
            topLevel.Add(symbol);
            contributed.Add(symbol);
        }

        return contributed;
    }

    static IEnumerable<string> SplitNamespace(string name) =>
        name.Length == 0 ? [] : name.Split('.', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>The loaded type with this qualified name, or null.</summary>
    public MetadataNamedTypeSymbol? Find(string qualifiedName) => byQualifiedName.GetValueOrDefault(qualifiedName);

    /// <summary>
    ///     Resolves a type reference read out of an artefact.
    /// </summary>
    /// <param name="reference">The reference to resolve.</param>
    /// <param name="scope">
    ///     Where a <see cref="LibraryTypeKind.TypeParameter" /> is looked up: the declaring type
    ///     and, inside a signature, the declaring method.
    /// </param>
    /// <param name="libraryName">Which library the reference came from, for the diagnostic.</param>
    internal TypeSymbol Resolve(LibraryTypeReference? reference, MetadataScope scope, string libraryName) {
        if (reference is null) {
            return ErrorTypeSymbol.Instance;
        }

        switch (reference.Kind) {
            case LibraryTypeKind.Primitive:
                // A primitive travels as its SpecialType, which is the identity the binder keys
                // numeric promotion, literal typing and swizzles off — so the loaded symbol is
                // the very singleton a source declaration would have resolved to.
                return reference.Special == SpecialType.None
                    ? ErrorTypeSymbol.Instance
                    : BuiltInTypes.FromSpecialType(reference.Special);

            case LibraryTypeKind.BuiltIn:
                return BuiltInResource(reference.Special);

            case LibraryTypeKind.Named:
                return ResolveNamed(reference, scope, libraryName);

            case LibraryTypeKind.Array: {
                var element = Resolve(reference.Element, scope, libraryName);
                return element.IsErrorType
                    ? element
                    : new ArrayTypeSymbol(element, Math.Max(1, reference.Rank), reference.Length);
            }

            case LibraryTypeKind.Buffer: {
                var element = Resolve(reference.Element, scope, libraryName);
                return element.IsErrorType ? element : new BufferTypeSymbol(element, reference.Writable);
            }

            case LibraryTypeKind.Tuple: {
                var elements = reference.Elements.Select(e => Resolve(e, scope, libraryName)).ToArray();
                return new TupleTypeSymbol(elements, [.. reference.ElementNames]);
            }

            case LibraryTypeKind.TypeParameter:
                return reference.Name is { } name
                    ? scope.Find(name) as TypeSymbol ?? ErrorTypeSymbol.Instance
                    : ErrorTypeSymbol.Instance;

            default:
                return ErrorTypeSymbol.Instance;
        }
    }

    TypeSymbol ResolveNamed(LibraryTypeReference reference, MetadataScope scope, string libraryName) {
        if (reference.Name is not { Length: > 0 } qualified) {
            return ErrorTypeSymbol.Instance;
        }

        if (byQualifiedName.GetValueOrDefault(qualified) is not { } definition) {
            // A missing reference is a command-line mistake, and without saying so its symptom is
            // a member that cannot be found on a type whose source nobody has.
            diagnostics.Add(LibraryDiagnostics.ReferenceTypeUnresolved, Location.None, libraryName, qualified);
            return ErrorTypeSymbol.Instance;
        }

        if (reference.TypeArguments.IsDefaultOrEmpty) {
            return definition;
        }

        var arguments = reference.TypeArguments.Select(a => Resolve(a, scope, libraryName)).ToArray();
        return arguments.Length == definition.Arity
            ? new ConstructedNamedTypeSymbol(definition, arguments)
            : definition;
    }

    /// <summary>
    ///     The compiler-supplied named type for a resource <see cref="SpecialType" />.
    /// </summary>
    /// <remarks>
    ///     Spelled out rather than going through <c>BuiltInTypes.FromSpecialType</c>, which covers
    ///     the primitives only and throws for these.
    /// </remarks>
    static TypeSymbol BuiltInResource(SpecialType special) =>
        special switch {
            SpecialType.Sampler => BuiltInTypes.Sampler,
            SpecialType.Texture2D => BuiltInTypes.Texture2D,
            SpecialType.Texture3D => BuiltInTypes.Texture3D,
            SpecialType.TextureCube => BuiltInTypes.TextureCube,
            SpecialType.AccelerationStructure => BuiltInTypes.AccelerationStructure,
            _ => ErrorTypeSymbol.Instance
        };
}

/// <summary>
///     Where a metadata type reference looks a generic parameter up: the declaring type, and the
///     declaring method when the reference is part of its signature.
/// </summary>
/// <remarks>
///     Method first, matching the source binder's scope chain — a method's own <c>T</c> shadows
///     its type's.
/// </remarks>
internal readonly record struct MetadataScope(MetadataNamedTypeSymbol? Type, MetadataMethodSymbol? Method) {
    public TypeParameterSymbol? Find(string name) {
        if (Method is not null) {
            foreach (var parameter in Method.TypeParameters) {
                if (string.Equals(parameter.Name, name, StringComparison.Ordinal)) {
                    return parameter;
                }
            }
        }

        if (Type is not null) {
            foreach (var parameter in Type.TypeParameters) {
                if (string.Equals(parameter.Name, name, StringComparison.Ordinal)) {
                    return parameter;
                }
            }
        }

        return null;
    }
}
