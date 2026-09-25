// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Vixen.Testing;
using Xunit;

namespace Vixen.Graphics.Golden.Tests;

/// <summary>
///     Every hand-compiled shader in the repository, held to the source committed beside it.
/// </summary>
/// <remarks>
///     <para>
///         <b>The gap this closes.</b> Four directories held a hand-written GLSL source and the
///         <c>.spv</c> a person compiled from it, side by side and committed, and nothing anywhere
///         proved the pair still agreed — <c>Core/Vixen.Rendering/Shaders/line.{vert,frag}</c>, which
///         is an <c>EmbeddedResource</c> in a shipping runtime assembly, and the shaders of samples
///         01, 11 and 12. Each <c>.csproj</c> writes the regeneration step out in a comment, which is
///         the tell: it is a step a person runs, and nothing noticed when they did not. Edit the
///         GLSL, commit, and the source in the tree says one thing while the bytes the assembly
///         embeds say another.
///     </para>
///     <para>
///         ⚠ <b><c>CheckShaders</c> does not cover these and cannot be extended to.</b> That target
///         compiles Raven and refuses a committed module no entry produces; these are not Raven, so
///         they fall outside it entirely. Nor can the fix be "recompile them in a gate":
///         <c>TestShaders</c> records the decision not to require <c>glslc</c> on every CI leg, and
///         that refusal is still right. So this records a digest instead, and is honest about what
///         that buys — it catches the edit-and-forget case exactly and says nothing whatever about
///         whether the bytes are <i>correct</i>, which nothing without a compiler can say.
///     </para>
///     <para>
///         ⚠ <b>The census found twenty-four more pairs than the issue that asked for it named, and
///         they are in this project.</b> <c>Shaders/</c> here holds thirty GLSL sources with a
///         committed module beside each, and <see cref="SharedUiShaderTests" />' ledger records eight
///         of them — the eight the UI suite renders with. The other twenty-two are the same
///         arrangement and were uncovered for the same reason: a hand-kept list of names, in a
///         directory nobody was counting. A census with a twenty-four-file exception list is not a
///         census, so they are in. ⚠ Twenty-two and not twenty-four because <c>line.vert</c> and
///         <c>line.frag</c> left: they were a byte-identical copy of <c>Core/Vixen.Rendering</c>'s
///         pair, and this suite now reads that assembly's embedded modules instead (#637).
///     </para>
///     <para>
///         ⚠ <b>Two ledgers, one mechanism, and the partition between them is what makes that
///         safe.</b> A pair a <c>modules.sha256</c> beside it already names belongs to that ledger and
///         is skipped here; everything else the walk finds belongs here. The two are therefore total
///         over the walk by construction, and a line deleted from the other one does not open a hole:
///         this walk claims the pair the moment that ledger stops naming it. The digest is computed by
///         <see cref="SharedUiShaderTests.Code" /> and <see cref="SharedUiShaderTests.Digest" /> —
///         the same code, not a second copy of it, because two comment-strippers that disagree would
///         make one ledger unwritable from the other's numbers.
///     </para>
///     <para>
///         ⚠ <b>And the flavour column is here too, because a fix in one of two ledgers built from
///         one mechanism is half a fix.</b> #1257 was about a regeneration message that named plain
///         <c>glslc</c> for a module built with <c>-O</c>; it was fixed in <c>modules.sha256</c> and
///         left standing here, where the message said <c>glslc {name} -o {name}.spv</c> with no flag
///         at all. Nobody is being misled today — all thirty modules this ledger governs carry a
///         debug section, so plain <c>glslc</c> does reproduce them — which is exactly why it had to
///         be fixed before an <c>-O</c> module arrives rather than after. The column, the refusal
///         without <see cref="SharedUiShaderTests.Reflavouring" /> and the detector's own check are
///         the sibling's, not a second implementation.
///     </para>
///     <para>
///         <b>Why it lives in this project</b>, when the module that matters most is
///         <c>Core/Vixen.Rendering</c>'s. Because the mechanism is here: the stripper, the digest and
///         the repository walk all already existed for the eight UI shaders, and a second
///         implementation of them somewhere more obvious is exactly the failure this closes. Nothing
///         in this class opens a device — the whole class is file reads.
///     </para>
///     <para>
///         ⚠ <b>What this prints on the day it stops walking.</b> Nothing, if the only assertion were
///         "every pair found has a line" — a walk that finds no pairs satisfies that trivially, which
///         is the "comparator that called three empty manifests identical" this repository has already
///         shipped once. So the ledger is asserted in the other direction too: every line in it names
///         a pair this walk must have found, and there are thirty of them. A root that moved, a
///         skip list that swallowed a real directory, a pattern that stopped matching — each of those
///         loses all thirty at once and is loud.
///     </para>
/// </remarks>
public class CommittedGlslModuleTests {
    /// <summary>The committed record of which source each hand-compiled module was built from.</summary>
    static string Ledger =>
        Path.Combine(SharedUiShaderTests.RepositoryRoot(), "Platform", "Vixen.Graphics.Golden.Tests", "hand-compiled.sha256");

