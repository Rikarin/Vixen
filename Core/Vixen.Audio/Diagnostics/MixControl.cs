// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Audio.Effects;
using Vixen.Audio.Mixing;

namespace Vixen.Audio.Diagnostics;

/// <summary>What a knob is, when it is being described rather than turned.</summary>
/// <param name="Path">How to name it — <c>bus/Music/gain</c>, <c>bus/Music/effect/0/Wet</c>.</param>
/// <param name="Kind">What sort of thing it is.</param>
/// <param name="Value">Where it currently sits.</param>
/// <param name="Minimum">A sensible bottom for a slider, which is not always a hard limit.</param>
/// <param name="Maximum">A sensible top.</param>
public readonly record struct MixControlInfo(
    string Path,
    MixControlKind Kind,
    float Value,
    float Minimum,
    float Maximum
);

/// <summary>What sort of thing a path names.</summary>
public enum MixControlKind {
    /// <summary>A bus's fader, in decibels.</summary>
    BusGain = 0,

    /// <summary>A bus's mute, as 0 or 1.</summary>
    BusMute = 1,

    /// <summary>How much of one bus reaches another, in decibels.</summary>
    SendLevel = 2,

    /// <summary>A named knob on one of a bus's inserts, in whatever unit that effect uses.</summary>
    EffectProperty = 3,

    /// <summary>An engine-wide parameter.</summary>
    Parameter = 4
}

/// <summary>Every knob in the mix, reachable by name.</summary>
/// <remarks>
///     <para>
///         <b>The runtime half of live update.</b> What makes a designer able to move a fader in an
///         editor while the game runs is not a socket — a socket is an afternoon — it is that every
///         knob has a stable name, that the name can be read as well as written, and that the whole
///         set can be enumerated so the other end knows what to draw. All three are here; what is not
///         is the transport, which belongs with the editor in Phase 6 and can be written against this
///         without touching the mixer again.
///     </para>
///     <para>
///         <b>Paths and not indices.</b> An index is a position in whatever order the mixer happened
///         to be built in, and it moves the day somebody inserts a bus — which is the day a saved
///         editor layout starts controlling the wrong thing. The one exception is an effect's
///         position in its own chain, which <em>is</em> its identity: two reverbs on one bus differ
///         only by where they sit.
///     </para>
///     <para>
///         <b>Decibels at the boundary, linear underneath.</b> A fader is drawn and thought about in
///         decibels; the mixer multiplies by a linear gain. Converting here rather than at either end
///         means an editor never has to know, and it means <c>−∞</c> has an obvious spelling.
///     </para>
///     <para>
///         <b>It only reads and writes what already exists.</b> Nothing here creates a bus, adds an
///         effect or changes a route: a live-update session adjusts a mix, and a mix whose shape can
///         change underneath a running game is a different and much harder problem.
///     </para>
/// </remarks>
/// <param name="engine">The engine whose mix this addresses.</param>
public sealed class MixControl(AudioEngine engine) {
    /// <summary>The level a fader shows when it is all the way down.</summary>
    /// <remarks>
    ///     Eighty decibels below unity, which is inaudible against anything and is what every mixing
    ///     desk's bottom stop means. Genuine silence has no decibel value, so a fader has to bottom
    ///     out somewhere and pretending otherwise gives an editor a <c>-∞</c> to render.
    /// </remarks>
    public const float SilenceDb = -80f;

    /// <summary>Reads a knob.</summary>
    /// <param name="path">What to read.</param>
    /// <param name="value">What it is worth.</param>
    /// <returns>Whether the path named anything.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="path" /> is null.</exception>
    public bool TryGet(string path, out float value) {
        ArgumentNullException.ThrowIfNull(path);
        value = 0f;
        var parts = path.Split('/');

        switch (parts) {
            case ["parameter", var name]:
                if (engine.Parameters is not { } parameters) {
                    return false;
                }

                var index = parameters.IndexOf(name);

                if (index < 0) {
                    return false;
                }

                value = parameters.ValueOf(index);
                return true;

            case ["bus", var busName, "gain"]:
                if (engine.FindBus(busName) is not { } gainBus) {
                    return false;
                }

                value = ToDecibels(gainBus.Gain);
                return true;

            case ["bus", var busName2, "mute"]:
                if (engine.FindBus(busName2) is not { } muteBus) {
                    return false;
                }

                value = muteBus.Muted ? 1f : 0f;
                return true;

            case ["bus", var fromName, "send", var toName]:
                if (FindSend(fromName, toName) is not { } send) {
                    return false;
                }

                value = ToDecibels(send.Level);
                return true;

            case ["bus", var effectBus, "effect", var slot, var property]:
                return Resolve(effectBus, slot) is { } effect && effect.TryGetProperty(property, out value);

            default:
                return false;
        }
    }

