// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Editor.Texturing.Layers;
using Vixen.Graphics.Vulkan;
using Vixen.TextureGraph;
using Vixen.Texturing.Layers;
using Vixen.Ui;
using Vixen.Ui.Controls.Advanced;
using Xunit;

namespace Vixen.Editor.Texturing.Tests;

/// <summary>What the paint pane holds under a drag is what a clean evaluation of the plan produces.</summary>
/// <remarks>
///     <para>
///         <b>The promise doc 48 § D13 made and the panes broke for fifteen of sixteen operators.</b>
///         The pane used to show a source-over composite in C#, which could only be compared with the
///         bake by eye. Here a real pointer goes through the real module and the bytes the pane
///         was handed, an upload and its patches replayed in order, are compared with a plan that an
///         evaluator sharing nothing with the module ran from nothing.
///     </para>
///     <para>
///         ⚠ <b>The picture is 300 square because the sizes are the trap.</b> It is more than a tile,
///         so a stroke is not the whole image, and it is not a whole number of workgroups, so a
///         repair's rectangle runs off the edge of it.
///     </para>
/// </remarks>
public class PaintPaneCompositeDeviceTests {
    const int Side = 300;
    const uint Brush = 0xFF2060E0u;

    /// <summary>Under the drag, on an operator that is not source-over and under a layer that is not either.</summary>
    [Theory]
    [InlineData(LayerBlendMode.Multiply)]
    [InlineData(LayerBlendMode.Overlay)]
    public void Under_the_brush_the_pane_shows_the_plan(LayerBlendMode mode) {
        using var pane = Pane.Open(mode, over: false);

        // The first stroke of a layer that names no canvas, which the document does not know about.
        pane.Stroke(new(40f, 40f), new(90f, 60f), release: false);
        pane.SamePicture("under the first stroke, before anything named its canvas");

        pane.Stroke(new(90f, 60f), new(90f, 60f), release: true, press: false);
        pane.SamePicture("after the first stroke let go");

        var plain = pane.Bytes();

        // The second, mid-air, far enough from the first that a union would be most of the image.
        pane.Stroke(new(270f, 270f), new(290f, 295f), release: false);
        pane.SamePicture("under a second stroke, reaching the far corner of a picture no workgroup fits");

        Assert.False(plain.AsSpan().SequenceEqual(pane.Bytes()), "the second stroke changed nothing the pane holds");

        pane.Stroke(new(290f, 295f), new(290f, 295f), release: true, press: false);
    }

    /// <summary>A drag costs its tiles: nothing compiles, and the pane is sent a part of the picture.</summary>
    [Fact]
    public void A_drag_costs_tiles_and_compiles_nothing() {
        // A tile is 256 texels, so a smaller picture is one tile and has nothing to show here.
        const int Large = 1024;

        using var pane = Pane.Open(LayerBlendMode.Multiply, over: false, Large);

        pane.Stroke(new(40f, 40f), new(40f, 40f), release: true);

        var compiled = pane.Module.Preview!.Compilations;
        var patched = pane.Fixture.Graphics!.Updates.Count;
        var uploaded = pane.Fixture.Graphics.Uploads.Count;

        pane.Stroke(new(20f, 20f), new(50f, 30f), release: false);

        Assert.Equal(compiled, pane.Module.Preview.Compilations);
        Assert.Equal(uploaded, pane.Fixture.Graphics.Uploads.Count);

        var written = pane.Fixture.Graphics.Updates.Skip(patched).Sum(static update => (long)update.Width * update.Height);

        Assert.True(written > 0, "the pane was sent nothing, so the bound below is empty");
        Assert.True(
            written < (long)Large * Large / 4,
            $"{written} texels sent for a stroke in one corner of a {Large}² picture."
        );

        pane.SamePicture("after a drag measured in tiles");
    }

    /// <summary>A filter over the layer spreads a stroke past its own rectangle, and the pane has to follow.</summary>
    [Fact]
    public void A_filter_over_the_layer_shows_what_it_spread() {
        using var pane = Pane.Open(LayerBlendMode.Multiply, over: true);

        pane.Stroke(new(100f, 100f), new(130f, 100f), release: false);
        pane.SamePicture("under a stroke with a blur over it");
    }

