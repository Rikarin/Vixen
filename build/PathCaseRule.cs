// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;

namespace Vixen.Build;

/// <summary>
///     A reference that names a committed path in the wrong case, as a function of the committed
///     path list and the text of the files that reference it.
/// </summary>
/// <remarks>
///     <para>
///         <b>doc 10 § Cross-platform discipline, the Case sensitivity row</b>, promises exactly
///         this and had nothing behind it
///         (<a href="https://github.com/Rikarin/Vixen/issues/329">#329</a>): <i>"Virtual paths are
///         case-sensitive everywhere, including Windows. A CI check on Linux catches
///         <c>Texture.PNG</c> vs <c>texture.png</c> before a user does."</i> Every neighbouring row
///         in that table is enforced — <c>System.IO.Path</c> by <c>VXIO0001</c>, native binaries by
///         checksum, endianness by an assertion — and this one was described and absent.
///     </para>
///     <para>
///         ⚠ <b>The direction is what makes the defect nasty: the machine that gets it wrong is the
///         one that cannot see it.</b> This repository is developed on macOS and Windows, where the
///         filesystem folds case, so <c>Assets/Textures/Crate.PNG</c> opens a file called
///         <c>Crate.png</c> and every local run is green. On Linux, on Android, and on the web —
///         where the same content is fetched over HTTP and a mis-cased path is a plain 404 — it is
///         a hard failure. ⚠ And this tree has already been bitten by the same fold once from the
///         other side: <c>.gitignore</c>'s comment records a <c>Build/</c> line that made the entire
///         Nuke <c>build/</c> project invisible to git on macOS and Windows.
///     </para>
///     <para>
///         <b>The rule is one sentence and it is what keeps the false-positive rate at three.</b> A
///         path-shaped literal is a violation when it resolves to a committed path
///         <em>case-insensitively</em> and does not resolve <em>exactly</em>. A literal that matches
///         nothing at all is not judged — most <c>"Assets/Walk.vxanim"</c> in this tree are
///         synthetic in-memory paths in a test and name no file, and a rule that demanded they exist
///         would be a different, much noisier rule than the one doc 10 asks for.
///     </para>
///     <para>
///         <b>Committed paths, from <c>git ls-files</c>, and not a directory walk.</b> That is the
///         definition <c>CheckLicenceHeaders</c> already settled on for the same
///         reason, and here it buys a second thing: a worktree's <c>git ls-files</c> lists that
///         worktree, so the sweep cannot read the whole checkout each agent keeps under
///         <c>.claude/worktrees/</c> — the trap <c>CheckStrings</c> and the golden walk each hit.
///     </para>
///     <para>
///         ⚠ <b>And the consequence of that choice, found by sabotage rather than by reasoning: a
///         reference to a file that is not yet <c>git add</c>ed is not judged.</b> The first attempt
///         to break this rule pointed a mis-cased reference at a file added in the same working tree
///         and the check stayed green, because the target was not in the index and a literal that
///         folds onto nothing is not a violation. That is correct for a gate about what deploys —
///         CI and every push see a fully staged tree — but it means the sweep is one commit behind a
///         brand-new file locally, and the run that matters is the one after <c>git add</c>.
///     </para>
///     <para>
///         ⚠ <b><see cref="Collisions" /> is the half that only has teeth on Linux</b>, and it is
///         worth knowing which half you are reading. Two committed paths differing only in case
///         cannot both exist in a macOS or Windows checkout — git writes one and the other is simply
///         missing from the working tree — so on those machines the collision is invisible in the
///         files and visible only in the index this reads. Which is why it reads the index.
///     </para>
///     <para>
///         <b>A pure function so that its answer can be read without running the gate</b>, the shape
///         <see cref="ComponentGeneratorRule" /> and <see cref="PluginReferenceRule" /> record:
///         <c>CheckPathCase</c> is one caller and <c>PathCaseRuleTests</c> is the other, and the
///         second is what has ever watched it produce a positive.
///     </para>
/// </remarks>
static partial class PathCaseRule {
    /// <summary>
    ///     Extensions whose bytes are not text, and which are therefore never scanned for
    ///     references.
    /// </summary>
    /// <remarks>
    ///     A conservative list rather than a byte sniff: a false negative here costs one unscanned
    ///     file and a false positive costs a mojibake match. The files that matter — every project
    ///     file, every <c>.rvn</c>, every <c>.vxml</c>, every <c>.cs</c>, every workflow — are text.
    /// </remarks>
    public static readonly string[] BinaryExtensions = [
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".tga", ".exr", ".hdr", ".ktx", ".dds",
        ".ttf", ".otf", ".woff", ".woff2", ".spv", ".bin", ".obj", ".dll", ".so", ".dylib", ".pdb",
        ".exe", ".zip", ".gz", ".7z", ".pdf", ".wav", ".ogg", ".mp3", ".mp4", ".fbx", ".glb",
        ".gltf", ".pyc"
    ];

