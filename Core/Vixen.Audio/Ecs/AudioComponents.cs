// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Audio.Events;
using Vixen.Audio.Mixing;
using Vixen.Audio.Spatial;
using Vixen.Core;
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
[Component]
[DataContract]
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

    /// <summary>How hard it is to displace when the voice pool is full. Higher survives.</summary>
    /// <remarks>See <see cref="PlaybackSettings.Priority" />. Read when the sound starts, and not after.</remarks>
    public int Priority;

    /// <summary>The voice it is playing on. Written by the system; read by anything that wants the detail.</summary>
    /// <remarks>
    ///     <see cref="VoiceHandle.None" /> when nothing is playing. Game code has no reason to write
    ///     it, and writing it will lose the sound rather than move it.
    /// </remarks>
    public VoiceHandle Voice;

    /// <summary>What the sound that started decided its own gain was. Owned by the system.</summary>
    /// <remarks>
    ///     <para>
    ///         One for a plain clip. For an <see cref="AudioEventRef" /> it is the event's level, the
    ///         chosen variant's correction and that play's randomisation, multiplied together — so
    ///         the per-frame push can send <see cref="Gain" /> times this and scale what the event
    ///         chose instead of overwriting it.
    ///     </para>
    ///     <para>
    ///         Without it, an event's two decibels of level variation would last exactly one frame,
    ///         and every copy of a sound would snap to the same level the moment the system pushed
    ///         its gain. Which is the sort of bug that is heard long before it is found.
    ///     </para>
    /// </remarks>
    public float VoiceGainScale;

    /// <summary>What the sound that started decided its own pitch ratio was. Owned by the system.</summary>
    public float VoicePitchScale;

    /// <summary>Full volume, unaltered pitch, centred, on the master bus, stopped.</summary>
    public static AudioSource Default => new() {
        Playback = AudioPlayback.Stopped,
        Gain = 1f,
        Pitch = 1f,
        VoiceGainScale = 1f,
        VoicePitchScale = 1f,
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

/// <summary>Which event an <see cref="AudioSource" /> plays, instead of a bare clip.</summary>
/// <remarks>
///     <para>
///         <b>It replaces <see cref="AudioClipRef" /> rather than joining it.</b> An entity carrying
///         both would be a question with no good answer, so the event wins and the clip is ignored —
///         and the queries are written so that only one of the two ever fires for an entity.
///     </para>
///     <para>
///         <b>The event decides more than the clip did.</b> Which take, at what level and pitch,
///         on which bus, how far it carries and how many copies may sound at once are all the
///         event's; <see cref="AudioSource.Bus" /> and <see cref="AudioSource.Loop" /> are not read.
///         <see cref="AudioSource.Gain" /> and <see cref="AudioSource.Pitch" /> still are, as trims
///         multiplied into what the event chose — so an emitter can be faded without knowing what it
///         is playing.
///     </para>
///     <para>
///         <b>An <see cref="AudioSpatial" /> beside it supplies the position and nothing else.</b>
///         Where a sound is belongs to the entity; how it attenuates belongs to the event, which is
///         the split that lets a designer change a rolloff without opening a scene.
///     </para>
/// </remarks>
public struct AudioEventRef {
    /// <summary>The event. A source with none plays nothing and is not an error.</summary>
    public AudioEvent? Event;
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
[Component]
[DataContract]
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
/// <remarks>
///     ⚠ <b>The alias drops the suffix, and the suffix is a C# problem rather than a file's.</b>
///     <see cref="Spatial.AudioListener" /> already has the obvious name in this assembly, which is
///     the whole reason this type carries <c>Component</c> — but a <c>.vxscene</c> has no such
///     collision, and neither does the inspector, where the alias is written out into the foldout's
///     title. Without this the panel offered "Audio Listener Component".
/// </remarks>
[Component]
[DataContract("AudioListener")]
public struct AudioListenerComponent {
    /// <summary>A gain over every positioned voice.</summary>
    public float Gain;

    /// <summary>How much of the mix these ears get, against the other listeners'.</summary>
    /// <remarks>
    ///     Only read when there is more than one listener, and equal weights are the split-screen
    ///     case. An unequal one is for ears that should be present without dominating — a spectator,
    ///     a security camera, a drone.
    /// </remarks>
    public float Weight;

    /// <summary>Whether the system works velocity out from how far the entity moved.</summary>
    public bool AutoVelocity;

    /// <summary>How fast it is moving, in units a second.</summary>
    public Vector3 Velocity;

    /// <summary>Where it was last frame. Owned by the system.</summary>
    public Vector3 PreviousPosition;

    /// <summary>Whether <see cref="PreviousPosition" /> means anything yet.</summary>
    public bool HasPreviousPosition;

    /// <summary>Full gain, velocity worked out from movement.</summary>
    public static AudioListenerComponent Default => new() { Gain = 1f, Weight = 1f, AutoVelocity = true };
}

/// <summary>Makes an entity a region of space that sounds like somewhere.</summary>
/// <remarks>
///     <para>
///         <b>The zone is placed, not written.</b> Everything else spatial in this engine is an
///         entity — a source, a listener — and a reverb zone is the one that most obviously belongs
///         in a level rather than in a method: it is a room. Without this component a zone could only
///         be added by calling <c>engine.ReverbZones.Add</c>, which is the wrong person doing it in
///         the wrong file.
///     </para>
///     <para>
///         <b>Position comes from <c>WorldTransform</c></b>, the same rule
///         <see cref="AudioSpatial" /> follows. The <see cref="AudioReverbZone.Position" /> on the
///         description is ignored here — a zone that moved because somebody edited an asset rather
///         than because the room moved would be a surprise.
///     </para>
///     <para>
///         <b>The description is shared and the placement is not.</b> One "cathedral" describes the
///         parameter it drives, its shape, how far in it reaches full strength and which zone it
///         beats; twenty entities carry it and are twenty different rooms. That is the same split as
///         <see cref="AudioEventRef" />, and it is what makes "make every cathedral boomier" one
///         edit.
///     </para>
/// </remarks>
public struct AudioReverbZoneRef {
    /// <summary>The zone. An entity with none is not a zone, and that is not an error.</summary>
    public AudioReverbZone? Zone;

    /// <summary>Whether it is currently doing anything.</summary>
    /// <remarks>
    ///     For a door that seals. Cheaper and more obvious than moving the zone somewhere the
    ///     listener cannot reach, and it keeps the parameter released rather than stuck.
    /// </remarks>
    public bool Enabled;

    /// <summary>An entity carrying <paramref name="zone" />, switched on.</summary>
    /// <param name="zone">The description.</param>
    /// <returns>The component.</returns>
    public static AudioReverbZoneRef Of(AudioReverbZone zone) => new() { Zone = zone, Enabled = true };
}
