// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Graphics.Null;
using Vixen.Rendering;
using Vixen.Ui.Renderer;
using Vixen.Ui.Rendering;
using Vixen.Ui.Text.Rasterizing;
using Xunit;

namespace Vixen.Ui.Desktop.Tests;

/// <summary>A host that records a frame before it has ever uploaded one is told so, by name (#1377).</summary>
/// <remarks>
///     <para>
///         ⚠ <b>On the null backend because that is where the fault was invisible.</b> The vertex and
///         index rings are created by the first <c>Upload</c>, so before it both handles name nothing.
///         Vulkan resolves the vertex handle inside <c>BindVertexBuffer</c> and threw an
///         <see cref="ArgumentException" /> out of the middle of a render graph;
///         <c>NullCommandList.BindVertexBuffer</c> records the bind of nothing and carries on, so on
///         this device the frame "worked". A test here therefore fails against the old renderer by
///         <i>not throwing at all</i>, which is the half no device run could show.
///     </para>
///     <para>
///         ⚠ <b>Inside an open pass, deliberately.</b> The null list refuses a <c>DrawIndexed</c>
///         outside one with an <see cref="InvalidOperationException" /> of its own — the same type this
///         asserts — so a test that recorded outside a pass would pass against the old code for the
///         wrong reason. The recorder is read afterwards to show the refusal came before any command.
///     </para>
/// </remarks>
public class RecordBeforeUploadTests {
    static UiDocument Panel(bool grouped) {
        var document = new UiDocument(200f, 200f);

        document.Load(
            grouped
                ? """
                  root { width: 200px; height: 200px; }
                  div { width: 80px; height: 40px; background-color: #345; opacity: 0.5; }
                  span { width: 20px; height: 10px; background-color: #f00; }
                  """
                : """
                  root { width: 200px; height: 200px; }
                  div { width: 80px; height: 40px; background-color: #345; }
                  """
        );

        var panel = document.Root.Add("div");

        if (grouped) {
            // Two children, so the faded subtree is more than one command and stays a group —
            // `DrawListBuilder` takes the layer back from a subtree that comes to one.
            panel.Add("span");
            panel.Add("span");
        }

        return document;
    }

    static (NullDevice Device, UiRenderer Renderer, UiGeometry Frame, GlyphAtlas Atlas) Frame(bool grouped) {
        using var document = Panel(grouped);

        var device = new NullDevice(new() { Record = true });
        var renderer = new UiRenderer(device, UiShaderLibrary.Load(device), new RenderOutput([PixelFormat.Bgra8UNormSrgb]));
        var glyphs = new GlyphFieldCache(new GlyphAtlas(256, 256), resolution: 32);
        var frame = default(UiGeometry);

        document.Update();
        document.Draw();
        new UiGeometryBuilder().TryBuild(document.Drawing, glyphs, new(0f, 0f, 200f, 200f), ref frame);

        // The frame really has something to draw, so a refusal below is about the missing upload and
        // not the early return an empty frame takes.
        Assert.NotEmpty(frame.Indices);
        Assert.Equal(grouped, frame.Layers.Count == 1);

        return (device, renderer, frame, glyphs.Atlas);
    }

    /// <summary>Records <paramref name="phase" /> on a list of its own and returns what reached the device.</summary>
    static IReadOnlyList<RecordedCommand> Submitted(NullDevice device, bool inPass, Action<ICommandList> phase) {
        using var commands = device.BeginCommandList(QueueKind.Graphics, "ui");

        if (inPass) {
            var target = device.CreateTextureView(
                device.CreateTexture(
                    new() {
                        Width = 16,
                        Height = 16,
                        Depth = 1,
                        MipLevels = 1,
                        ArrayLayers = 1,
                        SampleCount = 1,
                        Format = PixelFormat.Bgra8UNormSrgb,
                        Usage = TextureUsage.ColourTarget
                    }
                )
            );

            commands.BeginRenderPass(new([new(target)], name: "ui"));
            phase(commands);
            commands.EndRenderPass();
        } else {
            phase(commands);
        }

        commands.Finish();
        device.Recorder!.Clear();
        device.GraphicsQueue.Submit([commands]);

        return [.. device.Recorder.Commands];
    }