    /// <summary>Rewrites the ledger instead of checking it, for the commit that regenerates a module.</summary>
    /// <remarks>
    ///     The same variable <see cref="SharedUiShaderTests" /> reads, so one regeneration run rewrites
    ///     both ledgers rather than leaving whichever one the author forgot.
    /// </remarks>
    static bool Updating =>
        Environment.GetEnvironmentVariable("VIXEN_UPDATE_SHADER_DIGESTS") is "1" or "true" or "TRUE";

    /// <summary>Every hand-compiled module is the one built from the source committed beside it.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The comparison is of the source's <i>code</i>, comments stripped.</b> A comment is
    ///         then free to edit, which matters more than it sounds: the check this replaces in spirit
    ///         — a module no older than its source — made correcting a wrong sentence in a shader
    ///         header expensive enough that a wrong sentence sat in one for months, and #588 is that
    ///         story.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The module's own digest is recorded as well as the source's, and it is not
    ///         redundant.</b> Source unchanged with a module that moved is a different fact from a
    ///         source that moved — the first says a binary arrived from somewhere the tree cannot
    ///         account for, and the message says so rather than telling the reader to recompile.
    ///     </para>
    /// </remarks>
    [Fact]
    public void EveryHandCompiledModuleMatchesTheSourceCommittedBesideIt() {
        var root = SharedUiShaderTests.RepositoryRoot();
        // ⚠ Read even when rewriting, which it was not before the flavour column: a rewrite that
        // cannot see the old ledger cannot notice that a module changed flavour, which is the one
        // thing it is supposed to refuse. The sibling ledger reads it unconditionally for the same
        // reason.
        var recorded = Recorded();
        var found = Pairs(root);

        var written = new List<string>();

        foreach (var name in found) {
            var source = Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar));
            var module = source + ".spv";

            var code = SharedUiShaderTests.Digest(Encoding.UTF8.GetBytes(SharedUiShaderTests.Code(File.ReadAllText(source))));
            var binary = SharedUiShaderTests.Digest(File.ReadAllBytes(module));
            var flavour = SharedUiShaderTests.Flavour(SharedUiShaderTests.WordsOf(module));

            if (Updating) {
                // ⚠ The one thing a rewrite refuses, exactly as the sibling ledger refuses it: a
                // module that arrived in the other flavour. Changing a shader is routine; changing
                // whether its constants are folded is a decision, and a rewrite that recorded it
                // silently is what #1257 was filed about.
                if (recorded.TryGetValue(name, out var was) && was.Flavour is not null) {
                    Assert.True(
                        SharedUiShaderTests.Reflavouring || string.Equals(was.Flavour, flavour, StringComparison.Ordinal),
                        $"'{name}.spv' was built with `glslc {flavour}` and the ledger records `glslc "
                        + $"{was.Flavour}`. Rebuild it the way it was built before: `glslc {was.Flavour} {name} -o "
                        + $"{name}.spv` from the repository root. If changing the flavour is the point, rerun with "
                        + "`VIXEN_UPDATE_SHADER_FLAVOUR=1` as well."
                    );
                }

                written.Add($"{name} {code} {binary} {flavour}");
                continue;
            }

            Assert.True(
                recorded.TryGetValue(name, out var pair),
                $"'{name}' has a compiled module committed beside it and no line in 'hand-compiled.sha256', so "
                + "nothing says which source those bytes were built from. Add one with "
                + "`VIXEN_UPDATE_SHADER_DIGESTS=1`, or delete the module if nothing loads it."
            );

