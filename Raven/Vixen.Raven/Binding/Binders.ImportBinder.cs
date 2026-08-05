// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Raven.Symbols;
using Vixen.Raven.Syntax;

namespace Vixen.Raven.Binding;

/// <summary>
///     A compilation unit's scope: its <c>package</c> namespace and each enclosing
///     one, plus everything its <c>import</c> directives bring in. Imports resolve on
///     first use, after every declaration in the compilation exists.
/// </summary>
internal sealed class ImportBinder(Binder next, NamespaceSymbol packageNamespace, CompilationUnitSyntax unit)
    : Binder(next) {
    NamespaceSymbol[]? importedNamespaces;
    NamespaceSymbol[]? packageChain;
    TypeSymbol[]? staticImports;

    /// <summary>The namespace this file's declarations go into.</summary>
    public NamespaceSymbol PackageNamespace { get; } = packageNamespace;

    void EnsureResolved() {
        if (packageChain is not null) {
            return;
        }

        // `package A.B` puts both A.B and A in scope, innermost first.
        List<NamespaceSymbol> chain = [];
        for (var ns = PackageNamespace;
             ns is { IsGlobalNamespace: false };
             ns = (NamespaceSymbol?)ns.ContainingSymbol) {
            chain.Add(ns);
        }

        packageChain = chain.ToArray();

        List<NamespaceSymbol> namespaces = [];
        List<TypeSymbol> types = [];

        foreach (var import in unit.Imports) {
            var path = FlattenName(import.Name);
            if (path.Count == 0) {
                continue;
            }

            if (import.StaticKeyword is not null) {
                if (ResolveNamespace(path.Take(path.Count - 1)) is { } container
                    && container.GetTypeMember(path[^1]) is { } type) {
                    types.Add(type);
                }

                continue;
            }

            // An import that names nothing is ignored rather than reported. A referenced
            // library's packages are in the global namespace by the time this runs, so an
            // import does reach them — but an unused import of a package this build did not
            // reference is not itself an error, and the missing name is reported where it is
            // used.
            if (ResolveNamespace(path) is { } resolved) {
                namespaces.Add(resolved);
            }
        }

        importedNamespaces = namespaces.ToArray();
        staticImports = types.ToArray();
    }

    NamespaceSymbol? ResolveNamespace(IEnumerable<string> path) {
        var current = Compilation.GlobalNamespace;
        foreach (var segment in path) {
            var next = current.GetNamespace(segment);
            if (next is null) {
                return null;
            }

            current = next;
        }

        return current;
    }

    /// <summary>Flattens a dotted name into its segments, left to right.</summary>
    internal static List<string> FlattenName(NameSyntax? syntax) {
        List<string> segments = [];

        void Walk(NameSyntax? node) {
            switch (node) {
                case QualifiedNameSyntax qualified:
                    Walk(qualified.Left);
                    segments.Add(qualified.Right.Identifier.ValueText);
                    break;
                case SimpleNameSyntax simple:
                    segments.Add(simple.Identifier.ValueText);
                    break;
            }
        }

        Walk(syntax);
        return segments;
    }

    private protected override void LookupInScope(string name, List<Symbol> results) {
        EnsureResolved();

        foreach (var ns in packageChain!) {
            results.AddRange(ns.GetMembers(name));
        }

        foreach (var ns in importedNamespaces!) {
            results.AddRange(ns.GetMembers(name));
        }

        foreach (var type in staticImports!) {
            foreach (var member in LookupMembers(type, name)) {
                if (member.IsStatic) {
                    results.Add(member);
                }
            }
        }
    }
}
