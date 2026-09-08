// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Editor.Texturing.Layers;
using Vixen.Editor.Texturing.Painting;
using Vixen.Input;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Vixen.Ui.Controls.Advanced;
using Xunit;

namespace Vixen.Editor.Texturing.Tests;

/// <summary>
///     A drag on the model paints the atlas:
///     <a href="https://github.com/Rikarin/Vixen/issues/1063">#1063</a>'s caller chain, driven the
///     way a person drives it.
/// </summary>
/// <remarks>
///     <para>
///         <b>Everything under this pane existed and nothing called it.</b> <c>PaintProjection</c>,
///         <c>PaintFootprint</c>, <c>PaintSymmetry</c>, <c>PaintProjector</c> and
///         <c>LayerStackMesh.Projection</c> landed across batches 18 and 19 with thirty-five tests
///         behind them and no production caller at all — so the suite that matters is this one: a
///         pointer event dispatched into a real document, ending at a texel of a <c>.vxpaint</c>.
///     </para>
///     <para>
///         ⚠ <b>Every event goes through <c>UiDocument.Dispatch</c> and never onto the view.</b>
///         <c>ImageView</c> marks its own pointer events handled, so a handler registered the
///         ordinary way is registered, looks right and never runs —
///         <c>PaintUvViewTests</c> states the same rule and it is what makes both suites more than
///         a call to a method.
///     </para>
///     <para>
///         ⚠ <b>The model is a quad in the <em>z = 0</em> plane whose layout is its own coordinates,
///         and the camera framed on it looks straight down <c>-z</c>.</b> That makes the middle of
///         the pane the middle of the atlas, which is what lets an assertion name a texel — and it
///         is also why the symmetry case below uses a quad that straddles <c>x = 0</c>: a mirror
///         through a plane the model does not straddle hits nothing, which is a correct answer and
///         proves nothing.
///     </para>
/// </remarks>
public class PaintMeshViewTests {
    /// <summary>A drag in the 3D pane puts texels in the layer's canvas and on both panes.</summary>
    /// <remarks>
    ///     ⚠ <b>Four places, and the last two are the ones this issue is about.</b> That a projected
    ///     stroke reaches a canvas is <c>PaintProjectorTests</c>' subject; what could not be asserted
    ///     until there was a pane is that a <em>pointer</em> reaches it, that the model on screen
    ///     shows the stroke without a bake, and that the 2D pane — a different picture of the same
    ///     layer — shows it too.
    /// </remarks>
    [Fact]
    public void A_drag_on_the_model_paints_the_layer_and_shows_on_both_panes() {
        using var fixture = new TexturingFixture(graphics: true);
        var document = Open(fixture, Quad(0f, 1f));
        var flat = Viewer(OpenPaint(fixture)!);
        var pane = OpenMesh(fixture);
        var image = Viewer(pane);

        // The two pictures on screen before the drag, by the number the interface draws them with.
        var model = image.Image;
        var atlas = flat.Image;

        Assert.NotEqual(0ul, model);
        Assert.NotEqual(model, atlas);

        var renders = fixture.Graphics!.Uploads.Count(one => one.Width == image.ImageWidth);

        Drag(fixture, image, new Vector2(-6f, -6f), new Vector2(6f, 6f));

        // The canvas beside the stack, named by the edit the first stroke made.
        var layer = Assert.Single(document.Document.Sets[0].Layers, one => one.Kind == LayerKind.Paint);

        Assert.NotEmpty(layer.Paint);

        var file = Path.Combine(Path.GetDirectoryName(document.AssetPath)!, layer.Paint);

        Assert.True(File.Exists(file), "the projected stroke did not reach a .vxpaint");

        using var stream = File.OpenRead(file);
        var canvas = PaintCanvas.Read(stream);
        var painted = canvas.Channel("baseColor");
        var opaque = 0;

        for (var index = 0; index < painted.Width * painted.Height; index++) {
            if (painted[index] >> 24 != 0u) {
                opaque++;
            }
        }

        Assert.True(opaque > 0, "the drag reached the canvas and painted no texel of it.");

        // ⚠ The middle of the atlas and not "somewhere", because the camera looks straight down the
        // quad's own normal and the quad's layout is its own coordinates — so the middle of the pane
        // *is* the middle of the atlas. An assertion that only counted texels would be green for a
        // projection that painted the wrong corner.
        Assert.NotEqual(0u, painted.At(painted.Width / 2, painted.Height / 2) >> 24);

        // ⚠ The model on screen was rewritten *in place* — the patch names the picture the pane was
        // already drawing. A whole re-upload would also make the stroke appear and would be #912
        // reintroduced one pane across, so the assertion is deliberately about which call was made.
        Assert.Contains(fixture.Graphics.Updates, patch => patch.Image == model);

        // And it is a render rather than the atlas: its extent is the pane's.
        Assert.Equal(image.ImageWidth, Assert.Single(fixture.Graphics.Uploads, one => one.Image == model).Width);
        Assert.NotEqual(painted.Width, image.ImageWidth);

        // ⚠ And the whole drag added no second render, which is doc 48's exit criterion 8 asserted
        // where it can fail. `PaintMeshCostTests` bounds what a stamp shades, but its loop calls
        // the rasteriser itself — so a pane that redrew the model on every pointer move would leave
        // that suite green and would put the triangle count straight back into the per-stamp path.
        // A redraw is a whole-picture upload by construction, so counting them here is the assertion.
        Assert.Equal(renders, fixture.Graphics.Uploads.Count(one => one.Width == image.ImageWidth));

        // And the 2D pane, which nobody dragged in: `TexturingModule.Redraw` serves both from one
        // composite, so a stroke aimed at the model shows up in the atlas view as well.
        Assert.Contains(fixture.Graphics.Updates, patch => patch.Image == atlas);
    }

