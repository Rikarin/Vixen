// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0


namespace Vixen.Raven.Symbols;

/// <summary>
///     Identifies a type the compiler knows intrinsically. Everything the binder
///     special-cases (numeric promotion, literal typing, swizzles, intrinsic
///     signatures) keys off this rather than off a name.
/// </summary>
public enum SpecialType {
    None,

    Void,
    Bool,
    Int,
    UInt,
    Float,
    Double,

    /// <summary>
    ///     64-bit integers, which exist for one reason: a word wide enough to hold a depth above an
    ///     id and be <c>atomicMax</c>'d as a unit.
    /// </summary>
    /// <remarks>
    ///     No vectors and no matrices, unlike every other scalar here. Nothing wants a
    ///     <c>uint64_2</c>, both targets' atomics are scalar anyway, and each lane would cost a
    ///     name, a layout rule and a conversion table entry for a shape that has no use.
    /// </remarks>
    Int64,

    UInt64,

    Bool2,
    Bool3,
    Bool4,
    Int2,
    Int3,
    Int4,
    UInt2,
    UInt3,
    UInt4,
    Float2,
    Float3,
    Float4,
    Double2,
    Double3,
    Double4,

    Mat2,
    Mat2x3,
    Mat2x4,
    Mat3,
    Mat3x2,
    Mat3x4,
    Mat4,
    Mat4x2,
    Mat4x3,

    Texture2D,
    Texture3D,
    TextureCube,
    Sampler,

    /// <summary>
    ///     The scene's ray-tracing acceleration structure, which a shader opens ray queries
    ///     against.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Appended after <see cref="Sampler" /> rather than filed with the other resources.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The reason this remark used to give is wrong, and it is worth knowing that it
    ///         is.</b> It said a <c>.rvnlib</c> carries these values as numbers, so inserting one
    ///         would retype every resource in every already-built library. A <c>.rvnlib</c> carries
    ///         them as <em>names</em>: <c>CompiledLibraryFormat.Json</c> registers a
    ///         <c>JsonStringEnumConverter</c>, and the bytes of an emitted library contain the word
    ///         <c>Texture2D</c>. The same is true of <c>.rvnfx</c> and of
    ///         <see cref="IR.IrTypeKind" /> and <see cref="IR.IrIntrinsic" />, which are written
    ///         through the same options — so a value may be inserted anywhere in any of the four
    ///         without invalidating an artefact. Appending is still the habit worth keeping, since
    ///         it costs nothing and the day a format goes binary the habit is already there; but a
    ///         reader deciding where to add one should not believe the old reason, because it would
    ///         also have them believe a renamed member is safe, and that is the change these
    ///         formats actually break on.
    ///     </para>
    /// </remarks>
    AccelerationStructure,

    /// <summary>
    ///     A depth texture: a shadow map, read by comparison rather than by value.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Its own special type rather than a <c>Texture2D</c> a comparison sampler happens to
    ///         be paired with, because <em>the texture binding</em> is what a host has to be told
    ///         about. Vulkan lets a depth view sit behind a plain sampled image and the shipped
    ///         library does exactly that; WebGPU does not — a bind group layout entry states
    ///         <c>sampleType: "depth"</c>, and it comes from the reflection, so a type the
    ///         reflection cannot tell apart is a layout the browser rejects.
    ///     </para>
    ///     <para>
    ///         Appended, keeping <see cref="AccelerationStructure" />'s habit — though not for the
    ///         reason that member's remark used to give, which turned out to be false.
    ///     </para>
    /// </remarks>
    DepthTexture2D,

    /// <summary>
    ///     A comparison sampler: the sampler state a depth texture is read through, which does the
    ///     compare-and-filter in fixed function rather than returning a texel.
    /// </summary>
    /// <remarks>
    ///     A separate type from <see cref="Sampler" /> because it is a separate GLSL type
    ///     (<c>samplerShadow</c>, not <c>sampler</c>) and a separate WebGPU binding type
    ///     (<c>comparison</c>, not <c>filtering</c>) — and because binding one where the other is
    ///     expected is a pipeline every backend refuses. SPIR-V is the one target where they are
    ///     the same <c>OpTypeSampler</c>; that is a fact about SPIR-V, not about the language.
    /// </remarks>
    ComparisonSampler
}
