// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Audio.Effects;
using Vixen.Audio.Mixing;

namespace Vixen.Audio.Parameters;

/// <summary>What a curve drives on the mix, rather than on one sound.</summary>
public enum AudioBusParameterTarget {
    /// <summary>A bus's fader, in decibels, on top of whatever the mix already has it at.</summary>
    GainDb = 0,

    /// <summary>One of a bus's sends, in decibels — how much of it reaches the reverb.</summary>
    SendDb = 1,

    /// <summary>A named knob on one of a bus's inserts, set to whatever the curve says.</summary>
    /// <remarks>
    ///     The one target that is set outright rather than combined, because the unit is the effect's
    ///     own — hertz, a ratio, seconds — and there is no rule for adding or multiplying two of
    ///     them that is right for all of those. Two parameters driving one property is therefore not
    ///     a thing to do: they are applied in declaration order and the last one wins, which is
    ///     deterministic and still not what anybody meant.
    /// </remarks>
    EffectProperty = 2
}

/// <summary>One mapping from an engine-wide parameter onto part of the mix.</summary>
/// <remarks>
///     Buses, sends and effects are named rather than indexed, exactly as <c>MixerAsset</c> names
///     them: an index is a position in whatever order the mixer happened to be built in, and it moves
///     the day somebody inserts a bus.
/// </remarks>
public sealed record AudioBusAutomation {
    /// <summary>Which bus, by name.</summary>
    public string Bus { get; init; } = string.Empty;

    /// <summary>What on it.</summary>
    public AudioBusParameterTarget Target { get; init; }

    /// <summary>For <see cref="AudioBusParameterTarget.SendDb" />, the bus sent to.</summary>
    public string Send { get; init; } = string.Empty;

    /// <summary>For <see cref="AudioBusParameterTarget.EffectProperty" />, which insert on the bus.</summary>
    public int Effect { get; init; }

    /// <summary>For <see cref="AudioBusParameterTarget.EffectProperty" />, the knob's own name.</summary>
    public string Property { get; init; } = string.Empty;

    /// <summary>How the parameter's range maps onto the target's unit.</summary>
    public AudioCurve Curve { get; init; } = AudioCurve.Constant(0f);
}

/// <summary>An engine-wide named value, and what moving it does to the mix.</summary>
public sealed record AudioBusParameterDefinition {
    /// <summary>What gameplay calls it.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>The bottom of its range.</summary>
    public float Minimum { get; init; }

    /// <summary>The top of its range.</summary>
    public float Maximum { get; init; } = 1f;

    /// <summary>Where it sits before anything sets it.</summary>
    public float Default { get; init; }

    /// <summary>How long it takes to cross its whole range. Zero arrives at once.</summary>
    public float SeekSeconds { get; init; }

    /// <summary>What moving it does.</summary>
    public AudioBusAutomation[] Automation { get; init; } = [];
}

/// <summary>Engine-wide parameters, resolved against a mixer and stepped once a frame.</summary>
/// <remarks>
///     <para>
///         <b>What a snapshot is not.</b> <c>MixerSnapshots</c> blends to a named state over a
///         duration — discrete destinations, arrived at. This is a continuous dial: <c>rain = 0.4</c>
///         is a real mix and so is 0.41. Where a snapshot says "the underwater mix", a parameter says
///         "this much underwater", and the difference is whether the transition is a thing that
///         happens or a thing that is being held at a position.
///     </para>
///     <para>
///         <b>And what a per-voice parameter is not.</b> Everything here is shared by everything
///         routed through it, so it cannot say "this player is underwater and that one is not". That
///         is <see cref="AudioParameterSheet" />, attached per sound. The two are deliberately
///         separate types rather than one with a scope flag, because the set of things each can drive
///         has no overlap at all.
///     </para>
///     <para>
///         <b>Resolved once, applied every frame.</b> Names become bus, send and effect references at
///         construction, and anything that does not resolve is reported rather than thrown — a mixer
///         is content. After that a step is arithmetic and a handful of property writes.
///     </para>
/// </remarks>
public sealed class MixerParameters {
    readonly AudioBusParameterDefinition[] parameters;
    readonly float[] inverseRanges;
    readonly float[] values;
    readonly float[] targets;
    readonly Binding[] bindings;