    /// <summary>Undo brings back a picture the cache may still hold, and that rewrites no rectangle.</summary>
    [Fact]
    public void An_undo_puts_the_plans_picture_back() {
        using var pane = Pane.Open(LayerBlendMode.Overlay, over: false);

        pane.Stroke(new(40f, 40f), new(40f, 40f), release: true);

        var before = pane.Bytes();

        pane.Stroke(new(120f, 120f), new(160f, 150f), release: true);

        Assert.False(before.AsSpan().SequenceEqual(pane.Bytes()), "the stroke changed nothing the pane holds");
        Assert.True(pane.Document.Stack.Undo());

        pane.Fixture.Host.Update(TimeSpan.Zero);
        pane.SamePicture("after an undo");

        Assert.Equal(before, pane.Bytes());

        Assert.True(pane.Document.Stack.Redo());

        pane.Fixture.Host.Update(TimeSpan.Zero);
        pane.SamePicture("after a redo");
    }

    /// <summary>⚠ A value moves numbers: the plan is not compiled again and the pane follows it.</summary>
    [Fact]
    public void A_value_edit_compiles_nothing_and_reaches_the_pane() {
        using var pane = Pane.Open(LayerBlendMode.Multiply, over: false);

        pane.Stroke(new(40f, 40f), new(60f, 60f), release: true);

        var compiled = pane.Module.Preview!.Compilations;
        var before = pane.Bytes();
        var layers = pane.Document.Document.Sets[0].Layers;
        var index = layers.FindIndex(static layer => layer.Id == "rust");

        // In the document and through the pane's own route, which is what an opacity slider does.
        layers[index] = layers[index] with { Opacity = 0.3f };

        Assert.True(pane.Fixture.Shell.Commands.Execute(TexturingModule.OpenStackCommand));

        Assert.Equal(compiled, pane.Module.Preview.Compilations);
        Assert.False(before.AsSpan().SequenceEqual(pane.Bytes()), "the opacity moved and the pane did not");
        pane.SamePicture("after a value edit");
    }

    /// <summary>The module, a stack and the pane, with the picture the pane was sent kept in step.</summary>
    sealed class Pane : IDisposable {
        readonly VulkanDevice device;
        readonly ImageView image;

        Pane(VulkanDevice device, TexturingFixture fixture, TexturingModule module, LayerStackDocument document, ImageView image) {
            this.device = device;
            this.image = image;

            Fixture = fixture;
            Module = module;
            Document = document;
        }

        public TexturingFixture Fixture { get; }

        public TexturingModule Module { get; }

        public LayerStackDocument Document { get; }

        public static Pane Open(LayerBlendMode mode, bool over, int side = Side) {
            var device = TexturingDevice.Open();
            var fixture = new TexturingFixture(device);

            TexturingModule module = new();

            fixture.Host.Activate(TexturingModule.ModuleId, TexturingModule.ModuleName, module);
            fixture.Project.Selection.Set(LayerStackPanelTests.AddStack(fixture, "Hull"));

            Assert.True(fixture.Shell.Commands.Execute(TexturingModule.OpenStackCommand));

            var document = Assert.IsType<LayerStackDocument>(fixture.Project.Documents.Single());
            var stack = LayerStackDocument.Starter("Hull") with { BaseWidth = side, BaseHeight = side };

            stack.Sets[0].Layers[0].Values["baseColor"] = [0.8f, 0.6f, 0.3f, 1f];
            stack.Sets[0].Layers.Add(new() { Id = "rust", Name = "Rust", Kind = LayerKind.Paint, Blend = mode, Opacity = 0.8f });
            stack.Sets[0].Layers.Add(
                new() {
                    Id = "over",
                    Name = "Over",
                    Kind = LayerKind.Fill,
                    Fill = LayerFillSource.Constant,
                    Blend = LayerBlendMode.Overlay,
                    Opacity = 0.5f,
                    Values = { ["baseColor"] = [0.3f, 0.7f, 0.5f, 1f] }
                }
            );

            if (over) {
                stack.Sets[0].Layers.Add(
                    new() {
                        Id = "blur",
                        Name = "Blur",
                        Kind = LayerKind.Filter,
                        Filter = LayerFilterKind.Blur,
                        Settings = { ["Radius"] = [3f] }
                    }
                );
            }

            document.Document = stack;
            module.Tool.Colour = Brush;

            Assert.True(fixture.Shell.Commands.Execute(TexturingModule.PaintCommand));

            var panel = fixture.Shell.Workspace.Open(TexturingModule.PaintPanel);

            Assert.NotNull(panel);

            fixture.Shell.Document.Update();

            return new(device, fixture, module, document, Find(panel)!);
        }

