// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Audio.Mixing;
using Vixen.Audio.Spatial;
using Vixen.Core.Mathematics;

namespace Vixen.Audio.Ecs;

/// <summary>What an <see cref="AudioSource" /> is meant to be doing.</summary>
/// <remarks>
///     <b>Wanted, not actual.</b> Game code writes this and <c>AudioSystem</c> makes it so; the
///     system writes it back to <see cref="Stopped" /> when a sound runs out on its own. That is what
///     makes "is my sound still playing" an ordinary component read rather than a call into the
///     mixer with a handle the caller had to keep.
/// </remarks>
public enum AudioPlayback {
    /// <summary>Silent. Setting it from <see cref="Playing" /> stops the sound.</summary>
    Stopped = 0,

    /// <summary>Playing, or about to be.</summary>
    Playing = 1,

    /// <summary>Holding its position.</summary>
    Paused = 2
}

/// <summary>A sound attached to an entity.</summary>
/// <remarks>
///     <para>
///         Unmanaged, so a scene full of emitters is a sweep over chunks. The clip itself is a
///         reference and lives in <see cref="AudioClipRef" /> beside it — one indirection, taken once
///         when a sound starts rather than once per entity per frame.
///     </para>
///     <para>
///         A zeroed <see cref="AudioSource" /> is silent and stopped, which is why
///         <see cref="Default" /> exists: <c>default</c> gives a gain of zero and a pitch of zero,
///         and a pitch of zero is a sound that never advances. Anything creating one by hand starts
///         from <see cref="Default" />, exactly as <c>LocalTransform.Identity</c> does.
///     </para>
/// </remarks>
public struct AudioSource {
    /// <summary>What it should be doing.</summary>
    public AudioPlayback Playback;

    /// <summary>Which bus it routes into. Zero is the master.</summary>
    public int Bus;

    /// <summary>Its linear gain.</summary>
    public float Gain;

    /// <summary>Its playback rate multiplier.</summary>
    public float Pitch;

    /// <summary>Where it sits between the speakers, when it is not spatialised.</summary>
    public float Pan;

    /// <summary>Whether it wraps round instead of ending.</summary>
    public bool Loop;

    /// <summary>The voice it is playing on. Written by the system; read by anything that wants the detail.</summary>
    /// <remarks>
    ///     <see cref="VoiceHandle.None" /> when nothing is playing. Game code has no reason to write
    ///     it, and writing it will lose the sound rather than move it.
    /// </remarks>
    public VoiceHandle Voice;

    /// <summary>Full volume, unaltered pitch, centred, on the master bus, stopped.</summary>
    public static AudioSource Default => new() {
        Playback = AudioPlayback.Stopped,
        Gain = 1f,
        Pitch = 1f,
        Voice = VoiceHandle.None
    };

    /// <summary>The same, already playing.</summary>
    public static AudioSource Playing => Default with { Playback = AudioPlayback.Playing };
}

/// <summary>Which clip an <see cref="AudioSource" /> plays.</summary>
/// <remarks>
///     A managed component, because a clip is a reference type — the case
///     <c>docs/plan/04</c> names when it explains why managed components exist. Separate from
///     <see cref="AudioSource" /> so that the settings stay in the chunk and only the clip pays for
///     the indirection.
/// </remarks>
public struct AudioClipRef {
    /// <summary>The clip. A source with none plays nothing and is not an error.</summary>
    public AudioClip? Clip;
}

/// <summary>Makes an <see cref="AudioSource" /> a thing in the world rather than a sound in the room.</summary>
/// <remarks>
///     <para>
///         Its presence is the switch: an entity with this component is spatialised, and one without
///         it is not. That makes "is this sound positional" an archetype question rather than a
///         boolean the mixer has to read per voice, and it means a UI click does not carry sixty
///         bytes of cone and doppler settings it will never use.
///     </para>
///     <para>
///         Position comes from <c>WorldTransform</c> and never from here, so a sound moves because
///         the thing making it moved.
///     </para>
/// </remarks>
public struct AudioSpatial {
    /// <summary>The distance at which it plays at full volume.</summary>
    public float MinDistance;

    /// <summary>Where it stops getting quieter.</summary>
    public float MaxDistance;

