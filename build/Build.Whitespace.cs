// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Nuke.Common;
using Nuke.Common.IO;
using Serilog;
using static Nuke.Common.Tools.DotNet.DotNetTasks;

/// <summary>
///     The half of `dotnet format` <see cref="CheckFormat" /> could not run, run over the part of
///     the tree it does not argue with.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Mis-indentation was ungated here, by design, and it reached master at least three
///         times</b> (#516). <see cref="CheckFormat" /> runs <c>style</c> and <c>analyzers</c> and
///         deliberately not <c>whitespace</c>, because this repository indents a lambda body passed
///         as an argument one level further than the tool does and no <c>.editorconfig</c> key
///         expresses that. The consequence was a gate that is green on visibly damaged code —
///         `NetworkSession.cs` carried two statements at eight spaces where twelve belonged, through
///         a full sweep that reported `CheckFormat Succeeded` — which is the shape this repository
///         calls a defect in the instrument.
///     </para>
///     <para>
///         ⚠ <b>The premise that made it unrunnable is wrong in both of its numbers.</b> Measured on
///         this tree: the violations are 5 167 and not "about nine hundred", and they fall in 551
///         files and not "twenty-eight". But that is 551 of 4 842 — <b>88% of the tree already
///         agrees with the whitespace pass</b>, including every file in <c>Core/Vixen.Net</c>, which
///         is where the damage that opened #516 landed. Refusing the pass whole gave up a gate over
///         seven eighths of the repository to avoid an argument about one eighth of it.
///     </para>
///     <para>
///         ⚠ <b>And it is not slow.</b> <c>--folder</c> treats the argument as a directory of files
///         and loads no MSBuild workspace at all, so the whole tree takes <b>13.6 s</b> against the
///         minutes each of <see cref="CheckFormat" />'s two workspace passes costs. The reason those
///         are expensive — evaluating 395 projects — does not apply to this one.
///     </para>
///     <para>
///         So the exemption is per file and committed, on the terms <c>docs/DocsExempt.txt</c>
///         already established: the list may only shrink, a file on it that has become clean is an
///         error rather than a line that rots, and rewriting it is a command somebody runs rather
///         than something the gate does for itself.
///     </para>
/// </remarks>
partial class Build {
    AbsolutePath WhitespaceExemptFile => RootDirectory / "docs" / "WhitespaceExempt.txt";

    /// <summary>One reported violation: the file it is in, and nothing else that matters here.</summary>
    [GeneratedRegex(@"^(?<file>.+?)\(\d+,\d+\): error WHITESPACE", RegexOptions.Multiline)]
    private static partial Regex WhitespaceViolation();

    Target CheckWhitespace => definition => definition
        .Description("Fails if a file outside docs/WhitespaceExempt.txt disagrees with `dotnet format whitespace`")
        .Executes(CheckWhitespaceFormatting);

