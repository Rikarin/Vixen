// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Raven.Binding;
using Vixen.Raven.Diagnostics;
using Vixen.Raven.Symbols;
using Vixen.Raven.Symbols.Source;
using Vixen.Raven.Syntax;
using Vixen.Core.Syntax.Diagnostics;

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
    readonly Dictionary<SyntaxTree, SemanticModel> semanticModels = [];
    readonly SyntaxTree[] syntaxTrees;
    readonly Dictionary<SyntaxTree, List<SourceNamedTypeSymbol>> typesByTree = [];

    BindingContext? declarationContext;
    bool declarationsBuilt;
    Binder? globalBinder;
    NamespaceSymbol? globalNamespace;

    public string AssemblyName { get; }

    public IReadOnlyList<SyntaxTree> SyntaxTrees => syntaxTrees;

    /// <summary>Root of the symbol table; every package hangs off it.</summary>
    public NamespaceSymbol GlobalNamespace {
        get {
            EnsureDeclarations();
            return globalNamespace!;
        }
    }

    internal BindingContext DeclarationContext => declarationContext ??= new(this, declarationDiagnostics);

    internal Binder GlobalBinder => globalBinder ??= new GlobalBinder(DeclarationContext);

    Compilation(string assemblyName, SyntaxTree[] syntaxTrees) {
        AssemblyName = assemblyName;
        this.syntaxTrees = syntaxTrees;
    }

    public static Compilation Create(string assemblyName, params SyntaxTree[] syntaxTrees) =>
        new(assemblyName, syntaxTrees);

    public static Compilation Create(string assemblyName, IEnumerable<SyntaxTree> syntaxTrees) =>
        new(assemblyName, syntaxTrees.ToArray());

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

        all.AddRange(declarationDiagnostics.ToArray());

        return all
            .OrderBy(d => d.Location.FilePath, StringComparer.Ordinal)
            .ThenBy(d => d.Location.SourceSpan.Start)
            .ThenBy(d => d.Id, StringComparer.Ordinal)
            .ToArray();
    }

    void EnsureDeclarations() {
        if (declarationsBuilt) {
            return;
        }

        // Published before building: creating a type symbol can look names up,
        // and lookup reads the (still growing) global namespace.
        declarationsBuilt = true;
        globalNamespace = new(string.Empty, null);

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
                    continue;
                }

                var symbol = new SourceNamedTypeSymbol(binder.PackageNamespace, declaration, binder);

                if (binder.PackageNamespace.GetTypeMember(
                        symbol.Name,
                        declaration.TypeParameterList?.Parameters.Count ?? 0
                    )
                    is not null) {
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

    /// <summary>The scope a compilation unit's declarations and bodies live in.</summary>
    internal ImportBinder GetImportBinder(SyntaxTree syntaxTree) {
        EnsureDeclarations();
        return importBinders[syntaxTree];
    }
}