    /// <summary>Turns a knob.</summary>
    /// <param name="path">What to turn.</param>
    /// <param name="value">Where to put it.</param>
    /// <returns>Whether the path named anything.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="path" /> is null.</exception>
    public bool TrySet(string path, float value) {
        ArgumentNullException.ThrowIfNull(path);
        var parts = path.Split('/');

        switch (parts) {
            case ["parameter", var name]:
                return engine.Parameters?.Set(name, value) == true;

            case ["bus", var busName, "gain"]:
                if (engine.FindBus(busName) is not { } gainBus) {
                    return false;
                }

                gainBus.Gain = FromDecibels(value);
                return true;

            case ["bus", var busName2, "mute"]:
                if (engine.FindBus(busName2) is not { } muteBus) {
                    return false;
                }

                muteBus.Muted = value >= 0.5f;
                return true;

            case ["bus", var fromName, "send", var toName]:
                if (FindSend(fromName, toName) is not { } send) {
                    return false;
                }

                send.Level = FromDecibels(value);
                return true;

            case ["bus", var effectBus, "effect", var slot, var property]:
                return Resolve(effectBus, slot) is { } effect && effect.TrySetProperty(property, value);

            default:
                return false;
        }
    }

    /// <summary>Every knob there is, so the other end knows what to draw.</summary>
    /// <returns>The knobs, buses first and in mixer order.</returns>
    /// <remarks>
    ///     <b>Allocates, and is meant to.</b> It is called when an editor connects and when the mix
    ///     is rebuilt, not once a frame — the per-frame path is <see cref="TrySet" /> with a path the
    ///     other end already has.
    /// </remarks>
    public IReadOnlyList<MixControlInfo> Enumerate() {
        var controls = new List<MixControlInfo>();

        foreach (var bus in engine.Mixer.Buses) {
            controls.Add(new($"bus/{bus.Name}/gain", MixControlKind.BusGain, ToDecibels(bus.Gain), SilenceDb, 12f));
            controls.Add(new($"bus/{bus.Name}/mute", MixControlKind.BusMute, bus.Muted ? 1f : 0f, 0f, 1f));

            foreach (var send in bus.Sends) {
                controls.Add(new(
                    $"bus/{bus.Name}/send/{send.Target.Name}",
                    MixControlKind.SendLevel,
                    ToDecibels(send.Level),
                    SilenceDb,
                    12f
                ));
            }

            for (var slot = 0; slot < bus.Effects.Count; slot++) {
                var effect = bus.Effects[slot];

                foreach (var property in effect.Properties) {
                    effect.TryGetProperty(property, out var current);

                    // No range, because an effect's knobs are in the effect's own units — hertz,
                    // a ratio, seconds — and there is no bound this could invent that would be right
                    // for all of them. An editor draws a number box rather than a slider.
                    controls.Add(new(
                        $"bus/{bus.Name}/effect/{slot}/{property}",
                        MixControlKind.EffectProperty,
                        current,
                        0f,
                        0f
                    ));
                }
            }
        }

        if (engine.Parameters is { } parameters) {
            for (var i = 0; i < parameters.Count; i++) {
                var parameter = parameters[i];

                controls.Add(new(
                    $"parameter/{parameter.Name}",
                    MixControlKind.Parameter,
                    parameters.ValueOf(i),
                    parameter.Minimum,
                    parameter.Maximum
                ));
            }
        }

        return controls;
    }

    IAudioEffect? Resolve(string busName, string slot) =>
        engine.FindBus(busName) is { } bus
        && int.TryParse(slot, System.Globalization.CultureInfo.InvariantCulture, out var index)
        && (uint)index < (uint)bus.Effects.Count
            ? bus.Effects[index]
            : null;

    AudioSend? FindSend(string from, string to) {
        if (engine.FindBus(from) is not { } bus) {
            return null;
        }

        foreach (var send in bus.Sends) {
            if (string.Equals(send.Target.Name, to, StringComparison.Ordinal)) {
                return send;
            }
        }

        return null;
    }

    static float ToDecibels(float linear) => linear <= 0f ? SilenceDb : MathF.Max(Decibels.FromLinear(linear), SilenceDb);

    static float FromDecibels(float decibels) => decibels <= SilenceDb ? 0f : Decibels.ToLinear(decibels);
}
