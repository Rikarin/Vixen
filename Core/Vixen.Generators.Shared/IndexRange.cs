// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace System;

/// <summary>An index from the start or from the end, for <c>^1</c> in net11.0 source a generator compiles.</summary>
/// <remarks>
///     A compiler contract like <c>IsExternalInit</c>: the compiler looks the type up by name to lower
///     <c>^n</c>, <c>a..b</c> and list patterns. netstandard2.0, the target a Roslyn generator has to
///     use, does not declare it, and the source the markup generator links from
///     <c>Vixen.Core.Syntax</c> and <c>Vixen.Ui.Markup</c> slices strings throughout. Only what the
///     compiler calls is here.
/// </remarks>
internal readonly struct Index : IEquatable<Index> {
    readonly int value;

    /// <summary>An index <paramref name="value" /> places from the start, or from the end.</summary>
    public Index(int value, bool fromEnd = false) {
        if (value < 0) {
            throw new ArgumentOutOfRangeException(nameof(value), "The value must not be negative.");
        }

        this.value = fromEnd ? ~value : value;
    }

    Index(int value, bool _, bool __) => this.value = value;

    /// <summary>The first element.</summary>
    public static Index Start => new(0, false, false);

    /// <summary>One past the last element.</summary>
    public static Index End => new(~0, false, false);

    /// <summary>The distance from whichever end the index counts from.</summary>
    public int Value => value < 0 ? ~value : value;

    /// <summary>Whether the index counts from the end.</summary>
    public bool IsFromEnd => value < 0;

    /// <summary>An index from the start.</summary>
    public static Index FromStart(int value) => new(value);

    /// <summary>An index from the end.</summary>
    public static Index FromEnd(int value) => new(value, true);

    /// <summary>The offset from the start of a collection of <paramref name="length" /> elements.</summary>
    public int GetOffset(int length) => value < 0 ? length + value + 1 : value;

    /// <inheritdoc />
    public bool Equals(Index other) => value == other.value;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is Index other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => value;

    /// <summary>An index from the start.</summary>
    public static implicit operator Index(int value) => FromStart(value);

    /// <inheritdoc />
    public override string ToString() => IsFromEnd ? "^" + Value : Value.ToString(Globalization.CultureInfo.InvariantCulture);
}

/// <summary>A start and an end <see cref="Index" />, for <c>a..b</c> in the source a generator compiles.</summary>
/// <remarks>The other half of the <see cref="Index" /> contract, for the same reason.</remarks>
internal readonly struct Range : IEquatable<Range> {
    /// <summary>A range from <paramref name="start" /> up to, and not including, <paramref name="end" />.</summary>
    public Range(Index start, Index end) {
        Start = start;
        End = end;
    }

    /// <summary>The first index in the range.</summary>
    public Index Start { get; }

    /// <summary>The index one past the last in the range.</summary>
    public Index End { get; }

    /// <summary>From <paramref name="start" /> to the end.</summary>
    public static Range StartAt(Index start) => new(start, Index.End);

    /// <summary>From the start to <paramref name="end" />.</summary>
    public static Range EndAt(Index end) => new(Index.Start, end);

    /// <summary>Everything.</summary>
    public static Range All => new(Index.Start, Index.End);

    /// <summary>The offset and length within a collection of <paramref name="length" /> elements.</summary>
    public (int Offset, int Length) GetOffsetAndLength(int length) {
        var start = Start.GetOffset(length);
        var end = End.GetOffset(length);

        if ((uint) end > (uint) length || (uint) start > (uint) end) {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        return (start, end - start);
    }

    /// <inheritdoc />
    public bool Equals(Range other) => Start.Equals(other.Start) && End.Equals(other.End);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is Range other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => (Start.GetHashCode() * 31) + End.GetHashCode();

    /// <inheritdoc />
    public override string ToString() => Start + ".." + End;
}
