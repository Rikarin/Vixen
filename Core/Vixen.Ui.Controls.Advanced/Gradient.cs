// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Ui.Controls.Advanced;

/// <summary>Which space a gradient's colours are mixed in.</summary>
/// <remarks>
///     ⚠ <b>Three answers because there are three right ones.</b> sRGB is what a designer's tool
///     showed them and what a CSS gradient does; linear is what light actually does and what a
///     renderer wants for anything physical; Oklab is what looks like the fade the designer drew.
///     They disagree visibly — blue to yellow goes through grey in linear, through green-ish in
///     sRGB, and through neither in Oklab — so a gradient that did not record which one it meant
///     could not be reproduced.
/// </remarks>
public enum GradientInterpolation : byte {
    /// <summary>Mixed in the encoded values, which is what a CSS gradient does.</summary>
    Srgb,

    /// <summary>Mixed in linear light, which is what a renderer wants.</summary>
    Linear,

    /// <summary>Mixed perceptually. The one that looks like the fade somebody drew.</summary>
    Oklab
}

/// <summary>A colour at a place along a gradient.</summary>
public sealed class GradientColorStop {
    /// <summary>Creates a stop.</summary>
    /// <param name="position">Where, from zero to one.</param>
    /// <param name="color">What colour. Its alpha is ignored — that is the alpha stops' business.</param>
    public GradientColorStop(float position, Color4 color) {
        Position = position;
        Color = color;
    }

    /// <summary>Where, from zero to one.</summary>
    public float Position { get; set; }

    /// <summary>What colour.</summary>
    public Color4 Color { get; set; }
}

/// <summary>An opacity at a place along a gradient.</summary>
public sealed class GradientAlphaStop {
    /// <summary>Creates a stop.</summary>
    /// <param name="position">Where.</param>
    /// <param name="alpha">How opaque.</param>
    public GradientAlphaStop(float position, float alpha) {
        Position = position;
        Alpha = alpha;
    }

    /// <summary>Where, from zero to one.</summary>
    public float Position { get; set; }

    /// <summary>How opaque, from zero to one.</summary>
    public float Alpha { get; set; }
}

/// <summary>Colour and opacity along a line, as two independent lists of stops.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Colour and alpha are separate lists, and that is the whole design.</b> A particle
///         that fades out at the end has one alpha stop; sharing one list would mean duplicating
///         every colour stop to carry the alpha, and then editing a colour in the middle would mean
///         remembering to keep two things in step. Every tool that has tried the single list has
///         ended up here.
///     </para>
///     <para>
///         Empty means transparent black, one stop means that stop everywhere, and outside the first
///         and last the ends hold — the same clamp <see cref="AnimationCurve" /> makes and for the
///         same reason.
///     </para>
/// </remarks>
public sealed class Gradient {
    readonly List<GradientColorStop> colors = [];
    readonly List<GradientAlphaStop> alphas = [];

    /// <summary>Creates an empty gradient.</summary>
    public Gradient() {
    }

    /// <summary>Creates a gradient between two colours.</summary>
    /// <param name="from">The colour at zero.</param>
    /// <param name="to">The colour at one.</param>
    public Gradient(Color4 from, Color4 to) {
        colors.Add(new GradientColorStop(0f, from));
        colors.Add(new GradientColorStop(1f, to));

        alphas.Add(new GradientAlphaStop(0f, from.A));
        alphas.Add(new GradientAlphaStop(1f, to.A));
    }

    /// <summary>The colour stops, in order.</summary>
    public IReadOnlyList<GradientColorStop> ColorStops => colors;

    /// <summary>The alpha stops, in order.</summary>
    public IReadOnlyList<GradientAlphaStop> AlphaStops => alphas;

    /// <summary>Which space the colours are mixed in.</summary>
    public GradientInterpolation Interpolation {
        get;
        set {
            if (field == value) {
                return;
            }

            field = value;
            Changed?.Invoke(this);
        }
    }

    /// <summary>How many stops of either kind a gradient may have.</summary>
    /// <remarks>
    ///     A limit rather than none, because a gradient is very often packed into a texture row or a
    ///     fixed-size struct by whatever consumes it, and eight of each is what every engine that
    ///     does that has settled on.
    /// </remarks>
    public const int MaximumStops = 8;

    /// <summary>Raised after anything changes.</summary>
    public event Action<Gradient>? Changed;

    /// <summary>Adds a colour stop.</summary>
    /// <param name="position">Where.</param>
    /// <param name="color">What.</param>
    /// <returns>The stop, or <c>null</c> if there is no room.</returns>
    public GradientColorStop? AddColorStop(float position, Color4 color) {
        if (colors.Count >= MaximumStops) {
            return null;
        }

        var stop = new GradientColorStop(Math.Clamp(position, 0f, 1f), color);
        colors.Add(stop);

        Sort();
        return stop;
    }

    /// <summary>Adds an alpha stop.</summary>
    /// <param name="position">Where.</param>
    /// <param name="alpha">How opaque.</param>
    /// <returns>The stop, or <c>null</c> if there is no room.</returns>
    public GradientAlphaStop? AddAlphaStop(float position, float alpha) {
        if (alphas.Count >= MaximumStops) {
            return null;
        }

        var stop = new GradientAlphaStop(Math.Clamp(position, 0f, 1f), Math.Clamp(alpha, 0f, 1f));
        alphas.Add(stop);

        Sort();
        return stop;
    }