    /// <summary>How many parameters there are.</summary>
    public int Count => parameters.Length;

    /// <summary>One of them.</summary>
    /// <param name="index">Which.</param>
    public AudioBusParameterDefinition this[int index] => parameters[index];

    /// <summary>Resolves some parameters against a mixer.</summary>
    /// <param name="mixer">The mixer whose buses, sends and effects the automation names.</param>
    /// <param name="definitions">The parameters.</param>
    /// <param name="problems">Everything that did not resolve. Empty is the good case.</param>
    /// <exception cref="ArgumentNullException">Either required argument is null.</exception>
    public MixerParameters(
        AudioMixer mixer,
        IReadOnlyList<AudioBusParameterDefinition> definitions,
        out IReadOnlyList<string> problems
    ) {
        ArgumentNullException.ThrowIfNull(mixer);
        ArgumentNullException.ThrowIfNull(definitions);

        var found = new List<string>();
        var resolved = new List<Binding>();
        parameters = new AudioBusParameterDefinition[definitions.Count];
        inverseRanges = new float[definitions.Count];
        values = new float[definitions.Count];
        targets = new float[definitions.Count];

        for (var i = 0; i < definitions.Count; i++) {
            var definition = definitions[i];
            parameters[i] = definition;
            var range = definition.Maximum - definition.Minimum;
            inverseRanges[i] = range > 0f ? 1f / range : 0f;
            values[i] = definition.Default;
            targets[i] = definition.Default;

            foreach (var automation in definition.Automation) {
                if (TryBind(mixer, i, automation, definition.Name, found) is { } binding) {
                    resolved.Add(binding);
                }
            }
        }

        bindings = [.. resolved];
        problems = found;

        // Once at construction, so the mix is at its defaults before the first frame rather than one
        // frame after it — otherwise a level that starts submerged is heard dry for a frame.
        Apply();
    }

