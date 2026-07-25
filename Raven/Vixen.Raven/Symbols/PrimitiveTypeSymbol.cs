namespace Vixen.Raven.Symbols;

/// <summary>
///     A built-in scalar, vector or matrix (<c>int</c>, <c>float3</c>, <c>mat3x4</c>).
///     Instances are singletons owned by <see cref="BuiltInTypes" />, so reference
///     equality is type identity.
/// </summary>
public sealed class PrimitiveTypeSymbol : NamedTypeSymbol {
    const string XyzwComponents = "xyzw";
    const string RgbaComponents = "rgba";

    readonly Dictionary<string, Symbol> swizzles = [];

    public override string Name { get; }
    public override SpecialType SpecialType { get; }
    public override TypeKind TypeKind { get; }

    /// <summary>The scalar each component holds (itself, for a scalar type).</summary>
    public SpecialType ComponentSpecialType { get; }

    public int Rows { get; }
    public int Columns { get; }

    /// <summary>Number of components: the vector length, or rows × columns for a matrix.</summary>
    public int ComponentCount => Rows * Columns;

    /// <summary>The scalar type of a component.</summary>
    public PrimitiveTypeSymbol ComponentType => BuiltInTypes.FromSpecialType(ComponentSpecialType);

    internal PrimitiveTypeSymbol(
        string name,
        SpecialType specialType,
        TypeKind typeKind,
        SpecialType componentType = SpecialType.None,
        int rows = 1,
        int columns = 1
    ) {
        Name = name;
        SpecialType = specialType;
        TypeKind = typeKind;
        ComponentSpecialType = componentType == SpecialType.None ? specialType : componentType;
        Rows = rows;
        Columns = columns;
    }

    public override string ToDisplayString() => Name;

    /// <summary>
    ///     Vectors expose swizzles as members: <c>v.x</c>, <c>v.xy</c>, <c>c.rgb</c>.
    ///     A one-component swizzle yields the scalar, longer ones a vector of that
    ///     length. The two component sets may not be mixed.
    /// </summary>
    public override IReadOnlyList<Symbol> GetMembers(string name) {
        if (TypeKind != TypeKind.Vector || name.Length is 0 or > 4) {
            return [];
        }

        if (swizzles.TryGetValue(name, out var cached)) {
            return [cached];
        }

        var set = XyzwComponents.Contains(name[0]) ? XyzwComponents
            : RgbaComponents.Contains(name[0]) ? RgbaComponents
            : null;

        if (set is null) {
            return [];
        }

        foreach (var character in name) {
            var index = set.IndexOf(character);
            if (index < 0 || index >= ComponentCount) {
                return [];
            }
        }

        var resultType = name.Length == 1
            ? ComponentType
            : BuiltInTypes.Vector(ComponentSpecialType, name.Length);

        if (resultType is null) {
            return [];
        }

        // A swizzle over distinct components is assignable; `v.xx` is not.
        var writable = name.Distinct().Count() == name.Length;
        var symbol = new SynthesizedFieldSymbol(this, name, resultType, !writable);
        swizzles[name] = symbol;
        return [symbol];
    }
}