    /// <summary>Removes a colour stop, unless it is the last one.</summary>
    /// <param name="stop">The stop.</param>
    /// <returns>Whether it went.</returns>
    /// <remarks>
    ///     ⚠ <b>The last one stays.</b> A gradient with no colour stops has no colour, and an editor
    ///     that let a user delete their way to one leaves them with a black bar and no way back.
    /// </remarks>
    public bool Remove(GradientColorStop stop) {
        if (colors.Count <= 1 || !colors.Remove(stop)) {
            return false;
        }

        Sort();
        return true;
    }

    /// <summary>Removes an alpha stop, unless it is the last one.</summary>
    /// <param name="stop">The stop.</param>
    /// <returns>Whether it went.</returns>
    public bool Remove(GradientAlphaStop stop) {
        if (alphas.Count <= 1 || !alphas.Remove(stop)) {
            return false;
        }

        Sort();
        return true;
    }

    /// <summary>Moves a stop and puts the list back in order.</summary>
    /// <param name="stop">The stop.</param>
    /// <param name="position">Where to.</param>
    public void Move(GradientColorStop stop, float position) {
        ArgumentNullException.ThrowIfNull(stop);

        stop.Position = Math.Clamp(position, 0f, 1f);
        Sort();
    }

    /// <summary>Ditto.</summary>
    /// <param name="stop">The stop.</param>
    /// <param name="position">Where to.</param>
    public void Move(GradientAlphaStop stop, float position) {
        ArgumentNullException.ThrowIfNull(stop);

        stop.Position = Math.Clamp(position, 0f, 1f);
        Sort();
    }

    /// <summary>Tells subscribers something changed, for an edit made through a stop directly.</summary>
    public void Touch() => Changed?.Invoke(this);

    /// <summary>The colour at a place, with its alpha.</summary>
    /// <param name="position">Where, from zero to one.</param>
    /// <returns>The colour.</returns>
    /// <remarks>
    ///     A gradient with no colour stops is transparent black rather than opaque black: it has no
    ///     colour at all, and reporting the alpha of a list that is also empty as one would make an
    ///     empty gradient paint over whatever is under it.
    /// </remarks>
    public Color4 Evaluate(float position) {
        if (colors.Count == 0) {
            return default;
        }

        var rgb = Rgb(position);
        return new Color4(rgb.R, rgb.G, rgb.B, Alpha(position));
    }

    Color4 Rgb(float position) {
        if (colors.Count == 0) {
            return default;
        }

        if (colors.Count == 1 || position <= colors[0].Position) {
            return colors[0].Color;
        }

        if (position >= colors[^1].Position) {
            return colors[^1].Color;
        }

        var index = 0;

        while (index < colors.Count - 2 && colors[index + 1].Position <= position) {
            index++;
        }

        var from = colors[index];
        var to = colors[index + 1];
        var span = to.Position - from.Position;
        var t = span <= 0f ? 1f : (position - from.Position) / span;

        return Mix(from.Color, to.Color, t);
    }

    float Alpha(float position) {
        if (alphas.Count == 0) {
            return 1f;
        }

        if (alphas.Count == 1 || position <= alphas[0].Position) {
            return alphas[0].Alpha;
        }

        if (position >= alphas[^1].Position) {
            return alphas[^1].Alpha;
        }

        var index = 0;

        while (index < alphas.Count - 2 && alphas[index + 1].Position <= position) {
            index++;
        }

        var from = alphas[index];
        var to = alphas[index + 1];
        var span = to.Position - from.Position;

        // ⚠ Alpha is always mixed straight, whatever the colour space is. Opacity is coverage rather
        // than light, and a perceptual curve applied to it would make a linear fade look like it
        // pauses in the middle.
        return span <= 0f ? to.Alpha : from.Alpha + ((to.Alpha - from.Alpha) * ((position - from.Position) / span));
    }

    Color4 Mix(Color4 from, Color4 to, float t) =>
        Interpolation switch {
            GradientInterpolation.Linear => FromLinear(
                Vector3.Lerp(ToLinear(from), ToLinear(to), t)
            ),
            GradientInterpolation.Oklab => FromLinear(
                Oklab.Lerp(Oklab.FromLinear(ToLinear(from)), Oklab.FromLinear(ToLinear(to)), t).ToLinear()
            ),
            _ => new Color4(
                from.R + ((to.R - from.R) * t),
                from.G + ((to.G - from.G) * t),
                from.B + ((to.B - from.B) * t),
                1f
            )
        };

    static Vector3 ToLinear(Color4 color) =>
        new(ColorSpace.SrgbToLinear(color.R), ColorSpace.SrgbToLinear(color.G), ColorSpace.SrgbToLinear(color.B));

    static Color4 FromLinear(Vector3 linear) =>
        new(
            ColorSpace.LinearToSrgb(Math.Clamp(linear.X, 0f, 1f)),
            ColorSpace.LinearToSrgb(Math.Clamp(linear.Y, 0f, 1f)),
            ColorSpace.LinearToSrgb(Math.Clamp(linear.Z, 0f, 1f)),
            1f
        );

    void Sort() {
        colors.Sort(static (left, right) => left.Position.CompareTo(right.Position));
        alphas.Sort(static (left, right) => left.Position.CompareTo(right.Position));

        Changed?.Invoke(this);
    }
}
