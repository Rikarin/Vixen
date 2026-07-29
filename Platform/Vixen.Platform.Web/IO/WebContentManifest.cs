// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Text;
using System.Text;
using Vixen.Core.IO;

namespace Vixen.Platform.Web;

/// <summary>One file under <c>/app</c>, as the manifest describes it.</summary>
/// <param name="Path">The virtual path below the mount, with a leading <c>/</c>.</param>
/// <param name="Length">The size in bytes.</param>
/// <param name="Modified">
///     When it last changed, in milliseconds since the Unix epoch, or <c>0</c> if the build did not
///     record one.
/// </param>
/// <param name="Url">
///     Where to fetch it from, relative to the content base, or <see langword="null" /> for the path
///     itself.
/// </param>
/// <remarks>
///     <see cref="Url" /> exists for content-addressed and fingerprinted builds — a CDN wants
///     <c>textures/a.4f2c9e.ktx2</c> with a far-future cache header, and the engine wants to keep
///     asking for <c>/app/textures/a.ktx2</c>. Without it the two could not both be true.
/// </remarks>
public readonly record struct WebContentEntry(
    string Path,
    long Length,
    long Modified = 0,
    string? Url = null
);

/// <summary>What is under <c>/app</c>, read once so that the synchronous queries can be answered.</summary>
/// <remarks>
///     <para>
///         <b>This is not a cache, it is a precondition.</b> HTTP has no directory listing, and
///         <see cref="Vixen.Core.IO.IFileProvider" />'s <c>Exists</c>, <c>TryGetEntry</c> and
///         <c>Enumerate</c> are synchronous — deliberately, because every other provider answers
///         them from something local. In a browser there is nothing local and nothing may block:
///         the WebAssembly runtime lives on the thread that also runs the event loop, so
///         <c>.GetAwaiter().GetResult()</c> does not wait for the fetch, it deadlocks the tab and
///         the fetch never completes. So the metadata is fetched once, before the platform exists,
///         and everything synchronous is answered from it.
///     </para>
///     <para>
///         The format is a JSON array of entries, which is what an asset build emits and what a
///         person can read:
///     </para>
///     <code language="json">
///     [
///       { "path": "/textures/atlas.ktx2", "length": 4194304, "modified": 1730000000000 },
///       { "path": "/bundles/level1.vxb", "length": 83886080, "url": "level1.4f2c9e.vxb" }
///     ]
///     </code>
/// </remarks>
public sealed class WebContentManifest {
    readonly Dictionary<string, WebContentEntry> entries;

    WebContentManifest(Dictionary<string, WebContentEntry> entries) => this.entries = entries;

    /// <summary>An empty manifest, which is what an application with no shipped content has.</summary>
    public static WebContentManifest Empty { get; } = new([]);

    /// <summary>How many files it names.</summary>
    public int Count => entries.Count;

    /// <summary>Every entry, in the order the manifest listed them.</summary>
    public IReadOnlyCollection<WebContentEntry> Entries => entries.Values;

    /// <summary>Reads a manifest.</summary>
    /// <param name="json">The manifest's bytes.</param>
    /// <returns>The manifest.</returns>
    /// <exception cref="InvalidDataException">It is not a manifest.</exception>
    /// <remarks>
    ///     Paths are normalised to a leading <c>/</c> and nothing else, so that a build which wrote
    ///     <c>textures/a.png</c> and one which wrote <c>/textures/a.png</c> both work. Case is left
    ///     alone: virtual paths are case-sensitive everywhere, including on the platforms whose file
    ///     systems are not, and a manifest that folded case would hide the mismatch until a Linux
    ///     CDN served the build.
    /// </remarks>
    public static WebContentManifest Parse(ReadOnlySpan<byte> json) {
        var reader = new ManifestReader(json);
        var entries = new Dictionary<string, WebContentEntry>(StringComparer.Ordinal);

        reader.ExpectArrayStart();

        while (reader.TryReadObjectStart()) {
            string? path = null;
            string? url = null;
            long length = 0;
            long modified = 0;

            while (reader.TryReadPropertyName(out var name)) {
                switch (name) {
                    case "path":
                        path = reader.ReadString();
                        break;

                    case "url":
                        url = reader.ReadString();
                        break;

                    case "length":
                        length = reader.ReadNumber();
                        break;

                    case "modified":
                        modified = reader.ReadNumber();
                        break;

                    default:
                        // Forwards compatibility: a build that records a hash or a content type is
                        // a build this reader should still be able to mount.
                        reader.SkipValue();
                        break;
                }
            }

            if (string.IsNullOrEmpty(path)) {
                continue;
            }

            var normalised = path[0] == '/' ? path : "/" + path;
            entries[normalised] = new(normalised, length, modified, url);
        }

        return new(entries);
    }

