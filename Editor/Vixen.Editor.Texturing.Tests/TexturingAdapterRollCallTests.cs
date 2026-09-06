// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using Vixen.Editor.Testing;
using Xunit;

namespace Vixen.Editor.Texturing.Tests;

/// <summary>
///     Doc 48's exit criterion 11 for the other half of its area — the same walk
///     <c>TextureAdapterRollCallTests</c> takes, over this project.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The criterion is "a device is confirmed by name in <em>every</em> GPU test in this
///         area", the area is two test projects, and one of them had nothing enumerating its device
///         files</b> (<a href="https://github.com/Rikarin/Vixen/issues/883">#883</a>). The convention
///         held — every file here names its adapter, and that was measured — but a convention is a
///         claim about the files that exist rather than about the next one, which is the sentence the
///         first roll call was written to stop being true.
///     </para>
///     <para>
///         ⚠ <b>The walk is <see cref="DeviceRollCall" />, taken as a parameterised call rather than
///         copied</b> — the arrangement <c>PluginReferenceRule</c> and <c>TextureKernelSurfaces</c>
///         use, and the one <a href="https://github.com/Rikarin/Vixen/issues/872">#872</a> says a
///         second roll call must not be without. A second transcription of this walk would have the
///         defect it is written to catch: two subject sets, one of which somebody forgets.
///     </para>
///     <para>
///         ✅ <b>The harness guarantee reached two of the seven device files here and now reaches all
///         seven</b> (<a href="https://github.com/Rikarin/Vixen/issues/923">#923</a>). Five of them
///         carried a private opener that created its own device and never reached
///         <see cref="TexturingDevice.Open" />; all five named their adapter, so this was never a
///         defect in the tree, and it meant the strong mechanism covered two files while this walk
///         carried the other five.
///     </para>
///     <para>
///         ⚠ <b>The consolidation is not what closed the hole — <see cref="DeviceRollCall.Sole" />
///         is.</b> Deleting five copies makes the tree tidy and says nothing about the eighth file,
///         which is the sentence #883 was written to stop being true one level along. So the roll
///         call now also requires the backend's creating call to appear in the harness and nowhere
///         else: an author cannot rename it, so a new device file has no way to get a device except
///         through the harness, and the harness names the adapter whether or not the file asks.
///     </para>
/// </remarks>
public class TexturingAdapterRollCallTests {
    /// <summary>Where this file was compiled from, which is the directory the roll call reads.</summary>
    static string Here([CallerFilePath] string path = "") => path;

    /// <summary>How a file in this project opens a device.</summary>
    /// <remarks>
    ///     ⚠ Matches the harness's declaration and every call site of it — which is what makes the
    ///     set the device files rather than a subset chosen by which helper a file happened to use.
    ///     <see cref="DeviceRollCall.Take" /> requires the harness to be among the matches, so a
    ///     rename empties the set loudly instead of quietly.
    /// </remarks>
    const string Opens = "Open()";

    /// <summary>What naming the adapter looks like.</summary>
    const string Names = "Adapter(";

    /// <summary>A file that opens a device and is excused from naming it, and why.</summary>
    /// <remarks>
    ///     Empty, and checked from both ends, so a line here that has stopped being needed is a line
    ///     to delete rather than one that rots.
    /// </remarks>
    static readonly (string File, string Reason)[] Anonymous = [];

    /// <summary>
    ///     ⚠ Every file here that opens a device names the adapter, or is on a written list.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>There are nine matches, and #923 said eight falling to seven.</b> Measured rather
    ///     than assumed, before and after the consolidation: the harness, the seven device files and
    ///     this file, which names <c>Open()</c> in its own second test. Deleting the five private
    ///     openers moved none of them out of the set, because every one of those files still calls
    ///     the harness — so the count was nine and stayed nine. The floor is five: below that the
    ///     detector has drifted and the walk is reading an almost empty set, which is a pass that
    ///     means nothing.
    /// </remarks>
    [Fact]
    public void Every_file_here_that_opens_a_device_names_the_adapter() =>
        DeviceRollCall.Take(
            DeviceRollCall.Read(Path.GetDirectoryName(Here())!, "TexturingAdapterRollCallTests.cs"),
            Opens,
            Names,
            harness: "TexturingDevice.cs",
            least: 5,
            Anonymous
        );

