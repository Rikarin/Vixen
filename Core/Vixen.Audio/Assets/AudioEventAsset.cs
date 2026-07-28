// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Audio.Events;
using Vixen.Audio.Spatial;
using Vixen.Core;
using Vixen.Core.Serialization;

namespace Vixen.Audio.Assets;

/// <summary>One of an event's sounds, as a file declares it.</summary>
/// <remarks>
///     <b>The clip is named, not contained.</b> Six events that all use one door slam are six
///     references and one clip in one bundle — containing it would put six copies in the build and
///     six in memory. That is the whole argument for
///     <see cref="ContentReference{T}" /> and it applies here more than almost anywhere, because
///     sharing a clip between events is the normal case rather than the clever one.
/// </remarks>
[DataContract("AudioEventVariant")]
public sealed record AudioEventVariantAsset {
    /// <summary>The clip.</summary>
    public ContentReference<AudioClip>? Clip { get; init; }

    /// <summary>How likely it is, against its siblings.</summary>
    public float Weight { get; init; } = 1f;

    /// <summary>A level correction for this take alone.</summary>
    public float GainDb { get; init; }

    /// <summary>A tuning correction for this take alone.</summary>
    public float PitchSemitones { get; init; }
}

/// <summary>How an event's plays sit in the world, as a file declares it.</summary>
/// <remarks>
///     A flattened <see cref="SpatialSettings" /> without the three fields a caller supplies —
///     position, velocity and facing — because those are the frame's and never the asset's. Writing
///     them into a file would produce a sound that always plays at the origin.
/// </remarks>
[DataContract("AudioEventSpatial")]
public sealed record AudioEventSpatialAsset {
    /// <summary>The distance at which it plays at full volume.</summary>
    public float MinDistance { get; init; } = 1f;

    /// <summary>Where it stops getting quieter.</summary>
    public float MaxDistance { get; init; } = 500f;

    /// <summary>Which curve it follows.</summary>
    public AttenuationModel Attenuation { get; init; } = AttenuationModel.Inverse;

    /// <summary>How hard that curve bites.</summary>
    public float RolloffFactor { get; init; } = 1f;

    /// <summary>The full angle, in degrees, inside which it is at full volume. 360 means no cone.</summary>
    public float ConeInnerAngle { get; init; } = 360f;

    /// <summary>The full angle, in degrees, outside which it is at <see cref="ConeOuterGain" />.</summary>
    public float ConeOuterAngle { get; init; } = 360f;

    /// <summary>How loud it is outside the cone.</summary>
    public float ConeOuterGain { get; init; }

    /// <summary>How much pitch shift movement causes. Zero switches doppler off.</summary>
    public float DopplerFactor { get; init; } = 1f;

    /// <summary>How big the source is, from a point at 0 to everywhere at 1.</summary>
    public float Spread { get; init; }

    /// <summary>How much distance muffles it, from 0 for none to 1 for as much as the model allows.</summary>
    public float AirAbsorption { get; init; }

    /// <summary>Where the muffling ends up, in hertz, at maximum distance and full absorption.</summary>
    public float AirAbsorptionCutoff { get; init; } = 700f;

    /// <summary>These settings, as the mixer wants them. The position is filled in per play.</summary>
    /// <returns>The settings.</returns>
    public SpatialSettings ToSettings() => new() {
        MinDistance = MinDistance,
        MaxDistance = MaxDistance,
        Attenuation = Attenuation,
        RolloffFactor = RolloffFactor,
        ConeInnerAngle = ConeInnerAngle,
        ConeOuterAngle = ConeOuterAngle,
        ConeOuterGain = ConeOuterGain,
        DopplerFactor = DopplerFactor,
        Spread = Spread,
        AirAbsorption = AirAbsorption,
        AirAbsorptionCutoff = AirAbsorptionCutoff
    };
}

/// <summary>An event, as a file declares it.</summary>
/// <remarks>
///     <para>
///         The unit gameplay should be playing. A call that names a clip has already decided which
///         take, how loud, how far it carries and how many may overlap — and every one of those is a
///         decision a sound designer will want back, usually late and usually all at once.
///     </para>
///     <para>
///         <b>The bus is named, not indexed.</b> An index is a position in whatever order the mixer
///         was built in, which changes when somebody inserts a bus; a name survives that, and it is
///         also what the mixer asset writes. An event whose bus does not resolve plays on the master
///         and says so, because a footstep on the wrong bus is a mix problem and a missing footstep
///         is a bug hunt.
///     </para>
/// </remarks>
[DataContract("AudioEvent")]
public sealed record AudioEventAsset {
    /// <summary>What it is called. Also how gameplay finds it, if something keeps a table of them.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>The sounds it can play.</summary>
    public AudioEventVariantAsset[] Variants { get; init; } = [];

    /// <summary>How it chooses between them.</summary>
    public VariantSelection Selection { get; init; } = VariantSelection.Shuffle;

    /// <summary>Where its random sequence starts. Zero is the shared default.</summary>
    public uint Seed { get; init; }

    /// <summary>Which bus it routes into, by name. Empty is the master.</summary>
    public string Bus { get; init; } = string.Empty;

    /// <summary>Its level, before any variation.</summary>
    public float GainDb { get; init; }

    /// <summary>How far either side of <see cref="GainDb" /> a play may land.</summary>
    public float GainVarianceDb { get; init; }

    /// <summary>How far either side of the written pitch a play may land, in semitones.</summary>
    public float PitchVarianceSemitones { get; init; }

    /// <summary>Whether a play wraps round instead of ending.</summary>
    public bool Loop { get; init; }

    /// <summary>How hard a play is to displace when the voice pool is full. Higher survives.</summary>
    public int Priority { get; init; }

    /// <summary>How many copies may sound at once. Zero is no limit.</summary>
    public int MaxInstances { get; init; }

    /// <summary>What gives way when <see cref="MaxInstances" /> is reached.</summary>
    public EventStealMode Steal { get; init; } = EventStealMode.Oldest;

    /// <summary>How its plays sit in the world, or null for a sound in the room.</summary>
    /// <remarks>
    ///     Its presence is the switch, as <c>AudioSpatial</c>'s is in the ECS: an event either is a
    ///     thing in the world or is not, and a flag beside a block of settings that mean nothing when
    ///     it is off is how the two get out of step.
    /// </remarks>
    public AudioEventSpatialAsset? Spatial { get; init; }
}
