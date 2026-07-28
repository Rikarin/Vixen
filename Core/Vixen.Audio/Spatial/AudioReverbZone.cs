// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Audio.Spatial;

/// <summary>What shape a zone is.</summary>
public enum AudioZoneShape {
    /// <summary>A ball around a point. What a room's worth of reverb usually wants.</summary>
    Sphere = 0,

    /// <summary>An axis-aligned box. What a corridor wants, where a sphere would spill through walls.</summary>
    Box = 1
}

/// <summary>A region of space that sounds like somewhere.</summary>
/// <remarks>
///     <para>
///         <b>It needs no physics, which is why it does not ask for any.</b> Occlusion is a raycast
///         and has to be answered by something that owns geometry; "is the listener inside this
///         volume" is a subtraction and a comparison. Writing this against a trigger volume would
///         have made reverb a feature only games with a physics engine could have, in exchange for
///         nothing — so a zone is arithmetic and works in a game that links no native library at all.
///     </para>
///     <para>
///         <b>The listener decides, not the source.</b> Reverb is the room <em>you</em> are standing
///         in. A gunshot fired outside and heard from inside a cathedral gets the cathedral, because
///         the reverberation happens around the ear and not around the muzzle. Testing the source
///         instead is a mistake that sounds subtly wrong everywhere and obviously wrong at a
///         threshold.
///     </para>
///     <para>
///         <b><see cref="Blend" /> is what stops a doorway being a switch.</b> Inside the inner
///         extent the zone is fully on; between there and the outer edge it fades. Without it,
///         walking across a boundary swaps one reverb for another in a single frame, which is a sound
///         nobody has ever made by walking.
///     </para>
///     <para>
///         <b>What it drives is a parameter, like everything else.</b> A zone does not hold a reverb;
///         it holds the <em>name</em> of a parameter and pushes a number into it. What that number
///         does — an aux send opening, a wet level, a filter — is drawn on a curve in an asset. So
///         "the cellar is boomier" is an edit rather than a build.
///     </para>
/// </remarks>
public sealed record AudioReverbZone {
    /// <summary>Which parameter this zone drives when the listener is inside it.</summary>
    public string Parameter { get; init; } = string.Empty;

    /// <summary>Where it is.</summary>
    public Vector3 Position { get; init; }

    /// <summary>Sphere or box.</summary>
    public AudioZoneShape Shape { get; init; }

    /// <summary>For a sphere, its radius. For a box, half its size on each axis.</summary>
    public Vector3 Extent { get; init; } = new(5f, 5f, 5f);

    /// <summary>How far in from the edge the zone reaches full strength, in world units.</summary>
    /// <remarks>Zero is a hard edge, which is occasionally what a sealed door wants and usually not.</remarks>
    public float Blend { get; init; } = 1f;

    /// <summary>How strong it is at full strength.</summary>
    public float Strength { get; init; } = 1f;

    /// <summary>Which zone wins where two overlap. Higher takes it.</summary>
    /// <remarks>
    ///     A cupboard inside a cathedral is inside both, and should sound like a cupboard. Blending
    ///     the two would produce a room that is neither, so the more specific one is given the higher
    ///     priority and simply wins — still faded across its own <see cref="Blend" />, so stepping
    ///     out of the cupboard is a walk rather than a jump.
    /// </remarks>
    public int Priority { get; init; }

    /// <summary>How much this zone applies at a point.</summary>
    /// <param name="listener">Where the ear is.</param>
    /// <returns>0 outside, <see cref="Strength" /> well inside, and a fade across <see cref="Blend" />.</returns>
    public float Evaluate(in Vector3 listener) {
        var depth = Shape is AudioZoneShape.Sphere ? SphereDepth(listener) : BoxDepth(listener);

        if (depth <= 0f) {
            return 0f;
        }

        return Blend <= 0f ? Strength : Strength * MathF.Min(depth / Blend, 1f);
    }

    /// <summary>How far inside the sphere the point is. Negative outside.</summary>
    float SphereDepth(in Vector3 listener) {
        // The radius is Extent.X: a sphere with three different extents is an ellipsoid, and calling
        // one a sphere would be the sort of thing somebody finds out from a bug report.
        var radius = Extent.X;
        return radius - Vector3.Distance(listener, Position);
    }

    /// <summary>How far inside the box the point is, on its tightest axis. Negative outside.</summary>
    float BoxDepth(in Vector3 listener) {
        var offset = listener - Position;

        // The nearest face governs, because a point one centimetre from a wall is one centimetre
        // inside the room however far it is from the far wall.
        var x = Extent.X - MathF.Abs(offset.X);
        var y = Extent.Y - MathF.Abs(offset.Y);
        var z = Extent.Z - MathF.Abs(offset.Z);

        return MathF.Min(x, MathF.Min(y, z));
    }
}
