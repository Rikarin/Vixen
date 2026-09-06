// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.NodeGraph;
using Vixen.Graphics.Vulkan;
using Vixen.Ui;
using Vixen.Ui.Controls.Advanced;
using Xunit;

namespace Vixen.Editor.Texturing.Tests;

/// <summary>The plugin draws: a kernel dispatched on the editor's device, in the editor's pane.</summary>
/// <remarks>
///     <para>
///         <b>What proves <c>IEditorGraphics</c> is sufficient rather than merely published.</b> Doc
///         36 § F2's whole complaint is an extension surface its own authors never had to use; a
///         device published through a contract nobody drew through would be the same claim with a
///         type attached. This suite runs the plugin's own path — activate, open, evaluate, upload,
///         show — and looks at the texels.
///     </para>
///     <para>
///         ⚠ <b>A real adapter or a loud skip.</b> Without one a headless run falls back to the Null
///         device on every platform and exits 0, so a green run here would be the claim that a black
///         image equals a black image. The adapter is named in every message and
///         <c>VIXEN_REQUIRE_VULKAN=1</c> turns the skip into a failure.
///     </para>
/// </remarks>
public class TexturePreviewDeviceTests {
    /// <summary>A device, or a loud skip — or, when one was required, a failure.</summary>
    /// <returns>The device.</returns>
    static VulkanDevice Open() {
        if (VulkanDevice.TryCreate(new(), out var device, out var reason)) {
            return device!;
        }

        if (Environment.GetEnvironmentVariable("VIXEN_REQUIRE_VULKAN") is "1" or "true" or "TRUE") {
            Assert.Fail($"VIXEN_REQUIRE_VULKAN is set and no device could be opened: {reason}");
        }

        Assert.Skip(reason ?? "no Vulkan device, so nothing here can be proved");

        throw new InvalidOperationException("unreachable");
    }

    static string Adapter(VulkanDevice device) =>
        $"{device.Adapter.Name} ({device.Adapter.Kind}, {device.Adapter.DriverVersion})";

    /// <summary>Opening a graph in a host with a device puts the <i>wired graph's</i> texels in the pane.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The oracle is closed form and the kernel has no say in it.</b> A checker at eight
    ///         cells across a 1024-square image is exactly two values — 0 and 255 — in exactly equal
    ///         numbers, because sixty-four cells of 128 × 128 texels alternate. A black image, an
    ///         unwritten target, a flat fill and a half-written dispatch each fail one of the three
    ///         assertions below, which is what a mean or a checksum would not.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The checker is now a node the test wired, and it used to be the pane's own fixed
    ///         plan</b> — <a href="https://github.com/Rikarin/Vixen/issues/792">#792</a>. That is the
    ///         whole difference: the same oracle over the same picture proved the device path when
    ///         <c>TextureGraphPreview</c> hard-coded a checkerboard, and proves the <em>compiler</em>
    ///         path now that it compiles the document. The instrument is the starter graph it
    ///         replaces: a new <c>.vxtexgraph</c> is a white <c>Source/Uniform</c> into an
    ///         <c>Output</c>, so a preview that ignored the wire would be a flat 255 — one distinct
    ///         value, which is the first assertion below.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And the pane draws the number that was uploaded</b>, which is the claim the rest
    ///         of the path rests on: pixels that reached the host and a view still showing zero would
    ///         be an empty pane with a green test behind it.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Opening_a_graph_evaluates_it_and_the_pane_shows_the_result() {
        using var device = Open();
        using var fixture = new TexturingFixture(device);

        fixture.Host.Activate(TexturingModule.ModuleId, TexturingModule.ModuleName, new TexturingModule());

        fixture.Project.Selection.Set(fixture.AddGraph("Bricks"));

        Assert.True(fixture.Shell.Commands.Execute(TexturingModule.OpenCommand));

        var document = Assert.Single(fixture.Project.Documents.OfType<TextureGraphDocument>());

        Checker(document);

        // The second run is what redraws with the wire above in place.
        Assert.True(fixture.Shell.Commands.Execute(TexturingModule.OpenCommand));

        var panel = fixture.Shell.Workspace.Open(TexturingModule.GraphPanel);
        var view = Find<ImageView>(panel!);

        Assert.NotNull(view);
        Assert.NotNull(fixture.Graphics);
        Assert.NotEmpty(fixture.Graphics.Uploads);

        var picture = fixture.Graphics.Uploads[^1];

