namespace Vixen.Raven.Symbols;

/// <summary>
///     A package namespace. <c>package A.B</c> creates the chain
///     <c>&lt;global&gt; → A → B</c>; every type declared in that file lands in
///     <c>B</c>.
/// </summary>
public sealed class NamespaceSymbol : Symbol {
    readonly Dictionary<string, NamespaceSymbol> namespaces = [];
    readonly Dictionary<string, List<NamedTypeSymbol>> types = [];

    public override SymbolKind Kind => SymbolKind.Namespace;
    public override string Name { get; }
    public override Symbol? ContainingSymbol { get; }

    public bool IsGlobalNamespace => ContainingSymbol is null;

    /// <summary>Dotted name from the global namespace down, empty for the global namespace.</summary>
    public string QualifiedName {
        get {
            if (IsGlobalNamespace) {
                return string.Empty;
            }

            var parent = (NamespaceSymbol?)ContainingSymbol;
            return parent!.IsGlobalNamespace ? Name : parent.QualifiedName + "." + Name;
        }
    }

    public IReadOnlyList<NamespaceSymbol> Namespaces => namespaces.Values.ToArray();

    public IReadOnlyList<NamedTypeSymbol> Types => types.Values.SelectMany(t => t).ToArray();

    internal NamespaceSymbol(string name, NamespaceSymbol? container) {
        Name = name;
        ContainingSymbol = container;
    }

    public IReadOnlyList<Symbol> GetMembers() => [.. Namespaces, .. Types];

    public IReadOnlyList<Symbol> GetMembers(string name) {
        List<Symbol> found = [];
        if (namespaces.TryGetValue(name, out var ns)) {
            found.Add(ns);
        }

        if (types.TryGetValue(name, out var matches)) {
            found.AddRange(matches);
        }

        return found;
    }

    public NamespaceSymbol? GetNamespace(string name) => namespaces.GetValueOrDefault(name);

    /// <summary>The type declared here with this name and generic arity, if any.</summary>
    public NamedTypeSymbol? GetTypeMember(string name, int arity = 0) {
        if (!types.TryGetValue(name, out var matches)) {
            return null;
        }

        foreach (var type in matches) {
            if (type.Arity == arity) {
                return type;
            }
        }

        return null;
    }

    public override string ToDisplayString() => IsGlobalNamespace ? "<global namespace>" : QualifiedName;

    /// <summary>Gets or creates the child namespace with this name.</summary>
    internal NamespaceSymbol GetOrAddNamespace(string name) {
        if (namespaces.TryGetValue(name, out var existing)) {
            return existing;
        }

        var created = new NamespaceSymbol(name, this);
        namespaces[name] = created;
        return created;
    }

    /// <summary>Resolves a dotted path, creating each segment as needed.</summary>
    internal NamespaceSymbol GetOrAddNamespace(IEnumerable<string> path) {
        var current = this;
        foreach (var segment in path) {
            current = current.GetOrAddNamespace(segment);
        }

        return current;
    }

    internal void AddType(NamedTypeSymbol type) {
        if (!types.TryGetValue(type.Name, out var list)) {
            types[type.Name] = list = [];
        }

        list.Add(type);
    }
}