            Assert.True(
                pair.Flavour is not null,
                $"'{name}'s line in 'hand-compiled.sha256' predates the flavour column, so the regeneration "
                + "message below cannot say which way to run glslc. Rewrite the ledger with "
                + "`VIXEN_UPDATE_SHADER_DIGESTS=1`."
            );

            Assert.True(
                string.Equals(pair.Flavour, flavour, StringComparison.Ordinal),
                $"'hand-compiled.sha256' says '{name}.spv' was built with `glslc {pair.Flavour}` and the module "
                + $"says `glslc {flavour}` — it {(flavour == SharedUiShaderTests.Optimised ? "carries no" : "carries a")} "
                + "debug section. The ledger was edited by hand, or a module arrived through a path other than "
                + "`VIXEN_UPDATE_SHADER_DIGESTS=1`."
            );

            Assert.True(
                string.Equals(pair.Code, code, StringComparison.Ordinal),
                $"'{name}' has changed since '{name}.spv' was built — its code, not its comments, which are "
                + "stripped before this digest. Whatever loads that module is not running this source. "
                + $"Regenerate it and the ledger: `glslc {pair.Flavour} {name} -o {name}.spv` from the repository "
                + "root, then rerun with `VIXEN_UPDATE_SHADER_DIGESTS=1`."
            );

