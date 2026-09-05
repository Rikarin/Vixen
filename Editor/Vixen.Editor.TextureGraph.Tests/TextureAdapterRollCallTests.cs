// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using Xunit;

namespace Tests;

/// <summary>
///     Doc 48's exit criterion 11 — "a device is confirmed by name in every GPU test in this area" —
///     as a property of the harness and a roll call over this project's own files, rather than as a
///     habit nineteen files happen to have.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>This is the criterion whose failure mode this repository has actually suffered.</b>
///         Eighteen golden files <em>passed</em> rather than skipped without a device until
///         2026-08-21, and the reason a whole suite can go quiet is always the same: the thing that
///         reports is the thing that was supposed to be watched. Every device file here does name its
///         adapter — that was measured, and it was true of all nineteen — but it was true by
///         convention, and a convention is a claim about the files that exist rather than about the
///         next one.
///     </para>
///     <para>
///         <b>So there are two mechanisms and they cover different holes.</b>
///         <see cref="TextureKernelHarness.Open" /> writes the adapter into the running test's output
///         itself, which means a device cannot be opened anonymously however forgetful the twentieth
///         file is; and the roll call below reads this project's sources and requires any file that
///         opens a device to name the adapter in its own text, which is what keeps the adapter in the
///         <em>failure messages</em> where a number is read.
///     </para>
///     <para>
///         ⚠ <b>Anchored at this file's own compiled path, deliberately.</b> A walk from the
///         repository root reads <c>.claude/worktrees</c>, which holds a whole checkout per agent —
///         a roll call that found those would be comparing other people's copies of these files with
///         each other. <see cref="Here" /> is <c>[CallerFilePath]</c>, so the directory walked is the
///         one this assembly was compiled from and nothing above it is ever opened.
///     </para>
/// </remarks>
public class TextureAdapterRollCallTests {
    /// <summary>Where this file was compiled from, which is the directory the roll call reads.</summary>
    static string Here([CallerFilePath] string path = "") => path;

    /// <summary>How a file in this project opens a device: there is one way, and this is its text.</summary>
    /// <remarks>
    ///     ⚠ <b>The detector is anchored to the harness, and the roll call checks the anchor.</b> If
    ///     <c>Open</c> is renamed, every file stops matching and the roll call would pass over an
    ///     empty set — the silent-success failure this whole file is about — so the harness itself is
    ///     required to match, and the failure says what to update.
    /// </remarks>
    const string Opens = "Open()";

    /// <summary>What naming the adapter looks like.</summary>
    const string Names = "Adapter(";

    /// <summary>A file that opens a device and is excused from naming it, and why.</summary>
    /// <remarks>
    ///     Empty. Every file in this project that opens a device names the adapter today, which is
    ///     what makes this roll call a statement of the state rather than a wish; the list is checked
    ///     from both ends, so an entry that stopped being needed is a line to delete.
    /// </remarks>
    static readonly (string File, string Reason)[] Anonymous = [];

    /// <summary>
    ///     ⚠ Every file here that opens a device names the adapter, or is on a written list.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The instruments first, because each is a way for this to be green over nothing.</b>
    ///         The directory has to exist and has to contain this very file — a run from a copied
    ///         binary, or a <c>[CallerFilePath]</c> from a machine that is not this one, would
    ///         otherwise enumerate nothing and pass. The harness has to be one of the matches, or the
    ///         detector no longer describes how a device is opened here. And the match set has to be
    ///         large enough to be the device suites rather than a stray file: it was nineteen when
    ///         this was written, so anything under ten means the pattern has drifted.
    ///     </para>
    ///     <para>
    ///         <b>What it cannot see, said plainly.</b> Only this project. The twentieth device file
    ///         in doc 48's area is <c>Vixen.Editor.Texturing.Tests/TexturePreviewDeviceTests.cs</c>,
    ///         which has a private copy of <c>Open</c> and <c>Adapter</c> and does name its adapter —
    ///         but nothing holds it to that, and a roll call in this assembly structurally cannot.
    ///         The rule that would cover both belongs in <c>build/</c>, and scoping it is not
    ///         obvious: every test project that reaches this one transitively pulls in
    ///         <c>Vixen.Graphics.Golden.Tests</c>, whose nineteen device files name no adapter and
    ///         are not doc 48's. See <see href="https://github.com/Rikarin/Vixen/issues/795" />.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Every_file_here_that_opens_a_device_names_the_adapter() {
        var directory = Path.GetDirectoryName(Here())!;

        Assert.True(
            Directory.Exists(directory),
            $"'{directory}' does not exist, so this roll call read no files at all. It is anchored at this "
            + "file's compiled path; a run whose sources are not on the machine cannot take it."
        );

        var sources = Directory.GetFiles(directory, "*.cs", SearchOption.TopDirectoryOnly)
            .Select(path => (Name: Path.GetFileName(path), Text: File.ReadAllText(path)))
            .ToArray();

        Assert.Contains(sources, source => source.Name == "TextureAdapterRollCallTests.cs");

        var opens = sources
            .Where(source => source.Text.Contains(Opens, StringComparison.Ordinal))
            .ToArray();

        Assert.Contains(opens, source => source.Name == "TextureKernelHarness.cs");

        Assert.True(
            opens.Length >= 10,
            $"Only {opens.Length} files here contain '{Opens}', and there were twenty when this was written. "
            + "The way a device is opened in this project has changed and this roll call is now reading an "
            + "almost empty set — which is the failure it exists to prevent, not a pass."
        );

        var unnamed = opens
            .Where(source => !source.Text.Contains(Names, StringComparison.Ordinal))
            .Select(source => source.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        var excused = Anonymous.Select(entry => entry.File).Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(excused, unnamed);

        // Both ends, and a reason that says something: a name here whose file names the adapter now
        // is an allowance that has outlived what it excused.
        Assert.All(excused, name => Assert.Contains(opens, source => source.Name == name));
        Assert.All(Anonymous, entry => Assert.True(entry.Reason.Length > 40, entry.File));
    }

    /// <summary>
    ///     ⚠ Opening a device names the adapter even when the test does not ask.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The half the roll call cannot make true.</b> A new device suite that never writes
    ///         its adapter anywhere would fail the roll call at compile-and-run time; this is what
    ///         makes the number in its output attributed anyway, and it is the mechanical form of
    ///         "a device is confirmed by name in every GPU test in this area".
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The instrument is the assertion before the finding: this test writes nothing
    ///         itself.</b> Its own output is empty until <see cref="TextureKernelHarness.Open" /> is
    ///         called, so the adapter's name being there afterwards is the harness's doing and cannot
    ///         be this test's. Without a device it skips, loudly, like every other device test here.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Opening_a_device_names_the_adapter_without_being_asked() {
        var output = TestContext.Current.TestOutputHelper;

        Assert.NotNull(output);
        Assert.DoesNotContain("adapter:", output!.Output, StringComparison.Ordinal);

        using var device = TextureKernelHarness.Open();

        // The whole line the harness writes, so this is a claim about the name, the kind and the
        // driver version rather than about the word "adapter" appearing.
        Assert.Contains("adapter:", output.Output, StringComparison.Ordinal);
        Assert.Contains(TextureKernelHarness.Adapter(device), output.Output, StringComparison.Ordinal);
    }
}