    /// <summary>
    ///     A run of path-looking characters containing at least one separator.
    /// </summary>
    /// <remarks>
    ///     ⚠ Deliberately does not admit <c>:</c> or whitespace. Admitting a colon would swallow
    ///     URLs and drive letters, and admitting whitespace would let a sentence in a doc comment
    ///     read as a path — both of which turn a rule with three exemptions into one nobody can
    ///     keep.
    /// </remarks>
    [GeneratedRegex(@"[A-Za-z0-9_.\-]+(?:[/\\][A-Za-z0-9_.\-]+)+")]
    private static partial Regex PathShaped();

    /// <summary>One reference that names a committed path in a case the repository does not use.</summary>
    /// <param name="File">The committed path of the file holding the reference.</param>
    /// <param name="Line">The 1-based line the reference is on.</param>
    /// <param name="Reference">The literal as written, with separators normalised to <c>/</c>.</param>
    /// <param name="CommittedAs">The committed path it resolves to when case is folded.</param>
    public sealed record Violation(string File, int Line, string Reference, string CommittedAs) {
        /// <summary>The key an exemption line carries, and the line this reports.</summary>
        /// <returns><c>&lt;file&gt;: &lt;reference&gt;</c>.</returns>
        public string Key() => $"{File}: {Reference}";

        /// <inheritdoc />
        public override string ToString() => $"{File}({Line}): \"{Reference}\" is committed as \"{CommittedAs}\"";
    }

    /// <summary>
    ///     Every committed path and every directory above one, keyed by its lowercase form.
    /// </summary>
    /// <param name="committed">Committed paths, relative to the repository root, <c>/</c>-separated.</param>
    /// <returns>Lowercase path to the single spelling the repository uses, for the unambiguous ones.</returns>
    /// <remarks>
    ///     ⚠ <b>The ancestor directories are not padding.</b> A reference to
    ///     <c>samples/13-ThirdPersonShooter</c> names no file and is exactly as broken on Linux as a
    ///     mis-cased file; without the directories in the index it would resolve to nothing and be
    ///     silently unjudged.
    ///     <para>
    ///         A lowercase key that two different spellings claim is dropped rather than resolved,
    ///         because with two committed spellings there is no single right answer and
    ///         <see cref="Collisions" /> is the assertion that wants to be reading that case.
    ///     </para>
    /// </remarks>
    public static Dictionary<string, string> Index(IEnumerable<string> committed) {
        var spellings = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var path in committed) {
            var normalised = path.Replace('\\', '/').Trim();

            while (normalised.Length > 0) {
                if (!spellings.TryGetValue(normalised.ToLowerInvariant(), out var set)) {
                    spellings[normalised.ToLowerInvariant()] = set = new(StringComparer.Ordinal);
                }

                set.Add(normalised);

                var cut = normalised.LastIndexOf('/');

                if (cut < 0) {
                    break;
                }

                normalised = normalised[..cut];
            }
        }

