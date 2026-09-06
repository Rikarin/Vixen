// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.IO.Watch;
using Vixen.Core.Mathematics;
using Vixen.Editor.Assets.Content;
using Vixen.Editor.Core;
using Vixen.Editor.Texturing.Layers;
using Vixen.Editor.Texturing.Painting;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Vixen.Ui.Controls.Advanced;
using Xunit;

namespace Vixen.Editor.Texturing.Tests;

/// <summary>
///     The binding reaches the brush: islands on the pane, a coverage map under the stroke, and a
///     selected layer — <a href="https://github.com/Rikarin/Vixen/issues/920">#920</a> and
///     <a href="https://github.com/Rikarin/Vixen/issues/910">#910</a>.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Every test here goes through the panel and ends at a texel or at an overlay
///         segment.</b> <c>LayerStackMeshTests</c> settles that a model resolves to UV triangles;
///         what is settled here is that something <em>calls</em> it — which is the half this
///         workstream keeps losing. A suite asserting that <c>LayerStackMesh.Open</c> works would be
///         green against exactly the state #920 reports, in which every piece existed and the paint
///         pane went on handing <c>PaintCoverage.Everywhere</c> to every stroke.
///     </para>
///     <para>
///         The atlas is 64×64 and the bound model's island is its left half, so "outside the island"
///         is a place a 32-texel brush centred inside it reaches and a coverage map refuses. That
///         differential is the whole instrument: with the coverage map the paint pane supplied
///         before this, the refused texel is painted.
///     </para>
/// </remarks>
public class LayerStackBindingTests {
    /// <summary>The picker offers the project's models and binding one reaches the file.</summary>
    /// <remarks>
    ///     ⚠ <b>And it is one undo entry, like every other edit this panel makes.</b> Binding changes
    ///     which texels the brush will accept, so it is exactly the kind of thing an artist tries and
    ///     takes back — <a href="https://github.com/Rikarin/Vixen/issues/819">#819</a>'s argument
    ///     about the rows, applied to the row above them.
    /// </remarks>
    [Fact]
    public void The_mesh_picker_binds_the_stack_and_the_binding_undoes() {
        using var fixture = new TexturingFixture();

        Model(fixture, "Hull.obj", Quad(0f, 0.5f));

        var document = Open(fixture, "Hull");
        var panel = Panel(fixture);
        var picker = Find<Select>(panel, "layer-stack-model");

        Assert.Equal(LayerStackView.NoMesh, picker.Value);
        Assert.Contains(picker.Options, option => option.Value == "Assets/Hull.obj");

        picker.Value = "Assets/Hull.obj";

        Assert.Equal("Assets/Hull.obj", document.Document.Model);
        Assert.Equal(1, document.Stack.Depth.Value);
        Assert.True(document.IsDirty.Value);

        Assert.True(document.Stack.Undo());
        Assert.Equal("", document.Document.Model);

        // ⚠ The picker is re-read rather than asserted straight after the undo, and that is a finding
        // rather than a convenience. Nothing in this plugin subscribes to `EditorDocument.Stack`, so
        // an undo taken anywhere but in one of these rows leaves every control in the panel showing
        // the value it had — the blend mode and the opacity as much as this. Filed separately; what
        // is asserted here is that a refresh reads the document rather than remembering the click.
        Refresh(fixture);

        Assert.Equal(LayerStackView.NoMesh, Find<Select>(Panel(fixture), "layer-stack-model").Value);
    }

    /// <summary>A model imported while a stack is open reaches the picker.</summary>
    /// <remarks>
    ///     <para>
    ///         <b><a href="https://github.com/Rikarin/Vixen/issues/954">#954</a>, and the remark it
    ///         refuted is above <c>LayerStackView.Rebind</c>.</b> That remark said the options are
    ///         re-read per stack "because the project's models are not fixed" — and the gate that
    ///         decided when to re-read them was the document reference and the bound path, while the
    ///         module hands the same reference to every refresh. So the one moment a picker had to be
    ///         refilled was the one moment it never was.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The refresh <em>before</em> the notification is the half that makes this a test
    ///         rather than a tautology.</b> Without it, a picker refilled unconditionally on every
    ///         show would pass — and refilling on every show is the trap the issue names, because the
    ///         fill walks every asset in the project and a show runs on every keystroke of a slider.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Driven through <c>ExternalEdits.Apply</c> and not by setting the flag.</b> The
    ///         notification is the mechanism under test: a document that never heard about the file
    ///         is the state this was in, and a test that set <c>ModelsChanged</c> itself would be
    ///         green against a document with no override at all.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_model_that_appears_while_a_stack_is_open_reaches_the_picker() {
        using var fixture = new TexturingFixture();
        var document = Open(fixture, "Hull");

