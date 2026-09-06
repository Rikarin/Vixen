// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using Vixen.Editor.Testing;
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
    ///         ✅ <b>It used to see only this project, and that was
    ///         <a href="https://github.com/Rikarin/Vixen/issues/883">#883</a>.</b> The walk is
    ///         <see cref="DeviceRollCall" /> now, taking its directory as a parameter, and
    ///         <c>Vixen.Editor.Texturing.Tests</c> — the other half of doc 48's area, five device
    ///         files — is its second caller. Scoping it through a project graph instead was the
    ///         wrong shape and is worth recording: every test project that reaches this one
    ///         transitively pulls in <c>Vixen.Graphics.Golden.Tests</c>, whose nineteen device files
    ///         name no adapter and are not doc 48's. See
    ///         <see href="https://github.com/Rikarin/Vixen/issues/795" />.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Every_file_here_that_opens_a_device_names_the_adapter() =>
        DeviceRollCall.Take(
            DeviceRollCall.Read(Path.GetDirectoryName(Here())!, "TextureAdapterRollCallTests.cs"),
            Opens,
            Names,
            harness: "TextureKernelHarness.cs",
            least: 10,
            Anonymous
        );

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

    /// <summary>
    ///     ⚠ Nothing here but the harness can get a device at all, so the twentieth device file
    ///     cannot have an anonymous one.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The strong half of criterion 11</b>
    ///         (<a href="https://github.com/Rikarin/Vixen/issues/923">#923</a>). The roll call above
    ///         asks whether a file that <em>looks like</em> it opens a device names the adapter, and
    ///         its detector is the word <c>Open()</c> — a naming convention the author of the next
    ///         device file chooses. This asks whether any file but the harness <em>can</em> open one,
    ///         and its detector is the backend call that produces a device, which nobody here can
    ///         rename.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It was already true of this project and is asserted anyway.</b> The nineteen
    ///         device files here all go through the harness; that was a habit until this line, and a
    ///         habit is what the sister project had too, right up until five files did not.
    ///         <c>Vixen.Editor.Texturing.Tests</c> is the second caller, where the same check is what
    ///         made the consolidation worth doing rather than merely tidy.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Only_the_harness_here_creates_a_device() =>
        DeviceRollCall.Sole(
            DeviceRollCall.Read(Path.GetDirectoryName(Here())!, "TextureAdapterRollCallTests.cs"),
            harness: "TextureKernelHarness.cs"
        );
}
