
namespace Vixen.Raven.IR;

/// <summary>
///     A built-in operation, resolved from an overload of the intrinsic library to
///     a single opcode. Backends map these to their own spelling
///     (<c>mix</c>/<c>lerp</c>, <c>fract</c>/<c>frac</c>) rather than matching on
///     names.
/// </summary>
public enum IrIntrinsic {
    Abs,
    Sign,
    Floor,
    Ceil,
    Round,
    Truncate,
    Fract,
    Saturate,
    Sqrt,
    InverseSqrt,
    Exp,
    Exp2,
    Log,
    Log2,
    Sin,
    Cos,
    Tan,
    Asin,
    Acos,
    Atan,
    Atan2,
    Radians,
    Degrees,
    DdX,
    DdY,
    Min,
    Max,
    Pow,
    Mod,
    Step,
    Clamp,
    Lerp,
    SmoothStep,
    Length,
    Distance,
    Dot,
    Cross,
    Normalize,
    Reflect,
    Refract,
    Transpose,
    All,
    Any,

    /// <summary>Sample a texture through a sampler.</summary>
    SampleTexture,

    /// <summary>Fetch a texel by integer coordinate.</summary>
    LoadTexture,

    /// <summary>Number of elements in an array.</summary>
    ArrayLength
}
