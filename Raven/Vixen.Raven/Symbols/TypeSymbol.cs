namespace Vixen.Raven.Symbols;

/// <summary>
///     Base of everything usable as a type. Type identity is <em>structural</em>:
///     two <c>int[]</c> symbols built independently compare equal, so the binder can
///     construct types on demand without interning them.
/// </summary>
public abstract class TypeSymbol : Symbol {
    public abstract TypeKind TypeKind { get; }

    /// <summary>The intrinsic type this is, or <see cref="SpecialType.None" />.</summary>
    public virtual SpecialType SpecialType => SpecialType.None;

    /// <summary>True when this type stands in for a type that failed to resolve.</summary>
    public bool IsErrorType => TypeKind == TypeKind.Error;

    public bool IsVoid => TypeKind == TypeKind.Void;

    /// <summary>Scalar, vector or matrix — the types arithmetic operators accept.</summary>
    public bool IsNumericLike => TypeKind is TypeKind.Scalar or TypeKind.Vector or TypeKind.Matrix;

    /// <summary>The type this one derives from, or null.</summary>
    public virtual NamedTypeSymbol? BaseType => null;

    /// <summary>Protocols this type conforms to.</summary>
    public virtual IReadOnlyList<NamedTypeSymbol> Interfaces => [];

    public virtual IReadOnlyList<Symbol> GetMembers() => [];

    public virtual IReadOnlyList<Symbol> GetMembers(string name) {
        List<Symbol>? matches = null;
        foreach (var member in GetMembers()) {
            if (member.Name == name) {
                (matches ??= []).Add(member);
            }
        }

        return matches ?? (IReadOnlyList<Symbol>)[];
    }

    /// <summary>
    ///     This type plus its base types and protocols, nearest first. Member lookup
    ///     walks this sequence and stops at the first type that has a match.
    /// </summary>
    public IEnumerable<TypeSymbol> TypeAndBases() {
        var seen = new HashSet<TypeSymbol>();
        var queue = new Queue<TypeSymbol>();
        queue.Enqueue(this);

        while (queue.Count > 0) {
            var current = queue.Dequeue();
            if (!seen.Add(current)) {
                continue;
            }

            yield return current;

            if (current.BaseType is { } baseType) {
                queue.Enqueue(baseType);
            }

            foreach (var protocol in current.Interfaces) {
                queue.Enqueue(protocol);
            }
        }
    }

    /// <summary>True when <paramref name="other" /> is this type or one of its bases/protocols.</summary>
    public bool IsSubtypeOf(TypeSymbol other) {
        foreach (var type in TypeAndBases()) {
            if (type.Equals(other)) {
                return true;
            }
        }

        return false;
    }
}