    /// <summary>
    ///     ⚠ Opening a device through the harness names the adapter even when the test does not ask.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The half the roll call cannot make true</b>, and the mechanical form of "a device is
    ///         confirmed by name" for everything that goes through <see cref="TexturingDevice.Open" />.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The instrument is the assertion before the finding: this test writes nothing
    ///         itself.</b> Its own output is empty until the harness is called, so the adapter's name
    ///         being there afterwards is the harness's doing and cannot be this test's. Without a
    ///         device it skips, loudly, like every other device test here.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Opening_a_device_through_the_harness_names_the_adapter_without_being_asked() {
        var output = TestContext.Current.TestOutputHelper;

        Assert.NotNull(output);
        Assert.DoesNotContain("adapter:", output!.Output, StringComparison.Ordinal);

        using var device = TexturingDevice.Open();

        // The whole line the harness writes, so this is a claim about the name, the kind and the
        // driver version rather than about the word "adapter" appearing.
        Assert.Contains("adapter:", output.Output, StringComparison.Ordinal);
        Assert.Contains(TexturingDevice.Adapter(device), output.Output, StringComparison.Ordinal);
    }

    /// <summary>
    ///     ⚠ Nothing here but the harness can get a device at all, so the eighth device file cannot
    ///     have an anonymous one.
    /// </summary>
    /// <remarks>
    ///     <b>The strong half, and the reason the five private openers were deleted rather than
    ///     merely tidied</b> (<a href="https://github.com/Rikarin/Vixen/issues/923">#923</a>). The
    ///     detector is the backend's own creating call, which no author of a test here can rename or
    ///     spell differently, so this is a claim about every file that could open a device rather
    ///     than about every file that named its opener <c>Open()</c>.
    /// </remarks>
    [Fact]
    public void Only_the_harness_here_creates_a_device() =>
        DeviceRollCall.Sole(
            DeviceRollCall.Read(Path.GetDirectoryName(Here())!, "TexturingAdapterRollCallTests.cs"),
            harness: "TexturingDevice.cs"
        );

    /// <summary>A file's text as the walk sees it, built from the detector rather than retyped.</summary>
    /// <param name="name">The file name.</param>
    /// <returns>A source that creates a device.</returns>
    /// <remarks>
    ///     ⚠ Composed from <see cref="DeviceRollCall.Creates" /> rather than written out, because a
    ///     fixture that spelled the call would make <em>this</em> file a match and turn the real roll
    ///     call above red — the same trap the detector's own two-piece declaration avoids.
    /// </remarks>
    static DeviceRollCall.Source Creating(string name) =>
        new(name, $"static VulkanDevice Mine() {{ {DeviceRollCall.Creates}new(), out var d, out var r); }}");

    /// <summary>
    ///     ⚠ And the check can be false: a second creator fails it, and so does a set with none.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Both halves, over synthetic sources, because a rule that has never produced an
    ///         answer is in the state <c>PluginReferenceRule</c> shipped in.</b> The passing case is
    ///         the roll call above; these are the two failing ones, and they are the two the walk is
    ///         written for — a file that went round the harness, and a walk that matched nothing at
    ///         all because it read no files or the backend renamed the call.
    ///     </para>
    ///     <para>
    ///         ⚠ The second case is the one that matters more: it is the only difference between this
    ///         and an instrument that reports success on the day it does not run.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_second_file_that_creates_a_device_fails_the_check_and_so_does_none() {
        DeviceRollCall.Source[] harnessOnly = [Creating("TexturingDevice.cs"), new("Other.cs", "TexturingDevice.Open();")];

        DeviceRollCall.Sole(harnessOnly, harness: "TexturingDevice.cs");

        var second = Assert.ThrowsAny<Exception>(
            () => DeviceRollCall.Sole(
                [Creating("TexturingDevice.cs"), Creating("Sneaky.cs")],
                harness: "TexturingDevice.cs"
            )
        );

        Assert.Contains("Sneaky.cs", second.Message, StringComparison.Ordinal);

        var none = Assert.ThrowsAny<Exception>(
            () => DeviceRollCall.Sole([new("TexturingDevice.cs", "nothing creates anything here")], harness: "TexturingDevice.cs")
        );

        Assert.Contains("no file at all", none.Message, StringComparison.Ordinal);
    }
}
