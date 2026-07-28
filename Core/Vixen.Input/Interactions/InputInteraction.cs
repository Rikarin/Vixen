// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Input;

/// <summary>What a binding looked like on one frame.</summary>
/// <param name="IsActuated">Whether it is past its press point.</param>
/// <param name="Magnitude">How far it is from rest, <c>[0, ∞)</c>.</param>
/// <param name="Time">The time now, in seconds, from a clock that only goes forward.</param>
public readonly record struct InputInteractionSample(bool IsActuated, float Magnitude, double Time);

/// <summary>What an interaction decided this frame.</summary>
/// <remarks>
///     Flags rather than one value, because a press both starts and performs on the same frame and
///     collapsing that into a single "performed" would leave a UI that listens for
///     <c>Started</c> — to show a charge bar — with nothing to hear.
/// </remarks>
[Flags]
public enum InputInteractionResult : byte {
    /// <summary>Nothing happened.</summary>
    None = 0,

    /// <summary>The interaction began: the control was actuated and is now being watched.</summary>
    Started = 1,

    /// <summary>The interaction's condition was met.</summary>
    Performed = 2,

    /// <summary>The interaction ended without being met, or the control was released after it was.</summary>
    Canceled = 4
}

/// <summary>
///     A pattern over time that decides <em>when</em> an actuated control counts as having acted.
/// </summary>
/// <remarks>
///     <para>
///         Stateful, and one instance per binding: a hold has a clock, a multi-tap has a count, and
///         two bindings of the same action must be able to be half-way through their own. That is
///         why <see cref="InputInteractionFactory" /> builds a fresh one per binding rather than
///         sharing, and why processors — which are pure — are shared and these are not.
///     </para>
///     <para>
///         An interaction never reads a control. It is handed a magnitude and a time and returns
///         what it decided, which is what makes every one of them testable without a device.
///     </para>
/// </remarks>
public interface IInputInteraction {
    /// <summary>The name it is written under in a <c>.vxinput</c>.</summary>
    string Name { get; }

    /// <summary>Advances the interaction by one frame.</summary>
    /// <param name="sample">What the binding looks like now.</param>
    /// <returns>What happened.</returns>
    InputInteractionResult Process(in InputInteractionSample sample);

    /// <summary>Forgets everything, as if the control had never been touched.</summary>
    /// <remarks>Called when the action is disabled, when the map is switched, and on focus loss —
    /// all three are moments where a half-finished hold must not complete on return.</remarks>
    void Reset();
}

/// <summary>The default: acts the moment the control is actuated.</summary>
/// <param name="pressPoint">How far from rest counts as pressed. Ignored for a digital control.</param>
/// <remarks>
///     What an action with no interactions written on it gets. A press performs and starts on the
///     same frame, and cancels on release, so <c>WasPressedThisFrame</c> and
///     <c>WasReleasedThisFrame</c> both mean what their names say.
/// </remarks>
public sealed class PressInteraction(float pressPoint = InputSettings.DefaultPressPoint) : IInputInteraction {
    bool pressed;

    /// <inheritdoc />
    public string Name => "press";

    /// <inheritdoc />
    public InputInteractionResult Process(in InputInteractionSample sample) {
        var actuated = sample.Magnitude >= pressPoint;

        if (actuated == pressed) {
            return InputInteractionResult.None;
        }

        pressed = actuated;

        return actuated
            ? InputInteractionResult.Started | InputInteractionResult.Performed
            : InputInteractionResult.Canceled;
    }

    /// <inheritdoc />
    public void Reset() => pressed = false;
}

/// <summary>Acts once the control has been held for long enough, and not before.</summary>
/// <param name="duration">How long, in seconds.</param>
/// <param name="pressPoint">How far from rest counts as pressed.</param>
/// <remarks>
///     Releasing early cancels, which is what lets a hold-to-confirm dialog be backed out of.
///     Releasing after it has performed also cancels, because the action has ended — a listener that
///     wants "still held" reads <c>IsPressed</c>.
/// </remarks>
public sealed class HoldInteraction(
    float duration = 0.4f,
    float pressPoint = InputSettings.DefaultPressPoint
) : IInputInteraction {
    double startedAt;
    bool holding;
    bool performed;

    /// <inheritdoc />
    public string Name => "hold";

    /// <inheritdoc />
    public InputInteractionResult Process(in InputInteractionSample sample) {
        var actuated = sample.Magnitude >= pressPoint;

        if (!holding) {
            if (!actuated) {
                return InputInteractionResult.None;
            }

            holding = true;
            performed = false;
            startedAt = sample.Time;
            return InputInteractionResult.Started;
        }

        if (!actuated) {
            holding = false;
            return InputInteractionResult.Canceled;
        }

        if (performed || sample.Time - startedAt < duration) {
            return InputInteractionResult.None;
        }

        performed = true;
        return InputInteractionResult.Performed;
    }

    /// <inheritdoc />
    public void Reset() {
        holding = false;
        performed = false;
    }
}