    /// <summary>The drag is exactly one undo entry, and undoing it takes the paint off.</summary>
    /// <remarks>
    ///     Two entries and not one, for the reason <c>PaintUvViewTests</c> gives: naming the layer's
    ///     canvas is a change to the <c>.vxlayers</c> and is its own entry, pushed first so that the
    ///     artist's undo takes the stroke off rather than the name.
    /// </remarks>
    [Fact]
    public void The_projected_drag_is_one_undo_entry() {
        using var fixture = new TexturingFixture(graphics: true);
        var document = Open(fixture, Quad(0f, 1f));
        var pane = OpenMesh(fixture);
        var image = Viewer(pane);

        Drag(fixture, image, new Vector2(-6f, -6f), new Vector2(6f, 6f));

        Assert.Equal("Paint stroke", document.Stack.UndoName.Value);

        var layer = Assert.Single(document.Document.Sets[0].Layers, one => one.Kind == LayerKind.Paint);
        var file = Path.Combine(Path.GetDirectoryName(document.AssetPath)!, layer.Paint);
        var depth = document.Stack.Depth.Value;

        Assert.True(document.Stack.Undo());
        Assert.Equal(depth - 1, document.Stack.Depth.Value);

        using (var undone = File.OpenRead(file)) {
            var canvas = PaintCanvas.Read(undone).Channel("baseColor");

            for (var index = 0; index < canvas.Width * canvas.Height; index++) {
                Assert.Equal(0u, canvas[index]);
            }
        }

        Assert.True(document.Stack.Redo());

        using (var redone = File.OpenRead(file)) {
            var canvas = PaintCanvas.Read(redone).Channel("baseColor");

            Assert.NotEqual(0u, canvas.At(canvas.Width / 2, canvas.Height / 2) >> 24);
        }
    }

    /// <summary>Symmetry paints the mirror of the stroke as well as the stroke.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The one stroke-level effect only geometry can supply</b> — <c>PaintSymmetry</c>'s
    ///         own remarks — and until this pane existed nothing set it. A plane mirrors a point in
    ///         the model's space and the mirrored point lands in an unrelated part of the atlas, so
    ///         there is no transform of an atlas that performs it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The instrument is the same drag with symmetry off.</b> A quad straddling
    ///         <c>x = 0</c> whose layout straddles <c>u = ½</c> means the mirror of a stroke on the
    ///         left is a stroke on the right — so the assertion is that the right half is clean
    ///         without the setting and painted with it, which a brush merely wide enough to reach
    ///         both would fail.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Symmetry_paints_the_mirror_of_the_stroke() {
        var alone = Halves(mirrored: false);
        var mirrored = Halves(mirrored: true);