        /// <summary>A press, a move or two and maybe a release, then the frame that repairs the plan.</summary>
        public void Stroke(Vector2 from, Vector2 to, bool release, bool press = true) {
            var start = image.ToScreen(from);
            var end = image.ToScreen(to);

            if (press) {
                Fixture.Shell.Document.Dispatch(
                    new PointerEvent { X = start.X, Y = start.Y, Action = PointerAction.Pressed, Button = PointerButton.Primary }
                );

                for (var step = 1; step <= 3; step++) {
                    var at = Vector2.Lerp(start, end, step / 3f);

                    Fixture.Shell.Document.Dispatch(new PointerEvent { X = at.X, Y = at.Y, Action = PointerAction.Moved });
                }
            }

            if (release) {
                Fixture.Shell.Document.Dispatch(
                    new PointerEvent { X = end.X, Y = end.Y, Action = PointerAction.Released, Button = PointerButton.Primary }
                );
            }

            // The update a frame runs, which is where a drag's owed tiles are repaired.
            Fixture.Host.Update(TimeSpan.Zero);
        }

        /// <summary>What the pane's texture holds: the last whole upload, then its patches in order.</summary>
        public byte[] Bytes() {
            var graphics = Fixture.Graphics!;
            var shown = graphics.Uploads.Last(upload => upload.Image == image.Image);
            var pixels = (byte[])shown.Pixels.Clone();

            foreach (var patch in graphics.Updates.Where(update => update.Image == shown.Image)) {
                for (var row = 0; row < patch.Height; row++) {
                    patch.Pixels.AsSpan(row * patch.Width * 4, patch.Width * 4)
                        .CopyTo(pixels.AsSpan((((patch.Y + row) * shown.Width) + patch.X) * 4));
                }
            }

            return pixels;
        }

        /// <summary>Compares the pane with the plan, run from nothing by an evaluator sharing nothing.</summary>
        public void SamePicture(string when) {
            var stack = Document.Document;
            var set = stack.Sets[0];
            var named = set with {
                Layers = [
                    .. set.Layers.Select(layer =>
                        layer.Kind == LayerKind.Paint && layer.Paint.Length == 0
                            ? layer with { Paint = LayerPaint.NameFor("Hull", set.Name, layer.Id) }
                            : layer
                    )
                ]
            };

            using TexturePlanEvaluator evaluator = new(device);
            using LayerStackEvaluator run = new(evaluator, Module.Canvases, Document.AssetPath);

            Assert.Empty(run.Prepare(stack, named, Document.Library.Registry, Document.Library.SubGraphs));

            using var bake = run.EvaluateClean();

            var output = run.Outputs.First(static one => one.Usage == "baseColor").Image;
            var clean = bake.Read(output).Pixels;
            var shown = Bytes();

            Assert.Equal(clean.Length, shown.Length);

            var differing = 0;
            var first = -1;

            for (var index = 0; index < clean.Length; index += 4) {
                if (!clean.AsSpan(index, 4).SequenceEqual(shown.AsSpan(index, 4))) {
                    first = first < 0 ? index / 4 : first;
                    differing++;
                }
            }

            Assert.True(
                differing == 0,
                $"{TexturingDevice.Adapter(device)}: {when}, {differing} texels of the pane differ from a clean "
                + $"evaluation of the plan, the texel {first} of {clean.Length / 4}."
            );
        }

        public void Dispose() {
            Fixture.Dispose();
            device.Dispose();
        }

        static ImageView? Find(UiElement element) {
            if (element is ImageView view) {
                return view;
            }

            foreach (var child in element.Children) {
                if (Find(child) is { } found) {
                    return found;
                }
            }

            return null;
        }
    }
}