        return spellings
            .Where(pair => pair.Value.Count == 1)
            .ToDictionary(pair => pair.Key, pair => pair.Value.First(), StringComparer.Ordinal);
    }

    /// <summary>Committed paths that differ from another committed path only in case.</summary>
    /// <param name="committed">Committed paths, relative to the repository root.</param>
    /// <returns>Each colliding group's spellings, sorted, one entry per group.</returns>
    /// <remarks>
    ///     ⚠ Such a pair cannot survive a checkout on a case-folding filesystem: git writes one of
    ///     the two and the working tree silently lacks the other, so a macOS or Windows developer
    ///     sees a file that is in the repository and not on disk. Reading the committed list rather
    ///     than the disk is what makes this answerable from either kind of machine.
    /// </remarks>
    public static IReadOnlyList<string[]> Collisions(IEnumerable<string> committed) =>
        committed
            .Select(path => path.Replace('\\', '/').Trim())
            .Where(path => path.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .GroupBy(path => path.ToLowerInvariant(), StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Order(StringComparer.Ordinal).ToArray())
            .OrderBy(group => group[0], StringComparer.Ordinal)
            .ToList();

    /// <summary>Whether a committed path is one whose bytes this rule reads as text.</summary>
    /// <param name="path">A committed path.</param>
    /// <returns>False for the extensions in <see cref="BinaryExtensions" />.</returns>
    public static bool IsScannable(string path) =>
        !BinaryExtensions.Any(extension => path.EndsWith(extension, StringComparison.OrdinalIgnoreCase));

    /// <summary>Every mis-cased reference in one file's text.</summary>
    /// <param name="path">The committed path of the file, which is also how a relative reference resolves.</param>
    /// <param name="text">The file's contents.</param>
    /// <param name="index">The output of <see cref="Index" />.</param>
    /// <returns>One violation per distinct literal, in the order they first appear.</returns>
    /// <remarks>
    ///     <para>
    ///         A literal is tried against the file's own directory first and then against the
    ///         repository root, because both spellings appear in this tree — a <c>ProjectReference</c>
    ///         is relative and a workflow's <c>./build.sh</c> argument is rooted. The first of the
    ///         two that resolves <em>exactly</em> ends the search: a literal that is right relative
    ///         to one base is right, whatever it would have meant relative to the other.
    ///     </para>
    ///     <para>
    ///         ⚠ Distinct by literal rather than by occurrence. A path repeated forty times in a
    ///         project file is one thing to fix, and reporting it forty times is how a gate's output
    ///         stops being read.
    ///     </para>
    /// </remarks>
    public static IEnumerable<Violation> Scan(string path, string text, IReadOnlyDictionary<string, string> index) {
        var directory = Parent(path.Replace('\\', '/'));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var violations = new List<Violation>();

        foreach (Match match in PathShaped().Matches(text)) {
            var reference = match.Value.Replace('\\', '/');

            if (!seen.Add(reference)) {
                continue;
            }

            string? folded = null;

            foreach (var candidate in Candidates(directory, reference)) {
                if (index.TryGetValue(candidate.ToLowerInvariant(), out var committed)) {
                    if (string.Equals(candidate, committed, StringComparison.Ordinal)) {
                        folded = null;

                        break;
                    }

                    folded ??= committed;
                }
            }

            if (folded is not null) {
                violations.Add(new(path, LineOf(text, match.Index), reference, folded));
            }
        }

        return violations;
    }

    /// <summary>The exemption keys in <c>docs/PathCaseExempt.txt</c>.</summary>
    /// <param name="text">The file's contents.</param>
    /// <returns>Every non-blank, non-comment line, trimmed.</returns>
    public static HashSet<string> ReadExemptions(string text) =>
        new(
            text
                .Split('\n')
                .Select(line => line.Trim())
                .Where(line => line.Length > 0 && !line.StartsWith('#')),
            StringComparer.Ordinal
        );

    /// <summary>The rooted forms one literal could mean, nearest base first.</summary>
    /// <param name="directory">The referencing file's directory, or the empty string at the root.</param>
    /// <param name="reference">The literal, <c>/</c>-separated.</param>
    /// <returns>Zero, one or two repository-relative paths.</returns>
    static IEnumerable<string> Candidates(string directory, string reference) {
        if (directory.Length > 0 && Normalise($"{directory}/{reference}") is { Length: > 0 } relative) {
            yield return relative;
        }

        if (Normalise(reference) is { Length: > 0 } rooted) {
            yield return rooted;
        }
    }

    /// <summary>Collapses <c>.</c> and <c>..</c> segments without touching the filesystem.</summary>
    /// <param name="path">A <c>/</c>-separated path.</param>
    /// <returns>The collapsed path, or the empty string if it escapes the repository root.</returns>
    /// <remarks>
    ///     ⚠ <see cref="System.IO.Path" /> is not used, and not only because <c>VXIO0001</c> bans it
    ///     in engine code: <c>GetFullPath</c> would resolve against the process's current directory
    ///     and, on Windows, would fold <c>/</c> and <c>\</c> in ways that make the answer depend on
    ///     the machine running the gate. The subject here is a string in a committed file.
    /// </remarks>
    static string Normalise(string path) {
        var segments = new List<string>();

        foreach (var segment in path.Split('/')) {
            switch (segment) {
                case "" or ".": continue;

                case "..":
                    if (segments.Count == 0) {
                        return string.Empty;
                    }

                    segments.RemoveAt(segments.Count - 1);

                    continue;

                default:
                    segments.Add(segment);

                    continue;
            }
        }

        return string.Join('/', segments);
    }

    /// <summary>The directory part of a <c>/</c>-separated path.</summary>
    /// <param name="path">The path.</param>
    /// <returns>Everything before the last separator, or the empty string.</returns>
    static string Parent(string path) {
        var cut = path.LastIndexOf('/');

        return cut < 0 ? string.Empty : path[..cut];
    }

    /// <summary>The 1-based line an offset falls on.</summary>
    /// <param name="text">The text.</param>
    /// <param name="offset">A character offset into it.</param>
    /// <returns>The line number, counting from one.</returns>
    static int LineOf(string text, int offset) {
        var line = 1;

        for (var index = 0; index < offset; index++) {
            if (text[index] == '\n') {
                line++;
            }
        }

        return line;
    }
}
