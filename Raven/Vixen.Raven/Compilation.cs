// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Syntax.Diagnostics;
using Vixen.Raven.Binding;
using Vixen.Raven.Diagnostics;
using Vixen.Raven.Symbols;
using Vixen.Raven.Symbols.Metadata;
using Vixen.Raven.Symbols.Source;
using Vixen.Raven.Syntax;

namespace Vixen.Raven;

/// <summary>
///     A set of syntax trees compiled together, and the entry point to the semantic
///     model. Modelled on Roslyn's <c>CSharpCompilation</c>: it owns the symbol
///     table, hands out a <see cref="SemanticModel" /> per tree, and aggregates
///     diagnostics from every phase.
/// </summary>
public sealed class Compilation {
    readonly DiagnosticBag declarationDiagnostics = new();
    readonly Dictionary<SyntaxTree, ImportBinder> importBinders = [];
    readonly RavenReference[] references;
    readonly Dictionary<SyntaxTree, SemanticModel> semanticModels = [];
    readonly SyntaxTree[] syntaxTrees;
    readonly Dictionary<SyntaxTree, List<SourceNamedTypeSymbol>> typesByTree = [];

    BindingContext? declarationContext;
    bool declarationsBuilt;
    Binder? globalBinder;
    NamespaceSymbol? globalNamespace;
    MetadataLoader? metadata;
    bool recursionChecked;

    readonly SortedSet<string> usedPermutationKeys = new(StringComparer.Ordinal);

    public string AssemblyName { get; }

    public IReadOnlyList<SyntaxTree> SyntaxTrees => syntaxTrees;

    /// <summary>
    ///     The compiled libraries this compilation binds against, in the order they were supplied.
    /// </summary>
    /// <remarks>
    ///     Deduplicated by library name — a duplicate is reported (<c>RVN5005</c>) and the first
    ///     wins, because two loads of one library would give its types two identities and a value
    ///     of one would not convert to the other.
    /// </remarks>
    public IReadOnlyList<RavenReference> References => references;

    /// <summary>
    ///     Values supplied for this compilation's <c>[Permutation]</c> keys. Keys with no
    ///     value here take the initializer in the source.
    /// </summary>
    public PermutationValues PermutationValues { get; }

    /// <summary>
    ///     Which concrete shader fills each <c>compose</c> slot. A slot with nothing bound is
    ///     an error, so this is required for any shader that composes.
    /// </summary>
    public ComposeBindings ComposeBindings { get; }

    /// <summary>
    ///     The permutation keys this compilation actually consulted, sorted by name.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This is the cache key the engine should use, and it is why it must come from
    ///         the semantic phase rather than from the caller's define list. A shader
    ///         declaring twenty independent flags has a million possible define
    ///         combinations, but any one entry point usually reads a handful; keying the
    ///         cache on the declared set produces a million entries where a dozen are
    ///         distinct.
    ///     </para>
    ///     <para>
    ///         Only meaningful once something has been bound — a key is recorded when its
    ///         value is read, so an unqueried compilation reports nothing. Read it after
    ///         <see cref="GetDiagnostics" /> or after lowering.
    ///     </para>
    /// </remarks>
    public IReadOnlyCollection<string> UsedPermutationKeys => usedPermutationKeys;

    /// <summary>Root of the symbol table; every package hangs off it.</summary>
    public NamespaceSymbol GlobalNamespace {
        get {
            EnsureDeclarations();
            return globalNamespace!;
        }
    }

    internal BindingContext DeclarationContext => declarationContext ??= new(this, declarationDiagnostics);

    internal Binder GlobalBinder => globalBinder ??= new GlobalBinder(DeclarationContext);

    Compilation(
        string assemblyName,
        SyntaxTree[] syntaxTrees,
        PermutationValues permutationValues,
        ComposeBindings composeBindings,
        RavenReference[] references
    ) {
        AssemblyName = assemblyName;
        this.syntaxTrees = syntaxTrees;
        PermutationValues = permutationValues;
        ComposeBindings = composeBindings;
        this.references = references;
    }

    public static Compilation Create(string assemblyName, params SyntaxTree[] syntaxTrees) =>
        new(assemblyName, syntaxTrees, PermutationValues.Empty, ComposeBindings.Empty, []);