/// <summary>Acts on a release that came quickly enough after the press.</summary>
/// <param name="duration">The longest a tap may last, in seconds.</param>
/// <param name="pressPoint">How far from rest counts as pressed.</param>
/// <remarks>
///     Held longer than <paramref name="duration" /> and it cancels <em>while still held</em> rather
///     than on release, so a tap and a hold bound to the same control do not both fire when the
///     player meant the hold.
/// </remarks>
public sealed class TapInteraction(
    float duration = 0.2f,
    float pressPoint = InputSettings.DefaultPressPoint
) : IInputInteraction {
    double startedAt;
    bool holding;

    /// <inheritdoc />
    public string Name => "tap";

    /// <inheritdoc />
    public InputInteractionResult Process(in InputInteractionSample sample) {
        var actuated = sample.Magnitude >= pressPoint;

        if (!holding) {
            if (!actuated) {
                return InputInteractionResult.None;
            }

            holding = true;
            startedAt = sample.Time;
            return InputInteractionResult.Started;
        }

        if (actuated) {
            if (sample.Time - startedAt <= duration) {
                return InputInteractionResult.None;
            }

            holding = false;
            return InputInteractionResult.Canceled;
        }

        holding = false;
        return InputInteractionResult.Performed;
    }

    /// <inheritdoc />
    public void Reset() => holding = false;
}

/// <summary>Acts on a release that came only after the control was held for a while.</summary>
/// <param name="duration">How long it must be held before a release counts, in seconds.</param>
/// <param name="pressPoint">How far from rest counts as pressed.</param>
public sealed class SlowTapInteraction(
    float duration = 0.5f,
    float pressPoint = InputSettings.DefaultPressPoint
) : IInputInteraction {
    double startedAt;
    bool holding;

    /// <inheritdoc />
    public string Name => "slowTap";

    /// <inheritdoc />
    public InputInteractionResult Process(in InputInteractionSample sample) {
        var actuated = sample.Magnitude >= pressPoint;

        if (!holding) {
            if (!actuated) {
                return InputInteractionResult.None;
            }

            holding = true;
            startedAt = sample.Time;
            return InputInteractionResult.Started;
        }

        if (actuated) {
            return InputInteractionResult.None;
        }

        holding = false;

        return sample.Time - startedAt >= duration
            ? InputInteractionResult.Performed
            : InputInteractionResult.Canceled;
    }

    /// <inheritdoc />
    public void Reset() => holding = false;
}

