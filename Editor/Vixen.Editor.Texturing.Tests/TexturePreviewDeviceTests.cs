// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.NodeGraph;
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
    /// <summary>Opening a graph in a host with a device puts real texels in the pane.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The oracle is closed form and the kernel has no say in it.</b> A checker at eight
    ///         cells across a 1024-square image is exactly two values — 0 and 255 — in exactly equal
    ///         numbers, because sixty-four cells of 128 × 128 texels alternate. A black image, an
    ///         unwritten target, a flat fill and a half-written dispatch each fail one of the three
    ///         assertions below, which is what a mean or a checksum would not.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And the pane draws the number that was uploaded</b>, which is the claim the rest
    ///         of the path rests on: pixels that reached the host and a view still showing zero would
    ///         be an empty pane with a green test behind it.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Opening_a_graph_evaluates_it_and_the_pane_shows_the_result() {
        using var device = TexturingDevice.Open();
        using var fixture = new TexturingFixture(device);

        fixture.Host.Activate(TexturingModule.ModuleId, TexturingModule.ModuleName, new TexturingModule());

        fixture.Project.Selection.Set(fixture.AddGraph("Bricks"));

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
            $"the base layer on {TexturingDevice.Adapter(device)} holds {values.Count} distinct reds: "
            + string.Join(", ", values.Order())
        );

        Assert.Equal(picture.Width * picture.Height / 2, lit);
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
        using var device = TexturingDevice.Open();
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
        using var device = TexturingDevice.Open();
        using var fixture = new TexturingFixture(device);

        fixture.Host.Activate(TexturingModule.ModuleId, TexturingModule.ModuleName, new TexturingModule());

        fixture.Project.Selection.Set(fixture.AddGraph("Bricks"));

        Assert.True(fixture.Shell.Commands.Execute(TexturingModule.OpenCommand));
        Assert.NotNull(fixture.Graphics);
        Assert.NotEmpty(fixture.Graphics.Uploads);

        Assert.True(fixture.Host.Unload(TexturingModule.ModuleId));
        Assert.Equal(fixture.Graphics.Uploads.Count, fixture.Graphics.Released);
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
