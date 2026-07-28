// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Audio.Effects;
using Vixen.Core;

namespace Vixen.Audio.Assets;

/// <summary>An effect, as a file declares it.</summary>
/// <remarks>
///     <para>
///         An interface with a <c>[DataContract]</c> name per implementation, which is how the rest
///         of the engine does polymorphism in a file — the contract name is the YAML tag, so
///         <c>!ReverbEffect</c> selects the type and nothing keeps a registration table in sync. The
///         compositor asset in <c>Vixen.Rendering</c> is the same arrangement.
///     </para>
///     <para>
///         <b>A parallel model, not annotations on the effects themselves.</b> An
///         <see cref="IAudioEffect" /> owns delay lines, filter state and a comb bank; the asset owns
///         numbers. Merging them would mean a type that is half serialisable, and the half that is
///         not is the half that matters at run time — nobody wants a reverb's tail in a file.
///     </para>
///     <para>
///         <b><see cref="Create" /> is a method and not a lookup table.</b> Constructing an effect
///         from a name would be reflection, which <c>ADR-002</c> forbids in runtime code and which
///         does not survive trimming. Every asset knows what it makes.
///     </para>
/// </remarks>
public interface IAudioEffectAsset {
    /// <summary>Whether the effect starts switched on.</summary>
    bool Enabled { get; }

    /// <summary>Builds the effect this describes.</summary>
    /// <returns>A new effect, not yet prepared — the bus does that when it is added.</returns>
    IAudioEffect Create();
}

/// <summary>A biquad, as a file declares it.</summary>
[DataContract("FilterEffect")]
public sealed record FilterEffectAsset : IAudioEffectAsset {
    /// <summary>Which shape.</summary>
    public BiquadFilterKind Kind { get; init; } = BiquadFilterKind.LowPass;

    /// <summary>Its cutoff or centre, in hertz.</summary>
    public float Frequency { get; init; } = 1_000f;

    /// <summary>Its resonance.</summary>
    public float Q { get; init; } = 0.70710678f;

    /// <summary>How much to boost or cut, for the peaking and shelf shapes.</summary>
    public float GainDb { get; init; }

    /// <inheritdoc />
    public bool Enabled { get; init; } = true;

    /// <inheritdoc />
    public IAudioEffect Create() => new BiquadFilterEffect {
        Kind = Kind,
        Frequency = Frequency,
        Q = Q,
        GainDb = GainDb,
        Enabled = Enabled
    };
}

/// <summary>One band of an equaliser.</summary>
[DataContract("EqualizerBand")]
public sealed record EqualizerBandAsset {
    /// <summary>Which shape.</summary>
    public BiquadFilterKind Kind { get; init; } = BiquadFilterKind.Peaking;

    /// <summary>Its cutoff or centre, in hertz.</summary>
    public float Frequency { get; init; } = 1_000f;

    /// <summary>Its resonance, or for a peaking band, how narrow it is.</summary>
    public float Q { get; init; } = 1f;

    /// <summary>How much to boost or cut.</summary>
    public float GainDb { get; init; }
}

/// <summary>An equaliser, as a file declares it.</summary>
[DataContract("EqualizerEffect")]
public sealed record EqualizerEffectAsset : IAudioEffectAsset {
    /// <summary>The bands, in the order they run — which is the order they are written.</summary>
    public EqualizerBandAsset[] Bands { get; init; } = [];

    /// <inheritdoc />
    public bool Enabled { get; init; } = true;

    /// <inheritdoc />
    public IAudioEffect Create() {
        var equalizer = new EqualizerEffect { Enabled = Enabled };

        foreach (var band in Bands) {
            equalizer.AddBand(band.Kind, band.Frequency, band.Q, band.GainDb);
        }

        return equalizer;
    }
}

/// <summary>A reverb, as a file declares it.</summary>
[DataContract("ReverbEffect")]
public sealed record ReverbEffectAsset : IAudioEffectAsset {
    /// <summary>How big the room is.</summary>
    public float RoomSize { get; init; } = 0.5f;

    /// <summary>How fast the high frequencies die away.</summary>
    public float Damping { get; init; } = 0.5f;

    /// <summary>How much of the reverberated signal to add.</summary>
    public float Wet { get; init; } = 0.3f;

    /// <summary>How much of the untouched signal to keep.</summary>
    public float Dry { get; init; } = 1f;

