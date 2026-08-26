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
///         <b>And rebuilt incrementally.</b> Each index entry records the size and write time of the
///         sidecar it came out of, so a scan over an index that was loaded from
///         <c>Library/GuidIndex</c> opens only the sidecars whose stamp has moved. That is what turns
///         a cold start with one changed file from a full rebuild into a directory walk — the
///         freshness question used to be asked once about the whole database, and a "no" cost
///         everything. Every reuse is still checked against the disk, so the index cannot be believed
///         into being wrong; the failure mode is a wasted read, never a missed asset.
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
    const string IndexHeader = "vixen-guid-index 2";
    const string ScannedPrefix = "scanned\t";
    const string TerminatorPrefix = "end\t";

    Dictionary<AssetId, AssetEntry> byGuid = [];
    Dictionary<string, AssetEntry> byPath = new(StringComparer.Ordinal);

    // The sidecar stamp each entry was read from, by the entry's project-relative path. Held beside
    // the entries rather than inside AssetEntry, which is documented as being the envelope and only
    // the envelope: this is a fact about the file the envelope came out of, not about the asset.
    //
    // An entry whose sidecar this scan *wrote* — minted, or re-GUIDed — records MetaStamp.Unknown
    // rather than the stamp the write left behind, which is what makes the next scan open it again.
    Dictionary<string, MetaStamp> stamps = new(StringComparer.Ordinal);

    // A second, weaker filter over the same question: nothing whose sidecar was written at or after
    // this instant is trusted, however well its stamp matches. It catches an edit by *somebody else*
    // that raced this scan — which the stamps cannot know about — on a filesystem whose write times
    // are fine-grained enough to place it. See MetaStamp for what it cannot do. Kinded UTC so that
    // it round trips through "O" as a UTC instant rather than as a local one nobody named.
    DateTime trustedBeforeUtc = DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc);

    // Injectable only so that a test can run a clock that leads the filesystem, which is what NTFS
    // and DateTime.UtcNow do to each other on Windows. Production always gets the system clock.
    readonly TimeProvider time;

    /// <summary>Where the project's directories are.</summary>
    public ProjectPaths Paths { get; }

    /// <summary>How many assets are indexed.</summary>
    public int Count => byPath.Count;

    /// <summary>Every asset, in no particular order.</summary>
    public IReadOnlyCollection<AssetEntry> Entries => byPath.Values;

    /// <summary>Opens a database over a project.</summary>
    /// <param name="paths">The project's directories.</param>
    public AssetDatabase(ProjectPaths paths) : this(paths, TimeProvider.System) { }

    /// <summary>Opens a database over a project, reading the time from somewhere a test chose.</summary>
    /// <param name="paths">The project's directories.</param>
    /// <param name="clock">Where "now" comes from.</param>
    internal AssetDatabase(ProjectPaths paths, TimeProvider clock) {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(clock);
        Paths = paths;
        time = clock;
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

    /// <summary>Brings the index up to date with the sidecars on disk.</summary>
    /// <param name="options">What to repair, or <see langword="null" /> to repair everything.</param>
    /// <returns>What it did, including how much of the previous index it was able to keep.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>Incremental against whatever the database already holds.</b> An asset whose sidecar
    ///         still has the size and write time the index recorded is kept without the file being
    ///         opened; everything else is read. So a scan after <see cref="TryLoad" /> costs one
    ///         directory walk plus the assets that actually moved, and a cold start with one changed
    ///         file is proportional to that one file rather than to the project.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Every reuse is checked against the disk, never assumed.</b> A truncated or
    ///         partly-written index cannot survive this: an entry whose stamp does not match is read,
    ///         and an entry with no asset under it is dropped. That is what makes a crash halfway
    ///         through a rescan cost a wasted read rather than an asset the editor never notices.
    ///     </para>
    /// </remarks>
    public ScanReport Scan(ScanOptions? options = null) {
        options ??= ScanOptions.Default;
        var clock = Stopwatch.StartNew();
        var issues = new ConcurrentBag<AssetIssue>();

        // Taken before the walk, and it is what the *next* scan will refuse to trust from. It catches
        // an edit by somebody else that landed while this scan was walking, which nothing else here
        // can see.
        //
        // ⚠ It is not, and cannot be, the defence against the scan's own writes, which is what this
        // comment used to claim. "A sidecar written while this scan runs has a write time at or after
        // startedUtc" is false wherever the clock is finer-grained than the filesystem's write times:
        // on Windows DateTime.UtcNow resolves through GetSystemTimePreciseAsFileTime while NTFS
        // stamps a write from the coarse clock, so a sidecar written a millisecond *after* this line
        // carries a write time up to a tick *before* it, and the next scan trusts it. The scan's own
        // writes are therefore recorded as MetaStamp.Unknown instead — a fact, not an inference from
        // two clocks that do not agree.
        var startedUtc = time.GetUtcNow().UtcDateTime;

        var previousEntries = byPath;
        var previousStamps = stamps;
        var previousCutoff = trustedBeforeUtc;

        if (!Directory.Exists(Paths.Assets)) {
            byGuid = [];
            byPath = new(StringComparer.Ordinal);
            stamps = new(StringComparer.Ordinal);
            trustedBeforeUtc = startedUtc;
            return new(0, clock.Elapsed, [], 0);
        }

        var survey = Walk();
        var candidates = survey.Candidates;
        var found = new Scanned?[candidates.Count];
        var claimed = new bool[candidates.Count];

        // Parallel because this is an I/O walk over thousands of small files and the cores are idle
        // during it. Each index is written by exactly one iteration, so the arrays need no locking.
        Parallel.For(
            0,
            candidates.Count,
            index => {
                claimed[index] = survey.Sidecars.ContainsKey(candidates[index].Absolute);
                found[index] = Read(candidates[index], survey, previousEntries, previousStamps, previousCutoff, options, issues);
            }
        );

        // After the read rather than before it, because "an orphan" is exactly "a sidecar no asset
        // claimed" and the read has just worked out which those are. A project in order — every
        // sidecar spoken for — never builds the set of paths the search would need.
        Quarantine(survey, claimed, options, issues);

        // Built into fresh dictionaries and swapped in at the end, so the previous index stays whole
        // for the reuse lookups above and a scan that throws leaves the database as it found it.
        // Sized from the walk: growing three dictionaries from empty to ten thousand string keys
        // rehashes every key a dozen times over, and the final size is already known here.
        byGuid = new(candidates.Count);
        byPath = new(candidates.Count, StringComparer.Ordinal);
        stamps = new(candidates.Count, StringComparer.Ordinal);

        var reused = 0;

        // Insertion is sequential and in path order, so two machines scanning one checkout resolve a
        // duplicate the same way. A parallel insert would make the winner depend on thread timing.
        foreach (var scanned in found) {
            if (scanned is not { } present) {
                continue;
            }

            if (present.Reused) {
                reused++;
            }

            Insert(present.Entry, present.Stamp, options, issues);
        }

        trustedBeforeUtc = startedUtc;
        return new(Count, clock.Elapsed, [.. issues.OrderBy(issue => issue.Path, StringComparer.Ordinal)], reused);
    }

    /// <summary>Writes the index to <c>Library/GuidIndex</c>.</summary>
    /// <remarks>
    ///     <para>
    ///         Tab-separated text rather than a binary blob. It lives in <c>Library/</c>, so it is
    ///         never committed and never has to be version-compatible with anything; what it does
    ///         have to be is readable by a person at four in the morning wondering why the editor
    ///         thinks a texture is somewhere it is not. That is why the per-entry freshness stamp is
    ///         a byte count and an ISO-8601 instant rather than the tick counts it replaced.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Written beside the index and renamed over it</b>, and closed with a terminator
    ///         line naming the entry count. A crash mid-write therefore leaves either the old index
    ///         or the new one, and a torn file that somehow reaches <see cref="TryLoad" /> anyway is
    ///         refused rather than half-believed. Both errors land on "rescan", which is the
    ///         direction that costs time instead of correctness.
    ///     </para>
    /// </remarks>
    public void Save() {
        Directory.CreateDirectory(Paths.Library);
        var temporary = Paths.GuidIndexFile + ".writing";

        using (var writer = new StreamWriter(temporary)) {
            writer.NewLine = "\n";
            writer.WriteLine(IndexHeader);

            writer.WriteLine(
                ScannedPrefix + trustedBeforeUtc.ToString("O", CultureInfo.InvariantCulture)
            );

            foreach (var entry in byPath.Values.OrderBy(entry => entry.Path, StringComparer.Ordinal)) {
                var stamp = stamps.GetValueOrDefault(entry.Path, MetaStamp.Unknown);

                writer.WriteLine(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"{entry.Guid}\t{entry.MetaVersion}\t{(entry.IsFolder ? 1 : 0)}\t{entry.ImporterTag}\t{stamp.Length}\t{stamp.WrittenUtc.ToString("O", CultureInfo.InvariantCulture)}\t{entry.Path}"
                    )
                );
            }

            writer.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{TerminatorPrefix}{byPath.Count}"));
        }

        File.Move(temporary, Paths.GuidIndexFile, overwrite: true);
    }

    /// <summary>Reads the index back, if it is there and it is whole.</summary>
    /// <remarks>
    ///     Refuses anything it cannot fully account for — a header it does not know, a missing
    ///     terminator, a count that disagrees with the lines — and refusing means a full rescan,
    ///     which is the affordable half of being wrong.
    /// </remarks>
    /// <returns>Whether it was.</returns>
    public bool TryLoad() {
        if (!File.Exists(Paths.GuidIndexFile)) {
            return false;
        }

        using var reader = new StreamReader(Paths.GuidIndexFile);

        if (reader.ReadLine() != IndexHeader) {
            return false;
        }

        var scanned = reader.ReadLine();

        if (scanned is null
            || !scanned.StartsWith(ScannedPrefix, StringComparison.Ordinal)
            || !DateTime.TryParse(
                scanned[ScannedPrefix.Length..],
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var cutoff
            )) {
            return false;
        }

        var entries = new Dictionary<string, AssetEntry>(StringComparer.Ordinal);
        var read = new Dictionary<string, MetaStamp>(StringComparer.Ordinal);
        var terminated = false;

        while (reader.ReadLine() is { } line) {
            if (line.StartsWith(TerminatorPrefix, StringComparison.Ordinal)) {
                terminated = int.TryParse(line[TerminatorPrefix.Length..], CultureInfo.InvariantCulture, out var declared)
                    && declared == entries.Count;

                break;
            }

            var parts = line.Split('\t');

            if (parts.Length != 7
                || !AssetId.TryParse(parts[0], out var guid)
                || !int.TryParse(parts[1], CultureInfo.InvariantCulture, out var version)
                || !long.TryParse(parts[4], CultureInfo.InvariantCulture, out var length)
                || !DateTime.TryParse(parts[5], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var written)) {
                // Skipped, which the terminator then catches: one line fewer than the count the file
                // declares means the whole index is refused. Salvaging the readable lines would be
                // the tempting thing and the wrong one — an index missing an entry it does not know
                // it is missing is exactly the "fresh but incomplete" state this must never reach.
                continue;
            }

            var entry = new AssetEntry(
                guid,
                parts[6],
                parts[3].Length == 0 ? null : parts[3],
                version,
                parts[2] == "1"
            );

            entries[entry.Path] = entry;
            read[entry.Path] = new(length, written);
        }

        if (!terminated) {
            return false;
        }

        byGuid = [];
        byPath = entries;
        stamps = read;
        trustedBeforeUtc = cutoff.ToUniversalTime();

        foreach (var entry in entries.Values) {
            byGuid[entry.Guid] = entry;
        }

        return true;
    }

    /// <summary>Whether a scan would change anything.</summary>
    /// <remarks>
    ///     <para>
    ///         One directory walk and not a single file read: every indexed asset is checked against
    ///         the size and write time its sidecar had when it was indexed, and the walk also notices
    ///         an asset nothing has indexed and a sidecar whose asset has gone.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>What it gets wrong is what a size and a write time get wrong</b>: an edit that
    ///         changes neither — a hand-edited GUID, which is fixed-width — reads as fresh, and a
    ///         checkout or a copy, which stamps files with the time it ran, reads as stale and costs
    ///         a scan it did not strictly need. The second is the affordable one, which is why the
    ///         pair is the test rather than a size alone.
    ///         It also stays <see langword="true" /> forever for a project holding a sidecar that
    ///         cannot be read, because a scan really would keep reporting that, and saying "fresh"
    ///         about a project with a known-broken asset is the lie of the two.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Calling this before <see cref="Scan" /> now buys nothing</b> and costs a second
    ///         walk: a scan is already incremental, so an unchanged project is the cheap case without
    ///         being asked about first. This is here for a caller that wants the answer rather than
    ///         the work — a status line, a build server's "is this in order?".
    ///     </para>
    /// </remarks>
    /// <returns>Whether a rescan would find something to do.</returns>
    public bool IsStale() {
        if (!Directory.Exists(Paths.Assets)) {
            return byPath.Count != 0;
        }

        var survey = Walk();

        // Every sidecar must belong to something indexed, or a scan would have an orphan to move.
        if (survey.Candidates.Count != byPath.Count || survey.Sidecars.Count != byPath.Count) {
            return true;
        }

        foreach (var candidate in survey.Candidates) {
            var relative = RelativeUnderRoot(candidate.Absolute);

            if (!byPath.TryGetValue(relative, out var entry)
                || entry.IsFolder != candidate.IsFolder
                || !survey.Sidecars.TryGetValue(candidate.Absolute, out var stamp)
                || !IsFresh(stamp, relative, stamps, trustedBeforeUtc)) {
                return true;
            }
        }

        return false;
    }

    Survey Walk() {
        var candidates = new List<(string Absolute, bool IsFolder)>();
        var sidecars = new Dictionary<string, MetaStamp>(StringComparer.Ordinal);

        // DirectoryInfo rather than the string overloads: the enumerator already has each entry's
        // length and write time from the directory read, so the per-entry stamp costs no extra stat.
        // Asking File.GetLastWriteTimeUtc afterwards would be a syscall per file for the same answer.
        foreach (var info in new DirectoryInfo(Paths.Assets).EnumerateFileSystemInfos("*", SearchOption.AllDirectories)) {
            if (info is FileInfo file && file.Name.EndsWith(AssetMetaFile.Extension, StringComparison.Ordinal)) {
                // Keyed by the asset it belongs to rather than by its own name, so that asking "what
                // is the stamp of this asset's sidecar" is a lookup and not a string concatenation
                // per asset. At ten thousand assets that concatenation was measurable.
                sidecars[file.FullName[..^AssetMetaFile.Extension.Length]] = new(file.Length, file.LastWriteTimeUtc);
                continue;
            }

            candidates.Add((info.FullName, info is DirectoryInfo));
        }

        // Sorted so that the scan, the duplicate resolution and the report are the same on every
        // machine. Directory enumeration order is not a promise any filesystem makes.
        candidates.Sort(static (left, right) => string.CompareOrdinal(left.Absolute, right.Absolute));
        return new(candidates, sidecars);
    }

    /// <summary>Whether a stamp seen on disk is the one an index entry may be kept on.</summary>
    /// <remarks>
    ///     Two independent refusals. The recorded stamp is <see cref="MetaStamp.Unknown" /> — matching
    ///     no real file — for every sidecar the recording scan wrote itself, so those are re-read on
    ///     evidence rather than on a clock. The cutoff then adds what only a clock can say: that
    ///     somebody else's edit landed while that scan was walking.
    /// </remarks>
    static bool IsFresh(MetaStamp current, string relative, Dictionary<string, MetaStamp> previousStamps, DateTime cutoff) =>
        previousStamps.TryGetValue(relative, out var recorded) && recorded == current && current.WrittenUtc < cutoff;

    /// <summary>The project-relative form of a path the walk produced, which is known to be under the root.</summary>
    /// <remarks>
    ///     <see cref="ProjectPaths.Relative" /> goes through <c>Path.GetRelativePath</c>, which
    ///     normalises both sides and is far and away the most expensive thing a scan does per asset
    ///     once the file reads are gone. Everything the walk hands over came out of an enumeration
    ///     rooted at <see cref="ProjectPaths.Assets" />, so the prefix is known rather than computed —
    ///     and anything that somehow is not gets the careful version.
    /// </remarks>
    string RelativeUnderRoot(string absolute) {
        var root = Paths.Root;

        if (absolute.Length > root.Length
            && absolute.StartsWith(root, StringComparison.Ordinal)
            && (absolute[root.Length] == Path.DirectorySeparatorChar || absolute[root.Length] == '/')) {
            return absolute[(root.Length + 1)..].Replace('\\', '/');
        }

        return Paths.Relative(absolute);
    }

    Scanned? Read(
        (string Absolute, bool IsFolder) candidate,
        Survey survey,
        Dictionary<string, AssetEntry> previousEntries,
        Dictionary<string, MetaStamp> previousStamps,
        DateTime cutoff,
        ScanOptions options,
        ConcurrentBag<AssetIssue> issues
    ) {
        var relative = RelativeUnderRoot(candidate.Absolute);
        var hasMeta = survey.Sidecars.TryGetValue(candidate.Absolute, out var stamp);

        // The whole point: an asset whose sidecar is the size and age the index left it does not get
        // opened, parsed, or thought about again.
        if (hasMeta
            && IsFresh(stamp, relative, previousStamps, cutoff)
            && previousEntries.TryGetValue(relative, out var kept)
            && kept.IsFolder == candidate.IsFolder) {
            return new(kept, stamp, true);
        }

        var metaPath = AssetMetaFile.PathFor(candidate.Absolute);

        if (!hasMeta) {
            if (!options.CreateMissingMeta) {
                issues.Add(new(AssetIssueKind.MetaCreated, relative, "Has no .meta, and none was created."));
                return null;
            }

            var minted = CreateMeta(metaPath);
            issues.Add(new(AssetIssueKind.MetaCreated, relative, $"Had no .meta; created one with GUID {minted}."));

            // No stamp, rather than the one the write just left behind. This scan cannot tell an edit
            // that lands a microsecond after its own write from the write itself — the two share a
            // filesystem tick — so it records nothing to trust and the next scan opens the file.
            return new(
                new(minted, relative, null, MetaMigrationChain.CurrentVersion, candidate.IsFolder),
                MetaStamp.Unknown,
                false
            );
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

        return new(
            new(envelope.Guid, relative, envelope.ImporterTag, envelope.MetaVersion, candidate.IsFolder),
            stamp,
            false
        );
    }

    void Insert(AssetEntry entry, MetaStamp stamp, ScanOptions options, ConcurrentBag<AssetIssue> issues) {
        byPath[entry.Path] = entry;
        stamps[entry.Path] = stamp;

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

        // Its sidecar was just rewritten, so the pre-rewrite stamp is a claim about a file that no
        // longer says what it says — the one shape of wrong an index must not persist. Re-stamping it
        // from the rewrite would only move the problem: this scan has no way to distinguish its own
        // write from an edit landing in the same filesystem tick. So it records no stamp at all and
        // the next scan reads the repaired sidecar back, which costs one file and settles it.
        stamps[repaired.Path] = MetaStamp.Unknown;
        issues.Add(new(AssetIssueKind.DuplicateGuid, loser.Path, $"{message} '{loser.Path}' was re-GUIDed to {minted}."));
    }

    void Quarantine(Survey survey, bool[] claimed, ScanOptions options, ConcurrentBag<AssetIssue> issues) {
        var spokenFor = 0;

        foreach (var claim in claimed) {
            if (claim) {
                spokenFor++;
            }
        }

        // The overwhelmingly common case, and the one worth not paying for: every sidecar belongs to
        // an asset, so there is nothing to search for and no set of paths to build in order to search.
        if (spokenFor == survey.Sidecars.Count) {
            return;
        }

        var owners = new HashSet<string>(StringComparer.Ordinal);

        foreach (var candidate in survey.Candidates) {
            owners.Add(candidate.Absolute);
        }

        // Ordered so that two machines quarantining the same wreckage write the same files in the
        // same order.
        foreach (var owner in survey.Sidecars.Keys.Order(StringComparer.Ordinal).ToList()) {
            // A sidecar for a sidecar — `foo.meta.meta` beside a real `foo.meta` — is not an orphan,
            // however little sense it makes, because the file it names is right there. Sidecars are
            // keyed by what they belong to, so "does foo.meta exist" is "is there a sidecar for foo".
            if (owners.Contains(owner)
                || (owner.EndsWith(AssetMetaFile.Extension, StringComparison.Ordinal)
                    && survey.Sidecars.ContainsKey(owner[..^AssetMetaFile.Extension.Length]))) {
                continue;
            }

            var meta = owner + AssetMetaFile.Extension;
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
            survey.Sidecars.Remove(owner);
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

    /// <summary>One directory walk, serving the candidates, every stamp and the quarantine pass.</summary>
    /// <param name="Candidates">Every asset — folders and non-sidecar files — in ordinal path order.</param>
    /// <param name="Sidecars">Every sidecar's stamp, keyed by the absolute path of the asset it belongs to.</param>
    /// <remarks>
    ///     One walk rather than the three this used to do — a quarantine pass over the sidecars, a
    ///     candidate pass over everything else, and a freshness survey over the sidecars again. The
    ///     enumerator hands over each entry's length and write time along with its name, so the walk
    ///     that has to happen anyway is also the one that collects every stamp.
    /// </remarks>
    sealed record Survey(List<(string Absolute, bool IsFolder)> Candidates, Dictionary<string, MetaStamp> Sidecars);

    /// <summary>What one candidate produced, and whether the index already knew it.</summary>
    readonly record struct Scanned(AssetEntry Entry, MetaStamp Stamp, bool Reused);
}

/// <summary>What identifies a sidecar as the one an index entry was read from.</summary>
/// <param name="Length">Its size in bytes.</param>
/// <param name="WrittenUtc">When it was last written, UTC.</param>
/// <remarks>
///     <para>
///         <b>A size and a write time, because the honest answer costs a read and the read is the
///         thing being avoided.</b> Hashing a sidecar's contents would never be wrong; it would also
///         mean opening every file in the project on every cold start, which is precisely the work
///         the index exists to skip. A size alone is far too weak — a sidecar is mostly fixed-width
///         fields, so most edits leave it exactly as long. The pair is the cheapest thing that is
///         wrong only in cases somebody had to construct.
///     </para>
///     <para>
///         ⚠ <b>What it gets wrong.</b> An edit that leaves a sidecar the same length <em>and</em>
///         the same write time is invisible — changing a GUID by hand is the realistic one, since a
///         GUID is fixed-width. So is swapping two same-length sidecars with a tool that preserves
///         write times. Both need the write time to survive, which rules out every ordinary editor
///         and every ordinary <c>git</c> operation: a checkout stamps the files it touches with the
///         time of the checkout, so a checkout reads as changed and costs a re-read it did not
///         strictly need. <b>That is the direction to be wrong in</b> — a copy or a checkout making
///         the index falsely stale wastes a scan, where falsely fresh loses an asset.
///     </para>
///     <para>
///         ⚠ <b>A scan's own writes get no stamp at all.</b> A filesystem whose write-time resolution
///         is coarser than the gap between two edits — HFS+ at a whole second, NTFS at a clock tick —
///         cannot distinguish an edit that lands a moment after a scan minted a sidecar from the
///         minting itself. So a scan records <see cref="Unknown" /> for every sidecar it wrote, which
///         matches no real file, and the next scan opens it. That is a fact the scan knows rather
///         than an inference from a timestamp, so it holds at every resolution, and it costs those
///         files one extra read.
///     </para>
///     <para>
///         ⚠ <b>The write-time cutoff is a weaker second filter</b>, and what it adds is the one
///         thing the stamps cannot know: an edit by <em>somebody else</em> that raced the recording
///         scan. A stamp is only trusted when its write time is strictly earlier than the instant
///         that scan began. <b>Where the clock is finer-grained than the filesystem's write times it
///         under-fires</b> — a file written after that instant can carry a write time floored below
///         it, which is exactly what NTFS and <c>DateTime.UtcNow</c> do to each other — and no cutoff
///         can fix that. Flooring it to the filesystem's own resolution would make it sound and would
///         also refuse every file written in the tick before a scan, turning an untouched project
///         into a full re-read. So the hole it leaves is a foreign edit landing in the same tick as a
///         scan's walk, on a file that scan did not itself write.
///     </para>
///     <para>
///         Internal on purpose: the public contract is that a scan is incremental and says how much
///         it kept, not <em>how</em> an entry is recognised. That is a decision this file should be
///         able to change — to a hash, if a project ever appears where the read is affordable —
///         without it being a breaking change to anybody.
///     </para>
/// </remarks>
readonly record struct MetaStamp(long Length, DateTime WrittenUtc) {
    /// <summary>No stamp this index will stand behind. Its negative length matches no real file's.</summary>
    /// <remarks>
    ///     Two things reach it: no sidecar there at all, and a sidecar the recording scan wrote
    ///     itself. Both mean the same thing to the next scan — open the file — and both survive
    ///     <see cref="AssetDatabase.Save" /> as a length of <c>-1</c>, so a warm start is told the
    ///     same thing the scan that wrote the index knew.
    /// </remarks>
    public static MetaStamp Unknown { get; } = new(-1, DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc));
}
