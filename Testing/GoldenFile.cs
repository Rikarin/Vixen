// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using Xunit.Sdk;

namespace Vixen.Testing;

/// <summary>
///     The text-snapshot helper: compares what the code produces against what is committed for it.
/// </summary>
/// <remarks>
///     <para>
///         <c>docs/plan/12</c> § "Test infrastructure worth building early" asks for a
///         <c>GoldenFile</c>: *"the snapshot helper: reads/writes under <c>__golden__/</c>, honours
///         <c>--update-golden</c>, produces a readable unified diff on mismatch"*. Two of those three
///         are here; the directory is not, and the reason is written under
///         <see cref="InProjectDirectory" />.
///     </para>
///     <para>
///         <b>The switch is <c>UPDATE_GOLDEN=1</c>, and that is not a new one.</b> ⚠ The tree was
///         read as having three conventions for this and it has two, split by <i>what is being
///         rewritten</i> rather than by accident: <c>UPDATE_GOLDEN</c> rewrites a snapshot of output
///         (Raven's four golden suites, the two <c>Vixen.Net</c> wire suites), and
///         <c>VIXEN_REGENERATE</c> rewrites a committed <i>artefact</i> that is not a test fixture at
///         all — generated binding code, the reflection JSON, the parity census. The image goldens
///         take <c>VIXEN_UPDATE_GOLDEN</c> because <c>build/Build.cs</c> sets it from the document's
///         own <c>--update-golden</c>, so both spellings are honoured here and nobody has to remember
///         which suite they are in.
///     </para>
///     <para>
///         <b>What it buys is the three refusals, not the comparison.</b> The comparison was already
///         one line in each of six suites. Each of those six also re-implemented the "write it if it
///         is missing" half, and two of them wrapped it in a <c>foreach</c> over a sequence that can
///         be empty — which is a pass, on a test that compared nothing, on the day code generation
///         produced no stages. <see cref="Batch" /> exists for exactly that shape and refuses it by
///         name.
///     </para>
/// </remarks>
static class GoldenFile {
    /// <summary>Lines of unchanged text shown either side of a hunk.</summary>
    const int Context = 3;

    /// <summary>How many changed lines a hunk prints before deferring to the '.actual' file.</summary>
    const int Budget = 40;

    /// <summary>Whether the goldens are being rewritten rather than asserted.</summary>
    /// <remarks>
    ///     Both spellings, because the tree has both: <c>UPDATE_GOLDEN</c> is what the six text
    ///     suites document and what people type, and <c>VIXEN_UPDATE_GOLDEN</c> is what
    ///     <c>build/Build.cs</c> exports when it is given <c>--update-golden</c>.
    /// </remarks>
    public static bool Rewriting =>
        Environment.GetEnvironmentVariable("UPDATE_GOLDEN") is "1" or "true" or "TRUE"
        || Environment.GetEnvironmentVariable("VIXEN_UPDATE_GOLDEN") is "1" or "true" or "TRUE";

    /// <summary>Compares one rendering against the snapshot committed at <paramref name="path" />.</summary>
    /// <param name="actual">What the code produced.</param>
    /// <param name="path">Where the snapshot lives. <see cref="InProjectDirectory" /> resolves it.</param>
    /// <remarks>
    ///     Fails rather than passes when the snapshot had to be created — a golden nobody has read is
    ///     not evidence of anything — and fails rather than passes when the rendering is empty, for
    ///     which see <see cref="Set.Matches" />.
    /// </remarks>
    public static void Matches(string actual, string path) {
        var batch = Batch();
        batch.Matches(actual, path);
        batch.Done();
    }

    /// <summary>Starts a set of goldens compared by one test.</summary>
    /// <returns>Something to add renderings to, then <see cref="Set.Done" />.</returns>
    /// <remarks>
    ///     ⚠ Use this wherever the number of goldens comes from the code under test — one per
    ///     generated stage, one per discovered fixture. The loop that writes it by hand passes when
    ///     the sequence is empty, and an empty sequence is precisely what a broken generator returns.
    ///     <see cref="Set.Done" /> is what makes that red.
    /// </remarks>
    public static Set Batch() => new();

