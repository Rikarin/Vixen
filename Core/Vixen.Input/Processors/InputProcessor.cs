// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Core.Mathematics;

namespace Vixen.Input;

/// <summary>Something that reshapes a binding's value on its way to the action.</summary>
/// <remarks>
///     <para>
///         One method over a <see cref="Vector2" /> rather than one per shape. A scalar binding is a
///         vector whose Y is zero, so a processor written once works on both, and the alternative —
///         <c>ProcessScalar</c> plus <c>ProcessVector</c> — makes every implementation write the
///         one-dimensional case twice and lets the two disagree.
///     </para>
///     <para>
///         Processors run in the order they are written, and that order is meaningful: an invert
///         after a clamp is not a clamp after an invert.
///     </para>
/// </remarks>
public interface IInputProcessor {
    /// <summary>The name it is written under in a <c>.vxinput</c>.</summary>
    string Name { get; }

    /// <summary>Reshapes a value.</summary>
    /// <param name="value">What the binding read. A scalar arrives in <c>X</c> with <c>Y</c> zero.</param>
    /// <param name="dimensions">How many components are real: <c>1</c> or <c>2</c>.</param>
    /// <returns>The reshaped value.</returns>
    Vector2 Process(Vector2 value, int dimensions);
}

/// <summary>
///     Cuts out the centre of a stick's range and rescales what is left, radially.
/// </summary>
/// <param name="Min">How far from centre the live range starts.</param>
/// <param name="Max">Where it saturates — below <c>1</c> because a real stick never reaches its
/// corners.</param>
/// <remarks>
///     <para>
///         <b>Radial, on the magnitude, not per axis.</b> An axial dead zone applied to each
///         component separately makes a stick pushed diagonally at half deflection read as zero on
///         both axes, and makes the live region a square with the corners of the circle cut off —
///         which is why diagonal movement feels wrong in games that get this wrong. The defaults are
///         Unity's, which are the values a decade of shipped games converged on.
///     </para>
///     <para>
///         On a one-dimensional binding it degenerates to the axial form, which is correct: a trigger
///         has no direction to preserve.
///     </para>
/// </remarks>
public sealed record StickDeadzoneProcessor(float Min = 0.125f, float Max = 0.925f) : IInputProcessor {
    /// <inheritdoc />
    public string Name => "stickDeadzone";

    /// <inheritdoc />
    public Vector2 Process(Vector2 value, int dimensions) {
        if (dimensions < 2) {
            var scalar = Rescale(Math.Abs(value.X));
            return new(value.X < 0f ? -scalar : scalar, 0f);
        }

        var magnitude = value.Length();

        if (magnitude <= float.Epsilon) {
            return Vector2.Zero;
        }

        var scaled = Rescale(magnitude);
        return scaled <= 0f ? Vector2.Zero : value * (scaled / magnitude);
    }

    float Rescale(float magnitude) {
        if (magnitude < Min) {
            return 0f;
        }

        // Rescaled rather than merely clipped: a stick that jumped from 0 to 0.125 the moment it left
        // the dead zone would be a visible step in the character's speed.
        var range = Max - Min;
        return range <= float.Epsilon ? 1f : Math.Min((magnitude - Min) / range, 1f);
    }
}

/// <summary>Negates one or both components.</summary>
/// <param name="X">Whether to negate the horizontal component.</param>
/// <param name="Y">Whether to negate the vertical component.</param>
/// <remarks>
///     What "invert Y" in an options screen is. Defaults to both, because a binding that writes
///     <c>invert</c> with no arguments means the whole value.
/// </remarks>
public sealed record InvertProcessor(bool X = true, bool Y = true) : IInputProcessor {
    /// <inheritdoc />
    public string Name => "invert";

    /// <inheritdoc />
    public Vector2 Process(Vector2 value, int dimensions) =>
        new(X ? -value.X : value.X, Y ? -value.Y : value.Y);
}

/// <summary>Multiplies by a constant.</summary>
/// <param name="Factor">What to multiply by.</param>
/// <remarks>Mouse sensitivity, in one processor. Applied to both components.</remarks>
public sealed record ScaleProcessor(float Factor = 1f) : IInputProcessor {
    /// <inheritdoc />
    public string Name => "scale";

