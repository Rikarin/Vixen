// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Vfx;

/// <summary>One per-particle quantity a graph can read or write.</summary>
/// <remarks>
///     <para>
///         A fixed set rather than an open one, for now. Every attribute here has a known type, a
///         known meaning to the renderers, and a place in the compiled graph's declaration — which is
///         what lets storage be allocated for exactly the ones a graph touches and nothing else. An
///         effect that never rotates its particles pays nothing for rotation.
///     </para>
///     <para>
///         The values are bit positions, so a declaration is a mask and "does this graph use
///         rotation" is one instruction.
///     </para>
/// </remarks>
[Flags]
public enum VfxAttribute : uint {
    /// <summary>Nothing.</summary>
    None = 0,

    /// <summary>Where the particle is, in the effect's space. <see cref="VfxAttributeType.Float3" />.</summary>
    Position = 1 << 0,

    /// <summary>How it is moving, per second. <see cref="VfxAttributeType.Float3" />.</summary>
    Velocity = 1 << 1,

    /// <summary>How big it is. <see cref="VfxAttributeType.Float" />.</summary>
    Size = 1 << 2,

    /// <summary>Its colour and opacity, linear and premultiplied by nothing. <see cref="VfxAttributeType.Float4" />.</summary>
    Colour = 1 << 3,

    /// <summary>How long it lives, in seconds. <see cref="VfxAttributeType.Float" />.</summary>
    Lifetime = 1 << 4,

    /// <summary>How long it has lived. <see cref="VfxAttributeType.Float" />.</summary>
    Age = 1 << 5,

    /// <summary>Its roll, in radians. <see cref="VfxAttributeType.Float" />.</summary>
    Rotation = 1 << 6,

    /// <summary>How fast that roll is changing, in radians per second. <see cref="VfxAttributeType.Float" />.</summary>
    AngularVelocity = 1 << 7,

    /// <summary>
    ///     The particle's identifier, unique within its system for as long as it lives.
    ///     <see cref="VfxAttributeType.UInt" />.
    /// </summary>
    /// <remarks>
    ///     <b>Every system has this, whether or not it was declared.</b> It is what
    ///     <see cref="VfxRandom" /> hashes, so a particle's randomness follows the particle rather
    ///     than the slot it happens to occupy — and a slot is reused the moment its previous occupant
    ///     dies. Without it, recycling a slot would silently re-roll every random value the particle
    ///     in it had already been given.
    /// </remarks>
    Identifier = 1 << 8
}

/// <summary>What an attribute is made of.</summary>
public enum VfxAttributeType {
    /// <summary>One 32-bit float.</summary>
    Float,

    /// <summary>Three of them.</summary>
    Float3,

    /// <summary>Four of them.</summary>
    Float4,

    /// <summary>One 32-bit unsigned integer.</summary>
    UInt
}

/// <summary>What each attribute is made of, and how to talk about a set of them.</summary>
public static class VfxAttributes {
    /// <summary>Every attribute, in bit order.</summary>
    public static ReadOnlySpan<VfxAttribute> All =>
    [
        VfxAttribute.Position,
        VfxAttribute.Velocity,
        VfxAttribute.Size,
        VfxAttribute.Colour,
        VfxAttribute.Lifetime,
        VfxAttribute.Age,
        VfxAttribute.Rotation,
        VfxAttribute.AngularVelocity,
        VfxAttribute.Identifier
    ];

    /// <summary>The type of one attribute.</summary>
    /// <param name="attribute">The attribute. Exactly one bit.</param>
    /// <returns>Its type.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="attribute" /> is not a single known attribute.</exception>
    public static VfxAttributeType TypeOf(VfxAttribute attribute) => attribute switch {
        VfxAttribute.Position or VfxAttribute.Velocity => VfxAttributeType.Float3,
        VfxAttribute.Colour => VfxAttributeType.Float4,
        VfxAttribute.Size or VfxAttribute.Lifetime or VfxAttribute.Age
            or VfxAttribute.Rotation or VfxAttribute.AngularVelocity => VfxAttributeType.Float,
        VfxAttribute.Identifier => VfxAttributeType.UInt,
        _ => throw new ArgumentOutOfRangeException(nameof(attribute), attribute, "Not a single known attribute.")
    };

    /// <summary>How many bytes one particle's worth of a set occupies.</summary>
    /// <param name="set">The set.</param>
    /// <returns>The size in bytes.</returns>
    /// <remarks>
    ///     For a caller deciding a capacity against a memory budget, and for the README to be able to
    ///     say what an effect costs rather than imply it.
    /// </remarks>
    public static int BytesPerParticle(VfxAttribute set) {
        var total = 0;

        foreach (var attribute in All) {
            if ((set & attribute) == 0) {
                continue;
            }

            total += TypeOf(attribute) switch {
                VfxAttributeType.Float or VfxAttributeType.UInt => 4,
                VfxAttributeType.Float3 => 12,
                VfxAttributeType.Float4 => 16,
                _ => 0
            };
        }

        return total;
    }
}