    /// <summary>Finds a parameter by name.</summary>
    /// <param name="name">What gameplay calls it.</param>
    /// <returns>Its index, or −1.</returns>
    public int IndexOf(string name) {
        for (var i = 0; i < parameters.Length; i++) {
            if (string.Equals(parameters[i].Name, name, StringComparison.Ordinal)) {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Points a parameter at a value.</summary>
    /// <param name="index">Which.</param>
    /// <param name="value">Where it should go. Clamped to its range.</param>
    /// <returns>Whether there was such a parameter.</returns>
    public bool Set(int index, float value) {
        if ((uint)index >= (uint)parameters.Length) {
            return false;
        }

        targets[index] = Math.Clamp(value, parameters[index].Minimum, parameters[index].Maximum);
        return true;
    }

    /// <summary>Points a parameter at a value, by name.</summary>
    /// <param name="name">Which.</param>
    /// <param name="value">Where it should go.</param>
    /// <returns>Whether there was such a parameter.</returns>
    public bool Set(string name, float value) => Set(IndexOf(name), value);

    /// <summary>Where a parameter currently is, which is not always where it was pointed.</summary>
    /// <param name="index">Which.</param>
    /// <returns>Its value.</returns>
    public float ValueOf(int index) => (uint)index < (uint)values.Length ? values[index] : 0f;

    /// <summary>Steps every parameter towards its target and pushes the result into the mix.</summary>
    /// <param name="deltaSeconds">How much game time has passed.</param>
    public void Step(float deltaSeconds) {
        var moved = false;

        for (var i = 0; i < parameters.Length; i++) {
            var before = values[i];
            Seek(i, deltaSeconds);
            moved |= values[i] != before;
        }

        if (moved) {
            Apply();
        }
    }

    void Seek(int index, float deltaSeconds) {
        var parameter = parameters[index];
        var target = targets[index];

        if (parameter.SeekSeconds <= 0f || deltaSeconds <= 0f) {
            values[index] = target;
            return;
        }

        var step = (parameter.Maximum - parameter.Minimum) * deltaSeconds / parameter.SeekSeconds;
        var difference = target - values[index];

        values[index] = MathF.Abs(difference) <= step
            ? target
            : values[index] + (MathF.Sign(difference) * step);
    }

    /// <summary>Combines every binding's contribution and writes it into the mix.</summary>
    /// <remarks>
    ///     Everything a binding touches is reset to neutral first, so a parameter that has moved off a
    ///     target leaves it where it was rather than where it last pushed it. Without that pass a bus
    ///     driven to −20 dB and then released would stay there.
    /// </remarks>
    void Apply() {
        foreach (var binding in bindings) {
            switch (binding.Target) {
                case AudioBusParameterTarget.GainDb:
                    binding.Bus.ParameterGain = 1f;
                    break;

                case AudioBusParameterTarget.SendDb:
                    binding.Send!.ParameterLevel = 1f;
                    break;

                default:
                    break;
            }
        }

        foreach (var binding in bindings) {
            var position = Normalize(binding.Parameter, values[binding.Parameter]);
            var value = binding.Curve.Evaluate(position);

            switch (binding.Target) {
                case AudioBusParameterTarget.GainDb:
                    binding.Bus.ParameterGain *= Decibels.ToLinear(value);
                    break;

                case AudioBusParameterTarget.SendDb:
                    binding.Send!.ParameterLevel *= Decibels.ToLinear(value);
                    break;

                case AudioBusParameterTarget.EffectProperty:
                    binding.Effect!.TrySetProperty(binding.Property, value);
                    break;

                default:
                    break;
            }
        }
    }

    float Normalize(int index, float value) =>
        Math.Clamp((value - parameters[index].Minimum) * inverseRanges[index], 0f, 1f);

    static Binding? TryBind(
        AudioMixer mixer,
        int parameter,
        AudioBusAutomation automation,
        string name,
        List<string> problems
    ) {
        var bus = mixer.FindBus(automation.Bus);

        if (bus is null) {
            problems.Add($"Parameter '{name}' drives bus '{automation.Bus}', which does not exist.");
            return null;
        }

        switch (automation.Target) {
            case AudioBusParameterTarget.GainDb:
                return new(parameter, automation.Target, bus, automation.Curve);

            case AudioBusParameterTarget.SendDb: {
                AudioSend? send = null;

                foreach (var candidate in bus.Sends) {
                    if (string.Equals(candidate.Target.Name, automation.Send, StringComparison.Ordinal)) {
                        send = candidate;
                        break;
                    }
                }

                if (send is null) {
                    problems.Add(
                        $"Parameter '{name}' drives a send from '{automation.Bus}' to '{automation.Send}', "
                        + "which does not exist."
                    );

                    return null;
                }

                return new(parameter, automation.Target, bus, automation.Curve) { Send = send };
            }

            default: {
                if ((uint)automation.Effect >= (uint)bus.Effects.Count) {
                    problems.Add(
                        $"Parameter '{name}' drives effect {automation.Effect} on '{automation.Bus}', "
                        + $"which has {bus.Effects.Count}."
                    );

                    return null;
                }

                var effect = bus.Effects[automation.Effect];

                // Probed with the value the curve starts at, which both validates the name and puts
                // the property where the parameter's default says it should be. A name nothing
                // recognises is a typo, and finding it here is the whole reason the match is exact.
                if (!effect.TrySetProperty(automation.Property, automation.Curve.Evaluate(0f))) {
                    problems.Add(
                        $"Parameter '{name}' drives '{automation.Property}' on {effect.GetType().Name}, "
                        + "which has no such property."
                    );

                    return null;
                }

                return new(parameter, automation.Target, bus, automation.Curve) {
                    Effect = effect,
                    Property = automation.Property
                };
            }
        }
    }

    sealed record Binding(int Parameter, AudioBusParameterTarget Target, AudioBus Bus, AudioCurve Curve) {
        public AudioSend? Send { get; init; }

        public IAudioEffect? Effect { get; init; }

        public string Property { get; init; } = string.Empty;
    }
}
