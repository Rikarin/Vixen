// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;

namespace Vixen.Ui.Text;

/// <summary>One axis a variable font can be instanced along.</summary>
/// <param name="Tag">The four-character tag — <c>wght</c>, <c>wdth</c>, <c>slnt</c>, <c>opsz</c>, <c>ital</c>.</param>
/// <param name="Minimum">The smallest value an instance may ask for, in user units.</param>
/// <param name="Default">What the font is when nobody asks — the instance every non-variable reader gets.</param>
/// <param name="Maximum">The largest.</param>
public readonly record struct FontAxis(string Tag, float Minimum, float Default, float Maximum) {
    /// <summary>Clamps a user value into the axis's own range.</summary>
    public float Clamp(float value) => Math.Clamp(value, Minimum, Maximum);

    /// <summary>Maps a user value onto the −1…0…+1 grid every variation table is written in.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The two halves are scaled separately, and that is the whole subtlety.</b> An axis
    ///         is not a line from minimum to maximum with the default somewhere on it — it is two
    ///         segments joined at the default, each mapped onto a unit interval. A <c>wght</c> axis of
    ///         100…400…900 puts 250 at −0.5, not at −0.1875: half way from the default to the
    ///         minimum, not a fraction of the whole span. Interpolating across the whole range instead
    ///         is the mistake that makes a font's own named instances land slightly off their own
    ///         designs, which reads as the font being badly made.
    ///     </para>
    ///     <para>
    ///         A degenerate axis — minimum equal to default, or default equal to maximum — is a real
    ///         thing fonts ship, and it answers zero on that side rather than dividing by it.
    ///     </para>
    /// </remarks>
    public float Normalize(float value) {
        var clamped = Clamp(value);

        if (clamped < Default) {
            var span = Default - Minimum;
            return span > 0f ? (clamped - Default) / span : 0f;
        }

        if (clamped > Default) {
            var span = Maximum - Default;
            return span > 0f ? (clamped - Default) / span : 0f;
        }

        return 0f;
    }
}

/// <summary>One <c>avar</c> segment map: a piecewise-linear warp of one normalised axis.</summary>
/// <remarks>
///     ⚠ <b>Applied after normalisation and before anything reads a delta.</b> <c>avar</c> exists
///     because a designer's idea of "half way to bold" is rarely the arithmetic middle — the table
///     lets the font say that normalised 0.5 should behave as 0.62. Skipping it leaves every
///     intermediate instance subtly wrong while both ends stay exactly right, which is the hardest
///     kind of wrong to notice.
/// </remarks>
public readonly record struct AxisSegmentMap(ImmutableArray<(float From, float To)> Pairs) {
    /// <summary>Warps one normalised coordinate.</summary>
    public float Apply(float coordinate) {
        if (Pairs.IsDefaultOrEmpty) {
            return coordinate;
        }

        for (var i = 1; i < Pairs.Length; i++) {
            var (fromLow, toLow) = Pairs[i - 1];
            var (fromHigh, toHigh) = Pairs[i];

            if (coordinate > fromHigh) {
                continue;
            }

            if (coordinate < fromLow) {
                break;
            }

            var span = fromHigh - fromLow;
            return span > 0f
                ? toLow + ((toHigh - toLow) * (coordinate - fromLow) / span)
                : toLow;
        }

        // Outside every segment the map says nothing, and saying nothing means the identity rather
        // than the nearest end — a coordinate the font did not map is one it had no opinion about.
        return coordinate;
    }
}

/// <summary>Where along its axes a variable font is being read.</summary>
/// <remarks>
///     <para>
///         <b>Normalised coordinates, in axis order, and immutable.</b> Normalised because that is
///         what every variation table is written in and converting once is cheaper than converting per
///         glyph; in axis order because the tables index axes positionally rather than by tag; and
///         immutable because this is about to become part of two cache keys, and a mutable key is a
///         cache that answers questions about a state it is no longer in.
///     </para>
///     <para>
///         ⚠ <b><see cref="None" /> is not "the default instance", it is "not a variable font".</b>
///         They produce identical outlines and must not produce identical cache entries: a font with
///         no <c>fvar</c> has one instance for ever, and a variable font sitting at its defaults is
///         one axis-set away from being something else. Keeping them distinct costs a field and
///         removes a class of stale-entry bug that only appears once something animates an axis.
///     </para>
/// </remarks>
public sealed class FontVariation : IEquatable<FontVariation> {
    /// <summary>A font that is not variable, or is not being varied.</summary>
    public static readonly FontVariation None = new([]);

    readonly int hash;

    /// <summary>Creates a position from already-normalised coordinates.</summary>
    /// <param name="coordinates">One per axis, in the font's axis order, each −1…1.</param>
    public FontVariation(ImmutableArray<float> coordinates) {
        Coordinates = coordinates.IsDefault ? [] : coordinates;

        var accumulated = new HashCode();

        foreach (var coordinate in Coordinates) {
            accumulated.Add(coordinate);
        }

        hash = accumulated.ToHashCode();
    }

    /// <summary>The normalised coordinates, one per axis.</summary>
    public ImmutableArray<float> Coordinates { get; }

    /// <summary>Whether this is the "not varied" position.</summary>
    public bool IsNone => Coordinates.IsEmpty;

    /// <inheritdoc />
    public bool Equals(FontVariation? other) {
        if (ReferenceEquals(this, other)) {
            return true;
        }

        if (other is null || other.Coordinates.Length != Coordinates.Length) {
            return false;
        }

        for (var i = 0; i < Coordinates.Length; i++) {
            // ⚠ Bitwise, not tolerant. These are keys: two positions that differ by a float epsilon
            // must miss rather than share, because a tolerant key makes a cache that returns an
            // outline for a *nearby* instance and there is no bound on how wrong that looks.
            if (!Coordinates[i].Equals(other.Coordinates[i])) {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as FontVariation);

    /// <inheritdoc />
    public override int GetHashCode() => hash;

    /// <summary>Normalises a set of user-space axis values against a font's axes and <c>avar</c>.</summary>
    /// <param name="axes">The font's axes, in order.</param>
    /// <param name="maps">Its <c>avar</c> segment maps, one per axis, or empty for none.</param>
    /// <param name="values">What the caller asked for, by tag. Axes it does not name keep their default.</param>
    /// <returns>The position.</returns>
    public static FontVariation Create(
        ImmutableArray<FontAxis> axes,
        ImmutableArray<AxisSegmentMap> maps,
        IReadOnlyDictionary<string, float> values
    ) {
        ArgumentNullException.ThrowIfNull(values);

        if (axes.IsDefaultOrEmpty) {
            return None;
        }

        var coordinates = ImmutableArray.CreateBuilder<float>(axes.Length);

        for (var i = 0; i < axes.Length; i++) {
            var axis = axes[i];
            var coordinate = axis.Normalize(values.TryGetValue(axis.Tag, out var asked) ? asked : axis.Default);

            if (!maps.IsDefaultOrEmpty && i < maps.Length) {
                coordinate = maps[i].Apply(coordinate);
            }

            coordinates.Add(coordinate);
        }

        return new FontVariation(coordinates.MoveToImmutable());
    }
}