    public static Compilation Create(string assemblyName, IEnumerable<SyntaxTree> syntaxTrees) =>
        new(assemblyName, syntaxTrees.ToArray(), PermutationValues.Empty, ComposeBindings.Empty, []);

    /// <summary>Creates a compilation that binds against compiled libraries.</summary>
    public static Compilation Create(
        string assemblyName,
        IEnumerable<RavenReference> references,
        IEnumerable<SyntaxTree> syntaxTrees
    ) =>
        Create(assemblyName, PermutationValues.Empty, ComposeBindings.Empty, references, syntaxTrees);

    /// <summary>
    ///     Creates one variant of a compilation. Each distinct combination of
    ///     <paramref name="permutationValues" /> and <paramref name="composeBindings" /> is a
    ///     separate compilation, because both change what the code means.
    /// </summary>
    public static Compilation Create(
        string assemblyName,
        PermutationValues permutationValues,
        IEnumerable<SyntaxTree> syntaxTrees
    ) =>
        Create(assemblyName, permutationValues, ComposeBindings.Empty, syntaxTrees);

    /// <inheritdoc cref="Create(string,PermutationValues,IEnumerable{SyntaxTree})" />
    public static Compilation Create(
        string assemblyName,
        PermutationValues permutationValues,
        ComposeBindings composeBindings,
        IEnumerable<SyntaxTree> syntaxTrees
    ) =>
        Create(assemblyName, permutationValues, composeBindings, [], syntaxTrees);

    /// <inheritdoc cref="Create(string,PermutationValues,IEnumerable{SyntaxTree})" />
    public static Compilation Create(
        string assemblyName,
        PermutationValues permutationValues,
        ComposeBindings composeBindings,
        IEnumerable<RavenReference> references,
        IEnumerable<SyntaxTree> syntaxTrees
    ) {
        ArgumentNullException.ThrowIfNull(permutationValues);
        ArgumentNullException.ThrowIfNull(composeBindings);
        ArgumentNullException.ThrowIfNull(references);
        ArgumentNullException.ThrowIfNull(syntaxTrees);

        return new(assemblyName, syntaxTrees.ToArray(), permutationValues, composeBindings, references.ToArray());
    }

    /// <summary>
    ///     Records that <paramref name="key" /> was consulted. Called when a permutation
    ///     field's value is read, which is the point at which the output starts depending
    ///     on it.
    /// </summary>
    internal void RecordPermutationUse(string key) => usedPermutationKeys.Add(key);

    public SemanticModel GetSemanticModel(SyntaxTree syntaxTree) {
        if (!semanticModels.TryGetValue(syntaxTree, out var model)) {
            semanticModels[syntaxTree] = model = new(this, syntaxTree);
        }

        return model;
    }

    /// <summary>Every type declared in the compilation, nested types included.</summary>
    public IReadOnlyList<NamedTypeSymbol> GetAllTypes() {
        EnsureDeclarations();
        List<NamedTypeSymbol> result = [];

        void Collect(SourceNamedTypeSymbol type) {
            result.Add(type);
            foreach (var nested in type.NestedTypes) {
                Collect(nested);
            }
        }

        foreach (var types in typesByTree.Values) {
            foreach (var type in types) {
                Collect(type);
            }
        }

        return result;
    }

    /// <summary>
    ///     Every type the referenced libraries declare, nested types included.
    /// </summary>
    /// <remarks>
    ///     Kept apart from <see cref="GetAllTypes" />, which answers "what does this compilation
    ///     declare?" — the question lowering asks, and it must not lower a library's types a second
    ///     time. What both are needed for is resolution by name: a <c>compose</c> slot may be
    ///     filled by a shader that ships in a library.
    /// </remarks>
    public IReadOnlyList<NamedTypeSymbol> GetReferencedTypes() {
        EnsureDeclarations();
        List<NamedTypeSymbol> result = [];

        void Collect(MetadataNamedTypeSymbol type) {
            result.Add(type);
            foreach (var nested in type.NestedTypes) {
                Collect(nested);
            }
        }

        foreach (var type in metadata?.TopLevelTypes ?? []) {
            Collect(type);
        }

        return result;
    }

    /// <summary>The loaded reference symbols, or null when nothing was referenced.</summary>
    internal MetadataLoader? Metadata {
        get {
            EnsureDeclarations();
            return metadata;
        }
    }