    /// <summary>Looks a path up.</summary>
    /// <param name="path">The path below the mount, with its leading <c>/</c>.</param>
    /// <param name="entry">What the manifest says about it.</param>
    /// <returns><see langword="false" /> if the manifest does not name it.</returns>
    public bool TryGet(string path, out WebContentEntry entry) => entries.TryGetValue(path, out entry);

    /// <summary>Whether anything is at or below a directory.</summary>
    /// <param name="directory">The directory, with its leading <c>/</c> and no trailing one.</param>
    /// <remarks>
    ///     A manifest lists files, so a directory exists exactly when something under it does —
    ///     which is also the only sense in which a directory exists on a web server.
    /// </remarks>
    public bool HasDirectory(string directory) {
        if (directory is "/" or "") {
            return entries.Count > 0;
        }

        var prefix = directory + "/";

        foreach (var path in entries.Keys) {
            if (path.StartsWith(prefix, StringComparison.Ordinal)) {
                return true;
            }
        }

        return false;
    }

    /// <summary>Lists what is directly, or eventually, under a directory.</summary>
    /// <param name="directory">The directory, as a path below the mount.</param>
    /// <param name="recursive">Whether to descend.</param>
    /// <returns>Directories first, then files, each group ordered by path.</returns>
    /// <remarks>
    ///     <para>
    ///         Here rather than in <see cref="FetchFileProvider" /> because it is a query over the
    ///         manifest and nothing else: a web server has no directories, so what "the contents of
    ///         <c>/textures</c>" means is entirely a question about the list of paths. Keeping it
    ///         here also keeps it testable without a browser, which the provider is not.
    ///     </para>
    ///     <para>
    ///         The order is part of <see cref="IFileProvider.Enumerate" />'s contract: a content
    ///         build that hashes a listing to decide whether anything changed must not get a
    ///         different answer from a manifest whose entries happen to be in a different order.
    ///     </para>
    /// </remarks>
    public IEnumerable<FileEntry> Enumerate(VirtualPath directory, bool recursive = false) {
        var prefix = directory.IsRoot ? "/" : directory.Value + "/";
        var directories = new SortedSet<string>(StringComparer.Ordinal);
        var files = new SortedDictionary<string, WebContentEntry>(StringComparer.Ordinal);

        foreach (var entry in entries.Values) {
            if (!entry.Path.StartsWith(prefix, StringComparison.Ordinal)) {
                continue;
            }

            var relative = entry.Path.AsSpan(prefix.Length);
            var separator = relative.IndexOf('/');

            if (separator < 0) {
                files[entry.Path] = entry;
                continue;
            }

            // A manifest lists files, so directories are inferred from the paths that pass through
            // them — and from *every* level of such a path, or a recursive listing skips the
            // intermediate ones: nothing names /textures/ui/deep except a path going through it.
            var at = separator;

            while (at >= 0) {
                directories.Add(prefix + relative[..at].ToString());

                if (!recursive) {
                    break;
                }

                var next = relative[(at + 1)..].IndexOf('/');
                at = next < 0 ? -1 : at + 1 + next;
            }

            if (recursive) {
                files[entry.Path] = entry;
            }
        }

        foreach (var child in directories) {
            yield return new(new(child), 0, default, IsDirectory: true);
        }

        foreach (var (path, entry) in files) {
            yield return new(new(path), entry.Length, Moment(entry.Modified), IsDirectory: false);
        }
    }

    /// <summary>A Unix millisecond stamp as a moment, or the default where the build recorded none.</summary>
    internal static DateTimeOffset Moment(long milliseconds) =>
        milliseconds > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(milliseconds) : default;
}