    /// <summary>The directory the test project's sources live in.</summary>
    /// <param name="parts">Path segments under it — a folder and a file name.</param>
    /// <returns>An absolute path.</returns>
    /// <remarks>
    ///     <para>
    ///         <c>bin/&lt;configuration&gt;/&lt;framework&gt;</c> up to the project root, which is the
    ///         walk every golden suite in the tree already writes inline. It is a convention rather
    ///         than a fact, and it is a safe one to get wrong: a golden resolved to the wrong place is
    ///         a golden that does not exist, and <see cref="Matches" /> fails on that rather than
    ///         inventing one.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The document's <c>__golden__/</c> is deliberately not imposed.</b> The corpora
    ///         predate this helper by years — Raven's are <c>Fixtures/&lt;name&gt;.ir</c> beside the
    ///         <c>.rvn</c> they were compiled from, and reading the pair together is the whole
    ///         review; <c>Vixen.Net</c>'s are <c>Wire/__wire__/</c>. Moving several hundred committed
    ///         files to satisfy a directory name buys nothing a reviewer can see, so the caller names
    ///         the path and the document has been corrected instead.
    ///     </para>
    /// </remarks>
    public static string InProjectDirectory(params string[] parts) =>
        Path.Combine([AppContext.BaseDirectory, "..", "..", "..", .. parts]);

    /// <summary>Several goldens, compared by one test, reported together.</summary>
    public sealed class Set {
        readonly List<string> regenerated = [];
        readonly List<string> mismatched = [];
        int compared;

        /// <summary>Compares one rendering against the snapshot committed at <paramref name="path" />.</summary>
        /// <param name="actual">What the code produced.</param>
        /// <param name="path">Where the snapshot lives.</param>
        /// <remarks>
        ///     Nothing throws here: a set regenerates or diffs <em>every</em> golden in it before
        ///     failing, so one run refreshes them all and one failure names all of them. The four
        ///     suites this was taken from each say so in a comment.
        /// </remarks>
        public void Matches(string actual, string path) {
            ArgumentNullException.ThrowIfNull(actual);
            ArgumentException.ThrowIfNullOrEmpty(path);

            compared++;

            var rendering = Normalize(actual);
            var name = Path.GetFileName(path);

            if (rendering.Length == 0) {
                throw new XunitException(
                    $"The rendering for golden '{name}' is empty, so comparing it with anything is "
                    + "meaningless — and an empty golden committed beside it would make this suite green "
                    + "for ever on a printer, a generator or an enumeration that produced nothing. Assert "
                    + "that the code under test produced something before handing it to GoldenFile."
                );
            }

            if (Rewriting || !File.Exists(path)) {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
                File.WriteAllText(path, rendering);
                regenerated.Add(name);

                return;
            }

            var expected = Normalize(File.ReadAllText(path));

            if (string.Equals(expected, rendering, StringComparison.Ordinal)) {
                return;
            }

            File.WriteAllText(path + ".actual", rendering);
            mismatched.Add($"{name}\n{Diff(expected, rendering)}");
        }

