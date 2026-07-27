// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0


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

    /// <summary>Sample a texture through a sampler, at the level of detail the derivatives imply.</summary>
    SampleTexture,

    /// <summary>
    ///     Sample a texture through a sampler at a stated level of detail. Separate from
    ///     <see cref="SampleTexture" /> rather than an optional operand, because the two are
    ///     different instructions in both targets and only this one is legal outside a fragment
    ///     stage.
    /// </summary>
    SampleTextureLevel,

    /// <summary>Fetch a texel by integer coordinate.</summary>
    LoadTexture,

    /// <summary>The size of one mip level of a texture, in texels.</summary>
    TextureSize,

    /// <summary>Read one texel of a storage image. No sampler and no filtering.</summary>
    LoadImage,

    /// <summary>Write one texel of a storage image. The only intrinsic that returns nothing.</summary>
    StoreImage,

    /// <summary>The size of a storage image, which has exactly one level.</summary>
    ImageSize,

    /// <summary>
    ///     The same bits read as another type of the same width — <c>asfloat</c> and friends.
    ///     One opcode rather than one per pair: the instruction carries both types already.
    /// </summary>
    BitCast,

    /// <summary>Number of elements in an array.</summary>
    ArrayLength
}
