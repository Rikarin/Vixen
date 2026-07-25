
namespace Vixen.Raven.Symbols;

/// <summary>How one type reaches another.</summary>
public enum ConversionKind {
    /// <summary>No conversion exists.</summary>
    None,
    Identity,

    /// <summary>A widening numeric conversion (<c>int</c> → <c>float</c>, <c>int2</c> → <c>float2</c>).</summary>
    ImplicitNumeric,

    /// <summary>A literal whose value fits the target (<c>val x: float = 1</c>).</summary>
    ImplicitConstant,

    /// <summary>A scalar broadcast across a vector's lanes (<c>float3(0)</c>, <c>v * 2</c>).</summary>
    ImplicitSplat,

    /// <summary>Derived → base, or a type → a protocol it conforms to.</summary>
    ImplicitReference,
    ExplicitNumeric,

    /// <summary>Base → derived.</summary>
    ExplicitReference,
    ExplicitEnumeration
}

/// <summary>
///     The result of classifying a conversion. <see cref="Cost" /> ranks candidates
///     during overload resolution — lower is a better match, and an identity
///     conversion costs nothing.
/// </summary>
public readonly struct Conversion {
    public static readonly Conversion None = new(ConversionKind.None);
    public static readonly Conversion Identity = new(ConversionKind.Identity);

    public ConversionKind Kind { get; }

    public bool Exists => Kind != ConversionKind.None;

    public bool IsImplicit =>
        Kind is ConversionKind.Identity
            or ConversionKind.ImplicitNumeric
            or ConversionKind.ImplicitConstant
            or ConversionKind.ImplicitSplat
            or ConversionKind.ImplicitReference;

    /// <summary>True when the conversion changes representation and must be materialized.</summary>
    public bool IsIdentity => Kind == ConversionKind.Identity;

    /// <summary>Ranking for overload resolution; only meaningful for implicit conversions.</summary>
    public int Cost =>
        Kind switch {
            ConversionKind.Identity => 0,
            ConversionKind.ImplicitNumeric => 1,
            ConversionKind.ImplicitConstant => 1,
            ConversionKind.ImplicitSplat => 3,
            ConversionKind.ImplicitReference => 4,
            _ => int.MaxValue
        };

    public Conversion(ConversionKind kind) {
        Kind = kind;
    }

    public override string ToString() => Kind.ToString();
}
