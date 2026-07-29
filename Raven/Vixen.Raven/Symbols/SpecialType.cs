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
    Sampler
}
