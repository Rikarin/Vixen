// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Audio.Parameters;
using Vixen.Core;

namespace Vixen.Audio.Assets;

/// <summary>One point on a curve, as a file declares it.</summary>
/// <remarks>
///     A record and not the runtime <see cref="AudioCurvePoint" /> struct, for the reason the whole
///     parallel model exists: an asset is read once by a tool that may know nothing about the runtime,
///     and the two are free to diverge — a point will grow a tangent long before the evaluator does.
/// </remarks>
[DataContract("AudioCurvePoint")]
public sealed record AudioCurvePointAsset {
    /// <summary>Where along the parameter's range, from 0 at its minimum to 1 at its maximum.</summary>
    public float Position { get; init; }

    /// <summary>What the curve is worth there, in the target's own unit.</summary>
    public float Value { get; init; }
}

/// <summary>A curve, as a file declares it.</summary>
[DataContract("AudioCurve")]
public sealed record AudioCurveAsset {
    /// <summary>Its points. Sorted on the way in, so the order they were drawn in does not matter.</summary>
    public AudioCurvePointAsset[] Points { get; init; } = [];

    /// <summary>How it gets between them.</summary>
    public AudioCurveInterpolation Interpolation { get; init; } = AudioCurveInterpolation.Linear;

    /// <summary>The curve this describes.</summary>
    /// <returns>The curve.</returns>
    public AudioCurve ToCurve() {
        var points = new AudioCurvePoint[Points.Length];

        for (var i = 0; i < Points.Length; i++) {
            points[i] = new(Points[i].Position, Points[i].Value);
        }

        return new(points, Interpolation);
    }
}

/// <summary>One mapping from a parameter onto something audible, as a file declares it.</summary>
[DataContract("AudioAutomation")]
public sealed record AudioAutomationAsset {
    /// <summary>What it drives.</summary>
    public AudioParameterTarget Target { get; init; }

    /// <summary>How the parameter's range maps onto that target's unit.</summary>
    public AudioCurveAsset Curve { get; init; } = new();
}

/// <summary>A parameter, as a file declares it.</summary>
/// <remarks>
///     The whole of what a sound designer needs a programmer for, moved into content. Gameplay writes
///     a number by name; what that number does is here.
/// </remarks>
[DataContract("AudioParameter")]
public sealed record AudioParameterAsset {
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

    /// <summary>Something the engine works out, instead of something gameplay sets.</summary>
    public AudioBuiltinParameter Builtin { get; init; }

    /// <summary>What moving it does.</summary>
    public AudioAutomationAsset[] Automation { get; init; } = [];

    /// <summary>The definition this describes.</summary>
    /// <returns>The definition.</returns>
    public AudioParameterDefinition ToDefinition() {
        var automation = new AudioAutomation[Automation.Length];

        for (var i = 0; i < Automation.Length; i++) {
            automation[i] = new(Automation[i].Target, Automation[i].Curve.ToCurve());
        }

        return new() {
            Name = Name,
            Minimum = Minimum,
            Maximum = Maximum,
            Default = Default,
            SeekSeconds = SeekSeconds,
            Builtin = Builtin,
            Automation = automation
        };
    }
}

/// <summary>One mapping from an engine-wide parameter onto part of the mix, as a file declares it.</summary>
[DataContract("AudioBusAutomation")]
public sealed record AudioBusAutomationAsset {
    /// <summary>Which bus, by name.</summary>
    public string Bus { get; init; } = string.Empty;

    /// <summary>What on it.</summary>
    public AudioBusParameterTarget Target { get; init; }

    /// <summary>For a send, the bus sent to.</summary>
    public string Send { get; init; } = string.Empty;

    /// <summary>For an effect property, which insert on the bus.</summary>
    public int Effect { get; init; }

    /// <summary>For an effect property, the knob's own name. Matched exactly.</summary>
    public string Property { get; init; } = string.Empty;

    /// <summary>How the parameter's range maps onto the target's unit.</summary>
    public AudioCurveAsset Curve { get; init; } = new();
}

/// <summary>An engine-wide parameter, as a file declares it.</summary>
/// <remarks>
///     Lives on the mixer asset rather than on an event, because what it drives is the mix: a bus, a
///     send, a knob on an insert. An event's parameters drive the sound and are declared there.
/// </remarks>
[DataContract("AudioBusParameter")]
public sealed record AudioBusParameterAsset {
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
    public AudioBusAutomationAsset[] Automation { get; init; } = [];

    /// <summary>The definition this describes.</summary>
    /// <returns>The definition.</returns>
    public AudioBusParameterDefinition ToDefinition() {
        var automation = new AudioBusAutomation[Automation.Length];

        for (var i = 0; i < Automation.Length; i++) {
            var entry = Automation[i];

            automation[i] = new() {
                Bus = entry.Bus,
                Target = entry.Target,
                Send = entry.Send,
                Effect = entry.Effect,
                Property = entry.Property,
                Curve = entry.Curve.ToCurve()
            };
        }

        return new() {
            Name = Name,
            Minimum = Minimum,
            Maximum = Maximum,
            Default = Default,
            SeekSeconds = SeekSeconds,
            Automation = automation
        };
    }
}
