// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;
using Vixen.Engine.Behaviors;
using Xunit;

namespace Vixen.Engine.Tests;

/// <summary>
///     The "worth a second look" threshold is doc 04's opinion, so the code that acts on it has to
///     still be reading doc 04's number — and only one piece of code may hold it.
/// </summary>
/// <remarks>
///     <para>
///         <b><a href="https://github.com/Rikarin/Vixen/issues/1230">#1230</a>.</b> Both readers of
///         <c>BehaviorStore.Population</c> — <c>vixen doctor behaviors</c> and the editor's statistics
///         panel — carried their own <c>const int … = 200</c>, each with a remark arguing that the
///         figure was the document's rather than the tool's. Both remarks were right and neither
///         could see the other, so the day doc 04 moved the figure the command and the panel would
///         have disagreed and nothing would have failed. <see cref="BehaviorScale.Many" /> is now the
///         only copy in code; this is the gate on the seam a constant cannot close by itself, which
///         is the one to the prose.
///     </para>
///     <para>
///         ⚠ <b>Anchored on the nearest checkout and not the outermost one.</b>
///         <c>.claude/worktrees/</c> holds a whole checkout per agent, so a walk that kept going would
///         leave a worktree's run asserting about the main tree's files — green about a document this
///         branch cannot change and blind to the one it can.
///     </para>
///     <para>
///         ⚠ <b>From <see cref="AppContext.BaseDirectory" /> rather than a <c>[CallerFilePath]</c>.</b>
///         CI sets <c>ContinuousIntegrationBuild</c> and the SDK derives <c>DeterministicSourcePaths</c>
///         from it, which rewrites a compiled path's repository root to <c>/_/</c> — a directory that
///         exists on no machine. <c>Directory.Build.props</c> turns that off for test projects, and
///         this walk does not depend on it having done so.
///     </para>
/// </remarks>
public class BehaviorScaleTests {
    /// <summary>The sentence in doc 04 that states the figure, and the figure in it.</summary>
    /// <remarks>
    ///     ⚠ <b><c>[\s&gt;]+</c> between the words, because the sentence is wrapped inside a block
    ///     quote</b> — it reaches the file as <c>this\n&gt; document</c>, and a pattern written
    ///     against the rendered prose would match nothing and this file would pass having read no
    ///     number at all.
    /// </remarks>
    static readonly Regex Stated = new(
        @"The threshold this[\s>]+document owes the tool is ([0-9]+)",
        RegexOptions.CultureInvariant
    );

    /// <summary>Where the two readers are, relative to the checkout root.</summary>
    /// <remarks>
    ///     Named rather than globbed. The claim is about these two files — a glob over the tree would
    ///     be a search for the string, which is a different and weaker question: it would pass on a
    ///     day both readers had been deleted.
    /// </remarks>
    static readonly string[] Readers = [
        Path.Combine("Tools", "Vixen.Cli", "BehaviorsRunner.cs"),
        Path.Combine("Editor", "Vixen.Editor.App", "EditorDiagnostics.cs")
    ];

    /// <summary>The checkout this assembly was compiled in.</summary>
    static string Root {
        get {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);

            while (directory is not null) {
                if (File.Exists(Path.Combine(directory.FullName, "Vixen.slnx"))) {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }

            throw new InvalidOperationException(
                "No Vixen.slnx above " + AppContext.BaseDirectory + ", so no checkout to read doc 04 from."
            );
        }
    }

    /// <summary>The constant is the number doc 04 § <i>When to write one</i> states.</summary>
    [Fact]
    public void The_threshold_is_the_one_doc_04_states() {
        var page = Path.Combine(Root, "docs", "plan", "04-ecs-and-scripting.md");

        Assert.True(File.Exists(page), page + " is not on this machine, so nothing was compared.");

        var stated = Stated.Match(File.ReadAllText(page));

        // The instrument first. A pattern that stopped matching — the sentence reworded, the block
        // quote unwrapped — would leave every assertion below trivially satisfiable, which is the
        // silent-success shape this repository keeps finding.
        Assert.True(
            stated.Success,
            "docs/plan/04-ecs-and-scripting.md no longer contains the sentence stating the threshold, so "
            + "this test compared BehaviorScale.Many with nothing. Either the sentence moved — update "
            + "the pattern — or the document stopped stating a figure, in which case the constant has "
            + "no source and #1230 is back."
        );

        Assert.Equal(
            BehaviorScale.Many,
            int.Parse(stated.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)
        );
    }

    /// <summary>Both readers reach for the constant, and neither declares a figure of its own.</summary>
    /// <remarks>
    ///     ⚠ <b>The second half is what #1230 actually was.</b> Two files agreeing today while each
    ///     holds its own literal is the arrangement that failed: it is correct until the document
    ///     moves and then silently is not. So a reader naming <see cref="BehaviorScale.Many" /> is
    ///     required, and a <c>const int</c> beside it is refused.
    /// </remarks>
    [Fact]
    public void Neither_reader_keeps_a_copy_of_the_number() {
        var threshold = new Regex(
            @"const\s+int\s+[A-Za-z]*Many[A-Za-z]*\s*=",
            RegexOptions.CultureInvariant
        );

        foreach (var reader in Readers) {
            var path = Path.Combine(Root, reader);

            Assert.True(File.Exists(path), path + " is not on this machine, so nothing was read.");

            var text = File.ReadAllText(path);

            Assert.True(
                text.Contains("BehaviorScale.Many", StringComparison.Ordinal),
                reader + " no longer reads BehaviorScale.Many. It is one of the two readers of "
                + "BehaviorStore.Population, and doc 04's threshold is meant to reach it from one place."
            );

            Assert.False(
                threshold.IsMatch(text),
                reader + " declares a threshold constant of its own again, which is #1230: two copies "
                + "of one figure that cannot see each other, both with a remark saying the figure "
                + "belongs to doc 04."
            );
        }
    }
}
