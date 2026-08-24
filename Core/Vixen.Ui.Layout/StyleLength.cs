// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Vixen.Ui.Layout;

/// <summary>A length as it was written: a number and what kind of number it is.</summary>
/// <remarks>
///     Eight bytes, and there are around fifty of them per node, which is most of what a style
///     costs. Keeping the unit here rather than in a parallel array is deliberate: every read of a
///     length reads both halves, and splitting them would double the cache lines a layout pass
///     touches for no gain.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public readonly struct StyleLength : IEquatable<StyleLength> {
    /// <summary>Nothing was set.</summary>
    public static readonly StyleLength Undefined;

    /// <summary>The algorithm decides.</summary>
    public static readonly StyleLength Auto = new(0f, LayoutUnit.Auto);

    /// <summary>Zero points.</summary>
    public static readonly StyleLength Zero = new(0f, LayoutUnit.Point);

    /// <summary>The number. Meaningless unless <see cref="Unit" /> says otherwise.</summary>
    public readonly float Value;

    /// <summary>What kind of length this is.</summary>
    public readonly LayoutUnit Unit;

    /// <summary>Creates a length.</summary>
    /// <param name="value">The number.</param>
    /// <param name="unit">What kind of number it is.</param>
    public StyleLength(float value, LayoutUnit unit) {
        // A NaN with a unit is a contradiction that would otherwise propagate silently into a
        // resolved size, where it is very hard to trace back to the assignment that made it.
        if (float.IsNaN(value) && unit is LayoutUnit.Point or LayoutUnit.Percent) {
            Value = 0f;
            Unit = LayoutUnit.Undefined;
            return;
        }

        Value = value;
        Unit = unit;
    }

    /// <summary>An absolute length.</summary>
    /// <param name="value">The number of points.</param>
    /// <returns>The length.</returns>
    public static StyleLength Points(float value) => new(value, LayoutUnit.Point);

    /// <summary>A fraction of the containing block.</summary>
    /// <param name="value">The percentage, where 100 means all of it.</param>
    /// <returns>The length.</returns>
    public static StyleLength Percent(float value) => new(value, LayoutUnit.Percent);

    /// <summary>A keyword length with no number of its own.</summary>
    /// <param name="unit">Which keyword.</param>
    /// <returns>The length.</returns>
    public static StyleLength Keyword(LayoutUnit unit) => new(0f, unit);

    /// <summary>Whether anything was set.</summary>
    public bool IsDefined => Unit != LayoutUnit.Undefined;

    /// <summary>Whether this is a length that resolves to a number against a containing size.</summary>
    public bool IsResolvable => Unit is LayoutUnit.Point or LayoutUnit.Percent;

    /// <summary>Whether this length is one of CSS Sizing § 5's three content keywords.</summary>
    /// <remarks>
    ///     ⚠ <b><see cref="Resolve" /> answers NaN for these, and that is not the same as "unset".</b>
    ///     A containing size cannot settle a content keyword because the answer is a property of the
    ///     subtree, so it is resolved a step earlier — <c>LayoutTree.Intrinsic.cs</c> measures the
    ///     node and substitutes a <see cref="LayoutUnit.Point" /> before the algorithm reads the
    ///     slot. This predicate is what tells it there is something to substitute.
    ///     <para>
    ///         <see cref="LayoutUnit.Stretch" /> is not one of them: it names the space left over
    ///         rather than the content, so no measurement of the subtree produces it.
    ///     </para>
    /// </remarks>
    public bool IsContentBased => Unit is LayoutUnit.MinContent or LayoutUnit.MaxContent or LayoutUnit.FitContent;

    /// <summary>Resolves against a containing size, or NaN if it does not resolve.</summary>
    /// <param name="referenceLength">The containing size a percentage is a fraction of.</param>
    /// <returns>The resolved length, or NaN.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float Resolve(float referenceLength) => Unit switch {
        LayoutUnit.Point => Value,
        LayoutUnit.Percent => Value * referenceLength * 0.01f,
        _ => float.NaN
    };

    /// <inheritdoc />
    public bool Equals(StyleLength other) =>
        Unit == other.Unit && (Unit is LayoutUnit.Point or LayoutUnit.Percent ? Value.Equals(other.Value) : true);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is StyleLength other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Value, Unit);

    /// <summary>Compares two lengths.</summary>
    /// <param name="left">One length.</param>
    /// <param name="right">The other.</param>
    /// <returns>Whether they are the same.</returns>
    public static bool operator ==(StyleLength left, StyleLength right) => left.Equals(right);

    /// <summary>Compares two lengths.</summary>
    /// <param name="left">One length.</param>
    /// <param name="right">The other.</param>
    /// <returns>Whether they differ.</returns>
    public static bool operator !=(StyleLength left, StyleLength right) => !left.Equals(right);

    /// <inheritdoc />
    public override string ToString() => Unit switch {
        LayoutUnit.Undefined => "undefined",
        LayoutUnit.Point => Value.ToString(CultureInfo.InvariantCulture),
        LayoutUnit.Percent => Value.ToString(CultureInfo.InvariantCulture) + "%",
        _ => Unit.ToString().ToLowerInvariant()
    };
}
