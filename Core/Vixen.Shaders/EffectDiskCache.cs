// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using System.Text;
using Vixen.Core.Serialization;

namespace Vixen.Shaders;

/// <summary>
///     The second tier: variants written down beside the project so the next run does not compile
///     them again.
/// </summary>
/// <remarks>
///     <para>
///         Read-through and write-back over an inner source. A miss asks whatever is behind it — the
///         Raven compiler in the editor, a dev machine over TCP on a device — and writes down what
///         comes back, which is the whole reason this tier answers with an
///         <see cref="EffectData" /> rather than an <see cref="Effect" />: a set of device handles
///         cannot be written to a file.
///     </para>
///     <para>
///         <strong>Keyed by (key, target), with the source hash checked rather than named.</strong>
///         Doc 06 asks for a cache keyed by the Raven source hash as well, and the difference matters
///         in one direction only: a reader has to be able to <em>find</em> an entry, and a runtime
///         asking for a variant does not know what the shader source hashed to — the compiler that
///         knew is the thing this tier exists to avoid running. So the hash rides inside the record,
///         where it can be compared once the entry is found, and <see cref="Expect" /> is what a host
///         that does know sets. An entry that fails the comparison is treated as absent and
///         overwritten, which is what makes editing a shader invalidate exactly the variants of it.
///     </para>
///     <para>
///         Every failure to read is a miss, never an exception. A cache is an optimisation, and a
///         truncated file — a build killed halfway through a write, a full disk, a directory two
///         machines share over a network — must cost a recompile and nothing else. A failure to
///         <em>write</em> is swallowed for the same reason.
///     </para>
/// </remarks>
public sealed class EffectDiskCache : IEffectSource {
    /// <summary>The extension every entry has.</summary>
    public const string Extension = ".vxfx";

    readonly Lock writing = new();

    /// <summary>Where the entries are.</summary>
    public string Directory { get; }

    /// <summary>The backend the entries are for, as Raven's <c>TargetBackends</c> names it.</summary>
    /// <remarks>
    ///     Part of the file name rather than checked after loading. Two backends' artefacts for one
    ///     key are both valid and neither is stale, so they have to be able to coexist — one machine
    ///     building for desktop and for mobile out of the same tree is the ordinary case, not a
    ///     conflict.
    /// </remarks>
    public string Target { get; }

    /// <summary>What answers a miss, or null for a cache that only reads.</summary>
    public IEffectSource? Source { get; }

    /// <summary>
    ///     The source hash entries must carry, or empty to accept any.
    /// </summary>
    /// <remarks>
    ///     Empty is the right default for a runtime, which has no sources to hash and would otherwise
    ///     reject a perfectly good cache written by the build that shipped with it.
    /// </remarks>
    public string Expect { get; init; } = string.Empty;

    /// <summary>How many entries have been read from disk.</summary>
    public int Hits { get; private set; }

    /// <summary>How many entries have been written.</summary>
    public int Writes { get; private set; }

    /// <summary>A cache in a directory, created on first write.</summary>
    /// <param name="directory">Where the entries live.</param>
    /// <param name="target">Which backend they are for.</param>
    /// <param name="source">What answers a miss.</param>
    public EffectDiskCache(string directory, string target, IEffectSource? source = null) {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        ArgumentException.ThrowIfNullOrEmpty(target);

        Directory = directory;
        Target = target;
        Source = source;
    }

    /// <inheritdoc />
    public EffectData? TryGet(EffectKey key) {
        var path = PathOf(key);

        if (Read(path) is { } cached) {
            Hits++;
            return cached;
        }

        if (Source?.TryGet(key) is not { } produced) {
            return null;
        }

        Write(path, produced);
        return produced;
    }

    /// <summary>Puts a variant in the cache without going through a miss.</summary>
    /// <remarks>
    ///     For a build step that compiled a batch and wants the next incremental build to reuse it,
    ///     and for a host that received a variant some other way.
    /// </remarks>
    public void Store(EffectData effect) {
        ArgumentNullException.ThrowIfNull(effect);
        Write(PathOf(effect.ToKey()), effect);
    }

    /// <summary>Where one key's entry lives.</summary>
    /// <remarks>
    ///     A hash rather than the key's own text, which contains <c>[</c>, <c>=</c>, <c>,</c> and
    ///     <c>{</c> and can name eight compose slots — legal in a key, not legal or not short enough
    ///     in a file name on every platform this runs on. The key is in the record anyway, so nothing
    ///     is lost but readability, and a directory of two thousand shader variants was never going
    ///     to be read.
    /// </remarks>
    public string PathOf(EffectKey key) => Path.Combine(Directory, Name(key) + Extension);

    /// <summary>The entry name for a key: the target and the key, hashed together.</summary>
    string Name(EffectKey key) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{Target}\n{key}")));

    EffectData? Read(string path) {
        byte[] bytes;

        try {
            bytes = File.ReadAllBytes(path);
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
            return null;
        }

        EffectData effect;

        try {
            effect = Serializer.Read<EffectData>(bytes);
        } catch (Exception exception) when (exception is SerializationException or InvalidDataException
                                                or ArgumentOutOfRangeException or IndexOutOfRangeException) {
            // A half-written or older entry. Absent, not fatal — see the class remarks.
            return null;
        }

        if (Expect.Length > 0 && !string.Equals(effect.SourceHash, Expect, StringComparison.Ordinal)) {
            return null;
        }

        return effect;
    }

    void Write(string path, EffectData effect) {
        // Written to a temporary neighbour and moved into place, because a reader may be another
        // process — two editors on one project, a build running while the game does. A move is
        // atomic within a volume, so a reader sees the whole entry or no entry, never the first
        // half of one.
        var temporary = path + ".tmp" + Environment.CurrentManagedThreadId.ToString(System.Globalization.CultureInfo.InvariantCulture);

        try {
            lock (writing) {
                System.IO.Directory.CreateDirectory(Directory);
                File.WriteAllBytes(temporary, Serializer.ToBytes(effect));
                File.Move(temporary, path, overwrite: true);
                Writes++;
            }
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
            try {
                File.Delete(temporary);
            } catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) {
                // Nothing further to do: the cache is already degraded to "does not cache".
            }
        }
    }
}
