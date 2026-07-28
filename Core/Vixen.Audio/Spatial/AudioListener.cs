// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Audio.Spatial;

/// <summary>How a positioned sound gets quieter with distance.</summary>
public enum AttenuationModel {
    /// <summary>It does not. A sound that is as loud across the level as it is next to you.</summary>
    None,

    /// <summary>
    ///     Straight down to silence at <see cref="SpatialSettings.MaxDistance" />, which is the only
    ///     model that actually reaches zero.
    /// </summary>
    /// <remarks>
    ///     Physically wrong and frequently what a designer wants, because it guarantees a sound stops
    ///     being audible at a distance they can point at on a map.
    /// </remarks>
    Linear,

    /// <summary>
    ///     Halving every time the distance doubles — the inverse-square law as a pressure amplitude.
    /// </summary>
    /// <remarks>The default, because it is what the world does.</remarks>
    Inverse,

    /// <summary>
    ///     <c>(d / min)^-rolloff</c>. Steeper than inverse for rolloff above one, gentler below.
    /// </summary>
    Exponential
}

/// <summary>Where the ears are.</summary>
/// <remarks>
///     <para>
///         One per mixer, and not one per camera. A split-screen game has two cameras and one set of
///         speakers, and the answer to "which one does the audio follow" is a decision the game makes
///         — usually a point between them — rather than something the mixer can average.
///     </para>
///     <para>
///         <b>A value, published through the command queue.</b> Fifty-two bytes cannot be written
///         atomically, so a listener assigned directly from the game thread would be read torn by the
///         audio thread — for one block, with a position from this frame and an orientation from the
///         last one, which is a click. It goes through <c>AudioEngine.SetListener</c> instead.
///     </para>
/// </remarks>
public readonly record struct AudioListener() {
    /// <summary>Where it is, in world space.</summary>
    public Vector3 Position { get; init; }

    /// <summary>Which way it faces. Need not be normalised.</summary>
    public Vector3 Forward { get; init; } = Vector3.Forward;

    /// <summary>Which way is up for it. Need not be normalised or perpendicular to <see cref="Forward" />.</summary>
    public Vector3 Up { get; init; } = Vector3.Up;

    /// <summary>How fast it is moving, in units a second. Only <see cref="SpatialSettings.DopplerFactor" /> reads it.</summary>
    public Vector3 Velocity { get; init; }

    /// <summary>A gain applied to every positioned voice. The master volume of the 3D world.</summary>
    public float Gain { get; init; } = 1f;

    /// <summary>At the origin, facing −Z, up +Y, still.</summary>
    public static AudioListener Default => new();
}

/// <summary>What makes one voice a thing in the world rather than a sound in the room.</summary>
/// <remarks>
///     The defaults describe a point source with an inverse rolloff from one metre, no cone, and
///     doppler on — which is what somebody who attaches a sound to a moving object means, and means
///     that <c>new SpatialSettings { Position = p }</c> is a complete answer.
/// </remarks>
public readonly record struct SpatialSettings() {
    /// <summary>Where the sound is, in world space.</summary>
    public Vector3 Position { get; init; }

    /// <summary>How fast it is moving, in units a second.</summary>
    public Vector3 Velocity { get; init; }

    /// <summary>Which way its cone points, if it has one.</summary>
    public Vector3 ConeDirection { get; init; } = Vector3.Forward;

    /// <summary>The distance at which it plays at full volume, and the reference for every model.</summary>
    /// <remarks>
    ///     One world unit. Raising it is how a sound is made to carry: an inverse rolloff from 20 is
    ///     still at half volume at 40, where one from 1 is down to a fortieth.
    /// </remarks>
    public float MinDistance { get; init; } = 1f;

    /// <summary>Where it stops getting quieter — and, for <see cref="AttenuationModel.Linear" />, silent.</summary>
    public float MaxDistance { get; init; } = 500f;

    /// <summary>Which curve it follows.</summary>
    public AttenuationModel Attenuation { get; init; } = AttenuationModel.Inverse;

    /// <summary>How hard that curve bites. One is the model as written; zero disables the rolloff.</summary>
    public float RolloffFactor { get; init; } = 1f;

    /// <summary>The full angle, in degrees, inside which the sound is at full volume.</summary>
    /// <remarks>360 — the default — means no cone, which is what almost everything is.</remarks>
    public float ConeInnerAngle { get; init; } = 360f;

    /// <summary>The full angle, in degrees, outside which the sound is at <see cref="ConeOuterGain" />.</summary>
    public float ConeOuterAngle { get; init; } = 360f;

    /// <summary>How loud it is outside the cone.</summary>
    public float ConeOuterGain { get; init; }

    /// <summary>How much pitch shift movement causes. Zero switches doppler off.</summary>
    public float DopplerFactor { get; init; } = 1f;

    /// <summary>
    ///     How big the source is, from a point at 0 to everywhere at 1. A spread source stops being
    ///     localisable and sits evenly across the speakers.
    /// </summary>
    /// <remarks>
    ///     What a river, a crowd or a machine room is. It is also what saves a point source from
    ///     snapping between the speakers as the listener walks through it: inside
    ///     <see cref="MinDistance" /> the spatialiser raises the spread on its own.
    /// </remarks>
    public float Spread { get; init; }

    /// <summary>How fast sound travels, in world units a second. Only doppler reads it.</summary>
    /// <remarks>343 is metres a second in air at 20 °C, so the default assumes a unit is a metre.</remarks>
    public float SpeedOfSound { get; init; } = 343f;
}
