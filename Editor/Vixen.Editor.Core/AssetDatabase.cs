// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO.Hashing;
using Vixen.Core;
using Vixen.Core.Yaml;
using Vixen.Core.Yaml.Meta;

namespace Vixen.Editor.Core;

/// <summary>Every asset in a project, by GUID and by path.</summary>
/// <remarks>
///     <para>
///         <b>The GUID is the identity and the path is a fact about today.</b> Everything stored in a
///         file — a material's reference to a texture, a scene's reference to a prefab — is a GUID,
///         so moving, renaming or reorganising folders changes nothing anywhere. This is the thing
///         that makes that true: the one place that knows which GUID is currently at which path.
///     </para>
///     <para>
///         <b>Rebuilt by scanning sidecars, and only their envelopes.</b>
///         [08](../../docs/plan/08-asset-pipeline-and-addressables.md) budgets a hundred-thousand
///         asset rebuild at under ten seconds. That is achievable because <see cref="MetaScanner" />
///         reads three lines of each file and stops, and because the files are read in parallel —
///         this is an I/O-bound walk, and the machine has cores idle during it.
///     </para>
///     <para>
///         <b>Nothing here is silently tolerant.</b> A file with no sidecar gets one; a sidecar with
///         no file is moved aside rather than deleted, because a mis-ordered git operation should be
///         recoverable; two assets claiming one GUID — the copy-pasted-folder disaster — are resolved
///         by rule and reported by name. Silent tolerance is how projects rot, and every one of those
///         is a real thing that happens to real projects weekly.
///     </para>
/// </remarks>
public sealed class AssetDatabase {
    const string IndexHeader = "vixen-guid-index 1";

    readonly Dictionary<AssetId, AssetEntry> byGuid = [];
    readonly Dictionary<string, AssetEntry> byPath = new(StringComparer.Ordinal);

    int indexedMetaCount;
    long indexedNewestTicks;

    /// <summary>Where the project's directories are.</summary>
    public ProjectPaths Paths { get; }

    /// <summary>How many assets are indexed.</summary>
    public int Count => byPath.Count;

    /// <summary>Every asset, in no particular order.</summary>
    public IReadOnlyCollection<AssetEntry> Entries => byPath.Values;

    /// <summary>Opens a database over a project.</summary>
    /// <param name="paths">The project's directories.</param>
    public AssetDatabase(ProjectPaths paths) {
        ArgumentNullException.ThrowIfNull(paths);
        Paths = paths;
    }

    /// <summary>Finds an asset by its identity.</summary>
    /// <param name="guid">The GUID.</param>
    /// <param name="entry">What is there.</param>
    /// <returns>Whether anything is.</returns>
    public bool TryGetByGuid(AssetId guid, out AssetEntry entry) => byGuid.TryGetValue(guid, out entry);

    /// <summary>Finds an asset by where it is.</summary>
    /// <param name="relativePath">The project-relative path, with forward slashes.</param>
    /// <param name="entry">What is there.</param>
    /// <returns>Whether anything is.</returns>
    public bool TryGetByPath(string relativePath, [MaybeNullWhen(false)] out AssetEntry entry) =>
        byPath.TryGetValue(relativePath, out entry);

    /// <summary>Rebuilds the index from the sidecars on disk.</summary>
    /// <param name="options">What to repair, or <see langword="null" /> to repair everything.</param>
    /// <returns>What it did.</returns>
    public ScanReport Scan(ScanOptions? options = null) {
        options ??= ScanOptions.Default;
        var clock = Stopwatch.StartNew();
        var issues = new ConcurrentBag<AssetIssue>();

        byGuid.Clear();
        byPath.Clear();

        if (!Directory.Exists(Paths.Assets)) {
            return new(0, clock.Elapsed, []);
        }

        Quarantine(options, issues);

        var candidates = Candidates();
        var found = new AssetEntry?[candidates.Count];

        // Parallel because this is an I/O walk over thousands of small files and the cores are idle
        // during it. Each index is written by exactly one iteration, so the array needs no locking.
        Parallel.For(0, candidates.Count, index => found[index] = Read(candidates[index], options, issues));

        // Insertion is sequential and in path order, so two machines scanning one checkout resolve a
        // duplicate the same way. A parallel insert would make the winner depend on thread timing.
        foreach (var entry in found) {
            if (entry is { } present) {
                Insert(present, options, issues);
            }
        }

        RecordFreshness();
        return new(Count, clock.Elapsed, [.. issues.OrderBy(issue => issue.Path, StringComparer.Ordinal)]);
    }