    /// <summary>Which curve it follows.</summary>
    public AttenuationModel Attenuation;

    /// <summary>How hard that curve bites.</summary>
    public float RolloffFactor;

    /// <summary>The full angle, in degrees, inside which it is at full volume. 360 means no cone.</summary>
    public float ConeInnerAngle;

    /// <summary>The full angle, in degrees, outside which it is at <see cref="ConeOuterGain" />.</summary>
    public float ConeOuterAngle;

    /// <summary>How loud it is outside the cone.</summary>
    public float ConeOuterGain;

    /// <summary>How much pitch shift movement causes. Zero switches doppler off.</summary>
    public float DopplerFactor;

    /// <summary>How big the source is, from a point at 0 to everywhere at 1.</summary>
    public float Spread;

    /// <summary>Whether the system works velocity out from how far the entity moved.</summary>
    /// <remarks>
    ///     On by default, because the alternative is every gameplay system that moves something also
    ///     remembering to tell the audio about it. A source with its own physics velocity to hand
    ///     turns this off and writes <see cref="Velocity" /> itself.
    /// </remarks>
    public bool AutoVelocity;

    /// <summary>How fast it is moving, in units a second.</summary>
    public Vector3 Velocity;

    /// <summary>Where it was last frame, for <see cref="AutoVelocity" />. Owned by the system.</summary>
    public Vector3 PreviousPosition;

    /// <summary>Whether <see cref="PreviousPosition" /> means anything yet.</summary>
    /// <remarks>
    ///     Without it, the first frame of a sound spawned away from the origin would compute a
    ///     velocity of "however far it is from nowhere, per frame" and doppler-shift it into a
    ///     whistle.
    /// </remarks>
    public bool HasPreviousPosition;

    /// <summary>A point source with an inverse rolloff from one unit, no cone, and doppler on.</summary>
    public static AudioSpatial Default => new() {
        MinDistance = 1f,
        MaxDistance = 500f,
        Attenuation = AttenuationModel.Inverse,
        RolloffFactor = 1f,
        ConeInnerAngle = 360f,
        ConeOuterAngle = 360f,
        DopplerFactor = 1f,
        AutoVelocity = true
    };

    /// <summary>These settings, at a position, as the mixer wants them.</summary>
    /// <param name="position">Where the entity is.</param>
    /// <param name="coneDirection">Which way it faces.</param>
    /// <returns>The settings.</returns>
    public readonly SpatialSettings ToSettings(Vector3 position, Vector3 coneDirection) => new() {
        Position = position,
        Velocity = Velocity,
        ConeDirection = coneDirection,
        MinDistance = MinDistance,
        MaxDistance = MaxDistance,
        Attenuation = Attenuation,
        RolloffFactor = RolloffFactor,
        ConeInnerAngle = ConeInnerAngle,
        ConeOuterAngle = ConeOuterAngle,
        ConeOuterGain = ConeOuterGain,
        DopplerFactor = DopplerFactor,
        Spread = Spread
    };
}

/// <summary>Marks the entity whose transform the mixer listens from.</summary>
/// <remarks>
///     <para>
///         Named with the suffix because <see cref="AudioListener" /> is already the value the mixer
///         holds, and a subsystem with two things of the same name is a bug waiting to be written.
///         This is the component; that is the state it produces.
///     </para>
///     <para>
///         <b>The first one found wins.</b> A world with two is a mistake — there is one set of
///         speakers — and the system says so rather than averaging them into a listener that is
///         nowhere.
///     </para>
/// </remarks>
public struct AudioListenerComponent {
    /// <summary>A gain over every positioned voice.</summary>
    public float Gain;

    /// <summary>Whether the system works velocity out from how far the entity moved.</summary>
    public bool AutoVelocity;

    /// <summary>How fast it is moving, in units a second.</summary>
    public Vector3 Velocity;

    /// <summary>Where it was last frame. Owned by the system.</summary>
    public Vector3 PreviousPosition;

    /// <summary>Whether <see cref="PreviousPosition" /> means anything yet.</summary>
    public bool HasPreviousPosition;

    /// <summary>Full gain, velocity worked out from movement.</summary>
    public static AudioListenerComponent Default => new() { Gain = 1f, AutoVelocity = true };
}
