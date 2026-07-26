// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;

namespace Vixen.Core.IO;

/// <summary>An absolute, normalised, case-sensitive path inside the virtual file system.</summary>
/// <remarks>
///     <para>
///         One path vocabulary for six platforms. A virtual path always begins with <c>/</c>, uses
///         <c>/</c> as its only separator, has no empty segments, no <c>.</c> or <c>..</c> left in
///         it, and no trailing slash. Constructing one normalises it; a path that cannot be
///         normalised — one that escapes above the root, or contains a backslash or a control
///         character — is rejected rather than repaired.
///     </para>
///     <para>
///         <b>Case-sensitive, everywhere, including Windows and macOS.</b> This is the rule from
///         <c>docs/plan/10-platforms.md</c> and it exists because the alternative is discovering on a
///         user's Linux machine that <c>Texture.PNG</c> and <c>texture.png</c> were the same file for
///         the eighteen months the project was developed on a Mac. Two paths that differ in case are
///         two different paths here, and <see cref="PhysicalFileProvider" /> refuses to serve a file
///         whose real name on disk differs in case from the one asked for.
///     </para>
///     <para>
///         The default value is the empty path. It is not a valid path, <see cref="IsEmpty" /> says
///         so, and every operation on it either says so too or throws — it exists so that a
///         <c>VirtualPath</c> field can be unset without being a lie.
///     </para>
/// </remarks>
public readonly record struct VirtualPath : IComparable<VirtualPath>, ISpanFormattable {
    /// <summary>The separator. There is exactly one, on every platform.</summary>
    public const char Separator = '/';

    readonly string? value;

    /// <summary>The root, <c>/</c>.</summary>
    public static VirtualPath Root { get; } = new("/", validated: true);

    /// <summary>The normalised text. Empty for the default value.</summary>
    public string Value => value ?? string.Empty;

    /// <summary>Whether this is the default, which is not a path.</summary>
    public bool IsEmpty => value is null;

    /// <summary>Whether this is the root, <c>/</c>.</summary>
    public bool IsRoot => value is "/";

    /// <summary>Creates a path, normalising and validating it.</summary>
    /// <param name="path">The path. Must be absolute.</param>
    /// <exception cref="ArgumentException"><paramref name="path" /> is not a valid virtual path.</exception>
    public VirtualPath(string path) {
        ArgumentNullException.ThrowIfNull(path);

        if (!TryNormalise(path, out var normalised, out var problem)) {
            throw new ArgumentException($"'{path}' is not a valid virtual path: {problem}", nameof(path));
        }

        value = normalised;
    }

    VirtualPath(string normalised, bool validated) {
        _ = validated;
        value = normalised;
    }

    /// <summary>Creates a path if it is valid.</summary>
    /// <param name="path">The path.</param>
    /// <param name="result">The normalised path, or the default if it was not valid.</param>
    /// <returns><see langword="false" /> if <paramref name="path" /> is not a valid virtual path.</returns>
    public static bool TryCreate([NotNullWhen(true)] string? path, out VirtualPath result) {
        if (path is not null && TryNormalise(path, out var normalised, out _)) {
            result = new(normalised, validated: true);
            return true;
        }

        result = default;
        return false;
    }

    /// <summary>The mount this path belongs to — its first segment, as a path.</summary>
    /// <remarks><c>/app/textures/a.png</c> has mount <c>/app</c>. The root's mount is the root.</remarks>
    public VirtualPath Mount {
        get {
            if (value is null || value.Length == 1) {
                return this;
            }

            var next = value.IndexOf(Separator, 1);
            return next < 0 ? this : new(value[..next], validated: true);
        }
    }

    /// <summary>The containing directory, or the root for a top-level entry.</summary>
    /// <remarks>The root's parent is the root, so walking upwards terminates instead of throwing.</remarks>
    public VirtualPath Parent {
        get {
            if (value is null || value.Length == 1) {
                return this;
            }

            var last = value.LastIndexOf(Separator);
            return last == 0 ? Root : new(value[..last], validated: true);
        }
    }

    /// <summary>The last segment: the file or directory name.</summary>
    public ReadOnlySpan<char> FileName =>
        value is null || value.Length == 1 ? default : value.AsSpan(value.LastIndexOf(Separator) + 1);

    /// <summary>The extension, including the leading dot, or empty if there is none.</summary>
    /// <remarks>
    ///     A leading dot is a hidden file, not an extension: <c>.gitignore</c> has no extension, the
    ///     same convention <see cref="Path.GetExtension(string)" /> uses.
    /// </remarks>
    public ReadOnlySpan<char> Extension {
        get {
            var name = FileName;
            var dot = name.LastIndexOf('.');
            return dot <= 0 ? default : name[dot..];
        }
    }

    /// <summary>The last segment without its extension.</summary>
    public ReadOnlySpan<char> FileNameWithoutExtension {
        get {
            var name = FileName;
            var dot = name.LastIndexOf('.');
            return dot <= 0 ? name : name[..dot];
        }
    }

    /// <summary>Appends a relative path.</summary>
    /// <param name="relative">A relative path. May contain <c>/</c>, <c>.</c> and <c>..</c>.</param>
    /// <returns>The combined, normalised path.</returns>
    /// <exception cref="ArgumentException">The result is not a valid virtual path.</exception>
    /// <exception cref="InvalidOperationException">This is the default value.</exception>
    public VirtualPath Combine(string relative) {
        ArgumentNullException.ThrowIfNull(relative);
        ThrowIfEmpty();

        if (relative.Length == 0) {
            return this;
        }

        // An absolute argument replaces rather than appends, which is Path.Combine's behaviour and
        // the one that surprises nobody.
        return relative[0] == Separator ? new(relative) : new(IsRoot ? value + relative : value + "/" + relative);
    }

    /// <summary>Appends a relative path.</summary>
    /// <param name="left">The base path.</param>
    /// <param name="right">The relative path.</param>
    /// <returns>The combined, normalised path.</returns>
    public static VirtualPath operator /(VirtualPath left, string right) => left.Combine(right);

    /// <summary>Replaces the extension.</summary>
    /// <param name="extension">The new extension, with or without a leading dot. Empty removes it.</param>
    /// <returns>The path with its extension replaced.</returns>
    /// <exception cref="InvalidOperationException">This is the root or the default value.</exception>
    public VirtualPath WithExtension(string extension) {
        ArgumentNullException.ThrowIfNull(extension);
        ThrowIfEmpty();

        if (IsRoot) {
            throw new InvalidOperationException("The root has no name, so it has no extension to replace.");
        }

        var stem = value.AsSpan(0, value!.Length - Extension.Length);
        var dot = extension.Length == 0 || extension[0] == '.' ? string.Empty : ".";
        return new(string.Concat(stem, dot, extension));
    }

    /// <summary>Whether <paramref name="other" /> is this path or is under it.</summary>
    /// <param name="other">The candidate descendant.</param>
    /// <returns><see langword="true" /> if <paramref name="other" /> is at or below this path.</returns>
    /// <remarks>
    ///     Segment-aware, which is the entire point: <c>/app</c> contains <c>/app/textures</c> and
    ///     does not contain <c>/application</c>. A prefix comparison on the raw text gets that wrong,
    ///     and gets it wrong in the direction where a mount silently swallows a sibling.
    /// </remarks>
    public bool Contains(VirtualPath other) {
        if (value is null || other.value is null) {
            return false;
        }

        if (IsRoot) {
            return true;
        }

        if (!other.value.StartsWith(value, StringComparison.Ordinal)) {
            return false;
        }

        return other.value.Length == value.Length || other.value[value.Length] == Separator;
    }

    /// <summary>Removes a leading path, leaving what is below it, still rooted.</summary>
    /// <param name="prefix">The prefix to remove. Must contain this path.</param>
    /// <returns><c>/app/a/b</c> relative to <c>/app</c> is <c>/a/b</c>; a path relative to itself is the root.</returns>
    /// <exception cref="ArgumentException"><paramref name="prefix" /> does not contain this path.</exception>
    public VirtualPath RelativeTo(VirtualPath prefix) {
        if (!prefix.Contains(this)) {
            throw new ArgumentException($"'{Value}' is not under '{prefix.Value}'.", nameof(prefix));
        }

        if (prefix.IsRoot) {
            return this;
        }

        return value!.Length == prefix.value!.Length ? Root : new(value[prefix.value.Length..], validated: true);
    }

    /// <summary>Walks the segments, without allocating a string for any of them.</summary>
    /// <returns>An enumerator over the segments, root first.</returns>
    public SegmentEnumerator EnumerateSegments() => new(Value);

    /// <summary>Compares ordinally, which is the same order on every platform.</summary>
    /// <param name="other">The path to compare against.</param>
    /// <returns>The comparison result.</returns>
    public int CompareTo(VirtualPath other) => string.CompareOrdinal(value, other.value);

    /// <summary>Renders the path.</summary>
    /// <returns>The normalised text.</returns>
    public override string ToString() => Value;

    /// <summary>Renders the path.</summary>
    /// <param name="format">Ignored; a path has one form.</param>
    /// <param name="formatProvider">Ignored.</param>
    /// <returns>The normalised text.</returns>
    public string ToString(string? format, IFormatProvider? formatProvider) => Value;

    /// <summary>Writes the path into a span.</summary>
    /// <param name="destination">Where to write.</param>
    /// <param name="charsWritten">How much was written.</param>
    /// <param name="format">Ignored; a path has one form.</param>
    /// <param name="provider">Ignored.</param>
    /// <returns><see langword="false" /> if <paramref name="destination" /> was too short.</returns>
    public bool TryFormat(
        Span<char> destination,
        out int charsWritten,
        ReadOnlySpan<char> format,
        IFormatProvider? provider
    ) {
        if (Value.AsSpan().TryCopyTo(destination)) {
            charsWritten = Value.Length;
            return true;
        }

        charsWritten = 0;
        return false;
    }

    /// <summary>Orders paths ordinally.</summary>
    /// <param name="left">The left path.</param>
    /// <param name="right">The right path.</param>
    /// <returns>Whether <paramref name="left" /> sorts first.</returns>
    public static bool operator <(VirtualPath left, VirtualPath right) => left.CompareTo(right) < 0;

    /// <summary>Orders paths ordinally.</summary>
    /// <param name="left">The left path.</param>
    /// <param name="right">The right path.</param>
    /// <returns>Whether <paramref name="left" /> sorts first or equal.</returns>
    public static bool operator <=(VirtualPath left, VirtualPath right) => left.CompareTo(right) <= 0;

    /// <summary>Orders paths ordinally.</summary>
    /// <param name="left">The left path.</param>
    /// <param name="right">The right path.</param>
    /// <returns>Whether <paramref name="left" /> sorts last.</returns>
    public static bool operator >(VirtualPath left, VirtualPath right) => left.CompareTo(right) > 0;

    /// <summary>Orders paths ordinally.</summary>
    /// <param name="left">The left path.</param>
    /// <param name="right">The right path.</param>
    /// <returns>Whether <paramref name="left" /> sorts last or equal.</returns>
    public static bool operator >=(VirtualPath left, VirtualPath right) => left.CompareTo(right) >= 0;

    static bool TryNormalise(string path, [NotNullWhen(true)] out string? result, out string problem) {
        result = null;

        if (path.Length == 0 || path[0] != Separator) {
            problem = "it is not absolute; a virtual path begins with '/'";
            return false;
        }

        foreach (var character in path) {
            if (character == '\\') {
                problem = "it contains a backslash; '/' is the only separator, on every platform";
                return false;
            }

            if (char.IsControl(character)) {
                problem = "it contains a control character";
                return false;
            }
        }

        // Segments are rewritten in place into a buffer no longer than the input: normalisation only
        // ever removes.
        var buffer = path.Length <= 256 ? stackalloc char[path.Length] : new char[path.Length];
        var length = 0;
        var start = 1;

        while (start <= path.Length) {
            var end = path.IndexOf(Separator, start);

            if (end < 0) {
                end = path.Length;
            }

            var segment = path.AsSpan(start, end - start);
            start = end + 1;

            switch (segment) {
                case "" or ".":
                    // An empty segment is a doubled slash or a trailing one; both are noise.
                    continue;

                case "..":
                    if (length == 0) {
                        problem = "it escapes above the root";
                        return false;
                    }

                    length = buffer[..length].LastIndexOf(Separator);
                    continue;

                default:
                    buffer[length++] = Separator;
                    segment.CopyTo(buffer[length..]);
                    length += segment.Length;
                    continue;
            }
        }

        result = length == 0 ? "/" : new(buffer[..length]);
        problem = string.Empty;
        return true;
    }

    [MemberNotNull(nameof(value))]
    void ThrowIfEmpty() {
        if (value is null) {
            throw new InvalidOperationException("The path is the default value, which is not a path.");
        }
    }

    /// <summary>Walks a path's segments as spans.</summary>
    public ref struct SegmentEnumerator {
        readonly ReadOnlySpan<char> path;
        int next;

        internal SegmentEnumerator(ReadOnlySpan<char> path) {
            this.path = path;
            next = path.Length > 0 ? 1 : -1;
            Current = default;
        }

        /// <summary>The segment at the current position.</summary>
        public ReadOnlySpan<char> Current { get; private set; }

        /// <summary>Allows the enumerator to be used directly in <c>foreach</c>.</summary>
        /// <returns>Itself.</returns>
        public readonly SegmentEnumerator GetEnumerator() => this;

        /// <summary>Advances to the next segment.</summary>
        /// <returns><see langword="false" /> when there are none left.</returns>
        public bool MoveNext() {
            if (next < 0 || next >= path.Length) {
                return false;
            }

            var end = path[next..].IndexOf(Separator);
            end = end < 0 ? path.Length : next + end;
            Current = path[next..end];
            next = end + 1;
            return true;
        }
    }
}
