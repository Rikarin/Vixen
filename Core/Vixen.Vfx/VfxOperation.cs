// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Vfx;

/// <summary>What one node of a compiled graph does.</summary>
/// <remarks>
///     <para>
///         Deliberately a small, closed set of coarse operations rather than a general expression
///         language. A node graph could be lowered to add/multiply/select and a register file, and
///         then both backends would need a register allocator; lowering to "apply gravity" instead
///         means the CPU backend is a loop and the GPU backend is a line of shader source. The cost is
///         that a genuinely novel node needs an opcode here — which is the right cost to pay while the
///         node library is small enough to enumerate.
///     </para>
/// </remarks>
public enum VfxOpcode {
    /// <summary>Position is a fixed point. <c>A.xyz</c> is the point.</summary>
    SetPosition,

    /// <summary>Position is uniform inside a sphere. <c>A.xyz</c> is the centre, <c>A.w</c> the radius.</summary>
    PositionInSphere,

    /// <summary>Position is uniform inside a box. <c>A.xyz</c> is the minimum, <c>B.xyz</c> the maximum.</summary>
    PositionInBox,

    /// <summary>Velocity is a fixed vector. <c>A.xyz</c> is it.</summary>
    SetVelocity,

    /// <summary>
    ///     Velocity is a random direction at a random speed. <c>A.x</c> and <c>A.y</c> are the speed
    ///     range.
    /// </summary>
    VelocityRandomDirection,

    /// <summary>
    ///     Velocity is inside a cone. <c>A.xyz</c> is the axis, <c>A.w</c> the half-angle in radians,
    ///     <c>B.x</c> and <c>B.y</c> the speed range.
    /// </summary>
    VelocityInCone,

    /// <summary>Lifetime is uniform in a range. <c>A.x</c> and <c>A.y</c> are it.</summary>
    SetLifetime,

    /// <summary>Size is uniform in a range. <c>A.x</c> and <c>A.y</c> are it.</summary>
    SetSize,

    /// <summary>Colour is fixed. <c>A</c> is it, linear RGBA.</summary>
    SetColour,

    /// <summary>Roll is uniform in a range. <c>A.x</c> and <c>A.y</c> are it, in radians.</summary>
    SetRotation,

    /// <summary>Angular velocity is uniform in a range. <c>A.x</c> and <c>A.y</c> are it.</summary>
    SetAngularVelocity,

    /// <summary>Position moves by velocity. No parameters.</summary>
    Integrate,

    /// <summary>Velocity gains an acceleration. <c>A.xyz</c> is it, per second squared.</summary>
    Gravity,

    /// <summary>Velocity decays. <c>A.x</c> is the coefficient, per second.</summary>
    Drag,

    /// <summary>Roll advances by angular velocity. No parameters.</summary>
    Rotate,

    /// <summary>Size follows age. <c>A.x</c> is the size at birth, <c>A.y</c> at death.</summary>
    SizeOverLife,

    /// <summary>Colour follows age. <c>A</c> is the colour at birth, <c>B</c> at death.</summary>
    ColourOverLife
}

