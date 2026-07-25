
namespace Vixen.Raven.Symbols;

/// <summary>
///     Type-level conversion rules. The binder layers the expression-aware case on
///     top (a constant literal that fits the target); everything decidable from the
///     two types alone lives here.
/// </summary>
public static class Conversions {
    /// <summary>Widening scalar conversions, keyed by source.</summary>
    static readonly Dictionary<SpecialType, SpecialType[]> ImplicitScalar = new() {
        [SpecialType.Int] = [SpecialType.Float, SpecialType.Double],
        [SpecialType.UInt] = [SpecialType.Float, SpecialType.Double],
        [SpecialType.Float] = [SpecialType.Double]
    };

    static readonly SpecialType[] NumericScalars = [
        SpecialType.Int, SpecialType.UInt, SpecialType.Float, SpecialType.Double
    ];

    /// <summary>Classifies the conversion from <paramref name="source" /> to <paramref name="target" />.</summary>
    public static Conversion Classify(TypeSymbol source, TypeSymbol target) {
        // An unresolved type already produced a diagnostic; let it convert
        // anywhere so a single mistake reports once.
        if (source.IsErrorType || target.IsErrorType) {
            return Conversion.Identity;
        }

        if (source.Equals(target)) {
            return Conversion.Identity;
        }

        if (source.IsVoid || target.IsVoid) {
            return Conversion.None;
        }

        if (ClassifyNumeric(source, target) is { Exists: true } numeric) {
            return numeric;
        }

        // enum <-> its underlying integral representation
        if ((source.TypeKind == TypeKind.Enum && target.SpecialType is SpecialType.Int or SpecialType.UInt)
            || (target.TypeKind == TypeKind.Enum && source.SpecialType is SpecialType.Int or SpecialType.UInt)) {
            return new(ConversionKind.ExplicitEnumeration);
        }

        if (source.IsSubtypeOf(target)) {
            return new(ConversionKind.ImplicitReference);
        }

        if (target.IsSubtypeOf(source)) {
            return new(ConversionKind.ExplicitReference);
        }

        return Conversion.None;
    }

    /// <summary>True when a value of <paramref name="source" /> may be used where <paramref name="target" /> is expected.</summary>
    public static bool HasImplicitConversion(TypeSymbol source, TypeSymbol target) {
        var conversion = Classify(source, target);
        return conversion.Exists && conversion.IsImplicit;
    }

    /// <summary>
    ///     The type both operands of a binary operator are converted to, or null if
    ///     there is no such type. Vectors win over the scalars they mix with, so
    ///     <c>float3 * 2</c> yields <c>float3</c>.
    /// </summary>
    public static TypeSymbol? FindCommonType(TypeSymbol left, TypeSymbol right) {
        if (left.Equals(right)) {
            return left;
        }

        if (HasImplicitConversion(left, right)) {
            return right;
        }

        if (HasImplicitConversion(right, left)) {
            return left;
        }

        // int2 + float2 -> float2: no direct conversion either way when the
        // component types widen in opposite directions, so widen componentwise.
        if (left is PrimitiveTypeSymbol { TypeKind: TypeKind.Vector } leftVector
            && right is PrimitiveTypeSymbol { TypeKind: TypeKind.Vector } rightVector
            && leftVector.ComponentCount == rightVector.ComponentCount) {
            var component = FindCommonScalar(leftVector.ComponentSpecialType, rightVector.ComponentSpecialType);
            return component is null ? null : BuiltInTypes.Vector(component.Value, leftVector.ComponentCount);
        }

        return null;
    }

    static SpecialType? FindCommonScalar(SpecialType left, SpecialType right) {
        if (left == right) {
            return left;
        }

        if (IsImplicitScalar(left, right)) {
            return right;
        }

        if (IsImplicitScalar(right, left)) {
            return left;
        }

        return null;
    }

    static bool IsImplicitScalar(SpecialType source, SpecialType target) =>
        ImplicitScalar.TryGetValue(source, out var targets) && Array.IndexOf(targets, target) >= 0;

    static Conversion ClassifyNumeric(TypeSymbol source, TypeSymbol target) {
        if (source is not PrimitiveTypeSymbol from || target is not PrimitiveTypeSymbol to) {
            return Conversion.None;
        }

        var fromScalar = from.TypeKind == TypeKind.Scalar;
        var toScalar = to.TypeKind == TypeKind.Scalar;

        if (fromScalar && toScalar) {
            if (IsImplicitScalar(from.SpecialType, to.SpecialType)) {
                return new(ConversionKind.ImplicitNumeric);
            }

            return IsNumericScalar(from.SpecialType) && IsNumericScalar(to.SpecialType)
                ? new(ConversionKind.ExplicitNumeric)
                : Conversion.None;
        }

        // scalar -> vector: broadcast, provided the component widens implicitly
        if (fromScalar && to.TypeKind == TypeKind.Vector) {
            var component = to.ComponentSpecialType;
            if (from.SpecialType == component || IsImplicitScalar(from.SpecialType, component)) {
                return new(ConversionKind.ImplicitSplat);
            }

            return IsNumericScalar(from.SpecialType) && IsNumericScalar(component)
                ? new(ConversionKind.ExplicitNumeric)
                : Conversion.None;
        }

        if (from.TypeKind == TypeKind.Vector
            && to.TypeKind == TypeKind.Vector
            && from.ComponentCount == to.ComponentCount) {
            if (IsImplicitScalar(from.ComponentSpecialType, to.ComponentSpecialType)) {
                return new(ConversionKind.ImplicitNumeric);
            }

            return IsNumericScalar(from.ComponentSpecialType) && IsNumericScalar(to.ComponentSpecialType)
                ? new(ConversionKind.ExplicitNumeric)
                : Conversion.None;
        }

        return Conversion.None;
    }

    static bool IsNumericScalar(SpecialType type) => Array.IndexOf(NumericScalars, type) >= 0;
}
