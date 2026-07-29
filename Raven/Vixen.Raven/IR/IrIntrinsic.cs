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

    /// <summary>
    ///     Sample a texture through a sampler at a level of detail the caller's own gradients
    ///     imply.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Neither <see cref="SampleTexture" /> nor <see cref="SampleTextureLevel" /> covers
    ///         this, and the gap is not a convenience one. The first takes its gradients from the
    ///         fragment quad, which means nothing where the pixel next door is a different triangle
    ///         of a different material — every silhouette and every material boundary in a
    ///         visibility-buffer resolve. The second states one number, and one number throws away
    ///         anisotropy, which is visible as blur on every floor seen at a grazing angle.
    ///     </para>
    ///     <para>
    ///         So the gradients arrive as values: computed analytically from the triangle's
    ///         screen-space plane, propagated through the UV interpolation, and handed over. Both
    ///         targets take them the same way — SPIR-V's <c>Grad</c> image operand and GLSL's
    ///         <c>textureGrad</c> — and both accept them in every stage, because a stated gradient
    ///         needs no quad to derive one from.
    ///     </para>
    /// </remarks>
    SampleTextureGrad,

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
    ArrayLength,

    /// <summary>
    ///     Waits until every invocation of the workgroup has reached this point, and until every
    ///     write to workgroup storage made before it is visible to all of them.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Both halves, because separating them would be a trap. An execution barrier alone
    ///         guarantees that the other invocations <em>arrived</em>, not that what they wrote can
    ///         be seen — and the code that follows a barrier is, without exception, code that reads
    ///         what they wrote. GLSL's <c>barrier()</c> in a compute stage is defined to do both,
    ///         and this matches it rather than inventing a weaker primitive.
    ///     </para>
    ///     <para>
    ///         Produces nothing, like <see cref="StoreImage" />, and unlike it is not even a store:
    ///         it is a statement whose whole effect is on other invocations.
    ///     </para>
    /// </remarks>
    ControlBarrier,

    /// <summary>
    ///     Orders this invocation's writes to workgroup storage against its later reads, without
    ///     waiting for anybody.
    /// </summary>
    /// <remarks>
    ///     The half of <see cref="ControlBarrier" /> that is about memory rather than about
    ///     arrival. Rarely what a shader wants on its own — if another invocation wrote the value,
    ///     visibility without arrival guarantees nothing — but it is the cheaper thing where the
    ///     arrival is already established, so it is spelled separately rather than folded in.
    /// </remarks>
    MemoryBarrierShared
}
