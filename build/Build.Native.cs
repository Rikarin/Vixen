// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nuke.Common;
using Nuke.Common.IO;
using Serilog;

/// <summary>
///     Acquiring the native binaries the engine links against, from pinned and checksummed sources.
/// </summary>
/// <remarks>
///     <para>
///         Spec: docs/plan/10 § Native binaries, and R10 — six native dependencies across ten RIDs,
///         each with its own cadence and licence. The mitigation R10 commits to is that all of it
///         lives in one manifest, none of it is committed, and a version bump is a single reviewed
///         diff. This is that target.
///     </para>
///     <para>
///         <b>Nothing here trusts the network.</b> An archive is accepted only if its SHA-256 is the
///         one <c>build/native-dependencies.json</c> names, and a mismatch deletes the file rather
///         than leaving it in the cache to be believed next time. That is the difference between a
///         checksum and a comment: an unverified download that happens to work is indistinguishable
///         from a verified one right up until the day it is not.
///     </para>
///     <para>
///         <b>And nothing here trusts the archive's shape either.</b> Only the files the manifest
///         names are extracted, by exact entry path, and an entry that is absent fails the target.
///         Globbing an archive into a directory is how a layout change becomes a silent no-op that
///         is found much later at link time.
///     </para>
/// </remarks>
partial class Build {
    /// <summary>Where the restored binaries live: one directory per runtime identifier.</summary>
    /// <remarks>
    ///     Under <c>artifacts/</c>, which is already ignored by git — so "never committed" is a
    ///     property of the layout rather than a rule somebody has to remember.
    /// </remarks>
    AbsolutePath NativeDirectory => ArtifactsDirectory / "native";

    /// <summary>Downloaded archives, keyed by the hash they were verified against.</summary>
    /// <remarks>
    ///     Keyed by hash rather than by file name so that re-pinning to a new version cannot hit a
    ///     stale cache entry: a different version is a different key, and the old one is simply
    ///     never asked for again.
    /// </remarks>
    AbsolutePath NativeCacheDirectory => NativeDirectory / ".cache";

    AbsolutePath NativeLicenceDirectory => NativeDirectory / "licences";

    AbsolutePath NativeManifestFile => RootDirectory / "build" / "native-dependencies.json";