    /// <summary>How wide the stereo image of the tail is.</summary>
    public float Width { get; init; } = 1f;

    /// <inheritdoc />
    public bool Enabled { get; init; } = true;

    /// <inheritdoc />
    public IAudioEffect Create() => new ReverbEffect {
        RoomSize = RoomSize,
        Damping = Damping,
        Wet = Wet,
        Dry = Dry,
        Width = Width,
        Enabled = Enabled
    };
}

/// <summary>A delay, as a file declares it.</summary>
[DataContract("DelayEffect")]
public sealed record DelayEffectAsset : IAudioEffectAsset {
    /// <summary>How long until the first repeat.</summary>
    public float DelaySeconds { get; init; } = 0.25f;

    /// <summary>The longest it can later be set to, which is what sizes the delay lines.</summary>
    public float MaxDelaySeconds { get; init; } = 2f;

    /// <summary>How much of each repeat feeds the next.</summary>
    public float Feedback { get; init; } = 0.4f;

    /// <summary>How much of the delayed signal to add.</summary>
    public float Wet { get; init; } = 0.35f;

    /// <summary>How much of the untouched signal to keep.</summary>
    public float Dry { get; init; } = 1f;

    /// <summary>Where the low-pass in the feedback path sits, in hertz.</summary>
    public float DampingHz { get; init; } = 4_000f;

    /// <summary>Whether the repeats alternate between the speakers.</summary>
    public bool PingPong { get; init; }

    /// <inheritdoc />
    public bool Enabled { get; init; } = true;

    /// <inheritdoc />
    public IAudioEffect Create() => new DelayEffect {
        MaxDelaySeconds = MaxDelaySeconds,
        DelaySeconds = DelaySeconds,
        Feedback = Feedback,
        Wet = Wet,
        Dry = Dry,
        DampingHz = DampingHz,
        PingPong = PingPong,
        Enabled = Enabled
    };
}

/// <summary>A compressor, as a file declares it.</summary>
[DataContract("CompressorEffect")]
public sealed record CompressorEffectAsset : IAudioEffectAsset {
    /// <summary>Above this level, in decibels, it starts working.</summary>
    public float ThresholdDb { get; init; } = -18f;

    /// <summary>How much is taken off above the threshold.</summary>
    public float Ratio { get; init; } = 4f;

    /// <summary>How wide the bend into the ratio is, in decibels.</summary>
    public float KneeDb { get; init; } = 6f;

    /// <summary>How fast it reacts to something getting louder.</summary>
    public float AttackSeconds { get; init; } = 0.01f;

    /// <summary>How fast it recovers.</summary>
    public float ReleaseSeconds { get; init; } = 0.2f;

    /// <summary>A gain applied after compression, in decibels.</summary>
    public float MakeupDb { get; init; }

    /// <inheritdoc />
    public bool Enabled { get; init; } = true;

    /// <inheritdoc />
    public IAudioEffect Create() => new CompressorEffect {
        ThresholdDb = ThresholdDb,
        Ratio = Ratio,
        KneeDb = KneeDb,
        AttackSeconds = AttackSeconds,
        ReleaseSeconds = ReleaseSeconds,
        MakeupDb = MakeupDb,
        Enabled = Enabled
    };
}

/// <summary>A noise gate, as a file declares it.</summary>
[DataContract("GateEffect")]
public sealed record GateEffectAsset : IAudioEffectAsset {
    /// <summary>The level below which it starts closing.</summary>
    public float ThresholdDb { get; init; } = -45f;

    /// <summary>How far below that it must fall before it is fully shut.</summary>
    public float KneeDb { get; init; } = 6f;

    /// <summary>How far down a closed gate goes.</summary>
    public float RangeDb { get; init; } = -60f;

    /// <summary>How quickly it opens.</summary>
    public float AttackSeconds { get; init; } = 0.002f;

    /// <summary>How long it stays open after the signal has fallen back.</summary>
    public float HoldSeconds { get; init; } = 0.15f;

    /// <summary>How slowly it closes once the hold has run out.</summary>
    public float ReleaseSeconds { get; init; } = 0.2f;

    /// <inheritdoc />
    public bool Enabled { get; init; } = true;