/// <summary>One operation, as the compiled graph stores it.</summary>
/// <param name="Opcode">What it does.</param>
/// <param name="Salt">
///     Which randomness it draws on. Distinct per operation, so two random values on one particle are
///     unrelated — see <see cref="VfxRandom.FirstSalt" />.
/// </param>
/// <param name="A">The first four parameters. What they mean depends on the opcode.</param>
/// <param name="B">The next four.</param>
/// <remarks>
///     <para>
///         <b>Two float4s and no more, on purpose.</b> The parameter block is a fixed size because
///         that is what lets a compiled graph be an array rather than a graph of objects — which is
///         what lets it be uploaded to a constant buffer verbatim, serialised without a visitor, and
///         compared for equality in a golden test. An operation that genuinely needs more parameters
///         than this is one that should be two operations.
///     </para>
///     <para>
///         Nothing here is a pointer, a delegate or an index into anything. That is the property the
///         GPU backend needs and the reason this type is the shape it is.
///     </para>
/// </remarks>
public readonly record struct VfxOperation(VfxOpcode Opcode, uint Salt, Vector4 A, Vector4 B) {
    /// <summary>An operation with no parameters.</summary>
    /// <param name="opcode">What it does.</param>
    /// <param name="salt">Which randomness it draws on.</param>
    public VfxOperation(VfxOpcode opcode, uint salt = 0) : this(opcode, salt, Vector4.Zero, Vector4.Zero) { }

    /// <summary>An operation with one parameter vector.</summary>
    /// <param name="opcode">What it does.</param>
    /// <param name="a">Its parameters.</param>
    /// <param name="salt">Which randomness it draws on.</param>
    public VfxOperation(VfxOpcode opcode, Vector4 a, uint salt = 0) : this(opcode, salt, a, Vector4.Zero) { }
}

/// <summary>What each opcode reads and writes, which is how a graph's storage is worked out.</summary>
/// <remarks>
///     The declaration is here rather than on the operation because it is a property of the
///     <i>opcode</i>, and putting it anywhere else would let a graph claim to touch something it does
///     not — which is exactly the disagreement the whole "allocate only what is used" idea depends on
///     never happening.
/// </remarks>
public static class VfxOpcodes {
    /// <summary>The attributes an opcode reads.</summary>
    /// <param name="opcode">The opcode.</param>
    /// <returns>The attributes.</returns>
    public static VfxAttribute Reads(VfxOpcode opcode) => opcode switch {
        VfxOpcode.Integrate => VfxAttribute.Velocity,
        VfxOpcode.Drag => VfxAttribute.Velocity,
        VfxOpcode.Rotate => VfxAttribute.AngularVelocity,
        VfxOpcode.SizeOverLife or VfxOpcode.ColourOverLife => VfxAttribute.Age | VfxAttribute.Lifetime,
        _ => VfxAttribute.None
    };

    /// <summary>The attributes an opcode writes.</summary>
    /// <param name="opcode">The opcode.</param>
    /// <returns>The attributes.</returns>
    public static VfxAttribute Writes(VfxOpcode opcode) => opcode switch {
        VfxOpcode.SetPosition or VfxOpcode.PositionInSphere or VfxOpcode.PositionInBox or VfxOpcode.Integrate =>
            VfxAttribute.Position,
        VfxOpcode.SetVelocity or VfxOpcode.VelocityRandomDirection or VfxOpcode.VelocityInCone
            or VfxOpcode.Gravity or VfxOpcode.Drag => VfxAttribute.Velocity,
        VfxOpcode.SetLifetime => VfxAttribute.Lifetime,
        VfxOpcode.SetSize or VfxOpcode.SizeOverLife => VfxAttribute.Size,
        VfxOpcode.SetColour or VfxOpcode.ColourOverLife => VfxAttribute.Colour,
        VfxOpcode.SetRotation or VfxOpcode.Rotate => VfxAttribute.Rotation,
        VfxOpcode.SetAngularVelocity => VfxAttribute.AngularVelocity,
        _ => VfxAttribute.None
    };

    /// <summary>Whether an opcode draws on randomness, and so needs a salt of its own.</summary>
    /// <param name="opcode">The opcode.</param>
    /// <returns><see langword="true" /> if it does.</returns>
    public static bool IsRandom(VfxOpcode opcode) => opcode is VfxOpcode.PositionInSphere
        or VfxOpcode.PositionInBox
        or VfxOpcode.VelocityRandomDirection
        or VfxOpcode.VelocityInCone
        or VfxOpcode.SetLifetime
        or VfxOpcode.SetSize
        or VfxOpcode.SetRotation
        or VfxOpcode.SetAngularVelocity;
}