/// <summary>Just enough JSON to read a manifest, and no more.</summary>
/// <remarks>
///     <para>
///         <b>Written rather than taken off the shelf, and the reason is measured.</b> The obvious
///         answer is <c>System.Text.Json</c> with a source-generated context, which is what
///         <c>Vixen.Shaders</c> uses and is trim-clean. On a browser build it costs <b>59 KB
///         Brotli</b> for <c>System.Text.Json</c> plus <c>System.Text.Encodings.Web</c> — about six
///         per cent of the 930 KB payload floor <c>docs/plan/10 § Web</c> measures the whole target
///         against — to read one array of four-field objects, once, at start-up. That is not a
///         trade worth making on the one platform where the payload is the product.
///     </para>
///     <para>
///         <b>What it does not do</b> is the honest half of that. No <c>\uXXXX</c> escapes beyond
///         the two-character ones, no exponent notation, no nested objects or arrays as values
///         (they are skipped rather than read), no comments. A manifest is machine-written by the
///         content build and its paths are virtual paths, which are already restricted to a
///         character set none of that is needed for. A manifest that needs more is a manifest this
///         rejects with a message saying where.
///     </para>
/// </remarks>
internal ref struct ManifestReader(ReadOnlySpan<byte> json) {
    readonly ReadOnlySpan<byte> json = json;
    int at;

    /// <summary>Steps over the opening bracket.</summary>
    /// <exception cref="InvalidDataException">The document is not an array.</exception>
    public void ExpectArrayStart() {
        SkipWhitespace();

        if (at >= json.Length || json[at] != (byte)'[') {
            throw Invalid("the document does not start with an array");
        }

        at++;
    }

    /// <summary>Steps over an element's opening brace, or the array's closing bracket.</summary>
    /// <returns><see langword="false" /> at the end of the array.</returns>
    public bool TryReadObjectStart() {
        SkipWhitespace();

        while (at < json.Length && json[at] == (byte)',') {
            at++;
            SkipWhitespace();
        }

        if (at >= json.Length || json[at] == (byte)']') {
            at++;
            return false;
        }

        if (json[at] != (byte)'{') {
            throw Invalid("an array element is not an object");
        }

        at++;
        return true;
    }

    /// <summary>Reads a property name and steps over its colon, or the object's closing brace.</summary>
    /// <returns><see langword="false" /> at the end of the object.</returns>
    public bool TryReadPropertyName(out string name) {
        SkipWhitespace();

        while (at < json.Length && json[at] == (byte)',') {
            at++;
            SkipWhitespace();
        }

        if (at >= json.Length || json[at] == (byte)'}') {
            at++;
            name = string.Empty;
            return false;
        }

        name = ReadString();
        SkipWhitespace();

        if (at >= json.Length || json[at] != (byte)':') {
            throw Invalid("a property name is not followed by a colon");
        }

        at++;
        return true;
    }

    /// <summary>Reads a string value.</summary>
    public string ReadString() {
        SkipWhitespace();

        if (at >= json.Length || json[at] != (byte)'"') {
            throw Invalid("a string was expected");
        }

        at++;
        var start = at;
        var escaped = false;

        while (at < json.Length && json[at] != (byte)'"') {
            if (json[at] == (byte)'\\') {
                escaped = true;
                at++;
            }

            at++;
        }

        if (at >= json.Length) {
            throw Invalid("a string is not terminated");
        }

        var raw = json[start..at];
        at++;

        return escaped ? Unescape(raw) : Encoding.UTF8.GetString(raw);
    }

    /// <summary>Reads an integer value.</summary>
    /// <remarks>
    ///     Integers only. Every number a manifest carries is a byte count or a Unix timestamp, and
    ///     a fractional one of either is a manifest that was written by something that
    ///     misunderstood the format.
    /// </remarks>
    public long ReadNumber() {
        SkipWhitespace();
        var start = at;

        while (at < json.Length && (json[at] == (byte)'-' || json[at] is >= (byte)'0' and <= (byte)'9')) {
            at++;
        }

        if (!Utf8Parser.TryParse(json[start..at], out long value, out _)) {
            throw Invalid("a number could not be read");
        }

        return value;
    }

    /// <summary>Steps over a value of any kind without reading it.</summary>
    public void SkipValue() {
        SkipWhitespace();

        if (at >= json.Length) {
            return;
        }

        switch (json[at]) {
            case (byte)'"':
                ReadString();
                return;

            case (byte)'{':
            case (byte)'[': {
                var depth = 0;

                while (at < json.Length) {
                    var character = json[at];

                    if (character == (byte)'"') {
                        ReadString();
                        continue;
                    }

                    at++;

                    if (character is (byte)'{' or (byte)'[') {
                        depth++;
                    } else if (character is (byte)'}' or (byte)']' && --depth == 0) {
                        return;
                    }
                }

                return;
            }

            default:
                // A number, true, false or null: everything up to the next separator.
                while (at < json.Length && json[at] is not ((byte)',' or (byte)'}' or (byte)']')) {
                    at++;
                }

                return;
        }
    }

    void SkipWhitespace() {
        while (at < json.Length && json[at] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n') {
            at++;
        }
    }

    static string Unescape(ReadOnlySpan<byte> raw) {
        var builder = new StringBuilder(raw.Length);

        for (var index = 0; index < raw.Length; index++) {
            if (raw[index] != (byte)'\\') {
                var run = index;

                while (index < raw.Length && raw[index] != (byte)'\\') {
                    index++;
                }

                builder.Append(Encoding.UTF8.GetString(raw[run..index]));
                index--;
                continue;
            }

            index++;

            if (index >= raw.Length) {
                break;
            }

            builder.Append(
                raw[index] switch {
                    (byte)'n' => '\n',
                    (byte)'t' => '\t',
                    (byte)'r' => '\r',
                    (byte)'b' => '\b',
                    (byte)'f' => '\f',
                    (byte)'/' => '/',
                    (byte)'\\' => '\\',
                    (byte)'"' => '"',
                    _ => throw new InvalidDataException(
                        $"The content manifest uses the escape '\\{(char)raw[index]}', which this "
                        + "reader does not implement. See ManifestReader for what it does and why."
                    )
                }
            );
        }

        return builder.ToString();
    }

    readonly InvalidDataException Invalid(string what) =>
        new($"The content manifest is not valid JSON at byte {at}: {what}.");
}
