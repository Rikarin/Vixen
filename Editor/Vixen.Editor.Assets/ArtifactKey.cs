// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.IO.Hashing;
using System.Text;
using Vixen.Core;

namespace Vixen.Editor.Assets;

/// <summary>What decides whether an importer runs at all.</summary>
/// <remarks>
///     <para>
///         <c>xxh128(importerType, importerVersion, sourceHash, settingsHash, target,
///         sorted(dependencyArtifactIds))</c>, exactly as
///         [08](../../docs/plan/08-asset-pipeline-and-addressables.md) specifies. A hit skips the
///         importer entirely.
///     </para>
///     <para>
///         Every part earns its place by naming something that, when it changes, must produce a
///         different artefact:
///     </para>
///     <list type="bullet">
///         <item>
///             <b>importerType and importerVersion</b> — "I fixed the mip filter, re-import
///             everything" is a version bump and nothing else.
///         </item>
///         <item><b>sourceHash</b> — the artist changed the file.</item>
///         <item><b>settingsHash</b> — someone changed a setting in the inspector.</item>
///         <item>
///             <b>target</b> — the same texture is BC7 on a desktop and ASTC on a phone, and the two
///             must not share a cache entry.
///         </item>
///         <item>
///             <b>dependencies</b> — the texture a material points at was replaced, so the material's
///             artefact is stale even though nothing about the material changed.
///         </item>
///     </list>
///     <para>
///         <b>The dependencies are sorted, and that is not tidiness.</b> A key that depended on the
///         order a set enumerated in would differ between two machines with identical inputs, which
///         turns a shared artefact cache from a speed-up into a source of confusion — and doc 08
///         wants exactly that cache shared across CI machines.
///     </para>
/// </remarks>
/// <param name="Value">The 128 bits.</param>
public readonly record struct ArtifactKey(ObjectId Value) {
    /// <summary>Renders the key as 32 lowercase hex digits.</summary>
    /// <returns>The text.</returns>
    public override string ToString() => Value.ToString();

    /// <summary>Computes a key.</summary>
    /// <param name="importer">The importer's name.</param>
    /// <param name="importerVersion">Its version.</param>
    /// <param name="sourceHash">What the source file hashes to.</param>
    /// <param name="settingsHash">What the resolved settings hash to.</param>
    /// <param name="target">Which build target, or empty for none in particular.</param>
    /// <param name="dependencies">The artefact ids this import depends on, in any order.</param>
    /// <returns>The key.</returns>
    public static ArtifactKey Compute(
        string importer,
        int importerVersion,
        ObjectId sourceHash,
        ObjectId settingsHash,
        string target,
        IEnumerable<ObjectId>? dependencies = null
    ) {
        ArgumentException.ThrowIfNullOrEmpty(importer);
        ArgumentNullException.ThrowIfNull(target);

        var hash = new XxHash128();
        Feed(hash, importer);
        Feed(hash, importerVersion.ToString(CultureInfo.InvariantCulture));
        Feed(hash, sourceHash.ToString());
        Feed(hash, settingsHash.ToString());
        Feed(hash, target);

        foreach (var dependency in (dependencies ?? []).Order()) {
            Feed(hash, dependency.ToString());
        }

        return new(ObjectId.FromBytes(hash.GetCurrentHash()));
    }

    /// <summary>Hashes a stream of bytes — a source file, an artefact.</summary>
    /// <param name="content">The bytes.</param>
    /// <returns>Their hash.</returns>
    public static ObjectId HashOf(Stream content) {
        ArgumentNullException.ThrowIfNull(content);
        var hash = new XxHash128();
        hash.Append(content);
        return ObjectId.FromBytes(hash.GetCurrentHash());
    }

    /// <summary>Hashes a span of bytes.</summary>
    /// <param name="content">The bytes.</param>
    /// <returns>Their hash.</returns>
    public static ObjectId HashOf(ReadOnlySpan<byte> content) => ObjectId.FromBytes(XxHash128.Hash(content));

    /// <summary>Hashes text — the emitted form of a settings block.</summary>
    /// <param name="text">The text.</param>
    /// <returns>Its hash.</returns>
    /// <remarks>
    ///     Settings are hashed as their emitted YAML rather than field by field, so that this needs
    ///     to know nothing about any importer's schema and an added setting changes the hash without
    ///     anybody remembering to include it. The dialect's byte fidelity is what makes that stable:
    ///     the same settings emit the same bytes.
    /// </remarks>
    public static ObjectId HashOf(string text) {
        ArgumentNullException.ThrowIfNull(text);
        return ObjectId.FromBytes(XxHash128.Hash(Encoding.UTF8.GetBytes(text)));
    }

    static void Feed(XxHash128 hash, string part) {
        hash.Append(Encoding.UTF8.GetBytes(part));
        hash.Append([0]);
    }
}