/// <summary>Acts on a run of quick taps.</summary>
/// <param name="tapCount">How many.</param>
/// <param name="tapDuration">The longest one tap may last, in seconds.</param>
/// <param name="tapDelay">The longest gap between two taps, in seconds.</param>
/// <param name="pressPoint">How far from rest counts as pressed.</param>
/// <remarks>
///     A double-tap-to-dodge, and the reason the gap is a separate number from the tap length: a
///     player taps quickly and pauses between taps, and one timeout covering both is either too
///     strict to hit or too loose to distinguish from two separate presses.
/// </remarks>
public sealed class MultiTapInteraction(
    int tapCount = 2,
    float tapDuration = 0.2f,
    float tapDelay = 0.75f,
    float pressPoint = InputSettings.DefaultPressPoint
) : IInputInteraction {
    double pressedAt;
    double lastReleaseAt;
    int taps;
    bool holding;

    /// <inheritdoc />
    public string Name => "multiTap";

    /// <inheritdoc />
    public InputInteractionResult Process(in InputInteractionSample sample) {
        var actuated = sample.Magnitude >= pressPoint;

        if (!holding) {
            if (!actuated) {
                // The run expires on its own, without waiting for another press. Otherwise a single
                // tap from ten minutes ago would combine with the next one into a double-tap.
                if (taps > 0 && sample.Time - lastReleaseAt > tapDelay) {
                    taps = 0;
                    return InputInteractionResult.Canceled;
                }

                return InputInteractionResult.None;
            }

            if (taps > 0 && sample.Time - lastReleaseAt > tapDelay) {
                taps = 0;
            }

            holding = true;
            pressedAt = sample.Time;
            return taps == 0 ? InputInteractionResult.Started : InputInteractionResult.None;
        }

        if (actuated) {
            if (sample.Time - pressedAt <= tapDuration) {
                return InputInteractionResult.None;
            }

            // Held too long to be a tap. The whole run is abandoned rather than the last tap alone,
            // because a player who held the button was not trying to double-tap.
            holding = false;
            taps = 0;
            return InputInteractionResult.Canceled;
        }

        holding = false;
        lastReleaseAt = sample.Time;
        taps++;

        if (taps < tapCount) {
            return InputInteractionResult.None;
        }

        taps = 0;
        return InputInteractionResult.Performed;
    }

    /// <inheritdoc />
    public void Reset() {
        holding = false;
        taps = 0;
    }
}

/// <summary>Turns the interaction names in a <c>.vxinput</c> into interactions.</summary>
/// <remarks>
///     A table, for the same reason <see cref="InputProcessorFactory" /> has one, and with the same
///     <c>name(arg=value)</c> syntax. Unlike a processor, every call builds a <em>new</em> instance:
///     an interaction carries the clock of the binding it belongs to.
/// </remarks>
public static class InputInteractionFactory {
    static readonly Dictionary<string, Func<ProcessorArguments, IInputInteraction>> Factories =
        new(StringComparer.OrdinalIgnoreCase) {
            ["press"] = static arguments =>
                new PressInteraction(arguments.Number("pressPoint", InputSettings.DefaultPressPoint)),
            ["hold"] = static arguments =>
                new HoldInteraction(
                    arguments.Number("duration", 0.4f),
                    arguments.Number("pressPoint", InputSettings.DefaultPressPoint)
                ),
            ["tap"] = static arguments =>
                new TapInteraction(
                    arguments.Number("duration", 0.2f),
                    arguments.Number("pressPoint", InputSettings.DefaultPressPoint)
                ),
            ["slowTap"] = static arguments =>
                new SlowTapInteraction(
                    arguments.Number("duration", 0.5f),
                    arguments.Number("pressPoint", InputSettings.DefaultPressPoint)
                ),
            ["multiTap"] = static arguments =>
                new MultiTapInteraction(
                    arguments.Integer("tapCount", 2),
                    arguments.Number("tapDuration", 0.2f),
                    arguments.Number("tapDelay", 0.75f),
                    arguments.Number("pressPoint", InputSettings.DefaultPressPoint)
                )
        };

    /// <summary>Adds an interaction this build understands.</summary>
    /// <param name="name">What it is written as in a <c>.vxinput</c>.</param>
    /// <param name="factory">Builds one from its arguments.</param>
    public static void Register(string name, Func<ProcessorArguments, IInputInteraction> factory) {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(factory);
        Factories[name] = factory;
    }

    /// <summary>Whether a name is one this build has.</summary>
    /// <param name="name">The name.</param>
    public static bool IsKnown(string name) => Factories.ContainsKey(name);

    /// <summary>Reads an interaction list.</summary>
    /// <param name="text">The list, semicolon-separated, or <see langword="null" />.</param>
    /// <param name="unknown">Called with the name of anything this build does not have.</param>
    /// <returns>Fresh interactions, in the order they were written.</returns>
    public static IReadOnlyList<IInputInteraction> Parse(string? text, Action<string>? unknown = null) {
        if (string.IsNullOrWhiteSpace(text)) {
            return [];
        }

        var interactions = new List<IInputInteraction>();

        foreach (var term in text!.Split(';')) {
            var trimmed = term.Trim();

            if (trimmed.Length == 0) {
                continue;
            }

            var name = ProcessorArguments.Split(trimmed, out var arguments);

            if (Factories.TryGetValue(name, out var factory)) {
                interactions.Add(factory(arguments));
            } else {
                unknown?.Invoke(name);
            }
        }

        return interactions;
    }
}