        Assert.True(alone.Left > 0, "the stroke did not paint its own side.");
        Assert.Equal(0, alone.Right);

        Assert.True(mirrored.Left > 0, "the mirrored stroke lost its own side.");
        Assert.True(
            mirrored.Right > 0,
            "symmetry was on and nothing was painted on the other side of the plane."
        );
    }

    /// <summary>A right-drag turns the camera and paints nothing.</summary>
    /// <remarks>
    ///     ⚠ <b>Both halves, and the second is the one that fails against the obvious mistake.</b> A
    ///     pane that treated every button as a stroke would turn the camera correctly and also paint
    ///     — and the picture would look right, because the orbit is what the artist is watching.
    /// </remarks>
    [Fact]
    public void A_right_drag_turns_the_camera_and_paints_nothing() {
        using var fixture = new TexturingFixture(graphics: true);
        var document = Open(fixture, Quad(0f, 1f));
        var pane = OpenMesh(fixture);
        var image = Viewer(pane);
        var before = fixture.Graphics!.Uploads[^1].Pixels;

        // ⚠ Long, and starting **on the model**, and both halves were found by sabotage rather than
        // reasoned out. Long, because the gesture is half a turn per pane height: the twelve pixels a
        // paint drag uses would turn the camera three hundredths of a radian, under which this quad
        // narrows by less than a pixel and the picture is legitimately identical. On the model,
        // because the first version began off it — so a pane that treated *every* button as a stroke
        // refused the press for missing the mesh, fell through to the orbit, and passed. The test was
        // the defect, and only breaking the code showed it.
        Drag(fixture, image, new Vector2(-60f, 0f), new Vector2(240f, 0f), PointerButton.Secondary);

        // ⚠ The picture and not a camera field, so the assertion is about what the artist sees. A
        // yaw that moved and a render that did not is exactly the state a pane is in when the
        // gesture is wired and the redraw is not, which is what this workstream ships most.
        Assert.NotEqual(before, fixture.Graphics.Uploads[^1].Pixels);
        Assert.Equal(0, document.Stack.Depth.Value);

        var layer = Assert.Single(document.Document.Sets[0].Layers, one => one.Kind == LayerKind.Paint);

        Assert.Empty(layer.Paint);
    }

    /// <summary>An unbound stack draws no model and says so rather than throwing.</summary>
    /// <remarks>
    ///     ⚠ <b>The extent is cleared and not merely the handle</b>, which is the state a pane is in
    ///     after the artist unbinds a model: a viewer left holding the previous model's size goes on
    ///     drawing its silhouette from a handle nothing refreshes, which is the same defect
    ///     <c>TexturingModule.Islands</c> names for the outlines one pane across.
    /// </remarks>
    [Fact]
    public void An_unbound_stack_draws_no_model() {
        using var fixture = new TexturingFixture(graphics: true);

        Open(fixture, null);

        var panel = OpenMesh(fixture);
        var image = Viewer(panel);

        Assert.Equal(0ul, image.Image);
        Assert.Equal(0, image.ImageWidth);

        // ⚠ And it says which of the two empty states it is in, which is the difference between a
        // pane an artist can act on and one that has apparently broken. The refusal is the mesh
        // resolver's own sentence, so it names the thing to fix rather than the symptom.
        Assert.Contains("model", Status(panel), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>What the sentence under the 3D pane says, off the element that renders it.</summary>
    /// <param name="panel">The panel.</param>
    /// <returns>The text.</returns>
    /// <remarks>
    ///     Off the tree rather than off <c>PaintMeshView.Status</c>, so what is asserted is what a
    ///     person can read — a property kept in step with nothing on screen is the state
    ///     <c>LayerStackPanelTests</c> names for the layer rows one pane across.
    /// </remarks>
    static string Status(UiElement panel) {
        var text = "";

        Walk(panel);

        return text;

        void Walk(UiElement element) {
            if (string.Equals(element.Tag, "paint-mesh-status", StringComparison.Ordinal)) {
                text = element.Text ?? "";
            }

            foreach (var child in element.Children) {
                Walk(child);
            }
        }
    }

    /// <summary>How many texels of each half of the atlas a stroke on the left painted.</summary>
    /// <param name="mirrored">Whether symmetry through <c>x = 0</c> is on.</param>
    /// <returns>The opaque texel count in each half.</returns>
    static (int Left, int Right) Halves(bool mirrored) {
        using var fixture = new TexturingFixture(graphics: true);

        Open(fixture, Quad(-1f, 1f));

        var image = Viewer(OpenMesh(fixture));

        if (mirrored) {
            // ⚠ Through the picker rather than by writing `PaintTool.Symmetry` directly, because the
            // control is the half that could be missing: a plane set by a test on a tool nothing
            // reads is exactly the shape this workstream keeps shipping.
            Find(fixture).Value = "X";
        }

        // ⚠ Well inside the left half, and "well" is measured rather than guessed: the quad is about
        // 390 pane pixels across for two world units, so eighty pixels left of the middle is u ≈ 0.3
        // — and the default 32-pixel brush is about five texels of this 64² atlas, or 0.08 of the
        // unit square. The stroke reaches u ≈ 0.43 and stops, which is what makes "the right half is
        // clean" a statement about symmetry rather than about the brush being small.
        Drag(fixture, image, new Vector2(-80f, 0f), new Vector2(-60f, 0f));

        var document = Assert.IsType<LayerStackDocument>(fixture.Project.Documents.Single());
        var layer = Assert.Single(document.Document.Sets[0].Layers, one => one.Kind == LayerKind.Paint);
        var file = Path.Combine(Path.GetDirectoryName(document.AssetPath)!, layer.Paint);

        using var stream = File.OpenRead(file);
        var canvas = PaintCanvas.Read(stream).Channel("baseColor");
        var left = 0;
        var right = 0;

        for (var y = 0; y < canvas.Height; y++) {
            for (var x = 0; x < canvas.Width; x++) {
                if (canvas.At(x, y) >> 24 == 0u) {
                    continue;
                }

                if (x < canvas.Width / 2) {
                    left++;
                } else {
                    right++;
                }
            }
        }

        return (left, right);
    }

    /// <summary>Opens a stack with a paint layer, optionally bound to a model this writes.</summary>
    /// <param name="fixture">The host.</param>
    /// <param name="obj">The model file's contents, or null to bind none.</param>
    /// <returns>The open document.</returns>
    static LayerStackDocument Open(TexturingFixture fixture, string? obj) {
        if (obj is not null) {
            File.WriteAllText(fixture.Paths.Absolute("Assets/Hull.obj"), obj);
        }

        fixture.Host.Activate(TexturingModule.ModuleId, TexturingModule.ModuleName, new TexturingModule());
        fixture.Project.Selection.Set(LayerStackPanelTests.AddStack(fixture, "Hull"));

        Assert.True(fixture.Shell.Commands.Execute(TexturingModule.OpenStackCommand));

        var document = Assert.IsType<LayerStackDocument>(fixture.Project.Documents.Single());
        var stack = LayerStackDocument.Starter("Hull") with { BaseWidth = 64, BaseHeight = 64 };

        stack.Sets[0].Layers.Add(new() { Id = "rust", Name = "Rust", Kind = LayerKind.Paint });

        if (obj is not null) {
            stack = stack with { Model = "Assets/Hull.obj" };
        }

        document.Document = stack;

        return document;
    }

    /// <summary>Opens the 3D pane with the paint verb on, and lays it out.</summary>
    /// <param name="fixture">The host.</param>
    /// <returns>The panel.</returns>
    static UiElement OpenMesh(TexturingFixture fixture) {
        Assert.True(fixture.Shell.Commands.Execute(TexturingModule.PaintCommand));

        var panel = fixture.Shell.Workspace.Open(TexturingModule.MeshPanel);

        Assert.NotNull(panel);

        // ⚠ Laid out and then refreshed, in that order, and both are needed. The panel's factory
        // runs while its box is still zero-sized, so the first render has no pane to draw into —
        // `PaintMeshView.Tick` is what notices, and it is the module's per-frame hook that calls it.
        fixture.Shell.Document.Update();
        fixture.Host.Update(TimeSpan.FromSeconds(1d / 60d));
        fixture.Shell.Document.Update();

        return panel;
    }

    /// <summary>Opens the 2D pane too, so a stroke can be watched reaching it.</summary>
    /// <param name="fixture">The host.</param>
    /// <returns>The panel.</returns>
    static UiElement? OpenPaint(TexturingFixture fixture) {
        var panel = fixture.Shell.Workspace.Open(TexturingModule.PaintPanel);

        fixture.Shell.Document.Update();

        return panel;
    }

    /// <summary>The 3D pane's viewer.</summary>
    /// <param name="panel">The panel.</param>
    /// <returns>The viewer.</returns>
    static ImageView Viewer(UiElement panel) => Walk<ImageView>(panel) ?? throw new InvalidOperationException(
        "the 3D pane holds no ImageView"
    );

    /// <summary>The symmetry picker in the 3D pane.</summary>
    /// <param name="fixture">The host.</param>
    /// <returns>The picker.</returns>
    static Select Find(TexturingFixture fixture) {
        var panel = fixture.Shell.Workspace.Open(TexturingModule.MeshPanel);

        Assert.NotNull(panel);

        return Walk<Select>(panel) ?? throw new InvalidOperationException("the 3D pane holds no symmetry picker");
    }

    /// <summary>The first control of a kind under an element.</summary>
    /// <typeparam name="T">What to look for.</typeparam>
    /// <param name="element">Where to look.</param>
    /// <returns>The control, or null.</returns>
    static T? Walk<T>(UiElement element) where T : UiElement {
        if (element is T found) {
            return found;
        }

        foreach (var child in element.Children) {
            if (Walk<T>(child) is { } deeper) {
                return deeper;
            }
        }

        return null;
    }

    /// <summary>A press, some moves and a release, in the pane's own pixels from its centre.</summary>
    /// <param name="fixture">The host.</param>
    /// <param name="image">The pane's viewer, for the pixel-to-screen conversion.</param>
    /// <param name="from">Where the drag starts, in pane pixels relative to the middle.</param>
    /// <param name="to">Where it ends.</param>
    /// <param name="button">Which button is held.</param>
    static void Drag(
        TexturingFixture fixture,
        ImageView image,
        Vector2 from,
        Vector2 to,
        PointerButton button = PointerButton.Primary
    ) {
        var middle = new Vector2(image.ImageWidth * 0.5f, image.ImageHeight * 0.5f);
        var start = image.ToScreen(middle + from);
        var end = image.ToScreen(middle + to);

        fixture.Shell.Document.Dispatch(
            new PointerEvent { X = start.X, Y = start.Y, Action = PointerAction.Pressed, Button = button }
        );

        for (var step = 1; step <= 4; step++) {
            var at = Vector2.Lerp(start, end, step / 4f);

            fixture.Shell.Document.Dispatch(new PointerEvent { X = at.X, Y = at.Y, Action = PointerAction.Moved });
        }

        fixture.Shell.Document.Dispatch(
            new PointerEvent { X = end.X, Y = end.Y, Action = PointerAction.Released, Button = button }
        );
    }

    /// <summary>A quad in the z = 0 plane spanning an x range, whose layout is the unit square.</summary>
    /// <param name="from">Its left edge, in the model's own units.</param>
    /// <param name="to">Its right edge.</param>
    /// <returns>The <c>.obj</c> text.</returns>
    static string Quad(float from, float to) =>
        "o hull\n"
        + $"v {from} -1 0\nv {to} -1 0\nv {to} 1 0\nv {from} 1 0\n"
        + "vt 0 0\nvt 1 0\nvt 1 1\nvt 0 1\n"
        + "f 1/1 2/2 3/3\nf 1/1 3/3 4/4\n";
}
