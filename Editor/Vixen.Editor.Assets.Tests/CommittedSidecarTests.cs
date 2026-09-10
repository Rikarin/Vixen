// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Yaml;
using Vixen.Core.Yaml.Meta;
using Xunit;

namespace Vixen.Editor.Assets.Tests;

/// <summary>
///     Every <c>.meta</c> committed to this repository, against the file it sits beside.
/// </summary>
/// <remarks>
///     <para>
///         <b>What this exists for</b> —
///         <a href="https://github.com/Rikarin/Vixen/issues/1223">#1223</a>. A full <c>Test</c> run
///         rewrote one sidecar's <c>sourceHash</c> and nothing else in the tree, and the reason was
///         that the <c>.vxmat</c> beside it had been edited — a comment, in
///         <c>63a9d7a01</c> — without an import running afterwards. The sidecar kept the pre-edit
///         hash, and the next import that actually ran corrected it.
///     </para>
///     <para>
///         ⚠ <b>Nothing could see that, and a warm cache is why.</b>
///         <c>ImportPipeline.Prepare</c> recomputes the hash from the source's bytes and never reads
///         the one in the sidecar, so a stale value does not invalidate anything; and
///         <c>ImportPipeline.ImportAsync</c> returns on the reuse path before <c>WriteBack</c> ever
///         runs, so once the cache holds a record at the new key, no later run rewrites the file.
///         The disagreement then survives indefinitely, invisible to every run that starts warm.
///     </para>
///     <para>
///         <b>It is not only a record.</b> <c>AssetDatabase.Insert</c> breaks a duplicate-GUID tie
///         by asking which of the two claimants' recorded hashes still describes its file, so an
///         asset carrying a stale one loses that tie-break to a copy of itself.
///     </para>
///     <para>
///         ⚠ <b>The instrument, before the claim.</b> A tree walk that finds nothing passes every
///         assertion written over it — and this walk has two ways to find nothing: an assembly that
///         is not inside the repository at all, and the <c>.claude/worktrees</c> filter matching the
///         whole tree because this checkout is itself under one. So the count of sidecars actually
///         opened is asserted first, against a floor well below the number in the tree.
///     </para>
/// </remarks>
public sealed class CommittedSidecarTests {
    /// <summary>How many sidecars carrying a <c>sourceHash</c> the walk must find before its
    ///     silence means anything.</summary>
    /// <remarks>
    ///     Seventy-eight in the tree the day this was written, of ninety-seven sidecars — the other
    ///     nineteen are folders, which have no source to hash. The floor is a long way below that
    ///     because it is answering "did the walk run", not "is the tree the size it was".
    /// </remarks>
    const int Floor = 50;

    /// <summary>The hash a sidecar records is the hash of the bytes it sits beside.</summary>
    [Fact]
    public void EverySidecarRecordsTheHashOfTheFileItSitsBeside() {
        var checkedCount = 0;
        var disagreements = new List<string>();

        foreach (var meta in Sidecars()) {
            if (RecordedHash(meta) is not { } recorded) {
                continue;
            }

            var source = meta[..^AssetMetaFile.Extension.Length];

            if (!File.Exists(source)) {
                disagreements.Add($"{Relative(source)}: its .meta records a sourceHash and the file is gone.");
                continue;
            }

            checkedCount++;

            using var stream = File.OpenRead(source);
            var actual = ArtifactKey.HashOf(stream).ToString();

            if (!string.Equals(recorded, actual, StringComparison.OrdinalIgnoreCase)) {
                disagreements.Add(
                    $"{Relative(source)}: its .meta records {recorded} and its bytes hash to {actual}. "
                    + "The file was edited without an import running, so the sidecar is stale — "
                    + "re-import it, or correct the recorded value."
                );
            }
        }

        Assert.True(
            checkedCount >= Floor,
            $"Only {checkedCount} sidecar(s) with a sourceHash were opened, under the floor of "
            + $"{Floor}, so this walk read next to nothing and the assertion below would have "
            + $"passed over an empty tree. Root: '{RepositoryRoot()}'."
        );

        Assert.True(
            disagreements.Count == 0,
            $"{disagreements.Count} of {checkedCount} committed sidecar(s) disagree with the file "
            + "they describe:\n  " + string.Join("\n  ", disagreements)
        );
    }

    /// <summary>
    ///     The <c>sourceHash</c> a sidecar records, or <see langword="null" /> if it records none.
    /// </summary>
    /// <remarks>
    ///     Read out of the node tree rather than by binding the settings, for
    ///     <c>AssetDatabase.SourceHashMatches</c>' reason: <c>sourceHash</c> belongs to whichever
    ///     importer wrote it, and binding a settings record for an importer that is not installed
    ///     here would throw on exactly the file most likely to be in trouble.
    /// </remarks>
    static string? RecordedHash(string metaPath) =>
        YamlReader.Read(File.ReadAllText(metaPath)) is YamlMapping root
        && root["importer"] is YamlMapping importer
        && importer["sourceHash"] is YamlScalar { Value.Length: > 0 } recorded
            ? recorded.Value
            : null;

    /// <summary>
    ///     ⚠ Skipping <c>.claude</c>, which holds a whole checkout of this repository per agent. A
    ///     walk that descends into it reads another agent's copy of these same files, and the
    ///     exclusions are matched against the path <em>below the repository root</em> because this
    ///     checkout may itself be inside a <c>.claude/worktrees</c> directory.
    /// </summary>
    static IEnumerable<string> Sidecars() {
        var root = RepositoryRoot();
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };

        return Directory.EnumerateFiles(root, "*" + AssetMetaFile.Extension, options)
            .Where(path => !Segments(Path.GetRelativePath(root, path))
                .Any(segment => segment is ".claude" or ".git" or "bin" or "obj" or "artifacts" or "node_modules")
            );
    }

    static IEnumerable<string> Segments(string path) => path.Replace('\\', '/').Split('/');

    static string Relative(string path) =>
        Path.GetRelativePath(RepositoryRoot(), path).Replace('\\', '/');

    static string RepositoryRoot() {
        var directory = AppContext.BaseDirectory;

        while (directory is not null) {
            if (File.Exists(Path.Combine(directory, "Vixen.slnx"))) {
                return directory;
            }

            directory = Path.GetDirectoryName(directory);
        }

        throw new InvalidOperationException("No Vixen.slnx above the test assembly, so no repository root.");
    }
}
