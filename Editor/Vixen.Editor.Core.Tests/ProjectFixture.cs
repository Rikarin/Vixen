// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Core;

namespace Vixen.Editor.Core.Tests;

/// <summary>A throwaway project on disk, deleted when the test finishes.</summary>
/// <remarks>
///     Real files rather than an abstracted filesystem. Everything the database does that is worth
///     testing — moving an orphan aside, rewriting a duplicate's GUID, reading ten thousand sidecars
///     in parallel — is about the filesystem, and a fake one would only prove the fake works.
/// </remarks>
public sealed class ProjectFixture : IDisposable {
    /// <summary>Where the project's directories are.</summary>
    public ProjectPaths Paths { get; }

    /// <summary>Creates an empty project in the temporary directory.</summary>
    public ProjectFixture() {
        Paths = new(Path.Combine(Path.GetTempPath(), "vixen-tests", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(Paths.Assets);
    }

    /// <summary>Writes an asset and its sidecar.</summary>
    /// <param name="relativePath">Where, under the project root.</param>
    /// <param name="content">What is in the asset.</param>
    /// <param name="guid">Its GUID, or <see langword="null" /> for a fresh one.</param>
    /// <param name="sourceHash">What the sidecar claims the asset hashes to.</param>
    /// <param name="importer">Which importer the sidecar names.</param>
    /// <returns>The GUID.</returns>
    public AssetId Add(
        string relativePath,
        string content = "content",
        AssetId? guid = null,
        string? sourceHash = null,
        string importer = "TextureImporter"
    ) {
        var absolute = Paths.Absolute(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        File.WriteAllText(absolute, content);

        var identity = guid ?? AssetId.New();
        WriteMeta(absolute, identity, sourceHash, importer);
        return identity;
    }

    /// <summary>Writes an asset with no sidecar.</summary>
    /// <param name="relativePath">Where, under the project root.</param>
    /// <param name="content">What is in the asset.</param>
    public void AddWithoutMeta(string relativePath, string content = "content") {
        var absolute = Paths.Absolute(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        File.WriteAllText(absolute, content);
    }

    /// <summary>Writes a sidecar with no asset next to it.</summary>
    /// <param name="relativePath">Where the asset would have been.</param>
    /// <returns>The GUID the orphan claims.</returns>
    public AssetId AddOrphanMeta(string relativePath) {
        var absolute = Paths.Absolute(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        var identity = AssetId.New();
        WriteMeta(absolute, identity, null, "TextureImporter");
        return identity;
    }

    /// <summary>Writes a sidecar for an asset that is already there.</summary>
    /// <param name="relativePath">The asset.</param>
    /// <param name="guid">The GUID it should claim.</param>
    /// <param name="sourceHash">What it claims the asset hashes to.</param>
    /// <param name="importer">Which importer it names.</param>
    public void WriteMetaFor(string relativePath, AssetId guid, string? sourceHash = null, string importer = "TextureImporter") =>
        WriteMeta(Paths.Absolute(relativePath), guid, sourceHash, importer);

    /// <summary>What an asset's bytes actually hash to, for a sidecar that should agree with them.</summary>
    /// <param name="content">The asset's content.</param>
    /// <returns>The hash, as 32 lowercase hex digits.</returns>
    public static string HashOf(string content) {
        var hash = new System.IO.Hashing.XxHash128();
        hash.Append(System.Text.Encoding.UTF8.GetBytes(content));
        return Convert.ToHexStringLower(hash.GetCurrentHash());
    }

    /// <inheritdoc />
    public void Dispose() {
        try {
            if (Directory.Exists(Paths.Root)) {
                Directory.Delete(Paths.Root, recursive: true);
            }
        } catch (IOException) {
            // A temporary directory that would not go is not a test failure.
        }
    }

    static void WriteMeta(string assetPath, AssetId guid, string? sourceHash, string importer) {
        var text = string.Create(
            CultureInfo.InvariantCulture,
            $"guid: {guid}\nmetaVersion: 1\nimporter: !{importer}\n  version: 1\n"
        );

        if (sourceHash is not null) {
            text += $"  sourceHash: {sourceHash}\n";
        }

        File.WriteAllText(assetPath + ".meta", text);
    }
}