    /// <summary>
    ///     Runs the whitespace pass over the whole tree and fails on any file that is not exempt.
    /// </summary>
    void CheckWhitespaceFormatting() {
        var exempt = ReadWhitespaceExemptions();

        // ⚠ Exit code ignored on purpose, and this is the one place that is safe: `--verify-no-changes`
        // exits 2 whenever *anything* would be reformatted, which on this tree is always, because the
        // exempt files are still there. The output is the measurement; the exit code carries no
        // information this gate can use.
        // ⚠ `.claude/worktrees` holds a whole checkout per agent, and `--folder` walks whatever it is
        // given — so without this the gate reports another session's files, by their worktree path,
        // and a developer is asked to reformat code that is not in this tree and may not be theirs.
        // It is the same trap the golden walk hit: a repo-walking check has to be told where the
        // repository stops.
        var output = DotNet(
            $"format whitespace \"{RootDirectory}\" --folder --verify-no-changes --exclude .claude/",
            logOutput: false,
            logInvocation: false,
            exitHandler: _ => null
        );

        var offending = new SortedSet<string>(StringComparer.Ordinal);

        foreach (Match match in WhitespaceViolation().Matches(string.Join('\n', output.Select(line => line.Text)))) {
            offending.Add(RootDirectory.GetRelativePathTo(AbsolutePath.Create(match.Groups["file"].Value.Trim())).ToUnixRelativePath());
        }

        // ⚠ The instrument, checked before it is trusted. On the day this pass does not run — a
        // renamed subcommand, a changed message, a `--folder` that stopped walking — it reports no
        // violations at all, and "no violations" is indistinguishable from success. It cannot be
        // success while the exemption list is non-empty, because every file on that list is a file
        // this same run has to have flagged.
        Assert.True(
            offending.Count > 0 || exempt.Count == 0,
            "`dotnet format whitespace` reported nothing at all while "
            + $"{exempt.Count} file(s) are exempt because they violate it. The pass did not run, or "
            + "its output no longer looks the way this gate reads it — fix that before trusting a "
            + "green run."
        );

        if (UpdateExemptions) {
            WriteWhitespaceExemptions(offending);

            Log.Warning(
                "{File} has been rewritten. Read the diff before committing: this list is supposed "
                + "to shrink, and a commit that grows it is a commit that added mis-indented code.",
                RootDirectory.GetRelativePathTo(WhitespaceExemptFile).ToUnixRelativePath()
            );

            return;
        }

        var unexpected = offending.Except(exempt, StringComparer.Ordinal).ToList();

        // A file that has become clean has to leave the list in the same commit that cleaned it, or
        // the list becomes a number nothing measures — which is exactly how the count in
        // Directory.Build.targets managed to be wrong four times running (#539).
        var stale = exempt.Except(offending, StringComparer.Ordinal).ToList();

        Assert.True(
            unexpected.Count == 0,
            $"{unexpected.Count} file(s) disagree with `dotnet format whitespace` and are not exempt. "
            + "Run `dotnet format whitespace . --folder --include <the file>` from the repository "
            + "root to fix them — ⚠ `--folder` names a DIRECTORY to walk, so passing the file to it "
            + "instead is the one spelling that cannot work: "
            + string.Join(", ", unexpected.Take(20))
        );

        Assert.True(
            stale.Count == 0,
            $"{stale.Count} file(s) in {WhitespaceExemptFile.Name} now agree with the whitespace "
            + "pass. Delete their lines — the list may only shrink: "
            + string.Join(", ", stale.Take(20))
        );

        Log.Information("Whitespace: every C# file agrees with the pass except the {Exempt} exempt.", exempt.Count);
    }

    HashSet<string> ReadWhitespaceExemptions() =>
        WhitespaceExemptFile.FileExists()
            ? [
                .. WhitespaceExemptFile.ReadAllLines()
                    .Select(line => line.Trim())
                    .Where(line => line.Length > 0 && !line.StartsWith('#'))
            ]
            : [];

    void WriteWhitespaceExemptions(IEnumerable<string> files) =>
        WhitespaceExemptFile.WriteAllLines([
            "# Files `dotnet format whitespace` disagrees with, and that nobody is reformatting today.",
            "#",
            "# ⚠ This list exists so that the whitespace pass can run at all. CheckFormat leaves it out",
            "# because this repository indents a lambda body passed as an argument one level further",
            "# than the tool does, and no .editorconfig key expresses that — but that argument is about",
            "# 551 files out of 4 842, and refusing the pass whole left mis-indentation ungated across",
            "# the other seven eighths of the tree. It reached master three times that way (#516).",
            "#",
            "# `nuke CheckWhitespace` fails when",
            "#",
            "#   * a file not listed here disagrees with the pass — that is the gate,",
            "#   * a file listed here now agrees with it: delete the line in the same commit.",
            "#",
            "# So this file should only ever shrink, and a commit that grows it is a commit that added",
            "# mis-indented code. `./build.sh CheckWhitespace --update-exemptions` rewrites it, and is",
            "# deliberately a command somebody runs: a gate that wrote its own exemptions would fail on",
            "# nothing, forever.",
            .. files
        ]);
}
