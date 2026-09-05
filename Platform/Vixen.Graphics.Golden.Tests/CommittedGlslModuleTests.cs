// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
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
    /// <summary>Directory names a walk from the repository root must not descend into.</summary>
    /// <remarks>
    ///     ⚠ <c>.claude</c> holds a whole checkout per agent. A walk that does not skip it compares
    ///     one agent's copy of a file with another's and reports work against a tree nobody is
    ///     editing. <c>bin</c> and <c>obj</c> hold copies of the very modules this checks.
    /// </remarks>
    static readonly string[] Skipped = [
        ".git", ".claude", ".vs", ".idea", "bin", "obj", "artifacts", "TestResults", "node_modules"
    ];

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
        var recorded = Updating ? [] : Recorded();
        var found = Pairs(root);

        var written = new List<string>();

        foreach (var name in found) {
            var source = Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar));
            var module = source + ".spv";

            var code = SharedUiShaderTests.Digest(Encoding.UTF8.GetBytes(SharedUiShaderTests.Code(File.ReadAllText(source))));
            var binary = SharedUiShaderTests.Digest(File.ReadAllBytes(module));

            if (Updating) {
                written.Add($"{name} {code} {binary}");
                continue;
            }

            Assert.True(
                recorded.TryGetValue(name, out var pair),
                $"'{name}' has a compiled module committed beside it and no line in 'hand-compiled.sha256', so "
                + "nothing says which source those bytes were built from. Add one with "
                + "`VIXEN_UPDATE_SHADER_DIGESTS=1`, or delete the module if nothing loads it."
            );

            Assert.True(
                string.Equals(pair.Code, code, StringComparison.Ordinal),
                $"'{name}' has changed since '{name}.spv' was built — its code, not its comments, which are "
                + "stripped before this digest. Whatever loads that module is not running this source. "
                + $"Regenerate it and the ledger: `glslc {name} -o {name}.spv` from the repository root, then "
                + "rerun with `VIXEN_UPDATE_SHADER_DIGESTS=1`."
            );

            Assert.True(
                string.Equals(pair.Module, binary, StringComparison.Ordinal),
                $"'{name}.spv' is not the module the ledger records, and its source is unchanged — so a binary "
                + "moved without the source that produced it. Rerun with `VIXEN_UPDATE_SHADER_DIGESTS=1` only "
                + "if that was deliberate."
            );
        }

        if (Updating) {
            File.WriteAllLines(Ledger, written);

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

    /// <summary>The ledger, by repository-relative source path.</summary>
    static Dictionary<string, (string Code, string Module)> Recorded() {
        Assert.True(
            File.Exists(Ledger),
            $"'{Ledger}' is missing, and it is the only thing that says which source each hand-compiled "
            + "module was built from. Write it with `VIXEN_UPDATE_SHADER_DIGESTS=1`."
        );

        var found = new Dictionary<string, (string, string)>(StringComparer.Ordinal);

        foreach (var line in File.ReadAllLines(Ledger)) {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length == 3) {
                found[parts[0]] = (parts[1], parts[2]);
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

        return File.ReadLines(beside).Any(line =>
            line.Split(' ', StringSplitOptions.RemoveEmptyEntries) is [var first, _, _]
            && string.Equals(first, name, StringComparison.Ordinal)
        );
    }

    /// <summary>Every <c>.spv</c> under <paramref name="root" />, skipping what a walk must not read.</summary>
    /// <remarks>
    ///     Hand-rolled rather than <c>EnumerateFiles(..., AllDirectories)</c> because that one cannot
    ///     be told to skip a directory — it walks every agent worktree and every <c>obj</c> first and
    ///     hands the caller the results afterwards.
    /// </remarks>
    static IEnumerable<string> Modules(string root) {
        var pending = new Stack<string>();

        pending.Push(root);

        while (pending.Count > 0) {
            var current = pending.Pop();

            foreach (var directory in Directory.EnumerateDirectories(current)) {
                if (!Skipped.Contains(Path.GetFileName(directory), StringComparer.Ordinal)) {
                    pending.Push(directory);
                }
            }

            foreach (var file in Directory.EnumerateFiles(current, "*.spv")) {
                yield return file;
            }
        }
    }
}