    /// <summary>Types declared at the top level of one syntax tree.</summary>
    public IReadOnlyList<NamedTypeSymbol> GetDeclaredTypes(SyntaxTree syntaxTree) {
        EnsureDeclarations();
        return typesByTree.GetValueOrDefault(syntaxTree) ?? (IReadOnlyList<NamedTypeSymbol>)[];
    }

    /// <summary>Entry-point methods across every shader in the compilation.</summary>
    public IReadOnlyList<MethodSymbol> GetEntryPoints() =>
        GetAllTypes()
            .Where(t => t.TypeKind == TypeKind.Shader)
            .SelectMany(t => t.GetMembers().OfType<MethodSymbol>())
            .Where(m => m.Stage != ShaderStage.None)
            .ToArray();

    /// <summary>
    ///     Syntax, declaration and binding diagnostics for the whole compilation,
    ///     ordered by file and position.
    /// </summary>
    public IReadOnlyList<Diagnostic> GetDiagnostics() {
        List<Diagnostic> all = [];

        foreach (var tree in syntaxTrees) {
            all.AddRange(tree.Diagnostics);
        }

        // Binding a tree can force declarations it had not needed yet, so collect
        // the per-tree diagnostics before snapshotting the declaration bag.
        foreach (var tree in syntaxTrees) {
            all.AddRange(GetSemanticModel(tree).Diagnostics);
        }

        ReportRecursion();
        all.AddRange(declarationDiagnostics.ToArray());

        return all
            .OrderBy(d => d.Location.FilePath, StringComparer.Ordinal)
            .ThenBy(d => d.Location.SourceSpan.Start)
            .ThenBy(d => d.Id, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>Refuses a call graph with a cycle in it — <c>RVN2139</c>.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Here rather than in <see cref="SemanticModel" />, because a call graph is the
    ///         compilation's and not a file's.</b> Two structs in two files calling each other's
    ///         methods is the same defect as one function calling itself, and a per-tree check would
    ///         close the second and leave the first — through which the identical
    ///         <c>spirv-val</c> refusal escapes. Every model is bound by the loop above, so by this
    ///         point every body in the compilation exists.
    ///     </para>
    ///     <para>
    ///         Guarded, because <see cref="GetDiagnostics" /> is a query a caller may run more than
    ///         once and the bag it reports into is kept for the compilation's lifetime.
    ///     </para>
    /// </remarks>
    void ReportRecursion() {
        if (recursionChecked) {
            return;
        }

        recursionChecked = true;

        RecursionCheck.Report(
            syntaxTrees.SelectMany(tree => GetSemanticModel(tree).GetBoundBodies()).ToArray(),
            declarationDiagnostics
        );
    }

    void EnsureDeclarations() {
        if (declarationsBuilt) {
            return;
        }

        // Published before building: creating a type symbol can look names up,
        // and lookup reads the (still growing) global namespace.
        declarationsBuilt = true;
        globalNamespace = new(string.Empty, null);

        LoadReferences();

        // Pass 1 — namespaces and per-file scopes. Imports inside those scopes
        // resolve lazily, once every declaration below exists.
        foreach (var tree in syntaxTrees) {
            if (tree.GetRoot() is not CompilationUnitSyntax unit) {
                continue;
            }

            var package = globalNamespace.GetOrAddNamespace(ImportBinder.FlattenName(unit.Package?.PackageName));
            importBinders[tree] = new(GlobalBinder, package, unit);
        }

        // Pass 2 — the types themselves.
        foreach (var tree in syntaxTrees) {
            if (tree.GetRoot() is not CompilationUnitSyntax unit || !importBinders.TryGetValue(tree, out var binder)) {
                continue;
            }

            List<SourceNamedTypeSymbol> declared = [];

            foreach (var member in unit.Members) {
                if (TypeDeclarationInfo.From(member) is not { } declaration) {
                    ReportMemberOutsideAType(member);
                    continue;
                }

                var symbol = new SourceNamedTypeSymbol(binder.PackageNamespace, declaration, binder);

                var existing = binder.PackageNamespace.GetTypeMember(
                    symbol.Name,
                    declaration.TypeParameterList?.Parameters.Count ?? 0
                );

                // A source declaration shadows a referenced library's type of the same name, which
                // is what every compiler with a reference model does — but silently preferring one
                // of two same-named types is how a shader ends up bound against the definition its
                // author was not reading, so it is said.
                if (existing is MetadataNamedTypeSymbol shadowed) {
                    declarationDiagnostics.Add(
                        LibraryDiagnostics.ReferenceHiddenBySource,
                        declaration.Identifier.GetLocation(),
                        shadowed.Declaration.QualifiedName,
                        shadowed.LibraryName
                    );

                    binder.PackageNamespace.ReplaceType(shadowed, symbol);
                    declared.Add(symbol);
                    continue;
                }

                if (existing is not null) {
                    declarationDiagnostics.Add(
                        SemanticDiagnostics.DuplicateDeclaration,
                        declaration.Identifier.GetLocation(),
                        symbol.Name
                    );
                    continue;
                }

                binder.PackageNamespace.AddType(symbol);
                declared.Add(symbol);
            }

            typesByTree[tree] = declared;
        }
    }

    /// <summary>
    ///     Reports a member written straight into a file — <see cref="SemanticDiagnostics.MemberOutsideAType" />.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ The loop above used to <c>continue</c> here, and that silence was the bug: a
    ///         package-level <c>func</c> parses (the compilation unit and a type body share one
    ///         <c>ParseMemberDeclaration</c>), is then not a type, and was dropped. Nothing bound
    ///         its body, so an undefined name inside it went unreported too — the file compiled
    ///         clean and the function was not there. A call to it was <c>RVN2010</c> at the call
    ///         site, pointing at the one line that was right.
    ///     </para>
    ///     <para>
    ///         Reported rather than bound because a namespace here holds namespaces and types and
    ///         nothing else, so nothing could name one however well it bound. Making these legal is
    ///         a language change — namespace members, import resolution, mangling, the library
    ///         format, reflection — and not this.
    ///     </para>
    ///     <para>
    ///         Named where it can be: a method, a property and a field all carry an identifier. An
    ///         <c>init</c> and an <c>operator</c> do not, so they are named by what they are.
    ///     </para>
    /// </remarks>
    void ReportMemberOutsideAType(MemberDeclarationSyntax member) {
        var (name, kind, location) = member switch {
            MethodDeclarationSyntax method => (
                method.Identifier.ValueText,
                "function",
                method.Identifier.GetLocation()
            ),
            PropertyDeclarationSyntax property => (
                property.Identifier.ValueText,
                "property",
                property.Identifier.GetLocation()
            ),
            FieldDeclarationSyntax field => (
                field.Declaration.Identifier.ValueText,
                field.Declaration.Keyword.Kind == SyntaxKind.ValKeyword ? "value" : "variable",
                field.Declaration.Identifier.GetLocation()
            ),
            ConstructorDeclarationSyntax constructor => (
                "init",
                "constructor",
                constructor.Keyword.GetLocation()
            ),
            OperatorDeclarationSyntax @operator => (
                @operator.OperatorToken.ValueText,
                "operator",
                @operator.OperatorKeyword.GetLocation()
            ),
            _ => (member.ToString().Trim(), "declaration", member.GetLocation())
        };

        declarationDiagnostics.Add(SemanticDiagnostics.MemberOutsideAType, location, name, kind);
    }

    /// <summary>
    ///     Loads every referenced library's declarations into the global namespace, before any
    ///     source declaration exists.
    /// </summary>
    /// <remarks>
    ///     References first so that a source type can shadow one and be seen to
    ///     (<see cref="LibraryDiagnostics.ReferenceHiddenBySource" />); resolution inside the
    ///     libraries stays lazy, so the order two libraries are loaded in does not decide whether a
    ///     cross-library reference resolves.
    /// </remarks>
    void LoadReferences() {
        if (references.Length == 0) {
            return;
        }

        metadata = new(declarationDiagnostics);
        HashSet<string> loaded = new(StringComparer.Ordinal);

        foreach (var reference in references) {
            if (!loaded.Add(reference.Name)) {
                declarationDiagnostics.Add(
                    LibraryDiagnostics.DuplicateReference,
                    Location.None,
                    reference.Name
                );
                continue;
            }

            metadata.Load(reference.Library, globalNamespace!);
        }
    }

    /// <summary>The scope a compilation unit's declarations and bodies live in.</summary>
    internal ImportBinder GetImportBinder(SyntaxTree syntaxTree) {
        EnsureDeclarations();
        return importBinders[syntaxTree];
    }
}
