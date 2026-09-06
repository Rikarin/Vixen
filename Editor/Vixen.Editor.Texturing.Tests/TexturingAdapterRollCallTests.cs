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
///         ⚠ <b>The harness guarantee reaches two of the seven files here, and that is a finding
///         rather than the state the issue described.</b>
///         <see cref="TexturingDevice.Open" /> now writes the adapter into the running test's output
///         itself, so a device opened <em>through it</em> cannot be anonymous — but five files
///         (<c>LayerCoverageDeviceTests</c>, <c>LayerStackBakeDeviceTests</c>,
///         <c>LayerStackPanelDeviceTests</c>, <c>PaintPreviewDeviceTests</c>,
///         <c>TexturePreviewDeviceTests</c>) each carry a private <c>static VulkanDevice Open()</c>
///         calling <c>VulkanDevice.TryCreate</c> directly and never reach it. All five name their
///         adapter today; nothing but this walk requires them to. Consolidating them onto the harness
///         is <a href="https://github.com/Rikarin/Vixen/issues/923">#923</a>, and until it lands the
///         walk is the load-bearing half here rather than the belt to the harness's braces.
///     </para>
/// </remarks>
public class TexturingAdapterRollCallTests {
    /// <summary>Where this file was compiled from, which is the directory the roll call reads.</summary>
    static string Here([CallerFilePath] string path = "") => path;

    /// <summary>How a file in this project opens a device.</summary>
    /// <remarks>
    ///     ⚠ Matches the harness's declaration, the five private copies' declarations, and every call
    ///     site of either — which is what makes the set the device files rather than a subset chosen
    ///     by which helper a file happened to use. <see cref="DeviceRollCall.Take" /> requires the
    ///     harness to be among the matches, so a rename empties the set loudly instead of quietly.
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
    ///     There were eight matches when this was written — the harness, the five private copies and
    ///     the two files that call the harness — so the floor is five: below that the detector has
    ///     drifted and the walk is reading an almost empty set, which is a pass that means nothing.
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
}