        Assert.DoesNotContain(
            Find<Select>(Panel(fixture), "layer-stack-model").Options,
            option => option.Value == "Assets/Late.obj"
        );

        Model(fixture, "Late.obj", Quad(0f, 0.5f));
        Refresh(fixture);

        // Nothing has told the document anything, so the picker is entitled to be as it was — and a
        // panel that refilled here would be paying a project walk per show.
        Assert.DoesNotContain(
            Find<Select>(Panel(fixture), "layer-stack-model").Options,
            option => option.Value == "Assets/Late.obj"
        );

        using var edits = new ExternalEdits(fixture.Project);

        Assert.Equal(0, edits.Apply([new FileChange(new("/Late.obj"), FileChangeKind.Created)]));
        Assert.True(document.ModelsChanged, "the document was not told a model appeared.");

        Refresh(fixture);

        Assert.Contains(
            Find<Select>(Panel(fixture), "layer-stack-model").Options,
            option => option.Value == "Assets/Late.obj"
        );

        // ⚠ And the flag is down again, or every subsequent show refills — which is the cost the
        // gate exists to avoid, arriving by the door that was opened to fix the staleness.
        Assert.False(document.ModelsChanged, "the refill did not clear the flag.");
    }

    /// <summary>A bound stack puts the mesh's UV islands under the brush.</summary>
    /// <remarks>
    ///     <b><c>PaintUvView.ShowIslands</c>' first caller</b> —
    ///     <a href="https://github.com/Rikarin/Vixen/issues/928">#928</a> lists it among five members
    ///     with a declaration and no use. The count is exact: three segments per triangle, and
    ///     nothing else is on the overlay until the pointer moves.
    /// </remarks>
    [Fact]
    public void A_bound_stack_draws_its_islands_on_the_paint_pane() {
        using var fixture = new TexturingFixture(graphics: true);

        Model(fixture, "Hull.obj", Quad(0f, 0.5f));

        var document = Paintable(fixture, "Hull");

        document.Document = document.Document with { Model = "Assets/Hull.obj" };

        var image = ImageIn(PaintPane(fixture));

        // Two triangles, three edges each. An unbound stack draws none of them, which is the state
        // every .vxlayers was in before it could name a model.
        Assert.Equal(6, image.Overlay.Count);
    }

    /// <summary>The part picker offers the model's meshes, and narrowing to one takes the rest away.</summary>
    /// <remarks>
    ///     <para>
    ///         <b><a href="https://github.com/Rikarin/Vixen/issues/941">#941</a>, whose only reason
    ///         for not existing was cost.</b> The field round-tripped through the YAML and narrowed
    ///         the resolve, and the sole way to set it was to edit the <c>.vxlayers</c> by hand —
    ///         because offering the names was thought to mean parsing the model on a panel build. It
    ///         means reading the sidecar, which an import has already written the names into.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It ends at the overlay and not at the field.</b> A test that read
    ///         <c>Sets[0].Mesh</c> back would pass against a picker wired to nothing but the
    ///         document — and what a narrowing is <em>for</em> is that the other mesh's islands stop
    ///         being paintable. Twelve segments for two quads, six for one: three per triangle, and
    ///         nothing else is on the overlay until the pointer moves.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The model is imported first, or the picker has nothing to offer.</b> That is the
    ///         honest limit of this control and it is the picker's own third state — a model whose
    ///         import has not run declares no sub-assets, so the field keeps whatever it names and
    ///         the list holds only that.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task The_part_picker_narrows_the_set_and_the_islands_follow() {
        using var fixture = new TexturingFixture(graphics: true);

        await Imported(fixture, "Panels.obj", Quad(0f, 0.4f) + Named("upper", 4, 0.6f, 1f));

        var document = Paintable(fixture, "Hull");
        var panel = Panel(fixture);
        var image = ImageIn(PaintPane(fixture));

        Find<Select>(panel, "layer-stack-model").Value = "Assets/Panels.obj";

        // Two quads, two triangles each, three segments a triangle.
        Assert.Equal(12, image.Overlay.Count);

        var parts = Find<Select>(Panel(fixture), "layer-stack-set-mesh");

        Assert.Contains(parts.Options, option => option.Value == LayerStackView.EveryMesh);
        Assert.Contains(parts.Options, option => option.Value == "hull");
        Assert.Contains(parts.Options, option => option.Value == "upper");
        Assert.Equal(LayerStackView.EveryMesh, parts.Value);

        var depth = document.Stack.Depth.Value;

        parts.Value = "upper";

        // ⚠ The overlay first and the field afterwards, because only one of the two can pass while
        // the panel is wrong: a picker that wrote the field and reached no resolve would satisfy
        // every other line here and leave the other mesh's islands paintable.
        Assert.Equal(6, image.Overlay.Count);
        Assert.Equal("upper", document.Document.Sets[0].Mesh);
        Assert.Equal(depth + 1, document.Stack.Depth.Value);

        // And back, because widening is a gesture too — a set narrowed to the wrong mesh has to be
        // able to stop being narrowed at all.
        Find<Select>(Panel(fixture), "layer-stack-set-mesh").Value = LayerStackView.EveryMesh;

        Assert.Equal(12, image.Overlay.Count);
        Assert.Equal("", document.Document.Sets[0].Mesh);
    }

    /// <summary>An unbound stack draws none, and unbinding takes the last mesh's outlines away.</summary>
    /// <remarks>
    ///     ⚠ <b>The second half is the one a cache breaks.</b> Islands are redrawn only when the
    ///     binding or the atlas changed — <c>ShowIslands</c> rebuilds the whole overlay and a refresh
    ///     happens at every pointer-up — so a key that did not include "unbound" would leave the old
    ///     mesh's outlines over an atlas they describe nothing about.
    /// </remarks>
    [Fact]
    public void Unbinding_a_stack_takes_its_islands_off_the_pane() {
        using var fixture = new TexturingFixture(graphics: true);

        Model(fixture, "Hull.obj", Quad(0f, 0.5f));

        var document = Paintable(fixture, "Hull");
        var panel = Panel(fixture);
        var image = ImageIn(PaintPane(fixture));

        Assert.Empty(image.Overlay);

        var picker = Find<Select>(panel, "layer-stack-model");

        picker.Value = "Assets/Hull.obj";

        Assert.Equal(6, image.Overlay.Count);

        picker.Value = LayerStackView.NoMesh;

        Assert.Empty(image.Overlay);
    }

    /// <summary>A stroke over a bound mesh paints inside the island and is refused outside it.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The point of the whole slice, and it is a differential rather than a bound.</b>
    ///         Both texels are inside the brush's footprint — the radius is 32 and they are 24 apart
    ///         — so the only thing that can separate them is the coverage map. Before this, the paint
    ///         pane supplied <c>PaintCoverage.Everywhere</c> and both were painted.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The refused texel is chosen beyond the gutter.</b> The seam dilation writes up to
    ///         four texels past the island's edge on purpose, so a texel at <c>x = 34</c> being
    ///         painted would be correct; <c>x = 40</c> is outside both the island and its halo, and
    ///         is the only kind of texel a coverage map may never write.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_stroke_is_refused_outside_the_bound_meshs_islands() {
        using var fixture = new TexturingFixture(graphics: true);

        Model(fixture, "Hull.obj", Quad(0f, 0.5f));

        var document = Paintable(fixture, "Hull");

        document.Document = document.Document with { Model = "Assets/Hull.obj" };

        var pane = PaintPane(fixture);
        var image = ImageIn(pane);

        Drag(fixture, image, new Vector2(8f, 16f), new Vector2(16f, 16f));

        var canvas = Painted(document);

        Assert.NotEqual(0u, canvas.At(16, 16));

        // ⚠ Inside the stamp, outside the island, and past the gutter. The whole claim in one texel.
        Assert.Equal(0u, canvas.At(40, 16));

        // The instrument: the brush really did reach that far, so the zero above is the coverage map
        // and not a stamp that stopped short. The mirror texel on the island's side of the centre is
        // the same distance away and is painted.
        Assert.NotEqual(0u, canvas.At(8, 40));
    }

    /// <summary>And refused above the island's rows, which the u-narrowed fixture cannot tell.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Every other quad in this file spans the whole of <c>v</c>, so every other
    ///         assertion here is satisfied by a coverage map flipped in <c>v</c></b> —
    ///         <a href="https://github.com/Rikarin/Vixen/issues/955">#955</a>. This island is the
    ///         atlas's bottom half and the two texels are the same distance from the stamp's centre
    ///         on opposite sides of the island's edge in <c>v</c>, so nothing but the row can
    ///         separate them: flip the map and the two swap.
    ///     </para>
    ///     <para>
    ///         <b>Which half of the atlas that is takes reading two conventions against each
    ///         other.</b> The OBJ says <c>v</c> ∈ [0, 0.5] and an OBJ's <c>v</c> counts up from the
    ///         bottom; <c>ModelReader</c> asks Assimp for <c>FlipUVs</c>; so the island is rows
    ///         32…63 and the refused texel is above it.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_stroke_is_refused_above_the_bound_meshs_islands() {
        using var fixture = new TexturingFixture(graphics: true);

        Model(fixture, "Band.obj", Quad(0f, 1f, 0f, 0.5f));

        var document = Paintable(fixture, "Hull");

        document.Document = document.Document with { Model = "Assets/Band.obj" };

        var image = ImageIn(PaintPane(fixture));

        Drag(fixture, image, new Vector2(24f, 48f), new Vector2(32f, 48f));

        var canvas = Painted(document);

        Assert.NotEqual(0u, canvas.At(32, 48));

        // ⚠ 24 rows above the last stamp's centre: inside the brush, above the island, and twice as
        // far out as the four texels of gutter the seam dilation is allowed to write.
        Assert.Equal(0u, canvas.At(32, 24));

        // The instrument, as in the test above: 24 texels from the same centre along the row, where
        // the island *is*. Without it the zero above would also be satisfied by a brush whose
        // falloff had simply run out — and it nearly does, since 28 texels out is zero either way.
        Assert.NotEqual(0u, canvas.At(56, 48));
    }

    /// <summary>The pane says which mesh it is painting on, or why it is not.</summary>
    [Fact]
    public void The_paint_panes_line_names_the_bound_mesh_or_the_refusal() {
        using var fixture = new TexturingFixture(graphics: true);

        Model(fixture, "Hull.obj", Quad(0f, 0.5f));

        var document = Paintable(fixture, "Hull");
        var pane = PaintPane(fixture);

        Assert.Contains("names no model", Status(pane), StringComparison.Ordinal);

        Find<Select>(Panel(fixture), "layer-stack-model").Value = "Assets/Hull.obj";

        Assert.Contains("Assets/Hull.obj", Status(pane), StringComparison.Ordinal);
        Assert.Contains("2 triangles", Status(pane), StringComparison.Ordinal);

        // ⚠ And the *set*, which is #927's other half. Every path here takes `Sets[0]` and the
        // messages used to read as though one had been chosen.
        Assert.Contains("'Default'", Status(pane), StringComparison.Ordinal);
    }

    /// <summary>The paint verb names the set and the layer a drag would reach.</summary>
    /// <remarks>
    ///     ⚠ <b>It said "this stack's first paint layer", which stopped being true the moment a row
    ///     could be selected</b> — <a href="https://github.com/Rikarin/Vixen/issues/910">#910</a> —
    ///     and never said which set, which is
    ///     <a href="https://github.com/Rikarin/Vixen/issues/927">#927</a>. A sentence that describes
    ///     a behaviour the build no longer has is worse than no sentence, because an artist acts on
    ///     it.
    /// </remarks>
    [Fact]
    public void The_paint_verb_names_the_set_and_the_layer_it_would_paint_into() {
        using var fixture = new TexturingFixture();

        Paintable(fixture, "Hull", "rust", "grime");

        Assert.True(fixture.Shell.Commands.Execute(TexturingModule.PaintCommand));

        // ⚠ `[0]`, because `NotificationCenter.Show` inserts at the front. Reading `[^1]` gets the
        // *oldest* notification of the session, which for the first assertion here is the same entry
        // — so the mistake is invisible until a test makes two.
        var unselected = fixture.Shell.Notifications.History[0].Detail ?? "";

        Assert.Contains("set 'Default'", unselected, StringComparison.Ordinal);
        Assert.Contains("no layer is selected", unselected, StringComparison.Ordinal);

        // ⚠ The panel is looked up again rather than held. Opening the paint pane rebuilds the
        // workspace, which re-runs the layers panel's factory — so a button captured before it is a
        // button in a tree nobody is showing, and clicking it proves nothing about the panel that is.
        // Row 0 is the topmost, which is the layer added last: "Grime".
        Selects(Panel(fixture))[0].Activate();

        // Toggled off and on again, because the sentence is written when the verb runs.
        Assert.True(fixture.Shell.Commands.Execute(TexturingModule.PaintCommand));
        Assert.True(fixture.Shell.Commands.Execute(TexturingModule.PaintCommand));

        var selected = fixture.Shell.Notifications.History[0].Detail ?? "";

        Assert.Contains("the layer 'grime'", selected, StringComparison.Ordinal);
        Assert.Contains("set 'Default'", selected, StringComparison.Ordinal);
    }

    /// <summary>Closing and reopening the layers panel keeps the layer the brush is aimed at.</summary>
    /// <remarks>
    ///     ⚠ <b>A dock panel's factory runs again on every reopen, so a view that reset the selection
    ///     when it built its rows would take the artist's chosen layer away whenever they closed the
    ///     panel.</b> Which of the two copies is durable is the decision: <c>PaintTool</c> is the
    ///     module's and outlives the panel — its own remarks say so about the brush — so the tool is
    ///     the model and the rows are its presenter, and the new view recovers the marker from it.
    /// </remarks>
    [Fact]
    public void Reopening_the_layers_panel_keeps_the_selected_layer() {
        using var fixture = new TexturingFixture();

        Paintable(fixture, "Hull", "rust", "grime");
        Selects(Panel(fixture))[0].Activate();

        Assert.Contains("●", Names(Panel(fixture))[0], StringComparison.Ordinal);
        Assert.True(fixture.Shell.Workspace.Close(TexturingModule.StackPanel));

        var reopened = Panel(fixture);

        Assert.Contains("●", Names(reopened)[0], StringComparison.Ordinal);
        Assert.Equal("Selected", Selects(reopened)[0].Label);
    }

    /// <summary>⚠ And a second stack does not inherit it, because two stacks share layer ids.</summary>
    /// <remarks>
    ///     <b>The half that makes the recovery above safe.</b> Every stack from
    ///     <c>LayerStackDocument.Starter</c> has a layer called <c>base</c>, and a selection recovered
    ///     by id alone would silently follow the artist from one file into another. The check is
    ///     against the document being built rather than against a remembered one, which is the only
    ///     form that can tell the two apart.
    /// </remarks>
    [Fact]
    public void A_second_stack_does_not_inherit_the_first_stacks_selection() {
        using var fixture = new TexturingFixture();
        var first = Paintable(fixture, "Hull", "rust");

        Selects(Panel(fixture))[0].Activate();

        Assert.Contains("●", Names(Panel(fixture))[0], StringComparison.Ordinal);

        // A second stack with no layer of that id in it.
        first.Document = LayerStackDocument.Starter("Other") with { BaseWidth = 64, BaseHeight = 64 };
        Refresh(fixture);

        Assert.DoesNotContain("●", Names(Panel(fixture))[0], StringComparison.Ordinal);
    }

    /// <summary>Selecting a row aims the brush at that layer, and it is not an edit.</summary>
    /// <remarks>
    ///     ⚠ <b>Two paint layers, because one cannot tell the two answers apart.</b> With a single
    ///     paint layer the brush's empty default — "the first paint layer in composite order" — and
    ///     a real selection produce the same stroke, which is why
    ///     <a href="https://github.com/Rikarin/Vixen/issues/910">#910</a> could exist for as long as
    ///     it did. The assertion is which <c>.vxpaint</c> the pixels landed in.
    /// </remarks>
    [Fact]
    public void Selecting_a_row_aims_the_brush_at_that_layer() {
        using var fixture = new TexturingFixture(graphics: true);
        var document = Paintable(fixture, "Hull", "rust", "grime");
        var panel = Panel(fixture);

        // Topmost first, so row 0 is "Grime", row 1 is "Rust" and row 2 is "Base".
        Assert.Equal(3, Names(panel).Count);
        Assert.StartsWith("Grime", Names(panel)[0], StringComparison.Ordinal);

        var image = ImageIn(PaintPane(fixture));

        Drag(fixture, image, new Vector2(8f, 16f), new Vector2(16f, 16f));

        // With nothing selected the brush takes the first paint layer in composite order, which is
        // the one *lower* in the file — "Rust".
        Assert.Equal("rust", Layer(document, painted: true).Id);
        Assert.Equal("", Find(document, "grime").Paint);

        var entries = document.Stack.Depth.Value;

        Selects(panel)[0].Activate();

        Assert.Contains("●", Names(panel)[0], StringComparison.Ordinal);

        // ⚠ A selection puts nothing on the undo stack. An artist who clicked a row and pressed undo
        // means to undo the last thing they *changed*, and a selection that made the document dirty
        // would make choosing a layer a reason to save the file.
        Assert.Equal(entries, document.Stack.Depth.Value);

        Drag(fixture, image, new Vector2(8f, 16f), new Vector2(16f, 16f));

        Assert.NotEmpty(Find(document, "grime").Paint);
    }

    /// <summary>Clicking the selected row again clears the selection.</summary>
    /// <remarks>
    ///     ⚠ <b>Empty is a state with its own meaning and it has to stay reachable.</b> A panel that
    ///     could enter a selection and never leave it would make "the first paint layer" — the
    ///     behaviour every stack has before anybody chooses — unreachable after the first click of a
    ///     session.
    /// </remarks>
    [Fact]
    public void Clicking_the_selected_row_again_clears_the_selection() {
        using var fixture = new TexturingFixture();
        var document = Paintable(fixture, "Hull");
        var panel = Panel(fixture);
        var buttons = Selects(panel);

        buttons[0].Activate();

        Assert.Equal("Selected", buttons[0].Label);
        Assert.Contains("●", Names(panel)[0], StringComparison.Ordinal);

        buttons[0].Activate();

        Assert.Equal("Select", buttons[0].Label);
        Assert.DoesNotContain("●", Names(panel)[0], StringComparison.Ordinal);
    }

    /// <summary>The selected row is marked, so the panel says what the brush is aimed at.</summary>
    [Fact]
    public void The_selected_row_is_marked_in_the_panel() {
        using var fixture = new TexturingFixture();

        Paintable(fixture, "Hull");

        var panel = Panel(fixture);

        Assert.DoesNotContain("●", Names(panel)[0], StringComparison.Ordinal);

        Selects(panel)[0].Activate();

        Assert.Contains("●", Names(panel)[0], StringComparison.Ordinal);
        Assert.DoesNotContain("●", Names(panel)[1], StringComparison.Ordinal);
    }

    /// <summary>Where a layer's pixels ended up.</summary>
    static PaintImage Painted(LayerStackDocument document) {
        var layer = Layer(document, painted: true);
        var file = Path.Combine(Path.GetDirectoryName(document.AssetPath)!, layer.Paint);

        Assert.True(File.Exists(file), "the stroke did not reach a .vxpaint");

        using var stream = File.OpenRead(file);

        return PaintCanvas.Read(stream).Channel("baseColor");
    }

    /// <summary>The one paint layer that has a canvas named, or the only one there is.</summary>
    static LayerAsset Layer(LayerStackDocument document, bool painted) {
        foreach (var layer in document.Document.Sets[0].Layers) {
            if (layer.Kind == LayerKind.Paint && (!painted || layer.Paint.Length > 0)) {
                return layer;
            }
        }

        throw new InvalidOperationException("the stack has no painted layer");
    }

    static LayerAsset Find(LayerStackDocument document, string id) {
        foreach (var layer in document.Document.Sets[0].Layers) {
            if (string.Equals(layer.Id, id, StringComparison.Ordinal)) {
                return layer;
            }
        }

        throw new InvalidOperationException("no layer called " + id);
    }

    /// <summary>A model in the project, as OBJ text the real reader parses.</summary>
    static void Model(TexturingFixture fixture, string file, string obj) {
        var relative = "Assets/" + file;

        File.WriteAllText(fixture.Paths.Absolute(relative), obj);
        fixture.Project.Assets.Scan();

        Assert.True(fixture.Project.Assets.TryGetByPath(relative, out _), "the scan missed " + file);
    }

    /// <summary>A second quad, named, offset past the first one's vertices.</summary>
    /// <remarks>
    ///     ⚠ <b>OBJ indices are one-based and <em>file-wide</em></b>, so the second object in a file
    ///     does not start at 1 — an offset that is wrong makes a two-object fixture describe one
    ///     object and one degenerate triangle, which proves less than it says.
    /// </remarks>
    static string Named(string name, int offset, float from, float to) =>
        $"o {name}\n"
        + $"v {from} 0 0\nv {to} 0 0\nv {to} 1 0\nv {from} 1 0\n"
        + $"vt {from} 0\nvt {to} 0\nvt {to} 1\nvt {from} 1\n"
        + $"f {offset + 1}/{offset + 1} {offset + 2}/{offset + 2} {offset + 3}/{offset + 3}\n"
        + $"f {offset + 1}/{offset + 1} {offset + 3}/{offset + 3} {offset + 4}/{offset + 4}\n";

    /// <summary>Writes a model into the project and runs a real import over it.</summary>
    /// <remarks>
    ///     ⚠ <b>The import is what puts the mesh names in the sidecar</b>, which is the only place the
    ///     part picker reads them from — so a fixture that only wrote the file would be testing a
    ///     picker with nothing in it.
    /// </remarks>
    static async Task Imported(TexturingFixture fixture, string file, string obj) {
        Model(fixture, file, obj);

        var workspace = new ProjectWorkspace(fixture.Paths);
        List<ContentDiagnostic> said = [];

        var summary = await ContentPipeline.ImportAsync(
            workspace,
            ProjectWorkspace.HostTarget,
            said.Add,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(
            summary.Failed == 0,
            "the import failed: " + string.Join("; ", said.Select(diagnostic => diagnostic.Message))
        );

        fixture.Project.Assets.Scan();
    }

    /// <summary>A quad spanning <paramref name="from" />…<paramref name="to" /> in <c>u</c>.</summary>
    /// <param name="from">Where the island starts in <c>u</c>.</param>
    /// <param name="to">Where it ends.</param>
    /// <param name="low">Where it starts in the OBJ's <c>v</c>, which counts up from the bottom.</param>
    /// <param name="high">Where it ends. ⚠ <c>1</c> is the atlas's <em>first</em> row, not its last.</param>
    /// <returns>The OBJ text.</returns>
    static string Quad(float from, float to, float low = 0f, float high = 1f) =>
        "o hull\n"
        + $"v {from} 0 0\nv {to} 0 0\nv {to} 1 0\nv {from} 1 0\n"
        + $"vt {from} {low}\nvt {to} {low}\nvt {to} {high}\nvt {from} {high}\n"
        + "f 1/1 2/2 3/3\nf 1/1 3/3 4/4\n";

    static LayerStackDocument Open(TexturingFixture fixture, string name) {
        fixture.Host.Activate(TexturingModule.ModuleId, TexturingModule.ModuleName, new TexturingModule());
        fixture.Project.Selection.Set(LayerStackPanelTests.AddStack(fixture, name));

        Assert.True(fixture.Shell.Commands.Execute(TexturingModule.OpenStackCommand));

        return Assert.IsType<LayerStackDocument>(fixture.Project.Documents.Single());
    }

    /// <summary>An open stack with paint layers in it, at an atlas small enough to reason about.</summary>
    /// <remarks>
    ///     ⚠ <b>The verb is run again after the stack is put in, because the panel drew the one that
    ///     was there when it opened.</b> A test that assigned <c>Document</c> and then read the rows
    ///     would be reading the starter stack, and every assertion about a layer it added would be
    ///     about a row that is not on the screen.
    /// </remarks>
    static LayerStackDocument Paintable(TexturingFixture fixture, string name, params string[] paint) {
        var document = Open(fixture, name);
        var stack = LayerStackDocument.Starter(name) with { BaseWidth = 64, BaseHeight = 64 };

        foreach (var id in paint.Length > 0 ? paint : ["rust"]) {
            stack.Sets[0].Layers.Add(
                new() { Id = id, Name = char.ToUpperInvariant(id[0]) + id[1..], Kind = LayerKind.Paint }
            );
        }

        document.Document = stack;
        Refresh(fixture);

        return document;
    }

    /// <summary>Puts the document the panel is showing back in step with the one in memory.</summary>
    static void Refresh(TexturingFixture fixture) =>
        Assert.True(fixture.Shell.Commands.Execute(TexturingModule.OpenStackCommand));

    static UiElement Panel(TexturingFixture fixture) {
        var panel = fixture.Shell.Workspace.Open(TexturingModule.StackPanel);

        Assert.NotNull(panel);

        return panel;
    }

    static UiElement PaintPane(TexturingFixture fixture) {
        Assert.True(fixture.Shell.Commands.Execute(TexturingModule.PaintCommand));

        var panel = fixture.Shell.Workspace.Open(TexturingModule.PaintPanel);

        Assert.NotNull(panel);

        // Laid out before anything is dispatched: a control with no box has no absolute position,
        // so a dispatch before this reaches no handler at all.
        fixture.Shell.Document.Update();

        return panel;
    }

    /// <summary>
    ///     What each row's name element says, topmost first — including the selection marker.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Read out of the tree rather than off <c>LayerStackView.Selected</c>.</b> The view is
    ///     built into a panel and is not in it, and asserting on a property the panel does not show
    ///     would be green against a selection nobody can see — which is the same defect one level
    ///     down from the one #910 is about.
    /// </remarks>
    static List<string> Names(UiElement panel) {
        List<string> found = [];

        foreach (var element in All(panel, "layer-stack-row-name")) {
            found.Add(element.Text ?? "");
        }

        return found;
    }

    static List<Button> Selects(UiElement panel) {
        List<Button> found = [];

        foreach (var element in All(panel, "layer-stack-select")) {
            found.Add(Assert.IsType<Button>(element));
        }

        Assert.NotEmpty(found);

        return found;
    }

    static string Status(UiElement panel) {
        var text = "";

        foreach (var element in All(panel, "paint-uv-status")) {
            text = element.Text ?? "";
        }

        return text;
    }

    static T Find<T>(UiElement root, string tag) where T : UiElement {
        var found = All(root, tag);

        Assert.NotEmpty(found);

        return Assert.IsType<T>(found[0]);
    }

    static List<UiElement> All(UiElement root, string tag) {
        List<UiElement> found = [];

        Walk(root);

        return found;

        void Walk(UiElement element) {
            if (string.Equals(element.Tag, tag, StringComparison.Ordinal)) {
                found.Add(element);
            }

            foreach (var child in element.Children) {
                Walk(child);
            }
        }
    }

    static ImageView ImageIn(UiElement panel) {
        foreach (var child in panel.Children) {
            if (Look(child) is { } found) {
                return found;
            }
        }

        throw new InvalidOperationException("the paint pane holds no ImageView");

        static ImageView? Look(UiElement element) {
            if (element is ImageView view) {
                return view;
            }

            foreach (var child in element.Children) {
                if (Look(child) is { } found) {
                    return found;
                }
            }

            return null;
        }
    }

    static void Drag(TexturingFixture fixture, ImageView image, Vector2 from, Vector2 to) {
        var start = image.ToScreen(from);
        var end = image.ToScreen(to);

        fixture.Shell.Document.Dispatch(
            new PointerEvent {
                X = start.X,
                Y = start.Y,
                Action = PointerAction.Pressed,
                Button = PointerButton.Primary
            }
        );

        for (var step = 1; step <= 4; step++) {
            var at = Vector2.Lerp(start, end, step / 4f);

            fixture.Shell.Document.Dispatch(
                new PointerEvent { X = at.X, Y = at.Y, Action = PointerAction.Moved }
            );
        }

        fixture.Shell.Document.Dispatch(
            new PointerEvent { X = end.X, Y = end.Y, Action = PointerAction.Released, Button = PointerButton.Primary }
        );
    }
}
