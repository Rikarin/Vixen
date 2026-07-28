// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Audio.Effects;
using Vixen.Audio.Mixing;

namespace Vixen.Audio.Assets;

/// <summary>The named mix states from a <see cref="MixerAsset" />, and the blend between them.</summary>
/// <remarks>
///     <para>
///         <b>Interpolated in decibels.</b> Halfway between −60 dB and 0 dB is −30 dB, not 0.5 —
///         loudness is roughly logarithmic, and a linear blend spends most of its duration at a level
///         indistinguishable from the loud end and then drops. The same reasoning as
///         <see cref="AudioFadeCurve.Decibel" />, and the same floor.
///     </para>
///     <para>
///         <b>A transition starts from wherever things are, not from the previous snapshot.</b> So
///         interrupting one halfway through and going somewhere else does not jump, and a bus whose
///         gain was moved by hand since the last transition is respected rather than snapped back.
///     </para>
///     <para>
///         <b>Only what a snapshot names is touched.</b> Anything it does not mention keeps what it
///         had — which is what stops every snapshot in a project going stale the moment somebody adds
///         a bus.
///     </para>
///     <para>
///         Stepped from <c>AudioEngine.Update</c> on game time, like every other fade here.
///     </para>
/// </remarks>
public sealed class MixerSnapshots {
    readonly MixerSnapshotAsset[] snapshots;
    readonly List<BusTarget> busTargets = [];
    readonly List<SendTarget> sendTargets = [];
    readonly AudioMixer mixer;

    float elapsed;
    float duration;

    internal MixerSnapshots(AudioMixer mixer, MixerSnapshotAsset[] snapshots) {
        this.mixer = mixer;
        this.snapshots = snapshots;
    }

    /// <summary>Every snapshot's name.</summary>
    public IEnumerable<string> Names => snapshots.Select(snapshot => snapshot.Name);

    /// <summary>Which snapshot was last asked for, or <see langword="null" /> if none has been.</summary>
    public string? Current { get; private set; }

    /// <summary>Whether a transition is still running.</summary>
    public bool IsTransitioning { get; private set; }

    /// <summary>Whether a snapshot of that name exists.</summary>
    /// <param name="name">The name.</param>
    /// <returns>Whether it is there.</returns>
    public bool Has(string name) => Find(name) is not null;

    /// <summary>Blends the mixer to a snapshot.</summary>
    /// <param name="name">Which one.</param>
    /// <param name="duration">How long to take. Zero or less arrives at once.</param>
    /// <returns>Whether a snapshot of that name was found.</returns>
    /// <remarks>
    ///     <b>Returns false rather than throwing for an unknown name.</b> A snapshot name is content:
    ///     it comes from an asset somebody edited, and a level that asks for a snapshot the mixer was
    ///     rebuilt without should keep playing with the mix it has, not stop with an exception.
    /// </remarks>
    public bool TransitionTo(string name, TimeSpan duration) {
        var snapshot = Find(name);

        if (snapshot is null) {
            return false;
        }

        Current = snapshot.Name;
        busTargets.Clear();
        sendTargets.Clear();

        foreach (var entry in snapshot.Buses) {
            if (mixer.FindBus(entry.Bus) is not { } bus) {
                continue;
            }

            // Mute is applied at once. There is no half-muted, and holding it until the end of the
            // transition would make a snapshot that mutes something take effect at a surprising
            // moment rather than at the obvious one.
            bus.Muted = entry.Muted;

            // Any manual fade on this bus is over: two things driving one gain is the kind of fight
            // that shows up as a fader that will not stay where it is put.
            bus.CancelFade();
            busTargets.Add(new BusTarget(bus, bus.Gain, Decibels.ToLinear(entry.GainDb)));
        }

        foreach (var entry in snapshot.Sends) {
            if (mixer.FindBus(entry.Bus) is not { } bus) {
                continue;
            }

            foreach (var send in bus.Sends) {
                if (send.Target.Name == entry.Target) {
                    sendTargets.Add(new SendTarget(send, send.Level, Decibels.ToLinear(entry.LevelDb)));
                }
            }
        }

        this.duration = (float)duration.TotalSeconds;
        elapsed = 0f;
        IsTransitioning = true;

        if (this.duration <= 0f) {
            Apply(1f);
            IsTransitioning = false;
        }

        return true;
    }

    internal void Step(float deltaSeconds) {
        if (!IsTransitioning) {
            return;
        }

        elapsed += deltaSeconds;
        var t = duration <= 0f ? 1f : Math.Clamp(elapsed / duration, 0f, 1f);
        Apply(t);

        if (t >= 1f) {
            IsTransitioning = false;
        }
    }

    void Apply(float t) {
        foreach (var target in busTargets) {
            target.Bus.Gain = AudioFade.Evaluate(target.From, target.To, t);
        }

        foreach (var target in sendTargets) {
            target.Send.Level = AudioFade.Evaluate(target.From, target.To, t);
        }
    }

    MixerSnapshotAsset? Find(string name) {
        foreach (var snapshot in snapshots) {
            if (snapshot.Name == name) {
                return snapshot;
            }
        }

        return null;
    }

    readonly record struct BusTarget(AudioBus Bus, float From, float To);

    readonly record struct SendTarget(AudioSend Send, float From, float To);
}
