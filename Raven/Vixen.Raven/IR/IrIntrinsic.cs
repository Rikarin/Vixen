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

    /// <summary>
    ///     Compare a depth image against a reference and filter the results, at the level of detail
    ///     the derivatives imply.
    /// </summary>
    /// <remarks>
    ///     ⚠ Not <see cref="SampleTexture" /> with an extra argument. The value that comes back is
    ///     the <em>filtered comparison</em> — one float, already averaged over the footprint by
    ///     fixed function — where a plain sample returns the texels. That is the whole reason a
    ///     hardware shadow lookup is worth having, and it is why this is a distinct instruction in
    ///     both targets: <c>OpImageSampleDrefImplicitLod</c> and <c>texture(sampler2DShadow, …)</c>.
    /// </remarks>
    SampleTextureCompare,

    /// <summary>
    ///     Compare a depth image against a reference at level zero.
    /// </summary>
    /// <remarks>
    ///     Level zero rather than a stated level, because a shadow map has one: an atlas page is
    ///     rendered at a single resolution and a mip of a depth buffer is a depth nothing was ever
    ///     at. Separate from <see cref="SampleTextureCompare" /> for
    ///     <see cref="SampleTextureLevel" />'s reason — an implicit level of detail needs a quad,
    ///     and this is the form a compute stage may use.
    /// </remarks>
    SampleTextureCompareLevelZero,

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
    MemoryBarrierShared,

    /// <summary>
    ///     Runs one whole ray query against an acceleration structure:
    ///     <c>scene.Trace(origin, tmin, direction, tmax)</c>. Arguments are
    ///     <c>[structure, origin, tmin, direction, tmax]</c> — the receiver first, like every
    ///     member intrinsic.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The contract is fixed here so both backends implement one thing: the query opens
    ///         with ray flags <em>Opaque</em> (0x01) and cull mask 0xFF, proceeds to completion,
    ///         and answers a <c>float4</c> — for a committed triangle hit
    ///         <c>(t, float(primitiveIndex), float(instanceId), 1.0)</c>, and for a miss
    ///         <c>(maxDistance, -1.0, -1.0, 0.0)</c>. So <c>.w</c> is the hit test and <c>.x</c>
    ///         is always a distance a march may take.
    ///     </para>
    ///     <para>
    ///         The indices ride in floats deliberately: a float is exact for every integer below
    ///         2^24, which is more primitives than a bottom-level structure may hold, so nothing is
    ///         lost — and no result struct has to exist in the IR, the reflection and two backends
    ///         for one intrinsic's sake.
    ///     </para>
    ///     <para>
    ///         There is no ray query <em>object</em> anywhere in the language or the IR. The whole
    ///         traversal — initialize, the proceed loop, the committed-hit read — is synthesized
    ///         inside each backend, which is what keeps mutable opaque locals out of Raven.
    ///     </para>
    /// </remarks>
    TraceRayQuery
}