    /// <inheritdoc />
    public IAudioEffect Create() => new GateEffect {
        ThresholdDb = ThresholdDb,
        KneeDb = KneeDb,
        RangeDb = RangeDb,
        AttackSeconds = AttackSeconds,
        HoldSeconds = HoldSeconds,
        ReleaseSeconds = ReleaseSeconds,
        Enabled = Enabled
    };
}

/// <summary>A limiter, as a file declares it.</summary>
[DataContract("LimiterEffect")]
public sealed record LimiterEffectAsset : IAudioEffectAsset {
    /// <summary>The loudest sample allowed out, in decibels.</summary>
    public float CeilingDb { get; init; } = -0.3f;

    /// <summary>How far ahead it looks, and therefore how much latency it adds.</summary>
    public float LookAheadSeconds { get; init; } = 0.002f;

    /// <summary>How fast it lets go.</summary>
    public float ReleaseSeconds { get; init; } = 0.1f;

    /// <inheritdoc />
    public bool Enabled { get; init; } = true;

    /// <inheritdoc />
    public IAudioEffect Create() => new LimiterEffect {
        LookAheadSeconds = LookAheadSeconds,
        CeilingDb = CeilingDb,
        ReleaseSeconds = ReleaseSeconds,
        Enabled = Enabled
    };
}

/// <summary>A chorus, flanger or vibrato, as a file declares it.</summary>
[DataContract("ModulatedDelayEffect")]
public sealed record ModulatedDelayEffectAsset : IAudioEffectAsset {
    /// <summary>Which of the three it is being.</summary>
    public ModulatedDelayKind Kind { get; init; } = ModulatedDelayKind.Chorus;

    /// <summary>The middle of the sweep, in seconds.</summary>
    public float DelaySeconds { get; init; } = 0.022f;

    /// <summary>How far either side of that it travels.</summary>
    public float DepthSeconds { get; init; } = 0.004f;

    /// <summary>How many times a second the oscillator goes round.</summary>
    public float RateHz { get; init; } = 0.4f;

    /// <summary>How much of the output feeds back in.</summary>
    public float Feedback { get; init; }

    /// <summary>How many taps to read the line at.</summary>
    public int Voices { get; init; } = 1;

    /// <summary>How far apart the channels are swept.</summary>
    public float StereoSpread { get; init; } = 0.25f;

    /// <summary>How much of the swept signal to add.</summary>
    public float Wet { get; init; } = 0.5f;

    /// <summary>How much of the untouched signal to keep.</summary>
    public float Dry { get; init; } = 1f;

    /// <inheritdoc />
    public bool Enabled { get; init; } = true;

    /// <inheritdoc />
    public IAudioEffect Create() => new ModulatedDelayEffect {
        Kind = Kind,
        DelaySeconds = DelaySeconds,
        DepthSeconds = DepthSeconds,
        RateHz = RateHz,
        Feedback = Feedback,
        Voices = Voices,
        StereoSpread = StereoSpread,
        Wet = Wet,
        Dry = Dry,
        Enabled = Enabled
    };
}

/// <summary>A phaser, as a file declares it.</summary>
[DataContract("PhaserEffect")]
public sealed record PhaserEffectAsset : IAudioEffectAsset {
    /// <summary>How many all-pass sections, and therefore how many notches.</summary>
    public int Stages { get; init; } = 4;

    /// <summary>The bottom of the sweep, in hertz.</summary>
    public float MinFrequency { get; init; } = 200f;

    /// <summary>The top of the sweep, in hertz.</summary>
    public float MaxFrequency { get; init; } = 2_000f;

    /// <summary>How many times a second the sweep goes round.</summary>
    public float RateHz { get; init; } = 0.3f;

    /// <summary>How much of the output feeds back in.</summary>
    public float Feedback { get; init; } = 0.5f;

    /// <summary>How far apart the channels are swept.</summary>
    public float StereoSpread { get; init; } = 0.25f;

    /// <summary>How much of the phase-shifted signal to add.</summary>
    public float Wet { get; init; } = 0.5f;

    /// <summary>How much of the untouched signal to keep.</summary>
    public float Dry { get; init; } = 0.5f;

    /// <inheritdoc />
    public bool Enabled { get; init; } = true;

    /// <inheritdoc />
    public IAudioEffect Create() => new PhaserEffect {
        Stages = Stages,
        MinFrequency = MinFrequency,
        MaxFrequency = MaxFrequency,
        RateHz = RateHz,
        Feedback = Feedback,
        StereoSpread = StereoSpread,
        Wet = Wet,
        Dry = Dry,
        Enabled = Enabled
    };
}