        /// <summary>Reports what the set found, and fails if it found nothing to compare.</summary>
        /// <remarks>
        ///     ⚠ The empty-set refusal is the reason this method exists rather than each
        ///     <see cref="Matches" /> throwing where it stands. A test that compared no goldens has
        ///     asserted nothing, and every form of it in the tree reported a pass.
        /// </remarks>
        public void Done() {
            if (compared == 0) {
                throw new XunitException(
                    "No goldens were compared. The sequence this test loops over was empty, so it "
                    + "asserted nothing at all — which is the failure it was written to catch, reported "
                    + "as a pass. Check what the code under test returned."
                );
            }

            if (regenerated.Count > 0) {
                throw new XunitException(
                    $"Goldens were (re)generated: {string.Join(", ", regenerated)}. Read the diff — it is "
                    + "the change you are making, stated — and re-run without UPDATE_GOLDEN."
                );
            }

            if (mismatched.Count > 0) {
                throw new XunitException(
                    $"{mismatched.Count} golden(s) differ from what the code produces. The renderings are "
                    + "beside them as '.actual'; regenerate with UPDATE_GOLDEN=1 once the new output is "
                    + "the one you want.\n\n"
                    + string.Join("\n\n", mismatched)
                );
            }
        }
    }

    /// <summary>A unified diff, hunked around what actually moved.</summary>
    /// <param name="expected">The committed snapshot.</param>
    /// <param name="actual">The rendering.</param>
    /// <returns>Something a reviewer can act on without opening either file.</returns>
    /// <remarks>
    ///     ⚠ <b>This is the half <c>Assert.Equal</c> cannot do.</b> On two strings of a few kilobytes
    ///     — a syntax tree, a SPIR-V listing, a catalog — its message is a window of some sixty
    ///     characters around the first divergence with the index it happened at, so a reviewer learns
    ///     that something moved at offset 4213 and nothing about what. Line numbers and a hunk say
    ///     which construct changed.
    ///     <para>
    ///         Common prefix and suffix rather than a longest-common-subsequence: it is a dozen lines
    ///         instead of a matrix, it is exact for the case that actually happens (one region
    ///         changed), and where several regions moved it degrades into one larger hunk rather than
    ///         into something wrong.
    ///     </para>
    /// </remarks>
    static string Diff(string expected, string actual) {

        var before = expected.Split('\n');
        var after = actual.Split('\n');

        var prefix = 0;

        while (prefix < before.Length
               && prefix < after.Length
               && string.Equals(before[prefix], after[prefix], StringComparison.Ordinal)) {
            prefix++;
        }

        var suffix = 0;

        while (suffix < before.Length - prefix
               && suffix < after.Length - prefix
               && string.Equals(
                   before[^(suffix + 1)],
                   after[^(suffix + 1)],
                   StringComparison.Ordinal
               )) {
            suffix++;
        }

        var start = Math.Max(0, prefix - Context);
        var beforeEnd = before.Length - suffix;
        var afterEnd = after.Length - suffix;

        var text = new StringBuilder();
        text.Append(
            CultureInfo.InvariantCulture,
            $"@@ -{start + 1},{beforeEnd - start} +{start + 1},{afterEnd - start} @@\n"
        );

        for (var i = start; i < prefix; i++) {
            text.Append("  ").Append(before[i]).Append('\n');
        }

        Hunk(text, before, prefix, beforeEnd, '-');
        Hunk(text, after, prefix, afterEnd, '+');

        for (var i = beforeEnd; i < Math.Min(before.Length, beforeEnd + Context); i++) {
            text.Append("  ").Append(before[i]).Append('\n');
        }

        return text.ToString().TrimEnd('\n');

        static void Hunk(StringBuilder text, string[] lines, int from, int to, char mark) {
            var shown = Math.Min(to - from, Budget);

            for (var i = from; i < from + shown; i++) {
                text.Append(mark).Append(' ').Append(lines[i]).Append('\n');
            }

            if (to - from > shown) {
                text.Append(
                    CultureInfo.InvariantCulture,
                    $"{mark} … {to - from - shown} more line(s), see the '.actual' file\n"
                );
            }
        }
    }

    /// <summary>What both sides are compared as.</summary>
    /// <remarks>
    ///     Line endings, because a golden committed on one platform is read on three; and the
    ///     trailing newline, because an editor that adds one on save would otherwise fail a suite it
    ///     did not touch. Every hand-rolled copy of this in the tree normalises exactly these two.
    /// </remarks>
    static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n');
}