    [Fact]
    public void Recording_before_any_upload_is_refused_by_name_before_a_command_is_recorded() {
        var (device, renderer, frame, _) = Frame(grouped: false);

        using (device)
        using (renderer) {
            InvalidOperationException? refused = null;

            var recorded = Submitted(
                device,
                inPass: true,
                list => refused = Assert.Throws<InvalidOperationException>(() => renderer.Record(list, frame, new Int2(200, 200)))
            );

            Assert.Contains("Upload", refused!.Message);
            Assert.DoesNotContain(recorded, command => command.Kind == RecordedCommandKind.BindVertexBuffer);
            Assert.DoesNotContain(recorded, command => command.Kind == RecordedCommandKind.DrawIndexed);
        }
    }

    /// <summary>
    ///     The pair that makes the refusal mean something: the same frame, uploaded first, records
    ///     the bind and the draw the refusal withheld — so it is the missing upload being refused, and
    ///     not every frame this document produces.
    /// </summary>
    [Fact]
    public void The_same_frame_uploaded_first_records_its_geometry() {
        var (device, renderer, frame, atlas) = Frame(grouped: false);

        using (device)
        using (renderer) {
            Submitted(device, inPass: false, list => renderer.Upload(list, frame, atlas));

            var recorded = Submitted(device, inPass: true, list => renderer.Record(list, frame, new Int2(200, 200)));

            Assert.Contains(recorded, command => command.Kind == RecordedCommandKind.BindVertexBuffer);
            Assert.Contains(recorded, command => command.Kind == RecordedCommandKind.DrawIndexed);
        }
    }

    /// <summary>
    ///     <c>Compose</c> draws a group's subtree into a surface of its own from the same rings, so it
    ///     is refused on the same terms — and outside a pass, where it is called.
    /// </summary>
    [Fact]
    public void Composing_before_any_upload_is_refused_by_name_before_a_command_is_recorded() {
        var (device, renderer, frame, atlas) = Frame(grouped: true);

        using (device)
        using (renderer) {
            InvalidOperationException? refused = null;

            var recorded = Submitted(
                device,
                inPass: false,
                list => refused = Assert.Throws<InvalidOperationException>(() => renderer.Compose(list, frame, new Int2(200, 200)))
            );

            // The submit itself is recorded; nothing the renderer would have issued is.
            Assert.Contains("Upload", refused!.Message);
            Assert.DoesNotContain(recorded, command => command.Kind == RecordedCommandKind.BeginRenderPass);
            Assert.DoesNotContain(recorded, command => command.Kind == RecordedCommandKind.BindVertexBuffer);

            // And the pair: uploaded first, the same frame composes its group.
            Submitted(device, inPass: false, list => renderer.Upload(list, frame, atlas));

            var composed = Submitted(device, inPass: false, list => renderer.Compose(list, frame, new Int2(200, 200)));

            Assert.Equal(1, renderer.Composited);
            Assert.Contains(composed, command => command.Kind == RecordedCommandKind.BindVertexBuffer);
        }
    }

    /// <summary>
    ///     A first frame with nothing in it is not a contract violation: there is nothing to draw and
    ///     nothing to have uploaded, and a window that opens blank must not be told off for it.
    /// </summary>
    [Fact]
    public void An_empty_frame_before_any_upload_records_nothing_and_is_not_refused() {
        var (device, renderer, _, _) = Frame(grouped: false);

        using (device)
        using (renderer) {
            var empty = new UiGeometry([], [], [], []);

            var recorded = Submitted(
                device,
                inPass: true,
                list => {
                    renderer.Record(list, empty, new Int2(200, 200));
                    renderer.Compose(list, empty, new Int2(200, 200));
                }
            );

            Assert.DoesNotContain(recorded, command => command.Kind == RecordedCommandKind.BindVertexBuffer);
        }
    }
}