/// <summary>A distortion, as a file declares it.</summary>
[DataContract("DistortionEffect")]
public sealed record DistortionEffectAsset : IAudioEffectAsset {
    /// <summary>Which curve.</summary>
    public DistortionCurve Curve { get; init; } = DistortionCurve.SoftClip;

    /// <summary>How hard the signal is pushed into it, in decibels.</summary>
    public float DriveDb { get; init; } = 12f;

    /// <summary>A gain applied after it, in decibels.</summary>
    public float OutputDb { get; init; } = -6f;

    /// <summary>How much of the bent signal to keep.</summary>
    public float Mix { get; init; } = 1f;

    /// <inheritdoc />
    public bool Enabled { get; init; } = true;

    /// <inheritdoc />
    public IAudioEffect Create() => new DistortionEffect {
        Curve = Curve,
        DriveDb = DriveDb,
        OutputDb = OutputDb,
        Mix = Mix,
        Enabled = Enabled
    };
}

/// <summary>A bit crusher, as a file declares it.</summary>
[DataContract("BitCrusherEffect")]
public sealed record BitCrusherEffectAsset : IAudioEffectAsset {
    /// <summary>How many bits of resolution to leave.</summary>
    public float Bits { get; init; } = 8f;

    /// <summary>How many output samples each input sample is held for.</summary>
    public float Downsample { get; init; } = 1f;

    /// <summary>How much of the ruined signal to keep.</summary>
    public float Mix { get; init; } = 1f;

    /// <inheritdoc />
    public bool Enabled { get; init; } = true;

    /// <inheritdoc />
    public IAudioEffect Create() => new BitCrusherEffect {
        Bits = Bits,
        Downsample = Downsample,
        Mix = Mix,
        Enabled = Enabled
    };
}

/// <summary>A pitch shifter, as a file declares it.</summary>
[DataContract("PitchShiftEffect")]
public sealed record PitchShiftEffectAsset : IAudioEffectAsset {
    /// <summary>How far to shift, in semitones.</summary>
    public float Semitones { get; init; }

    /// <summary>How long each grain is.</summary>
    public float GrainSeconds { get; init; } = 0.05f;

    /// <summary>How much of the shifted signal to keep.</summary>
    public float Mix { get; init; } = 1f;

    /// <inheritdoc />
    public bool Enabled { get; init; } = true;

    /// <inheritdoc />
    public IAudioEffect Create() => new PitchShiftEffect {
        Semitones = Semitones,
        GrainSeconds = GrainSeconds,
        Mix = Mix,
        Enabled = Enabled
    };
}

/// <summary>A spectrum analyser, as a file declares it.</summary>
/// <remarks>
///     In the asset because a debug overlay is part of a mix's configuration too: a project that
///     wants a meter on the music bus should be able to say so without a programmer.
/// </remarks>
[DataContract("SpectrumAnalyzerEffect")]
public sealed record SpectrumAnalyzerEffectAsset : IAudioEffectAsset {
    /// <summary>How many samples each picture is taken from. A power of two.</summary>
    public int Size { get; init; } = 1_024;

    /// <summary>How much of the previous picture each new one keeps.</summary>
    public float Smoothing { get; init; } = 0.6f;

    /// <inheritdoc />
    public bool Enabled { get; init; } = true;

    /// <inheritdoc />
    public IAudioEffect Create() => new SpectrumAnalyzerEffect(Size) {
        Smoothing = Smoothing,
        Enabled = Enabled
    };
}

/// <summary>A send, as a file declares it.</summary>
[DataContract("MixerSend")]
public sealed record MixerSendAsset {
    /// <summary>The bus the copy goes to, by name.</summary>
    public string Target { get; init; } = string.Empty;

    /// <summary>How much of it, in decibels. Zero is unity.</summary>
    public float LevelDb { get; init; }

    /// <summary>Whether the copy is taken before the source bus's own gain.</summary>
    public bool PreFader { get; init; }
}

/// <summary>A bus, as a file declares it.</summary>
/// <remarks>
///     Everything a human sets is in decibels, and everything the mixer runs on is linear. The
///     conversion happens once, here, on the way in — a file full of <c>0.7943282</c> where somebody
///     meant −2 dB is a file nobody can edit.
/// </remarks>
[DataContract("MixerBus")]
public sealed record MixerBusAsset {
    /// <summary>What it is called. Unique within the mixer.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>What it sums into, by name. Empty means the master.</summary>
    public string Parent { get; init; } = string.Empty;