    /// <summary>Writes the index to <c>Library/GuidIndex</c>.</summary>
    /// <remarks>
    ///     Tab-separated text rather than a binary blob. It lives in <c>Library/</c>, so it is never
    ///     committed and never has to be version-compatible with anything; what it does have to be is
    ///     readable by a person at four in the morning wondering why the editor thinks a texture is
    ///     somewhere it is not.
    /// </remarks>
    public void Save() {
        Directory.CreateDirectory(Paths.Library);
        using var writer = new StreamWriter(Paths.GuidIndexFile);
        writer.NewLine = "\n";
        writer.WriteLine(IndexHeader);

        writer.WriteLine(
            string.Create(CultureInfo.InvariantCulture, $"{indexedMetaCount}\t{indexedNewestTicks}")
        );

        foreach (var entry in byPath.Values.OrderBy(entry => entry.Path, StringComparer.Ordinal)) {
            writer.WriteLine(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{entry.Guid}\t{entry.MetaVersion}\t{(entry.IsFolder ? 1 : 0)}\t{entry.ImporterTag}\t{entry.Path}"
                )
            );
        }
    }

    /// <summary>Reads the index back, if it is there.</summary>
    /// <returns>Whether it was.</returns>
    public bool TryLoad() {
        if (!File.Exists(Paths.GuidIndexFile)) {
            return false;
        }

        using var reader = new StreamReader(Paths.GuidIndexFile);

        if (reader.ReadLine() != IndexHeader) {
            return false;
        }

        var freshness = reader.ReadLine()?.Split('\t');

        if (freshness is not { Length: 2 }
            || !int.TryParse(freshness[0], CultureInfo.InvariantCulture, out var metaCount)
            || !long.TryParse(freshness[1], CultureInfo.InvariantCulture, out var newest)) {
            return false;
        }

        byGuid.Clear();
        byPath.Clear();
        indexedMetaCount = metaCount;
        indexedNewestTicks = newest;

        while (reader.ReadLine() is { } line) {
            var parts = line.Split('\t');

            if (parts.Length != 5
                || !AssetId.TryParse(parts[0], out var guid)
                || !int.TryParse(parts[1], CultureInfo.InvariantCulture, out var version)) {
                continue;
            }

            var entry = new AssetEntry(
                guid,
                parts[4],
                parts[3].Length == 0 ? null : parts[3],
                version,
                parts[2] == "1"
            );

            byGuid[guid] = entry;
            byPath[entry.Path] = entry;
        }

        return true;
    }

    /// <summary>Whether the sidecars on disk have moved on since the index was written.</summary>
    /// <remarks>
    ///     <para>
    ///         A count and a newest-write-time, compared without reading a single file. It is a
    ///         heuristic and it is allowed to be: a same-second edit that leaves the count unchanged
    ///         slips past it.
    ///     </para>
    ///     <para>
    ///         That is acceptable because the index is a <b>cold-start optimisation and not the source
    ///         of truth</b> — a running editor watches the filesystem and updates the index as things
    ///         change. This answers "is it worth loading, or should I just rescan", and being wrong
    ///         in the cheap direction costs one rescan.
    ///     </para>
    /// </remarks>
    /// <returns>Whether a rescan is worthwhile.</returns>
    public bool IsStale() {
        var (count, newest) = SurveySidecars();
        return count != indexedMetaCount || newest != indexedNewestTicks;
    }

    List<(string Absolute, bool IsFolder)> Candidates() {
        var candidates = new List<(string, bool)>();

        foreach (var directory in Directory.EnumerateDirectories(Paths.Assets, "*", SearchOption.AllDirectories)) {
            candidates.Add((directory, true));
        }

        foreach (var file in Directory.EnumerateFiles(Paths.Assets, "*", SearchOption.AllDirectories)) {
            if (!file.EndsWith(AssetMetaFile.Extension, StringComparison.Ordinal)) {
                candidates.Add((file, false));
            }
        }

        // Sorted so that the scan, the duplicate resolution and the report are the same on every
        // machine. Directory enumeration order is not a promise any filesystem makes.
        candidates.Sort(static (left, right) => string.CompareOrdinal(left.Item1, right.Item1));
        return candidates;
    }

    AssetEntry? Read((string Absolute, bool IsFolder) candidate, ScanOptions options, ConcurrentBag<AssetIssue> issues) {
        var relative = Paths.Relative(candidate.Absolute);
        var metaPath = AssetMetaFile.PathFor(candidate.Absolute);

        if (!File.Exists(metaPath)) {
            if (!options.CreateMissingMeta) {
                issues.Add(new(AssetIssueKind.MetaCreated, relative, "Has no .meta, and none was created."));
                return null;
            }

            var minted = CreateMeta(metaPath);
            issues.Add(new(AssetIssueKind.MetaCreated, relative, $"Had no .meta; created one with GUID {minted}."));
            return new(minted, relative, null, MetaMigrationChain.CurrentVersion, candidate.IsFolder);
        }

        if (!MetaScanner.TryScanFile(metaPath, out var envelope)) {
            issues.Add(
                new(
                    AssetIssueKind.MetaUnreadable,
                    relative,
                    "Its .meta has no readable GUID. It is being ignored rather than re-created, because "
                    + "minting a new GUID would break every reference to this asset."
                )
            );

            return null;
        }

        return new(envelope.Guid, relative, envelope.ImporterTag, envelope.MetaVersion, candidate.IsFolder);
    }

    void Insert(AssetEntry entry, ScanOptions options, ConcurrentBag<AssetIssue> issues) {
        byPath[entry.Path] = entry;

        if (!byGuid.TryGetValue(entry.Guid, out var existing)) {
            byGuid[entry.Guid] = entry;
            return;
        }

        // Two assets claiming one GUID: someone copied a folder, or merged two branches that each
        // added a file. The one whose recorded source hash still matches its file is the original.
        var incomingMatches = SourceHashMatches(entry);
        var existingMatches = SourceHashMatches(existing);

        // If the hashes do not settle it, the one already in the index keeps the GUID — and because
        // insertion is in path order, that is "the first path in order", which is a rule rather than
        // an accident of which file the filesystem handed over first.
        var incomingWins = incomingMatches && !existingMatches;
        var winner = incomingWins ? entry : existing;
        var loser = incomingWins ? existing : entry;

        var message =
            $"'{winner.Path}' and '{loser.Path}' both claim GUID {entry.Guid}. "
            + (incomingMatches != existingMatches
                ? "The one whose recorded sourceHash still matches its file kept it."
                : "Neither sourceHash settled it, so the first path in order kept it.");

        if (!options.ResolveDuplicateGuids) {
            byGuid[winner.Guid] = winner;
            issues.Add(new(AssetIssueKind.DuplicateGuid, loser.Path, message + " Nothing was changed."));
            return;
        }

        var minted = ReGuid(loser);
        var repaired = loser with { Guid = minted };
        byGuid[winner.Guid] = winner;
        byGuid[minted] = repaired;
        byPath[repaired.Path] = repaired;
        issues.Add(new(AssetIssueKind.DuplicateGuid, loser.Path, $"{message} '{loser.Path}' was re-GUIDed to {minted}."));
    }

    void Quarantine(ScanOptions options, ConcurrentBag<AssetIssue> issues) {
        foreach (var meta in Directory.EnumerateFiles(Paths.Assets, "*.meta", SearchOption.AllDirectories)) {
            var owner = meta[..^AssetMetaFile.Extension.Length];

            if (File.Exists(owner) || Directory.Exists(owner)) {
                continue;
            }

            var relative = Paths.Relative(meta);

            if (!options.QuarantineOrphanMeta) {
                issues.Add(new(AssetIssueKind.MetaOrphaned, relative, "Its asset is gone. It was left where it is."));
                continue;
            }

            // Moved, never deleted. A mis-ordered git operation — the asset removed before its
            // sidecar, a partial checkout — is recoverable if the GUID is still somewhere on disk,
            // and is not if the editor helpfully tidied it away.
            var destination = Path.Combine(Paths.OrphanMeta, Paths.Relative(meta));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Move(meta, destination, overwrite: true);
            issues.Add(new(AssetIssueKind.MetaOrphaned, relative, $"Its asset is gone. Moved to '{Paths.Relative(destination)}'."));
        }
    }

    /// <summary>Whether a sidecar's recorded source hash still describes the file next to it.</summary>
    /// <remarks>
    ///     Read out of the node tree rather than by binding the settings, because <c>sourceHash</c>
    ///     belongs to whichever importer wrote it and this has no business knowing which that is —
    ///     and because binding a settings record for an importer that is no longer installed would
    ///     throw on exactly the file most likely to be in trouble.
    /// </remarks>
    bool SourceHashMatches(AssetEntry entry) {
        if (entry.IsFolder) {
            return false;
        }

        var absolute = Paths.Absolute(entry.Path);
        var metaPath = AssetMetaFile.PathFor(absolute);

        try {
            if (YamlReader.Read(File.ReadAllText(metaPath)) is not YamlMapping root
                || root["importer"] is not YamlMapping importer
                || importer["sourceHash"] is not YamlScalar recorded
                || recorded.Value.Length == 0) {
                return false;
            }

            return string.Equals(recorded.Value, HashOf(absolute), StringComparison.OrdinalIgnoreCase);
        } catch (Exception failure) when (failure is IOException or YamlParseException) {
            return false;
        }
    }

    AssetId ReGuid(AssetEntry entry) {
        var metaPath = AssetMetaFile.PathFor(Paths.Absolute(entry.Path));
        var minted = AssetId.New();

        // Read and rewrite as nodes, so everything the file said other than its GUID — the importer
        // settings, the addressable block, the comments — comes back out byte for byte.
        var root = YamlReader.Read(File.ReadAllText(metaPath)) as YamlMapping ?? new YamlMapping();
        root.Set("guid", new YamlScalar(minted.ToString()));
        File.WriteAllText(metaPath, YamlWriter.Write(root));
        return minted;
    }

    static AssetId CreateMeta(string metaPath) {
        var minted = AssetId.New();

        // No importer key: which importer claims a file is decided at import time, and writing a
        // guess here would be a fact the file asserts and nothing checks.
        var root = new YamlMapping()
            .Set("guid", new YamlScalar(minted.ToString()))
            .Set(
                "metaVersion",
                new YamlScalar(
                    MetaMigrationChain.CurrentVersion.ToString(CultureInfo.InvariantCulture),
                    YamlScalarStyle.Plain
                )
            );

        File.WriteAllText(metaPath, YamlWriter.Write(root));
        return minted;
    }

    static string HashOf(string path) {
        using var stream = File.OpenRead(path);
        var hash = new XxHash128();
        hash.Append(stream);
        return Convert.ToHexStringLower(hash.GetCurrentHash());
    }

    void RecordFreshness() => (indexedMetaCount, indexedNewestTicks) = SurveySidecars();

    (int Count, long NewestTicks) SurveySidecars() {
        if (!Directory.Exists(Paths.Assets)) {
            return (0, 0);
        }

        var count = 0;
        var newest = 0L;

        foreach (var meta in Directory.EnumerateFiles(Paths.Assets, "*.meta", SearchOption.AllDirectories)) {
            count++;
            var written = File.GetLastWriteTimeUtc(meta).Ticks;

            if (written > newest) {
                newest = written;
            }
        }

        return (count, newest);
    }
}