            Assert.True(
                string.Equals(pair.Module, binary, StringComparison.Ordinal),
                $"'{name}.spv' is not the module the ledger records, and its source is unchanged — so a binary "
                + "moved without the source that produced it. Rerun with `VIXEN_UPDATE_SHADER_DIGESTS=1` only "
                + "if that was deliberate."
            );
        }

        if (Updating) {
            // LF whatever the host, because the file is committed and `.gitattributes` says LF.
            File.WriteAllText(Ledger, string.Join('\n', written) + '\n');

            return;
        }

        // ⚠ The instrument, and it is the half that is not free. Every line above asks "is this pair
        // recorded"; a walk that has stopped finding pairs asks nothing at all and passes. So every
        // recorded line has to be a pair the walk found — which is false the moment the walk breaks,
        // and false loudly, because it breaks for all of them at once.
        var lost = recorded.Keys
            .Where(name => !found.Contains(name, StringComparer.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            lost.Count == 0,
            "'hand-compiled.sha256' records pairs this test's own walk of the tree did not find:\n  "
            + string.Join("\n  ", lost)
            + "\nEither the source moved and the line is stale, or the walk is no longer reaching it — and a "
            + "walk that reaches nothing reports every ledger complete."
        );

        Assert.Equal(found.Count, recorded.Count);
    }

    /// <summary>
    ///     The one pair this walk is impossible to be right without, named rather than counted.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Because the check above is circular if the ledger is empty too.</b> "Every pair has a
    ///     line" and "every line is a pair" are both satisfied by nothing and nothing, so a walk that
    ///     broke in the same commit that emptied the ledger would agree with itself. This names the
    ///     module the issue calls the one that matters most — an <c>EmbeddedResource</c> in a shipping
    ///     runtime assembly rather than a sample's file on disk — so the walk has to reach out of this
    ///     project and into <c>Core/</c> before anything it says counts.
    /// </remarks>
    [Fact]
    public void TheWalkReachesTheModuleAShippingAssemblyEmbeds() {
        var found = Pairs(SharedUiShaderTests.RepositoryRoot());

        Assert.Contains("Core/Vixen.Rendering/Shaders/line.vert", found);
        Assert.Contains("Core/Vixen.Rendering/Shaders/line.frag", found);
    }

    /// <summary>The flavour this ledger records is read off each module's bytes, not assumed.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The instrument for the fourth column, and deliberately not a partition.</b>
    ///         <see cref="SharedUiShaderTests.TheFlavourColumnIsWhatTheModulesBytesSay" /> can name
    ///         which three of its eight are optimised because its eight are a fixed list; this walk
    ///         finds whatever the tree holds, and every one of them carries a debug section today.
    ///         Pinning that would make the column's whole purpose — letting an <c>-O</c> module
    ///         arrive deliberately — turn this red.
    ///     </para>
    ///     <para>
    ///         So what is pinned is that the detector reads the debug section and not some other
    ///         property: cut that section out in memory and a module that read as
    ///         <see cref="SharedUiShaderTests.Unoptimised" /> reads as
    ///         <see cref="SharedUiShaderTests.Optimised" />. ⚠ And that the loop ran at all, because
    ///         a walk that found nothing would satisfy every assertion inside it.
    ///     </para>
    /// </remarks>
    [Fact]
    public void TheFlavourColumnIsWhatEachModulesBytesSay() {
        var root = SharedUiShaderTests.RepositoryRoot();
        var probed = 0;

        foreach (var name in Pairs(root)) {
            var module = Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar)) + ".spv";
            var words = SharedUiShaderTests.WordsOf(module);

            if (SharedUiShaderTests.Flavour(words) == SharedUiShaderTests.Optimised) {
                continue;
            }

            Assert.Equal(
                SharedUiShaderTests.Optimised,
                SharedUiShaderTests.Flavour(SharedUiShaderTests.WithoutDebugSection(words))
            );

            probed++;
        }

        Assert.True(
            probed > 0,
            "not one hand-compiled module read as unoptimised, so nothing above exercised the detector. "
            + "Either every module in the tree is `-O` now — in which case this check needs rewriting — or "
            + "the walk found nothing, which is the failure this class exists to be loud about."
        );
    }

    /// <summary>The ledger, by repository-relative source path.</summary>
    /// <remarks>
    ///     ⚠ <b>Three columns before #1257 and four since</b>, and a three-column line is read rather
    ///     than rejected here so that the line that says so is the assertion in
    ///     <see cref="EveryHandCompiledModuleMatchesTheSourceCommittedBesideIt" /> and not a silent
    ///     parse failure that empties the dictionary.
    /// </remarks>
    static Dictionary<string, (string Code, string Module, string? Flavour)> Recorded() {
        Assert.True(
            File.Exists(Ledger),
            $"'{Ledger}' is missing, and it is the only thing that says which source each hand-compiled "
            + "module was built from. Write it with `VIXEN_UPDATE_SHADER_DIGESTS=1`."
        );

        var found = new Dictionary<string, (string, string, string?)>(StringComparer.Ordinal);

        foreach (var line in File.ReadAllLines(Ledger)) {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            if (parts is [var name, var code, var binary, ..]) {
                found[name] = (code, binary, parts.Length >= 4 ? parts[3] : null);
            }
        }

        return found;
    }

    /// <summary>
    ///     Every source in the tree with a compiled module committed beside it, less those another
    ///     ledger already names.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Keyed on the module and not on the source's extension</b>, so there is no list of
    ///     shader suffixes to keep. A <c>.spv</c> whose name minus <c>.spv</c> is a file that exists
    ///     is a hand-compiled pair by construction; a <c>.spv</c> whose name minus <c>.spv</c> is not
    ///     a file is a module Raven emitted, named after a shader rather than after a source, and none
    ///     of this test's business — <c>CheckShaders</c> owns those and #564 closed its last hole.
    /// </remarks>
    static List<string> Pairs(string root) {
        var found = new List<string>();

        foreach (var module in Modules(root)) {
            var source = module[..^4];

            if (!File.Exists(source)) {
                continue;
            }

            var name = Path.GetRelativePath(root, source).Replace('\\', '/');

            if (!Delegated(source)) {
                found.Add(name);
            }
        }

        found.Sort(StringComparer.Ordinal);

        return found;
    }

    /// <summary>Whether a <c>modules.sha256</c> beside this source already records it.</summary>
    static bool Delegated(string source) {
        var beside = Path.Combine(Path.GetDirectoryName(source)!, "modules.sha256");

        if (!File.Exists(beside)) {
            return false;
        }

        var name = Path.GetFileName(source);

        // Three columns before #1257 and four since; the name is the first either way.
        return File.ReadLines(beside).Any(line =>
            line.Split(' ', StringSplitOptions.RemoveEmptyEntries) is [var first, _, _, ..]
            && string.Equals(first, name, StringComparison.Ordinal)
        );
    }

    /// <summary>Every <c>.spv</c> under <paramref name="root" />, as git defines the tree.</summary>
    /// <remarks>
    ///     ⚠ Not a directory walk (#1424): <c>.claude</c> holds a whole checkout per agent, and
    ///     <c>bin</c> and <c>obj</c> hold copies of the very modules this checks — and a hand-kept list
    ///     of names to skip is a second, drifting answer to the question git already answers.
    /// </remarks>
    static List<string> Modules(string root) => RepositoryFiles.Files(root, "*.spv");
}