    /// <summary>Its gain, in decibels.</summary>
    public float GainDb { get; init; }

    /// <summary>Whether it starts muted.</summary>
    public bool Muted { get; init; }

    /// <summary>The bus whose signal keys this one's sidechained effects, by name.</summary>
    public string Sidechain { get; init; } = string.Empty;

    /// <summary>Copies of this bus's signal sent elsewhere.</summary>
    public MixerSendAsset[] Sends { get; init; } = [];

    /// <summary>The inserts, in the order they run.</summary>
    public IAudioEffectAsset[] Effects { get; init; } = [];
}

/// <summary>What one snapshot does to one bus.</summary>
/// <remarks>
///     A snapshot names only the buses it changes. Anything it does not mention keeps whatever it
///     had, which is what makes a snapshot for "the player is underwater" a two-line thing rather
///     than a copy of the whole mixer that goes stale the moment a bus is added.
/// </remarks>
[DataContract("SnapshotBus")]
public sealed record SnapshotBusAsset {
    /// <summary>Which bus, by name.</summary>
    public string Bus { get; init; } = string.Empty;

    /// <summary>What its gain becomes, in decibels.</summary>
    public float GainDb { get; init; }

    /// <summary>What its mute becomes.</summary>
    /// <remarks>Applied the moment the transition starts, because there is no half-muted.</remarks>
    public bool Muted { get; init; }
}

/// <summary>What one snapshot does to one send.</summary>
[DataContract("SnapshotSend")]
public sealed record SnapshotSendAsset {
    /// <summary>The bus the send leaves from.</summary>
    public string Bus { get; init; } = string.Empty;

    /// <summary>The bus it arrives at, which is what identifies it.</summary>
    public string Target { get; init; } = string.Empty;

    /// <summary>What its level becomes, in decibels.</summary>
    public float LevelDb { get; init; }
}

/// <summary>A named state of the mixer that can be blended to.</summary>
/// <remarks>
///     Combat, underwater, paused, dead. A snapshot is how a sound designer expresses "when this is
///     happening, the mix looks like that" without any gameplay code naming a bus — the code says
///     which snapshot, and the mixer asset says what that means.
/// </remarks>
[DataContract("MixerSnapshot")]
public sealed record MixerSnapshotAsset {
    /// <summary>What it is called, and what <c>TransitionTo</c> is given.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>The buses it changes.</summary>
    public SnapshotBusAsset[] Buses { get; init; } = [];

    /// <summary>The sends it changes.</summary>
    public SnapshotSendAsset[] Sends { get; init; } = [];
}

/// <summary>A whole mixer, as a file declares it.</summary>
/// <remarks>
///     <para>
///         The authoring layer <c>docs/plan/14</c> asks for. Buses built in code are fine for a
///         prototype and wrong for a project: a sound designer who has to open a C# file to move a
///         fader is a sound designer who does not move the fader.
///     </para>
///     <para>
///         <b>No file format here.</b> This is a serialisable record graph and nothing more — the
///         editor writes YAML, the content build bakes a chunk, and a shipping runtime reads the
///         chunk with no parser linked in. That separation is the same one <c>Vixen.Rendering</c>'s
///         compositor asset makes, and for the same reason.
///     </para>
/// </remarks>
[DataContract("MixerAsset")]
public sealed record MixerAsset {
    /// <summary>The buses, in any order — a bus may name a parent declared after it.</summary>
    public MixerBusAsset[] Buses { get; init; } = [];

    /// <summary>The snapshots.</summary>
    public MixerSnapshotAsset[] Snapshots { get; init; } = [];

    /// <summary>Which snapshot the mixer starts in, by name. Empty means whatever the buses say.</summary>
    public string DefaultSnapshot { get; init; } = string.Empty;

    /// <summary>Engine-wide parameters and what moving them does to the mix.</summary>
    /// <remarks>
    ///     Beside the snapshots rather than instead of them: a snapshot is a named mix arrived at over
    ///     a duration, and a parameter is a dial held at a position. "The underwater mix" is the first;
    ///     "this much rain" is the second.
    /// </remarks>
    public AudioBusParameterAsset[] Parameters { get; init; } = [];
}
