// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Vixen.Core;

/// <summary>
///     A 128-bit content hash: the key of a blob in the object database, and therefore the identity
///     of a *compiled* artefact rather than of a source asset. Two builds that produce the same
///     bytes produce the same <see cref="ObjectId" />, which is what makes incremental content
///     builds and delta bundle updates possible.
/// </summary>
/// <remarks>
///     <para>
///         Deliberately carries no hash function. The algorithm (XxHash128) belongs with the code
///         that has the content in front of it — <c>Vixen.Core.Serialization</c>'s object database —
///         and keeping it there is what lets this assembly depend on nothing but the BCL.
///     </para>
///     <para>
///         Bytes are big-endian, so the 32-digit hex text form reads in the same order as
///         <see cref="WriteTo" /> writes. Ids from different machines are byte-comparable.
///     </para>
/// </remarks>
[DataContract]
[StructLayout(LayoutKind.Sequential, Size = SizeInBytes)]
public readonly record struct ObjectId
    : IComparable<ObjectId>, ISpanFormattable, IUtf8SpanFormattable, ISpanParsable<ObjectId> {
    /// <summary>Size of the raw id, in bytes.</summary>
    public const int SizeInBytes = 16;

    /// <summary>Number of characters <see cref="ToString()" /> writes.</summary>
    public const int TextLength = SizeInBytes * 2;

    readonly ulong high;
    readonly ulong low;

    /// <summary>The first eight bytes, read big-endian.</summary>
    public ulong High => high;

    /// <summary>The last eight bytes, read big-endian.</summary>
    public ulong Low => low;

    /// <summary>The id of no content.</summary>
    public static ObjectId Empty => default;

    /// <summary>Whether every bit is zero — the id no real content is expected to have.</summary>
    public bool IsEmpty => (high | low) == 0;

    /// <summary>Builds an id from its two halves.</summary>
    /// <param name="high">The first eight bytes, read big-endian.</param>
    /// <param name="low">The last eight bytes, read big-endian.</param>
    public ObjectId(ulong high, ulong low) {
        this.high = high;
        this.low = low;
    }

    /// <summary>Reinterprets 16 raw bytes — typically a hash digest — as an id.</summary>
    /// <param name="bytes">Exactly <see cref="SizeInBytes" /> bytes.</param>
    /// <returns>The id those bytes spell.</returns>
    /// <exception cref="ArgumentException"><paramref name="bytes" /> is not 16 bytes long.</exception>
    public static ObjectId FromBytes(ReadOnlySpan<byte> bytes) {
        if (bytes.Length != SizeInBytes) {
            throw new ArgumentException($"An object id is exactly {SizeInBytes} bytes.", nameof(bytes));
        }

        return new(BinaryPrimitives.ReadUInt64BigEndian(bytes), BinaryPrimitives.ReadUInt64BigEndian(bytes[8..]));
    }

    /// <summary>Writes the id as 16 big-endian bytes.</summary>
    /// <param name="destination">A span of at least <see cref="SizeInBytes" /> bytes.</param>
    /// <exception cref="ArgumentException"><paramref name="destination" /> is too short.</exception>
    public void WriteTo(Span<byte> destination) {
        if (!TryWriteBytes(destination)) {
            throw new ArgumentException($"An object id needs {SizeInBytes} bytes.", nameof(destination));
        }
    }

    /// <summary>Writes the id as 16 big-endian bytes if there is room.</summary>
    /// <param name="destination">The span to write to.</param>
    /// <returns><see langword="false" /> if <paramref name="destination" /> is too short.</returns>
    public bool TryWriteBytes(Span<byte> destination) {
        if (destination.Length < SizeInBytes) {
            return false;
        }

        BinaryPrimitives.WriteUInt64BigEndian(destination, high);
        BinaryPrimitives.WriteUInt64BigEndian(destination[8..], low);
        return true;
    }

    /// <inheritdoc />
    public int CompareTo(ObjectId other) {
        var byHigh = high.CompareTo(other.high);
        return byHigh != 0 ? byHigh : low.CompareTo(other.low);
    }

    /// <summary>Whether <paramref name="left" /> sorts before <paramref name="right" />.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    /// <returns>The comparison result.</returns>
    public static bool operator <(ObjectId left, ObjectId right) => left.CompareTo(right) < 0;

    /// <summary>Whether <paramref name="left" /> sorts before or equal to <paramref name="right" />.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    /// <returns>The comparison result.</returns>
    public static bool operator <=(ObjectId left, ObjectId right) => left.CompareTo(right) <= 0;

    /// <summary>Whether <paramref name="left" /> sorts after <paramref name="right" />.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    /// <returns>The comparison result.</returns>
    public static bool operator >(ObjectId left, ObjectId right) => left.CompareTo(right) > 0;

    /// <summary>Whether <paramref name="left" /> sorts after or equal to <paramref name="right" />.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    /// <returns>The comparison result.</returns>
    public static bool operator >=(ObjectId left, ObjectId right) => left.CompareTo(right) >= 0;

    /// <summary>
    ///     The value is already a hash, so folding it again would only cost cycles: this returns
    ///     bits of it directly.
    /// </summary>
    /// <returns>A hash code.</returns>
    public override int GetHashCode() => (int)low ^ (int)(low >> 32);

    /// <summary>Renders the id as 32 lowercase hex digits.</summary>
    /// <returns>The id in hex.</returns>
    public override string ToString() => string.Create(
        TextLength,
        this,
        static (span, id) => id.TryFormat(span, out _, default, null)
    );

    /// <inheritdoc />
    public string ToString(string? format, IFormatProvider? formatProvider) {
        if (!IsUpper(format, out var upper)) {
            throw new FormatException($"'{format}' is not a valid object id format; use \"x\" or \"X\".");
        }

        return string.Create(
            TextLength,
            (Id: this, Upper: upper),
            static (span, state) => state.Id.Write(span, state.Upper)
        );
    }

    /// <inheritdoc />
    public bool TryFormat(
        Span<char> destination,
        out int charsWritten,
        ReadOnlySpan<char> format = default,
        IFormatProvider? provider = null
    ) {
        charsWritten = 0;
        if (destination.Length < TextLength || !IsUpper(format, out var upper)) {
            return false;
        }

        Write(destination, upper);
        charsWritten = TextLength;
        return true;
    }

    /// <inheritdoc />
    public bool TryFormat(
        Span<byte> utf8Destination,
        out int bytesWritten,
        ReadOnlySpan<char> format = default,
        IFormatProvider? provider = null
    ) {
        bytesWritten = 0;
        if (utf8Destination.Length < TextLength || !IsUpper(format, out var upper)) {
            return false;
        }

        Span<char> chars = stackalloc char[TextLength];
        Write(chars, upper);
        for (var i = 0; i < TextLength; i++) {
            utf8Destination[i] = (byte)chars[i];
        }

        bytesWritten = TextLength;
        return true;
    }

    /// <inheritdoc />
    public static ObjectId Parse(string s, IFormatProvider? provider = null) => Parse(s.AsSpan(), provider);

    /// <inheritdoc />
    public static ObjectId Parse(ReadOnlySpan<char> s, IFormatProvider? provider = null) =>
        TryParse(s, provider, out var result)
            ? result
            : throw new FormatException($"'{s}' is not a {TextLength}-digit hex object id.");

    /// <inheritdoc />
    public static bool TryParse([NotNullWhen(true)] string? s, IFormatProvider? provider, out ObjectId result) =>
        TryParse(s.AsSpan(), provider, out result);

    /// <inheritdoc />
    public static bool TryParse(ReadOnlySpan<char> s, IFormatProvider? provider, out ObjectId result) {
        // AllowHexSpecifier and not HexNumber: the latter also allows surrounding whitespace, which
        // would let " 0123…" through with only 31 significant digits.
        if (s.Length == TextLength
            && ulong.TryParse(s[..16], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var high)
            && ulong.TryParse(s[16..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var low)) {
            result = new(high, low);
            return true;
        }

        result = default;
        return false;
    }

    /// <summary>Parses 32 hex digits, in either case.</summary>
    /// <param name="s">The text to parse.</param>
    /// <param name="result">The parsed id, or <see cref="Empty" /> on failure.</param>
    /// <returns><see langword="true" /> if <paramref name="s" /> was an id.</returns>
    public static bool TryParse([NotNullWhen(true)] string? s, out ObjectId result) => TryParse(s, null, out result);

    /// <inheritdoc cref="TryParse(string?, out ObjectId)" />
    public static bool TryParse(ReadOnlySpan<char> s, out ObjectId result) => TryParse(s, null, out result);

    void Write(Span<char> destination, bool upper) {
        var digits = upper ? "0123456789ABCDEF" : "0123456789abcdef";
        WriteHalf(destination, high, digits);
        WriteHalf(destination[16..], low, digits);

        static void WriteHalf(Span<char> destination, ulong value, ReadOnlySpan<char> digits) {
            for (var i = 0; i < 16; i++) {
                destination[i] = digits[(int)((value >> ((15 - i) * 4)) & 0xF)];
            }
        }
    }

    static bool IsUpper(ReadOnlySpan<char> format, out bool upper) {
        upper = format.Length == 1 && format[0] == 'X';
        return format.IsEmpty || (format.Length == 1 && (format[0] is 'x' or 'X'));
    }

    static bool IsUpper(string? format, out bool upper) => IsUpper(format.AsSpan(), out upper);
}