    Target RestoreNativeDeps => definition => definition
        .Description("Downloads and verifies the pinned native binaries named in build/native-dependencies.json")
        .Executes(async () => {
                var manifest = ReadNativeManifest();

                NativeCacheDirectory.CreateDirectory();
                NativeLicenceDirectory.CreateDirectory();

                using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };

                foreach (var dependency in manifest.Dependencies) {
                    foreach (var artifact in dependency.Artifacts) {
                        var archive = await EnsureArchive(client, dependency, artifact);
                        ExtractArtifact(archive, dependency, artifact);
                    }
                }

                WriteLicenceManifest(manifest);
            }
        );

    /// <summary>Comments and trailing commas are allowed, because the manifest carries its reasons.</summary>
    static readonly JsonSerializerOptions ManifestFormat = new() {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    NativeManifest ReadNativeManifest() {
        Assert.FileExists(NativeManifestFile);

        var manifest = JsonSerializer.Deserialize<NativeManifest>(NativeManifestFile.ReadAllText(), ManifestFormat);

        return manifest is null || manifest.Dependencies.Count == 0
            ? throw new InvalidOperationException($"{NativeManifestFile} names no dependencies.")
            : manifest;
    }

    /// <summary>The archive, downloaded if it is not cached and verified either way.</summary>
    /// <remarks>
    ///     A cached file is re-hashed rather than trusted for existing. It costs a fraction of a
    ///     second on thirty megabytes, and the alternative is that a half-written file from an
    ///     interrupted run is indistinguishable from a good one.
    /// </remarks>
    async Task<AbsolutePath> EnsureArchive(HttpClient client, NativeDependency dependency, NativeArtifact artifact) {
        var cached = NativeCacheDirectory / $"{artifact.Sha256}{ArchiveExtension(artifact.Url)}";

        if (cached.FileExists() && Hash(cached).Equals(artifact.Sha256, StringComparison.OrdinalIgnoreCase)) {
            Log.Information("{Name} {Version} for {Rid}: cached and verified", dependency.Name, dependency.Version, artifact.Rid);
            return cached;
        }

        cached.DeleteFile();

        Log.Information(
            "{Name} {Version} for {Rid}: downloading {Size:N0} bytes from {Url}",
            dependency.Name,
            dependency.Version,
            artifact.Rid,
            artifact.Size,
            artifact.Url
        );

        // Written to a sibling and moved, so an interrupted download cannot be found later under the
        // name that means "verified".
        var partial = cached + ".partial";
        partial.DeleteFile();

        await using (var response = await client.GetStreamAsync(artifact.Url))
        await using (var file = File.Create(partial)) {
            await response.CopyToAsync(file);
        }

        var actual = Hash(partial);

        if (!actual.Equals(artifact.Sha256, StringComparison.OrdinalIgnoreCase)) {
            partial.DeleteFile();

            throw new InvalidOperationException(
                $"""
                 {dependency.Name} {dependency.Version} for {artifact.Rid} does not match its pinned checksum.
                   url:      {artifact.Url}
                   expected: {artifact.Sha256}
                   actual:   {actual}
                 The download was discarded. Either the pin in build/native-dependencies.json is wrong,
                 or what is being served is not what was pinned.
                 """
            );
        }

        File.Move(partial, cached);
        return cached;
    }

    /// <summary>Pulls exactly the named entries out of the archive, and the licence beside them.</summary>
    void ExtractArtifact(AbsolutePath archive, NativeDependency dependency, NativeArtifact artifact) {
        var destination = NativeDirectory / artifact.Rid;
        destination.CreateDirectory();

        var wanted = artifact.Files.ToDictionary(
            file => file.From,
            file => (AbsolutePath)(destination / file.To),
            StringComparer.Ordinal
        );

        if (dependency.LicenceFile is { Length: > 0 } licence) {
            wanted[licence] = NativeLicenceDirectory / $"{dependency.Id}-LICENSE";
        }

        var found = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (entry, name) in ReadEntries(archive)) {
            if (!wanted.TryGetValue(name, out var target)) {
                continue;
            }

            target.Parent.CreateDirectory();
            target.DeleteFile();
            entry(target);
            found.Add(name);
        }

        var missing = wanted.Keys.Except(found, StringComparer.Ordinal).ToList();

        if (missing.Count > 0) {
            throw new InvalidOperationException(
                $"""
                 {dependency.Name} {dependency.Version}: {archive.Name} does not contain {missing.Count} of the
                 entries build/native-dependencies.json names for {artifact.Rid}:
                   {string.Join("\n  ", missing)}
                 The archive verified against its checksum, so this is a manifest that has drifted from
                 the layout of the release it pins rather than a corrupt download.
                 """
            );
        }

        foreach (var file in artifact.Files) {
            Log.Information(
                "  {Rid}/{File} ({Size:N0} bytes)",
                artifact.Rid,
                file.To,
                new FileInfo(destination / file.To).Length
            );
        }
    }

    /// <summary>
    ///     Every entry in the archive, as a name and a delegate that writes it somewhere.
    /// </summary>
    /// <remarks>
    ///     One pass, streaming: a tar cannot be seeked to a named entry, so the alternative to
    ///     walking it once is walking it once per file.
    /// </remarks>
    static IEnumerable<(Action<AbsolutePath> Extract, string Name)> ReadEntries(AbsolutePath archive) {
        if (archive.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) {
            using var zip = ZipFile.OpenRead(archive);

            foreach (var entry in zip.Entries) {
                yield return (target => entry.ExtractToFile(target, overwrite: true), entry.FullName);
            }

            yield break;
        }

        using var file = File.OpenRead(archive);
        Stream stream = archive.Name.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)
            ? new GZipStream(file, CompressionMode.Decompress)
            : file;

        using var reader = new TarReader(stream);

        while (reader.GetNextEntry() is { } entry) {
            if (entry.EntryType is TarEntryType.Directory) {
                continue;
            }

            yield return (target => entry.ExtractToFile(target, overwrite: true), entry.Name);
        }
    }

    /// <summary>
    ///     The third-party notice for the binaries, generated from what was actually verified.
    /// </summary>
    /// <remarks>
    ///     ADR-015 requires a manifest for the natives as well as the packages. Generated rather than
    ///     hand-kept, and generated from the same records the download used, so it cannot describe a
    ///     version that is not the one on disk. The licence text itself is copied out of the archive
    ///     beside it — a link to a licence is not a copy of one, and the obligation is to ship the
    ///     text.
    /// </remarks>
    void WriteLicenceManifest(NativeManifest manifest) {
        var text = new StringBuilder()
            .AppendLine("# Third-party native binaries")
            .AppendLine()
            .AppendLine("Generated by `nuke RestoreNativeDeps` from `build/native-dependencies.json`. Do not edit.")
            .AppendLine()
            .AppendLine("Each licence text is copied out of the archive it was verified from, into `licences/`.")
            .AppendLine();

        foreach (var dependency in manifest.Dependencies) {
            text.AppendLine($"## {dependency.Name} {dependency.Version}")
                .AppendLine()
                .AppendLine($"- Licence: {dependency.Licence} (`licences/{dependency.Id}-LICENSE`)")
                .AppendLine($"- Home: {dependency.Homepage}");

            foreach (var artifact in dependency.Artifacts) {
                text.AppendLine($"- `{artifact.Rid}`: {artifact.Url}")
                    .AppendLine($"  - sha256 `{artifact.Sha256}`")
                    .AppendLine($"  - files: {string.Join(", ", artifact.Files.Select(file => $"`{file.To}`"))}");
            }

            text.AppendLine();
        }

        (NativeDirectory / "THIRD-PARTY-NATIVE.md").WriteAllText(text.ToString());
        Log.Information("Wrote {File}", NativeDirectory / "THIRD-PARTY-NATIVE.md");
    }

    static string Hash(AbsolutePath file) {
        using var stream = File.OpenRead(file);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    /// <summary>The archive's extension, kept so the cached file can still be opened by kind.</summary>
    static string ArchiveExtension(string url) {
        var name = url.Split('/')[^1];

        return name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) ? ".tar.gz"
            : name.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase) ? ".tar.gz"
            : name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? ".zip"
            : ".tar";
    }

    sealed record NativeManifest(IReadOnlyList<NativeDependency> Dependencies);

    sealed record NativeDependency(
        string Id,
        string Name,
        string Version,
        string Homepage,
        string Licence,
        string? LicenceFile,
        IReadOnlyList<NativeArtifact> Artifacts
    );

    sealed record NativeArtifact(
        string Rid,
        string Url,
        string Sha256,
        long Size,
        IReadOnlyList<NativeFile> Files
    );

    sealed record NativeFile(string From, string To);
}