    /// <inheritdoc />
    public Vector2 Process(Vector2 value, int dimensions) => value * Factor;
}

/// <summary>Holds the value inside a range.</summary>
/// <param name="Min">The floor.</param>
/// <param name="Max">The ceiling.</param>
public sealed record ClampProcessor(float Min = 0f, float Max = 1f) : IInputProcessor {
    /// <inheritdoc />
    public string Name => "clamp";

    /// <inheritdoc />
    public Vector2 Process(Vector2 value, int dimensions) =>
        new(Math.Clamp(value.X, Min, Max), dimensions < 2 ? value.Y : Math.Clamp(value.Y, Min, Max));
}

/// <summary>Scales the value to unit length, leaving anything shorter alone.</summary>
/// <remarks>
///     What keeps diagonal WASD movement from being forty per cent faster than cardinal movement,
///     and the reason it does <em>not</em> lengthen a short vector: a stick held gently must stay
///     gentle, which is the difference between this and a plain <c>Normalize</c>.
/// </remarks>
public sealed record NormalizeProcessor : IInputProcessor {
    /// <inheritdoc />
    public string Name => "normalize";

    /// <inheritdoc />
    public Vector2 Process(Vector2 value, int dimensions) {
        if (dimensions < 2) {
            return new(Math.Clamp(value.X, -1f, 1f), value.Y);
        }

        var magnitude = value.Length();
        return magnitude > 1f ? value * (1f / magnitude) : value;
    }
}

/// <summary>
///     Turns the processor names in a <c>.vxinput</c> into processors, and back.
/// </summary>
/// <remarks>
///     <para>
///         A table rather than an assembly scan, for the reason
///         <c>Vixen.Editor.Assets.BuiltInImporters</c> gives about importers: a scan reads metadata a
///         trimmed publish has already deleted, and would make "which processors does this build
///         have" a question with a different answer in the editor and in the player. A game adds its
///         own with <see cref="Register" /> at boot.
///     </para>
///     <para>
///         The syntax is <c>name(arg=value,arg=value)</c>, semicolon-separated —
///         <c>stickDeadzone(min=0.2);invert(y=true)</c>. Arguments are named rather than positional
///         because a binding that reads <c>clamp(0,1)</c> tells a reviewer nothing about which is
///         which.
///     </para>
/// </remarks>
public static class InputProcessorFactory {
    static readonly Dictionary<string, Func<ProcessorArguments, IInputProcessor>> Factories =
        new(StringComparer.OrdinalIgnoreCase) {
            ["stickDeadzone"] = static arguments =>
                new StickDeadzoneProcessor(arguments.Number("min", 0.125f), arguments.Number("max", 0.925f)),
            ["deadzone"] = static arguments =>
                new StickDeadzoneProcessor(arguments.Number("min", 0.125f), arguments.Number("max", 0.925f)),
            ["invert"] = static arguments =>
                new InvertProcessor(arguments.Flag("x", true), arguments.Flag("y", true)),
            ["scale"] = static arguments => new ScaleProcessor(arguments.Number("factor", 1f)),
            ["clamp"] = static arguments =>
                new ClampProcessor(arguments.Number("min", 0f), arguments.Number("max", 1f)),
            ["normalize"] = static _ => new NormalizeProcessor()
        };

    /// <summary>Adds a processor this build understands.</summary>
    /// <param name="name">What it is written as in a <c>.vxinput</c>.</param>
    /// <param name="factory">Builds one from its arguments.</param>
    /// <remarks>Called at boot, before any asset is loaded. Replacing a built-in is allowed and is
    /// how a game changes what <c>stickDeadzone</c> means for all of its bindings at once.</remarks>
    public static void Register(string name, Func<ProcessorArguments, IInputProcessor> factory) {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(factory);
        Factories[name] = factory;
    }

    /// <summary>Whether a name is one this build has.</summary>
    /// <param name="name">The name.</param>
    public static bool IsKnown(string name) => Factories.ContainsKey(name);