        Assert.Equal(1024, picture.Width);
        Assert.Equal(1024, picture.Height);
        Assert.Equal(picture.Image, view.Image);
        Assert.Equal(1024, view.ImageWidth);

        HashSet<byte> values = [];
        var lit = 0;

        for (var index = 0; index < picture.Pixels.Length; index += 4) {
            values.Add(picture.Pixels[index]);

            if (picture.Pixels[index] > 127) {
                lit++;
            }
        }

        Assert.True(
            values.SetEquals([(byte)0, (byte)255]),
            $"the compiled graph on {Adapter(device)} holds {values.Count} distinct reds: "
            + string.Join(", ", values.Order())
            + ". A flat 255 is the starter graph's white Uniform, which is what a pane that did not "
            + "compile the wire would show."
        );

        Assert.Equal(picture.Width * picture.Height / 2, lit);

        // ⚠ And it is *this* checker rather than the one the pane used to hard-code. Without this
        // line the sabotage that restores `Base(width, height)` — a checker at eight cells — leaves
        // every assertion above green, because a checker is a checker whatever drew it.
        Assert.True(
            Transitions(picture.Pixels, picture.Width) == (int)WiredCells - 1,
            $"the top row on {Adapter(device)} changes {Transitions(picture.Pixels, picture.Width)} times. "
            + $"The wired graph is a {WiredCells}-cell checker, so it should change {(int)WiredCells - 1} "
            + $"times; {(int)Cells - 1} is the fixed base-layer plan this pane used to show instead."
        );
    }

    /// <summary>How many cells across the wired checker is — deliberately not <see cref="Cells" />.</summary>
    /// <remarks>
    ///     ⚠ <b>The whole instrument, and the first draft of this test did not have it.</b>
    ///     <c>TextureGraphPreview.Base</c> is a checker at <see cref="Cells" /> across, so a wired
    ///     graph asking for the same picture is one this test cannot tell from the fixed plan it
    ///     replaced — the sabotage that restores the old behaviour leaves it green, which means the
    ///     test is the defect. A different cell count is what makes the two distinguishable.
    /// </remarks>
    const float WiredCells = 4f;

    /// <summary>The checker the pane's own fixed plan drew, before it compiled the document.</summary>
    const float Cells = TextureGraphPreview.Cells;

    /// <summary>Rewires the starter graph to a <c>Source/Checker</c>, keeping its Output node.</summary>
    /// <remarks>
    ///     The uniform is removed rather than disconnected, so the graph has exactly one source and
    ///     an <c>Output</c> left unfed by nothing.
    /// </remarks>
    static void Checker(TextureGraphDocument document) {
        var output = document.Graph.Nodes.Single(node => node.Type == "Output/Output");

        foreach (var node in document.Graph.Nodes.Where(node => node.Type == "Source/Uniform").ToArray()) {
            document.Graph.Remove(node.Id, out _);
        }

        var checker = document.Graph.Add("Source/Checker");

        checker.SetValue("Scale X", WiredCells);
        checker.SetValue("Scale Y", WiredCells);

        document.Graph.Connect(new(checker.Id, "Out"), new(output.Id, "Input"));
    }

    /// <summary>How many times the top row of an image changes value.</summary>
    /// <remarks>
    ///     An N-cell checker across the width alternates N times and so changes N − 1 times along one
    ///     row — a closed form that separates the wired graph's four from the fixed plan's eight, and
    ///     which a flat picture answers with zero.
    /// </remarks>
    static int Transitions(byte[] pixels, int width) {
        var changes = 0;

        for (var x = 1; x < width; x++) {
            if (pixels[x * 4] != pixels[(x - 1) * 4]) {
                changes++;
            }
        }

        return changes;
    }

    /// <summary>⚠ A bitmap naming a picture this project has not got is a sentence, not a crash.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The external half of <a href="https://github.com/Rikarin/Vixen/issues/792">#792</a>,
    ///         and it is the reason the wire needed more than one line.</b> A compiled graph can ask
    ///         for images the compiler did not carry — a <c>Source/Bitmap</c> names a project asset,
    ///         because a compilation that runs on every edit must not touch an
    ///         <c>AssetDatabase</c> — and <c>TexturePlanEvaluator.Evaluate</c> refuses a plan with an
    ///         external nothing supplied by <em>throwing</em>, out of a panel build, taking the
    ///         editor's frame with it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>So the assertion is that the pane came back at all</b>, and that what it says
    ///         names the file rather than the device. A device is opened for exactly this reason:
    ///         the resolve loop runs after the device check, so a device-free run would take the
    ///         other branch and prove nothing about it.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_bitmap_naming_a_missing_asset_is_said_rather_than_thrown() {
        using var device = Open();
        using var fixture = new TexturingFixture(device);

        fixture.Host.Activate(TexturingModule.ModuleId, TexturingModule.ModuleName, new TexturingModule());
        fixture.Project.Selection.Set(fixture.AddGraph("Bricks"));

        Assert.True(fixture.Shell.Commands.Execute(TexturingModule.OpenCommand));

        var document = Assert.Single(fixture.Project.Documents.OfType<TextureGraphDocument>());
        var output = document.Graph.Nodes.Single(node => node.Type == "Output/Output");

        foreach (var node in document.Graph.Nodes.Where(node => node.Type == "Source/Uniform").ToArray()) {
            document.Graph.Remove(node.Id, out _);
        }

        var bitmap = document.Graph.Add("Source/Bitmap");

        bitmap.SetText("Source", "Assets/NoSuchPicture.png");
        document.Graph.Connect(new(bitmap.Id, "Out"), new(output.Id, "Input"));

        Assert.True(fixture.Shell.Commands.Execute(TexturingModule.OpenCommand));

        var status = Status(fixture.Shell.Workspace.Open(TexturingModule.GraphPanel)!);

        Assert.Contains("NoSuchPicture.png", status, StringComparison.Ordinal);

        Assert.False(
            status.Contains("no graphics device", StringComparison.Ordinal),
            $"on {Adapter(device)} the pane blamed the device for a missing file: {status}"
        );
    }

    /// <summary>⚠ Re-evaluating releases the picture it replaces: one live upload, however many runs.</summary>
    /// <remarks>
    ///     The leak with no symptom. A pane re-evaluated on every edit holds a texture and a
    ///     descriptor set per keystroke, and nothing in the editor reports it — a plugin's registered
    ///     things are undone by the scope, and a plugin's <em>textures</em> are not registered with
    ///     anything.
    /// </remarks>
    [Fact]
    public void Re_evaluating_gives_the_previous_picture_back() {
        using var device = Open();
        using var fixture = new TexturingFixture(device);

        fixture.Host.Activate(TexturingModule.ModuleId, TexturingModule.ModuleName, new TexturingModule());

        fixture.Project.Selection.Set(fixture.AddGraph("Bricks"));

        Assert.True(fixture.Shell.Commands.Execute(TexturingModule.OpenCommand));
        Assert.True(fixture.Shell.Commands.Execute(TexturingModule.OpenCommand));

        Assert.NotNull(fixture.Graphics);
        Assert.True(fixture.Graphics.Uploads.Count > 1, "the second open did not re-evaluate");
        Assert.Equal(fixture.Graphics.Uploads.Count - 1, fixture.Graphics.Released);
    }

    /// <summary>And unloading the module gives the last one back too.</summary>
    [Fact]
    public void Unloading_gives_the_last_picture_back() {
        using var device = Open();
        using var fixture = new TexturingFixture(device);

        fixture.Host.Activate(TexturingModule.ModuleId, TexturingModule.ModuleName, new TexturingModule());

        fixture.Project.Selection.Set(fixture.AddGraph("Bricks"));

        Assert.True(fixture.Shell.Commands.Execute(TexturingModule.OpenCommand));
        Assert.NotNull(fixture.Graphics);
        Assert.NotEmpty(fixture.Graphics.Uploads);

        Assert.True(fixture.Host.Unload(TexturingModule.ModuleId));
        Assert.Equal(fixture.Graphics.Uploads.Count, fixture.Graphics.Released);
    }

    /// <summary>The sentence under the preview pane.</summary>
    static string Status(UiElement panel) => Find(panel, "texture-graph-status")?.Text ?? "";

    /// <summary>The first element in the tree with that tag.</summary>
    static UiElement? Find(UiElement element, string tag) {
        if (string.Equals(element.Tag, tag, StringComparison.Ordinal)) {
            return element;
        }

        foreach (var child in element.Children) {
            if (Find(child, tag) is { } found) {
                return found;
            }
        }

        return null;
    }

    static T? Find<T>(UiElement element) where T : UiElement {
        if (element is T found) {
            return found;
        }

        foreach (var child in element.Children) {
            if (Find<T>(child) is { } inside) {
                return inside;
            }
        }

        return null;
    }
}