    /// <summary>Reads a processor list.</summary>
    /// <param name="text">The list, semicolon-separated, or <see langword="null" />.</param>
    /// <param name="unknown">Called with the name of anything this build does not have.</param>
    /// <returns>The processors, in the order they were written.</returns>
    public static IReadOnlyList<IInputProcessor> Parse(string? text, Action<string>? unknown = null) {
        if (string.IsNullOrWhiteSpace(text)) {
            return [];
        }

        var processors = new List<IInputProcessor>();

        foreach (var term in text!.Split(';')) {
            var trimmed = term.Trim();

            if (trimmed.Length == 0) {
                continue;
            }

            var name = ProcessorArguments.Split(trimmed, out var arguments);

            if (Factories.TryGetValue(name, out var factory)) {
                processors.Add(factory(arguments));
            } else {
                unknown?.Invoke(name);
            }
        }

        return processors;
    }

    /// <summary>Applies a list of processors in order.</summary>
    /// <param name="processors">The processors.</param>
    /// <param name="value">The value.</param>
    /// <param name="dimensions">How many components are real.</param>
    /// <returns>The reshaped value.</returns>
    public static Vector2 Apply(IReadOnlyList<IInputProcessor> processors, Vector2 value, int dimensions) {
        ArgumentNullException.ThrowIfNull(processors);

        for (var index = 0; index < processors.Count; index++) {
            value = processors[index].Process(value, dimensions);
        }

        return value;
    }
}

/// <summary>The <c>name=value</c> pairs inside a processor's or an interaction's parentheses.</summary>
/// <remarks>
///     Shared by both because the syntax is the same one, and having two parsers for
///     <c>hold(duration=0.4)</c> and <c>scale(factor=2)</c> would let them drift.
/// </remarks>
public sealed class ProcessorArguments {
    readonly Dictionary<string, string> values;

    ProcessorArguments(Dictionary<string, string> values) => this.values = values;

    /// <summary>No arguments at all.</summary>
    public static ProcessorArguments Empty { get; } = new(new(StringComparer.OrdinalIgnoreCase));

    /// <summary>Reads a number, or a default.</summary>
    /// <param name="name">The argument's name.</param>
    /// <param name="fallback">What to use when it is absent or unreadable.</param>
    public float Number(string name, float fallback) =>
        values.TryGetValue(name, out var text)
        && float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;

    /// <summary>Reads an integer, or a default.</summary>
    /// <param name="name">The argument's name.</param>
    /// <param name="fallback">What to use when it is absent or unreadable.</param>
    public int Integer(string name, int fallback) =>
        values.TryGetValue(name, out var text)
        && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;

    /// <summary>Reads a flag, or a default.</summary>
    /// <param name="name">The argument's name.</param>
    /// <param name="fallback">What to use when it is absent.</param>
    /// <remarks>A bare name with no <c>=</c> — <c>invert(y)</c> — is <see langword="true" />, which
    /// is how a flag reads naturally.</remarks>
    public bool Flag(string name, bool fallback) {
        if (!values.TryGetValue(name, out var text)) {
            return fallback;
        }

        return text.Length == 0
            || string.Equals(text, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(text, "1", StringComparison.Ordinal);
    }

    /// <summary>Splits <c>name(a=1,b=2)</c> into its name and its arguments.</summary>
    /// <param name="term">The term.</param>
    /// <param name="arguments">The arguments, empty when there are no parentheses.</param>
    /// <returns>The name.</returns>
    public static string Split(string term, out ProcessorArguments arguments) {
        ArgumentNullException.ThrowIfNull(term);

        var open = term.IndexOf('(');

        if (open < 0) {
            arguments = Empty;
            return term.Trim();
        }

        var close = term.LastIndexOf(')');
        var body = close > open ? term[(open + 1)..close] : term[(open + 1)..];
        var parsed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var pair in body.Split(',')) {
            var separator = pair.IndexOf('=');

            if (separator < 0) {
                var flag = pair.Trim();

                if (flag.Length > 0) {
                    parsed[flag] = string.Empty;
                }

                continue;
            }

            parsed[pair[..separator].Trim()] = pair[(separator + 1)..].Trim();
        }

        arguments = new(parsed);
        return term[..open].Trim();
    }
}
