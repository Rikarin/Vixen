// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Editor.Core;
using Vixen.Editor.TextureGraph;
using Vixen.Editor.Texturing.Layers;
using Vixen.Editor.Texturing.Painting;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Xunit;

namespace Vixen.Editor.Texturing.Tests;

/// <summary>The layers panel edits, and every edit is on the document's own undo stack.</summary>
/// <remarks>
///     <para>
///         <b><a href="https://github.com/Rikarin/Vixen/issues/819">#819</a>, and the reason it waited
///         for a model.</b> Nothing in the layer stack was routed through
///         <c>EditorDocument.Stack</c>, so a panel that offered a reorder would have offered a gesture
///         with no undo and no dirty flag. What is asserted here is both halves at once: the row
///         does the thing, and the stack remembers it.
///     </para>
///     <para>
///         ⚠ <b>Every test here walks the tree the panel built and presses the control an artist
///         would press.</b> Batch 9 added a public <c>Messages</c> property with no readers and then
///         wrote its own tests against the tree instead, which is the stronger assertion and the one
///         this file keeps to: a view method called only from xunit proves that the method works, not
///         that the panel does.
///     </para>
///     <para>
///         ⚠ <b>And a reorder is checked against a compiled plan rather than against a list.</b> A
///         list assertion is a restatement of the command's own arithmetic; what a reorder actually
///         means is that a different layer's colour arrives at the last composite, and only the
///         compilation says which.
///     </para>
/// </remarks>
public class LayerStackEditingTests {
    /// <summary>Where <see cref="Viewed" /> puts a view of its own, beside the module's.</summary>
    const string ViewPanel = "texturing.tests.layer-stack";

    /// <summary>Moving a layer up changes which colour the last composite reads.</summary>
    /// <remarks>
    ///     ⚠ <b>The panel draws topmost first and the file stores bottom first, so the button labelled
    ///     <em>up</em> is <c>+1</c> in the file's order.</b> A view that passed <c>-1</c> would leave
    ///     the bottom row's button doing nothing at all — <c>MoveLayerCommand.CanMove</c> answers no
    ///     at index 0 — and a view that reversed the rows instead of the deltas would move the wrong
    ///     layer. Both are invisible in a list of names and both change which uniform feeds the last
    ///     blend.
    /// </remarks>
    [Fact]
    public void Moving_a_layer_up_changes_the_compiled_composite() {
        using var fixture = new TexturingFixture();
        var document = Open(fixture, Two());
        var panel = Panel(fixture);

        Assert.Equal(0.75f, TopColour(document));

        // Row 0 is the top layer and row 1 is the bottom one, so this is the lower layer being sent
        // over the upper one.
        Buttons(panel, "layer-stack-move-up")[1].Activate();

        Assert.Equal(0.25f, TopColour(document));
    }

    /// <summary>And undoing it puts the composite back.</summary>
    /// <remarks>
    ///     <b>The half that makes the panel worth opening.</b> A reorder that could not be undone is
    ///     the state <a href="https://github.com/Rikarin/Vixen/issues/819">#819</a> refused to ship,
    ///     and the document's dirty flag is the other half of the same claim: a stack edited in this
    ///     panel is a stack the editor knows to write back.
    /// </remarks>
    [Fact]
    public void A_reorder_is_one_undo_entry_and_it_reverses() {
        using var fixture = new TexturingFixture();
        var document = Open(fixture, Two());
        var panel = Panel(fixture);

        Assert.False(document.IsDirty.Value);

        Buttons(panel, "layer-stack-move-up")[1].Activate();

        Assert.True(document.IsDirty.Value);
        Assert.Equal(1, document.Stack.Depth.Value);
        Assert.Equal(0.25f, TopColour(document));

        Assert.True(document.Stack.Undo());
        Assert.Equal(0.75f, TopColour(document));
        Assert.False(document.IsDirty.Value);
    }

    /// <summary>⚠ Two reorders are two entries, which is the opposite answer from the slider.</summary>
    /// <remarks>
    ///     <b>A predicate that could not be false if <c>MoveLayerCommand</c> merged.</b> An artist who
    ///     moved a layer up twice and pressed undo means to be one step down; a command type that
    ///     absorbed its predecessor would put them back where they started, and the depth would say
    ///     one.
    /// </remarks>
    [Fact]
    public void Two_reorders_are_two_undo_entries() {
        using var fixture = new TexturingFixture();
        var document = Open(fixture, Three());
        var panel = Panel(fixture);

        // The bottom row twice: once past the middle layer, once past the top one.
        Buttons(panel, "layer-stack-move-up")[2].Activate();
        Buttons(panel, "layer-stack-move-up")[1].Activate();

        Assert.Equal(2, document.Stack.Depth.Value);
        Assert.Equal(0.25f, TopColour(document));

        Assert.True(document.Stack.Undo());
        Assert.Equal(0.75f, TopColour(document));
    }

    /// <summary>Choosing a blend mode puts that operator in the plan.</summary>
    [Fact]
    public void Choosing_a_blend_mode_reaches_the_plan() {
        using var fixture = new TexturingFixture();
        var document = Open(fixture, Two());
        var panel = Panel(fixture);

        var blend = Find<Select>(panel, "layer-stack-blend");

        Assert.Equal(nameof(LayerBlendMode.Copy), blend.Value);

        blend.Value = nameof(LayerBlendMode.Overlay);

        Assert.Equal(
            (float)(int)LayerBlendMode.Overlay,
            Last(Compile(document), "Blend").Find("mode")!.Value.Value
        );

        Assert.True(document.Stack.Undo());
        Assert.Equal(
            (float)(int)LayerBlendMode.Copy,
            Last(Compile(document), "Blend").Find("mode")!.Value.Value
        );
    }

    /// <summary>Clearing a channel's tick stops the layer writing that channel.</summary>
    /// <remarks>
    ///     ⚠ <b>Counted in the plan rather than read off <c>LayerAsset.Channels</c>.</b> The member is
    ///     a list of names and the thing an artist means by it is a composite that does not happen —
    ///     <c>LayerStackGraph.Layer</c> returns the cursor untouched for a channel a layer does not
    ///     write, so what a cleared tick removes is a whole <c>Blend</c> op from that channel's chain.
    /// </remarks>
    [Fact]
    public void Clearing_a_channel_removes_that_channels_composite() {
        using var fixture = new TexturingFixture();
        var document = Open(fixture, TwoChannels());
        var panel = Panel(fixture);

        Assert.Equal(2, Count(Compile(document), "Blend"));

        // The second tick on the only row is 'roughness'.
        Ticks(panel, "layer-stack-channel")[1].Activate();

        Assert.Equal(1, Count(Compile(document), "Blend"));

        var layer = document.Document.Sets[0].Layers[0];

        Assert.Equal("baseColor", Assert.Single(layer.Channels));
        Assert.True(document.Stack.Undo());

        // ⚠ And it comes back as *empty*, not as the two names. Empty means every channel, so a
        // round trip that stored the list would silently stop the layer writing a channel the set
        // gained afterwards — `LayerAsset.Channels`' own argument, from the other end.
        Assert.Empty(document.Document.Sets[0].Layers[0].Channels);
        Assert.Equal(2, Count(Compile(document), "Blend"));
    }

    /// <summary>⚠ The last remaining tick cannot be cleared, and the legend says why.</summary>
    /// <remarks>
    ///     <b>The ambiguity in the file kept out of the panel.</b> Clearing the last tick would leave
    ///     <c>Channels</c> empty — and empty means <em>all</em>, so the gesture an artist reads as
    ///     "and now it writes nothing" would make the layer write everything, including channels it
    ///     was just restricted away from. The control that cannot express it is the answer; the
    ///     sentence under the rows is what makes the disabled box make sense.
    /// </remarks>
    [Fact]
    public void The_last_channel_tick_cannot_be_cleared() {
        using var fixture = new TexturingFixture();
        var document = Open(fixture, TwoChannels());
        var panel = Panel(fixture);
        var ticks = Ticks(panel, "layer-stack-channel");

        Assert.All(ticks, tick => Assert.False(tick.Disabled));

        ticks[1].Activate();

        var left = Ticks(panel, "layer-stack-channel");

        Assert.True(left[0].IsChecked);
        Assert.True(left[0].Disabled);
        Assert.False(left[1].IsChecked);
        Assert.False(left[1].Disabled);

        // Pressing it anyway is what a disabled control refuses, so the stack never sees a second
        // entry and the layer still writes one channel.
        left[0].Activate();

        Assert.Equal(1, document.Stack.Depth.Value);
        Assert.Equal("baseColor", Assert.Single(document.Document.Sets[0].Layers[0].Channels));

        var legend = Element(panel, "layer-stack-legend");

        Assert.Contains("unrestricted", legend.Text ?? "", StringComparison.Ordinal);
        Assert.Contains("switched off", legend.Text ?? "", StringComparison.Ordinal);
    }

    /// <summary>A drag of the opacity slider is one undo entry, and the next drag is another.</summary>
    /// <remarks>
    ///     ⚠ <b>Both halves, because either alone is satisfied by a bug.</b> A command that never
    ///     merged would put one entry per frame in the history — three hundred for one drag — and one
    ///     that merged unconditionally would fold every drag an artist ever makes into the first,
    ///     so undo would jump back to the value the layer had when the file was opened.
    ///     <c>CommandStack.Seal</c> is what separates them and it is explicit rather than a time
    ///     window, so the panel has to call it: this is that call, made the way a pointer release
    ///     makes it.
    /// </remarks>
    [Fact]
    public void An_opacity_drag_is_one_undo_entry_and_the_next_drag_is_another() {
        using var fixture = new TexturingFixture();
        var document = Open(fixture, Two());
        var panel = Panel(fixture);
        var slider = Find<Slider>(panel, "layer-stack-opacity");

        slider.Value = 0.8f;
        slider.Value = 0.6f;
        slider.Value = 0.4f;

        Assert.Equal(1, document.Stack.Depth.Value);
        Assert.Equal(0.4f, Top(document).Opacity, 5);

        document.Stack.Seal();

        slider.Value = 0.2f;

        Assert.Equal(2, document.Stack.Depth.Value);

        // ⚠ The merged entry undoes to the value before the *drag*, not to the value one frame ago.
        Assert.True(document.Stack.Undo());
        Assert.Equal(0.4f, Top(document).Opacity, 5);

        Assert.True(document.Stack.Undo());
        Assert.Equal(1f, Top(document).Opacity, 5);
    }

    /// <summary>⚠ And letting go of the slider is what seals it, which nothing else does.</summary>
    /// <remarks>
    ///     <b>The production half of the test above, and it is a different claim.</b>
    ///     <c>CommandStack.Seal</c> is explicit rather than a time window, so somebody has to call it
    ///     — and until this line nothing in the panel did, which would have folded every drag an
    ///     artist ever made on one layer into the first entry. Raising the pointer event is the route
    ///     a real release takes; asserting on <c>Seal()</c> called by the test would prove only that
    ///     <c>CommandStack</c> works.
    /// </remarks>
    [Fact]
    public void Letting_go_of_the_opacity_slider_starts_a_new_undo_entry() {
        using var fixture = new TexturingFixture();
        var document = Open(fixture, Two());
        var panel = Panel(fixture);
        var slider = Find<Slider>(panel, "layer-stack-opacity");

        slider.Value = 0.8f;
        slider.Value = 0.6f;

        Assert.Equal(1, document.Stack.Depth.Value);

        // ⚠ Pressed first, and that is the point of this test rather than a detail of it. `Range`
        // marks the release that ends a drag as handled, so a bare Released — no press, `dragging`
        // still false — takes the default branch and reaches a bubbling handler that a real one
        // never would. The seal was registered without `handledEventsToo` and this test passed.
        slider.Raise(new PointerEvent { Action = PointerAction.Pressed, Button = PointerButton.Primary });
        slider.Raise(new PointerEvent { Action = PointerAction.Released, Button = PointerButton.Primary });

        slider.Value = 0.4f;

        Assert.Equal(2, document.Stack.Depth.Value);
    }

    /// <summary>⚠ An edit re-evaluates the map, which is the one line in the module.</summary>
    /// <remarks>
    ///     <b>Without it this whole panel is a finished thing nothing calls.</b>
    ///     <c>LayerStackView</c> holds no evaluator and redraws its own rows, so every assertion above
    ///     passes against a module that never subscribed to <c>Edited</c> — and an artist would see
    ///     the row move and the picture stay. What is read here is a message that can only appear if
    ///     the stack was compiled <em>again</em> after the edit: the layer switched on is a Paint
    ///     layer with no canvas yet, which <c>LayerStackGraph</c> has something to say about by name.
    ///     <para>
    ///         ⚠ <b>A warning rather than an error, and the severity is not incidental.</b> This was
    ///         written when a Paint layer was refused outright; #852 wired the layer kind and chose to
    ///         warn instead, because <c>LayerStackCompiler</c> throws the whole plan away on any
    ///         error — so a Paint layer that refused until its first stroke would blank every other
    ///         layer's preview at the one moment it can happen, which is when a panel has just
    ///         created one. What this test needs is only that the message is <em>new</em>, and a
    ///         warning is as new as an error.
    ///     </para>
    /// </remarks>
    [Fact]
    public void An_edit_re_evaluates_the_map() {
        using var fixture = new TexturingFixture(graphics: true);

        Open(
            fixture,
            Stack(
                [new() { Usage = "baseColor", Default = [0f, 0f, 0f, 1f] }],
                Fill("bottom", "Bottom", 0.25f),
                new LayerAsset { Id = "paint", Name = "Paint", Kind = LayerKind.Paint, Enabled = false }
            )
        );

        var panel = Panel(fixture);

        Assert.Empty(Texts(panel, "layer-stack-message"));

        // The top row is the paint layer, and its tick is the first `layer-stack-enabled` in the tree.
        Ticks(panel, "layer-stack-enabled")[0].Activate();

        var message = Assert.Single(Texts(panel, "layer-stack-message"));

        Assert.StartsWith("Warning", message, StringComparison.Ordinal);
        Assert.Contains("paint", message, StringComparison.Ordinal);
    }

    /// <summary>A mask's own entries are rows in the same list, outermost first.</summary>
    /// <remarks>
    ///     <b>Doc 48 § D10's "a mask is itself a small stack", on the screen.</b> The panel listed
    ///     neither the base nor the entries before this, so a layer with a two-entry mask looked
    ///     exactly like a layer with none.
    /// </remarks>
    [Fact]
    public void A_masks_entries_are_rows_under_their_layer() {
        using var fixture = new TexturingFixture();

        Open(fixture, Masked());

        var lines = Texts(Panel(fixture), "layer-stack-mask-name");

        Assert.Equal(2, lines.Count);

        // The entry composites over the base, so it is the outer one and is listed first — the same
        // rule the layer rows follow, and the reverse of the file.
        Assert.StartsWith("Mask — Constant 0.5, Multiply", lines[0], StringComparison.Ordinal);
        Assert.StartsWith("Mask base — Bake 'curvature'", lines[1], StringComparison.Ordinal);
    }

    /// <summary>And switching a mask entry off removes its composite from the plan.</summary>
    [Fact]
    public void Switching_a_mask_entry_off_removes_its_composite() {
        using var fixture = new TexturingFixture();
        var document = Open(fixture, Masked());
        var panel = Panel(fixture);

        var before = Count(Compile(document), "Blend");

        Ticks(panel, "layer-stack-mask-enabled")[0].Activate();

        var after = Count(Compile(document), "Blend");

        // The entry's own `Colour/Blend` and nothing else: the layer's composite and the mask's
        // product multiply are still there.
        Assert.Equal(before - 1, after);
        Assert.False(document.Document.Sets[0].Layers[0].Mask.Layers[0].Enabled);

        Assert.True(document.Stack.Undo());
        Assert.Equal(before, Count(Compile(document), "Blend"));
    }

    /// <summary>⚠ A group's children are rows too, and they reorder inside the group.</summary>
    /// <remarks>
    ///     <b>A list that stopped at the top level was honest while nothing could be moved.</b> It
    ///     stops being honest the moment there is an <em>up</em> button, because a layer inside a
    ///     group is then one an artist cannot reach — and <c>LayerStackEdit</c> already reorders
    ///     inside whichever list a layer is really in, so the gap was entirely in the view.
    /// </remarks>
    [Fact]
    public void A_group_child_is_a_row_and_moves_inside_its_group() {
        using var fixture = new TexturingFixture();
        var document = Open(fixture, Grouped());
        var panel = Panel(fixture);

        var rows = Texts(panel, "layer-stack-row-name");

        Assert.Equal(3, rows.Count);
        Assert.StartsWith("Group", rows[0], StringComparison.Ordinal);
        Assert.Contains("Upper", rows[1], StringComparison.Ordinal);
        Assert.Contains("Lower", rows[2], StringComparison.Ordinal);

        Buttons(panel, "layer-stack-move-up")[2].Activate();

        var children = document.Document.Sets[0].Layers[0].Children;

        Assert.Equal("upper", children[0].Id);
        Assert.Equal("lower", children[1].Id);

        // ⚠ Inside the group and not out of it: the group still holds both, which is what makes the
        // move a reorder rather than a reparent — a gesture this panel deliberately does not offer.
        Assert.Equal(2, children.Count);
        Assert.Single(document.Document.Sets[0].Layers);
    }

    /// <summary>The panel's own rows survive an edit that did not change their shape.</summary>
    /// <remarks>
    ///     ⚠ <b>The property a rebuild-on-every-refresh cannot have, and it is not a saving.</b> Every
    ///     refresh runs on every evaluation, so rebuilding unconditionally removes the control under
    ///     the artist's captured pointer and a slider stops mid-drag. What this reads is that the
    ///     element is the same object after an edit that changed only a value, and a different one
    ///     after an edit that changed the row set.
    /// </remarks>
    [Fact]
    public void A_value_edit_keeps_the_row_and_a_reorder_rebuilds_it() {
        using var fixture = new TexturingFixture();

        Open(fixture, Two());

        var panel = Panel(fixture);
        var slider = Find<Slider>(panel, "layer-stack-opacity");

        slider.Value = 0.5f;

        Assert.Same(slider, Find<Slider>(panel, "layer-stack-opacity"));

        Buttons(panel, "layer-stack-move-up")[1].Activate();

        Assert.NotSame(slider, Find<Slider>(panel, "layer-stack-opacity"));
    }

    /// <summary>⚠ An undo taken outside the panel puts the document's value back on the control.</summary>
    /// <remarks>
    ///     <para>
    ///         <b><a href="https://github.com/Rikarin/Vixen/issues/933">#933</a>, and it is the defect
    ///         the whole undoable model was built for.</b> Every other test in this file presses a
    ///         control and then reads the <em>document</em> — so all of them are green against a
    ///         panel that never reads the document back. What a person does is press Ctrl+Z, which
    ///         reaches <c>CommandStack.Undo</c> through the editor's own verb and not through
    ///         anything in these rows.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The flush is what a frame does, and it is not test ceremony.</b> Writing a signal
    ///         only queues; <c>EditorShell</c> drains its own document's queue once per frame —
    ///         <c>UiDocument.Effects</c> says why it is the document's queue and not the thread's —
    ///         so a test that asserted without flushing would be asserting that the refresh happened
    ///         at a moment the editor never assigns work to.
    ///     </para>
    /// </remarks>
    [Fact]
    public void An_undo_taken_outside_the_panel_reaches_the_controls() {
        using var fixture = new TexturingFixture();
        var document = Open(fixture, Two());
        var panel = Panel(fixture);

        // The top row's tick, pressed the way an artist presses it.
        Ticks(panel, "layer-stack-enabled")[0].Activate();

        Assert.False(Ticks(panel, "layer-stack-enabled")[0].IsChecked);
        Assert.False(Top(document).Enabled);

        Assert.True(document.Stack.Undo());
        fixture.Shell.Document.Effects.Flush();

        // ⚠ The document, first: an assertion on the tick alone would pass against a panel that had
        // simply failed to write the edit through in the first place.
        Assert.True(Top(document).Enabled);
        Assert.True(Ticks(panel, "layer-stack-enabled")[0].IsChecked);
    }

    /// <summary>⚠ And an undone reorder puts the rows back in the order the file has them.</summary>
    /// <remarks>
    ///     <b>The other half of <a href="https://github.com/Rikarin/Vixen/issues/933">#933</a>, and a
    ///     different code path.</b> A value edit is re-read by the row's own binding; a reorder
    ///     changes the shape signature, so what has to run is the rebuild. A panel that re-read its
    ///     values and never rebuilt would pass the test above and leave the layers on screen in an
    ///     order the file no longer has.
    /// </remarks>
    [Fact]
    public void An_undone_reorder_puts_the_rows_back() {
        using var fixture = new TexturingFixture();
        var document = Open(fixture, Two());
        var panel = Panel(fixture);

        Buttons(panel, "layer-stack-move-up")[1].Activate();

        Assert.StartsWith("Bottom", Texts(panel, "layer-stack-row-name")[0], StringComparison.Ordinal);

        Assert.True(document.Stack.Undo());
        fixture.Shell.Document.Effects.Flush();

        Assert.StartsWith("Top", Texts(panel, "layer-stack-row-name")[0], StringComparison.Ordinal);
    }

    /// <summary>What a mask row reads can be changed, and the base can be switched off and back on.</summary>
    /// <remarks>
    ///     <para>
    ///         <b><a href="https://github.com/Rikarin/Vixen/issues/882">#882</a>.</b> The panel listed
    ///         a mask's entries and its base as sentences with no control on them, so an artist could
    ///         see that a layer had a generator mask and could not point it anywhere else.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And back on is the half that decides the row is drawn unconditionally.</b>
    ///         Switching a base off means setting its source to <c>None</c>; the base row used to
    ///         exist only when the source was not <c>None</c>, so the one gesture that turns a mask
    ///         off would have removed the control that turns it back on.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_masks_base_source_changes_and_switches_off_and_back_on() {
        using var fixture = new TexturingFixture();
        var document = Open(fixture, Masked());
        var panel = Panel(fixture);

        // The entry's editor comes first and the base's is last, which is the order the rows are in.
        var sources = Controls<Select>(panel, "layer-stack-mask-source");

        Assert.Equal(2, sources.Count);
        Assert.Equal(nameof(LayerMaskSource.Bake), sources[1].Value);

        sources[1].Value = nameof(LayerMaskSource.None);

        Assert.Equal(LayerMaskSource.None, Mask(document).Source);
        Assert.Equal(1, document.Stack.Depth.Value);

        // ⚠ The row is still there with its selector on it, which is the whole reason it is drawn
        // for a mask that reads nothing.
        Controls<Select>(panel, "layer-stack-mask-source")[^1].Value = nameof(LayerMaskSource.Bake);

        Assert.Equal(LayerMaskSource.Bake, Mask(document).Source);

        // ⚠ And the map survived the round trip through None: switching a source is not a reset, so
        // an artist who turns a mask off and on again has the mask they had.
        Assert.Equal("curvature", Mask(document).Map);

        Assert.True(document.Stack.Undo());
        Assert.Equal(LayerMaskSource.None, Mask(document).Source);
    }

    /// <summary>A constant mask's number is a slider, and a drag is one undo entry.</summary>
    /// <remarks>
    ///     ⚠ <b>The merge key is per row rather than per layer, which is what the slot in it buys.</b>
    ///     A layer's mask base and its entries all share one <c>LayerPath</c>, so a key of
    ///     <c>mask-value</c> alone would collapse a drag on one row into the drag on another and
    ///     undo both at once.
    /// </remarks>
    [Fact]
    public void A_constant_masks_number_is_editable() {
        using var fixture = new TexturingFixture();
        var document = Open(fixture, Masked());
        var panel = Panel(fixture);

        var numbers = Controls<Slider>(panel, "layer-stack-mask-value");

        Assert.Equal(0.5f, numbers[0].Value);

        numbers[0].Value = 0.25f;

        Assert.Equal(0.25f, Mask(document).Layers[0].Value);
        Assert.Equal(1, document.Stack.Depth.Value);

        Assert.True(document.Stack.Undo());
        Assert.Equal(0.5f, Mask(document).Layers[0].Value);
    }

    /// <summary>A bake mask's map is chosen from what the node accepts, and reaches the external.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A picker, and it took a plugin seam to draw one —
    ///         <a href="https://github.com/Rikarin/Vixen/issues/964">#964</a>.</b> This was a
    ///         <c>TextBox</c>, because <c>TextureMeshMaps.Known</c> is <c>internal</c> to
    ///         <c>Vixen.Editor.TextureGraph</c>: neither the panel nor this test could ask what the
    ///         nine were, and writing them here would have been the second transcription of a known
    ///         set that five roll calls in this workstream have gone red on.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>So the assertion is still not a list of nine names.</b> It is that what the
    ///         picker offers <em>is</em> what the node declares — <c>TextureNodeLibrary.MeshMaps</c>
    ///         reads the node type's own <c>Accepted</c> rather than copying it — and that choosing
    ///         one reaches the plan's external, which is what an artist is after. A test that spelled
    ///         the nine would stay green while the picker offered a tenth the compiler refuses.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And the offer is asserted non-empty, because an equality against an empty list is
    ///         satisfied by an empty picker</b> — which is exactly what an <c>Accepted</c> that never
    ///         reached the generated definition would produce.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_bake_masks_map_is_chosen_from_what_the_node_accepts_and_reaches_the_external() {
        using var fixture = new TexturingFixture();
        var document = Open(fixture, Masked());
        var panel = Panel(fixture);

        var picker = Controls<Select>(panel, "layer-stack-mask-map")[^1];

        Assert.Equal("curvature", picker.Value);
        Assert.NotEmpty(picker.Options);
        Assert.Equal(TextureNodeLibrary.MeshMaps, picker.Options.Select(option => option.Value ?? "").ToList());

        picker.Value = "thickness";

        Assert.Equal("thickness", Mask(document).Map);

        var compilation = LayerStackCompiler.Compile(document.Document, document.Document.Sets[0]);

        Assert.Contains(compilation.Externals, external => external.Asset.EndsWith("thickness", StringComparison.Ordinal));

        Assert.True(document.Stack.Undo());
        Assert.Equal("curvature", Mask(document).Map);
    }

    /// <summary>A stored map this build does not bake stays on the screen rather than being replaced.</summary>
    /// <remarks>
    ///     ⚠ <b><c>Rebind</c>'s three-state rule, one control along from the anchor picker.</b> A
    ///     dropdown that dropped a value it cannot offer shows the first option instead — which
    ///     <em>says</em> the mask measures that, and writes it on the next click, losing a value the
    ///     author was never told was wrong. What the stack holds stays visible; the compile's own
    ///     refusal is what says it is wrong.
    /// </remarks>
    [Fact]
    public void A_bake_masks_unknown_map_is_offered_rather_than_replaced() {
        using var fixture = new TexturingFixture();

        Open(fixture, Masked("porosity"));

        var picker = Controls<Select>(Panel(fixture), "layer-stack-mask-map")[^1];

        Assert.Equal("porosity", picker.Value);
        Assert.Contains(picker.Options, option => option.Value == "porosity");

        // ⚠ And the nine are still all there: a stranger is appended, not substituted, so the author
        // can still pick a real one without retyping.
        Assert.Equal(TextureNodeLibrary.MeshMaps.Count + 1, picker.Options.Count);
    }

    /// <summary>⚠ The anchor picker offers the layers whose result exists before this one's.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>An anchor onto a layer at or above its own is a loop</b>, which
    ///         <c>LayerStackGraph.Anchors</c> refuses through the graph model. A picker that offered
    ///         one would be a dropdown every entry of which fails, so what it offers is what the
    ///         model accepts.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Composite order and not row order, and a group is where the two differ.</b> A
    ///         group's blend node is emitted <em>after</em> its children's, so a child may anchor
    ///         onto nothing in its own group — which a picker built on the panel's top-to-bottom
    ///         order would get exactly backwards.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_anchor_picker_offers_only_the_layers_below() {
        using var fixture = new TexturingFixture();

        Open(fixture, Anchored());

        var panel = Panel(fixture);
        // The rows are topmost first, so the mask pickers are the top layer's, the middle's and the
        // bottom's — and only the middle one's mask is an anchor, so only it is filled.
        var picker = Controls<Select>(panel, "layer-stack-mask-anchor")[1];

        var offered = picker.Options.Select(option => option.Value ?? "").ToArray();

        // Neither 'middle', which is itself, nor 'top', which is composited after it.
        Assert.Equal([LayerStackView.NoAnchor, "bottom"], offered);
        Assert.Equal("bottom", picker.Value);

        picker.Value = LayerStackView.NoAnchor;

        var document = fixture.Project.Documents.OfType<LayerStackDocument>().Single();

        Assert.Equal("", document.Document.Sets[0].Layers[1].Mask.Anchor);
    }

    /// <summary>⚠ And the bottom layer of a stack is offered nothing to anchor onto.</summary>
    /// <remarks>
    ///     <b>The predicate that could not be false if the picker simply listed the set.</b> Every
    ///     assertion above is satisfied by a picker that offered every layer except the one holding
    ///     it; the bottom layer is the case where "everything else" and "everything below" differ by
    ///     the whole list.
    /// </remarks>
    [Fact]
    public void The_bottom_layers_anchor_picker_offers_nothing() {
        using var fixture = new TexturingFixture();

        Open(fixture, AnchoredFromTheBottom());

        var picker = Controls<Select>(Panel(fixture), "layer-stack-mask-anchor")[^1];

        // ⚠ The stored anchor is kept as an option even though nothing below can be named, so that
        // the picker does not read as unanchored and then unanchor it on the next click — the mesh
        // picker's three-state rule. What is not there is a layer this one could legally read.
        var offered = picker.Options.Select(option => option.Value ?? "").ToArray();

        Assert.Equal([LayerStackView.NoAnchor, "top"], offered);
        Assert.Empty(LayerStackView.Anchorable(fixture.Project.Documents.OfType<LayerStackDocument>()
            .Single()
            .Document.Sets[0], "bottom"));
    }

    /// <summary>⚠ A layer inside a group is not offered its own group, which is a cycle.</summary>
    /// <remarks>
    ///     <para>
    ///         <b><a href="https://github.com/Rikarin/Vixen/issues/980">#980</a>: the two tests above
    ///         run on flat stacks, and their own remark says a group is where composite order and row
    ///         order differ.</b> On a flat stack the two <em>are</em> the same list, so neither could
    ///         tell the post-order walk from any other ordering, and <c>Anchorable</c>'s group
    ///         handling — the only non-obvious part of it — was unasserted.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The distinguishing option is the group itself, and nothing else in this fixture
    ///         is.</b> <c>LayerStackGraph.Stack</c> composites a group's children <em>inside</em> the
    ///         group's own composite, so the group's blend node exists only after every child's — a
    ///         child reading it is a loop. A walk that emitted a parent before recursing into it, which
    ///         is the obvious way to write this, offers <c>'g'</c> to <c>'child'</c>; the panel's own
    ///         top-to-bottom row order offers the same set as the correct answer here, so the group is
    ///         the one option that separates right from wrong.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And the group <em>is</em> offered its own children, which reads backwards and is
    ///         the same fact.</b> Its blend node is emitted last, so anchoring onto a child of its own
    ///         is no cycle at all.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_layer_inside_a_group_is_not_offered_its_own_group() {
        using var fixture = new TexturingFixture();
        var document = Open(fixture, AnchoredInsideAGroup());
        var set = document.Document.Sets[0];

        // The only anchored row in the fixture, so the only picker with anything in it — found that
        // way rather than by index, because "which row is the child's" is what the walk decides.
        var picker = Assert.Single(
            Controls<Select>(Panel(fixture), "layer-stack-mask-anchor"),
            one => one.Options.Any()
        );

        var offered = picker.Options.Select(option => option.Value ?? "").ToArray();

        // Not 'g', which contains it; not 'top', which is composited after the whole group.
        Assert.Equal([LayerStackView.NoAnchor, "bottom", "inner"], offered);
        Assert.Equal("inner", picker.Value);

        // The reverse reading, off the model: the group may anchor onto the children it holds.
        Assert.Equal(["bottom", "inner", "child"], LayerStackView.Anchorable(set, "g"));

        // And the layer above the whole group is offered every one of them.
        Assert.Equal(["bottom", "inner", "child", "g"], LayerStackView.Anchorable(set, "top"));
    }

    /// <summary>⚠ And a group is where composite order and row order actually differ.</summary>
    /// <remarks>
    ///     <para>
    ///         <b><a href="https://github.com/Rikarin/Vixen/issues/980">#980</a>: the two tests above
    ///         run on flat stacks, where the two orders are the same thing.</b> So neither could
    ///         distinguish the post-order walk from any other ordering, and the group handling —
    ///         which is the only part of <c>Anchorable</c> that is not obvious — was unasserted while
    ///         its own remark named it as the case that mattered.
    ///     </para>
    ///     <para>
    ///         <b>Four ids over one stack, because the walk is only pinned by the whole set.</b> A
    ///         group's blend is emitted after its children's, so the group may read them and they may
    ///         not read it: the first child is offered nothing from inside its own group, the second
    ///         is offered its sibling and still not the parent, the group is offered both children,
    ///         and the layer above the group is offered all four. ⚠ A walk that visited a layer
    ///         before its children — the arrangement a panel's rows suggest — answers the first two
    ///         differently and the last two identically, which is why the children are the assertion.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Against <c>Anchorable</c> rather than through the pickers</b>, because the panel
    ///         is what the test above it already drives; what is unasserted here is the walk, and a
    ///         nested row's <c>Select</c> is found by counting controls, which would make the fixture
    ///         about the row order this test exists to say is not the answer.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_layer_inside_a_group_anchors_in_composite_order_and_not_row_order() {
        using var fixture = new TexturingFixture();

        var set = Open(fixture, AnchoredAroundAGroup()).Document.Sets[0];

        // Nothing of its own group: not its sibling, which is composited after it, and not the group
        // itself, which is composited after both of them.
        Assert.Equal(["bottom"], LayerStackView.Anchorable(set, "lower"));

        // Its sibling, and still not the group.
        Assert.Equal(["bottom", "lower"], LayerStackView.Anchorable(set, "upper"));

        // And the group reads what it contains, which is the half a row-order picker gets backwards.
        Assert.Equal(["bottom", "lower", "upper"], LayerStackView.Anchorable(set, "g"));
        Assert.Equal(["bottom", "lower", "upper", "g"], LayerStackView.Anchorable(set, "top"));
    }

    /// <summary>⚠ An anchor picker walks the set once, however often its row is refreshed.</summary>
    /// <remarks>
    ///     <para>
    ///         <b><a href="https://github.com/Rikarin/Vixen/issues/979">#979</a>.</b> The picker
    ///         cached what it last offered — and the cache guarded only <c>ClearOptions</c>, while the
    ///         walk that <em>produces</em> the options ran first and built the key it was compared on.
    ///         So every anchor-masked row walked the whole layer tree and allocated three collections
    ///         on every refresh, and a refresh is once per frame of an opacity drag.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Both halves, because a picker that stopped building options at all would leave the
    ///         count at zero and read as a perfect result.</b> The options are asserted after the
    ///         refreshes, so "the work was not repeated" is a claim about work that happened.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A count of work rather than a duration.</b> A wall-clock budget calibrated on an
    ///         idle laptop is this repository's largest flake source; what the fix claims is a number
    ///         of walks, so that is what is read.
    ///     </para>
    /// </remarks>
    [Fact]
    public void An_anchor_picker_walks_the_set_once_however_often_the_row_refreshes() {
        using var fixture = new TexturingFixture();
        var (view, document) = Viewed(fixture, Anchored());

        Assert.Equal(1, view.AnchorWalks);

        // What an opacity drag does: the document is unchanged, so the rows are not rebuilt and every
        // binding re-reads. Ten frames of one gesture.
        for (var frame = 0; frame < 10; frame++) {
            view.Show(document);
        }

        Assert.Equal(1, view.AnchorWalks);

        var picker = Assert.Single(
            Controls<Select>(view.Root, "layer-stack-mask-anchor"),
            one => one.Options.Any()
        );

        Assert.Equal(
            [LayerStackView.NoAnchor, "bottom"],
            picker.Options.Select(option => option.Value ?? "").ToArray()
        );

        Assert.Equal("bottom", picker.Value);
    }

    /// <summary>⚠ A refresh keeps the artist's zoom, and a different stack is framed afresh.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The second half of <a href="https://github.com/Rikarin/Vixen/issues/979">#979</a>,
    ///         and it is <a href="https://github.com/Rikarin/Vixen/issues/957">#957</a>'s defect in
    ///         the panel #957 did not touch.</b> <c>Show</c> ended in a bare <c>Preview.Fit()</c>,
    ///         which overwrites <c>Zoom</c> and <c>Pan</c> outright — and <c>Show</c> runs on every
    ///         edit, so an artist who had zoomed into a corner of the map to see what an opacity drag
    ///         did lost it on the first frame of the drag.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Both halves, because "never fit" passes the first one on its own</b> — and never
    ///         fitting is the worse defect: <c>Fit</c> answers false before the first layout, which is
    ///         when a panel's first <c>Show</c> runs, so a view that framed once and gave up would
    ///         open every stack at whatever zoom nothing set. The zoom the artist is given is
    ///         deliberately not the fitted one, so "it was left alone" is a statement about a number
    ///         rather than a coincidence.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Refreshing_the_panel_keeps_the_zoom_and_showing_another_stack_refits() {
        using var fixture = new TexturingFixture();
        var (view, document) = Viewed(fixture, Two());

        // ⚠ The instrument, and it is the failure this fix could have introduced. `Viewed`'s own
        // first `Show` runs before the panel is laid out, so `Fit` answered false and framed nothing;
        // the `Show` inside it after the layout is the retry. A view that gave up leaves these equal.
        var unframed = view.Preview.Zoom;

        view.Show(document);

        var framed = view.Preview.Zoom;

        Assert.True(
            framed > 0f && framed != unframed,
            $"the preview was never framed — it is still at {unframed}, which is the zoom nothing set"
        );

        view.Preview.Zoom = framed * 4f;
        view.Preview.Pan = new(11f, 13f);

        // What an edit does: the same stack, recompiled.
        view.Show(document);

        Assert.Equal(framed * 4f, view.Preview.Zoom);
        Assert.Equal(new(11f, 13f), view.Preview.Pan);

        // A different stack is a different picture, and is framed.
        view.Show(Another(fixture, "Tiles", Two()));

        Assert.Equal(framed, view.Preview.Zoom);
    }

    /// <summary>⚠ Choosing a texture set changes every control on the panel, not only the list.</summary>
    /// <remarks>
    ///     <para>
    ///         <b><a href="https://github.com/Rikarin/Vixen/issues/927">#927</a>: seven places took
    ///         <c>Sets[0]</c> and it is one decision rather than seven edits.</b> Sets are
    ///         independent — a set is a material slot with its own atlas, its own channels and its own
    ///         <c>.vxpaint</c> files — so the editor works on one at a time, chosen once and read by
    ///         everything. Four of the seven are this panel's and are what this reads.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The two sets differ in <em>everything</em> the panel reads, which is the issue's
    ///         own warning.</b> A fixture whose sets carry the same channels and the same layer ids is
    ///         passed by a panel still pinned to the first — the shape of a test that cannot see its
    ///         subject, which this workstream has shipped twice. So the second set has a different
    ///         channel list, a different layer id and a different <c>Mesh</c>, and all three are read.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The layer ids differing is also what the row-shape comparison needs.</b>
    ///         <c>Show</c> keeps the rows when the shape is unchanged, and two sets copied from one
    ///         another have an identical shape — so the set's own name is in it, and a stack whose two
    ///         sets really were identical would still swap correctly.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Choosing_a_set_changes_the_rows_the_ticks_and_the_part_picker() {
        using var fixture = new TexturingFixture();

        var document = Open(fixture, TwoSets());

        var panel = Panel(fixture);

        // The state before, so that every assertion after it is about a change.
        Assert.Contains(Texts(panel, "layer-stack-row-name"), row => row.Contains("Body", StringComparison.Ordinal));
        Assert.Single(Ticks(panel, "layer-stack-channel"));
        Assert.Equal(LayerStackView.EveryMesh, Find<Select>(panel, "layer-stack-set-mesh").Value);
        Assert.Empty(Texts(panel, "layer-stack-row-refusal"));

        Find<Select>(panel, "layer-stack-set").Value = "Head";

        panel = Panel(fixture);

        Assert.Contains(Texts(panel, "layer-stack-row-name"), row => row.Contains("Head", StringComparison.Ordinal));

        Assert.DoesNotContain(
            Texts(panel, "layer-stack-row-name"),
            row => row.Contains("Body", StringComparison.Ordinal)
        );

        Assert.Equal(2, Ticks(panel, "layer-stack-channel").Count);
        Assert.Equal("head", Find<Select>(panel, "layer-stack-set-mesh").Value);

        // ⚠ And the brush went with it — #927, which this assertion used to say the opposite of. A
        // row of the second set was disarmed with `LayerStackView.OtherSet` under it, because
        // `PaintSurface.Open` took `Sets[0]` whatever the panel showed. The choice is now
        // `LayerStackDocument.PaintSet` and the pane resolves it, so the row selects and the stroke
        // lands in the set on the screen. `PaintSurfaceTests` is where the stroke's half is proved;
        // this is the panel's.
        Assert.False(Buttons(panel, "layer-stack-select")[0].Disabled);
        Assert.Empty(Texts(panel, "layer-stack-row-refusal"));
        Assert.Equal("Head", document.PaintSet);
    }

    /// <summary>⚠ And two sets of identical shape still swap, which the test above cannot see.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The rows are rebuilt only when their <em>shape</em> changed</b> — a correctness
    ///         property, because rebuilding unconditionally destroys the control an artist is holding
    ///         — and two sets copied from one another have the same channels, the same layer ids and
    ///         the same mask counts. So the shape string carries the set's own name, and without it
    ///         the panel would keep the rows and leave every control editing the set the artist
    ///         navigated away from.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>What differs here is a layer's <em>name</em>, which is deliberately not in the
    ///         shape.</b> The shape is identity and structure only; a name is a value, read by the
    ///         row's binding. That is what makes this fixture indistinguishable to everything except
    ///         the term under test — the sabotage that removes the set name from the shape leaves
    ///         <c>Choosing_a_set_changes_the_rows_the_ticks_and_the_part_picker</c> green and this
    ///         one red.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Two_sets_of_identical_shape_still_swap_their_rows() {
        using var fixture = new TexturingFixture();

        Open(fixture, TwinSets());

        var panel = Panel(fixture);

        Assert.Contains("Body layer", Assert.Single(Texts(panel, "layer-stack-row-name")), StringComparison.Ordinal);

        Find<Select>(panel, "layer-stack-set").Value = "Head";

        Assert.Contains(
            "Head layer",
            Assert.Single(Texts(Panel(fixture), "layer-stack-row-name")),
            StringComparison.Ordinal
        );
    }

    /// <summary>⚠ Adding a layer changes the compiled composite, and undo takes it back out.</summary>
    /// <remarks>
    ///     <para>
    ///         <b><a href="https://github.com/Rikarin/Vixen/issues/882">#882</a>'s remaining half.</b>
    ///         The panel could edit every property of a layer and could not make one — no add, no
    ///         delete, for layers or for mask rows.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Read off the plan and not off the list, because a list assertion is a restatement
    ///         of the command's own arithmetic</b> — the rule the reorder tests in this file already
    ///         keep to. It is also the only assertion that can see the second half of the fix: a fill
    ///         added with an empty <c>Values</c> is a layer that writes <em>nothing</em>
    ///         (<a href="https://github.com/Rikarin/Vixen/issues/807">#807</a> · 2), so the row would
    ///         appear, the picture would not move, and a list assertion would call that a pass.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Adding_a_layer_puts_it_on_top_and_undo_takes_it_out() {
        using var fixture = new TexturingFixture();
        var document = Open(fixture, Two());
        var panel = Panel(fixture);

        Assert.Equal(0.75f, TopColour(document));

        Find<Button>(panel, "layer-stack-add").Activate();

        Assert.Equal(3, document.Document.Sets[0].Layers.Count);

        // The new layer is the one the last blend now reads, which is what "on top" means.
        Assert.Equal(0.5f, TopColour(document));

        Assert.True(document.Stack.Undo());
        Assert.Equal(2, document.Document.Sets[0].Layers.Count);
        Assert.Equal(0.75f, TopColour(document));
    }

    /// <summary>⚠ A layer added while a child of a group is selected goes into that group.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>What <see cref="LayerSlot" /> exists for.</b> A layer's parent list is not always
    ///         the set's — <c>LayerStackEdit</c>'s own opening remark — so a button that appended to
    ///         <c>TextureSetAsset.Layers</c> would make a group something an artist can open, reorder
    ///         inside and never add to.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The selected child is the <em>lower</em> one, so "over the selected layer" and
    ///         "at the end of the list" are different places.</b> Selecting the upper child makes the
    ///         two the same index and the fixture stops being able to tell them apart — which is the
    ///         shape of a test that cannot see its own subject.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_layer_added_under_a_selected_child_goes_into_that_childs_group() {
        using var fixture = new TexturingFixture();
        var document = Open(fixture, Grouped());
        var panel = Panel(fixture);

        // Rows are topmost first: the group, then its children topmost first, so [2] is 'lower'.
        Buttons(panel, "layer-stack-select")[2].Activate();
        Find<Button>(panel, "layer-stack-add").Activate();

        var group = Assert.Single(document.Document.Sets[0].Layers);
        var children = group.Children.Select(child => child.Id).ToArray();

        Assert.Equal(3, children.Length);
        Assert.Equal("lower", children[0]);
        Assert.Equal("upper", children[2]);

        // Over the selected layer and under the one that was above it — not at either end.
        Assert.Equal("layer-1", children[1]);
    }

    /// <summary>⚠ Deleting a group takes its children, and undo gives the whole subtree back.</summary>
    /// <remarks>
    ///     ⚠ <b>The children are what the assertion is about.</b> A removal that recorded only the
    ///     layer's own members would put back a group with nothing in it, and the set's own list would
    ///     look identical — one layer called <c>'g'</c>, in the same place.
    /// </remarks>
    [Fact]
    public void Deleting_a_group_takes_its_children_and_undo_gives_them_back() {
        using var fixture = new TexturingFixture();
        var document = Open(fixture, Grouped());
        var panel = Panel(fixture);

        // The group's own row is the topmost, so its Delete is the first.
        Buttons(panel, "layer-stack-delete")[0].Activate();

        Assert.Empty(document.Document.Sets[0].Layers);
        Assert.True(document.Stack.Undo());

        var group = Assert.Single(document.Document.Sets[0].Layers);

        Assert.Equal("g", group.Id);
        Assert.Equal(["lower", "upper"], group.Children.Select(child => child.Id).ToArray());
    }

    /// <summary>⚠ Two layers added in a row get two ids, and neither row is disarmed.</summary>
    /// <remarks>
    ///     ⚠ <b><c>LayerAsset.Id</c> defaults to empty and two empties are ambiguous</b>
    ///     (<a href="https://github.com/Rikarin/Vixen/issues/893">#893</a>), so an add that left the
    ///     id alone would work once and turn both new rows into refusals on the second press — and
    ///     refuse the stack at compile. The refusal count is read off the tree rather than the ids
    ///     compared, because that is the state an artist would be looking at.
    /// </remarks>
    [Fact]
    public void Two_added_layers_get_two_ids() {
        using var fixture = new TexturingFixture();
        var document = Open(fixture, Two());
        var panel = Panel(fixture);

        Find<Button>(panel, "layer-stack-add").Activate();
        Find<Button>(panel, "layer-stack-add").Activate();

        var ids = document.Document.Sets[0].Layers.Select(layer => layer.Id).ToArray();

        Assert.Equal(4, ids.Length);
        Assert.Equal(4, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.Empty(Texts(panel, "layer-stack-row-refusal"));
    }

    /// <summary>⚠ An added mask entry multiplies, so the mask it joined does not change.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b><c>MaskLayerAsset.Blend</c> defaults to <c>Copy</c>, and <c>Copy</c> at a value of
    ///         1 replaces the whole mask with white.</b> So an add that took the record's default
    ///         would make an artist's bake mask vanish on a click that was meant to be additive. What
    ///         is asserted is the operator rather than the picture because the picture is the operator
    ///         — <c>Multiply</c> at 1 is the identity, which is what an unconfigured row should be.
    ///     </para>
    ///     <para>
    ///         And the delete beside it takes the same row out again, which is the half that makes the
    ///         add safe to press.
    ///     </para>
    /// </remarks>
    [Fact]
    public void An_added_mask_entry_is_neutral_and_the_delete_takes_it_back_out() {
        using var fixture = new TexturingFixture();
        var document = Open(fixture, Masked());
        var panel = Panel(fixture);

        Assert.Single(Mask(document).Layers);

        Find<Button>(panel, "layer-stack-mask-add-entry").Activate();

        var added = Mask(document).Layers[^1];

        Assert.Equal(2, Mask(document).Layers.Count);
        Assert.Equal(LayerBlendMode.Multiply, added.Blend);
        Assert.Equal(1f, added.Value);

        // The rows are outermost first, so the first Delete is the entry that was just added.
        Buttons(Panel(fixture), "layer-stack-entry-delete")[0].Activate();

        Assert.Single(Mask(document).Layers);
    }

    /// <summary>⚠ An added mask effect is switched off, so the map an artist was looking at survives.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>An effect that names no node type is a <em>refusal</em> in
    ///         <c>LayerStackGraph</c></b> — an error, which stops the map — and an effect with no node
    ///         is exactly what pressing the button makes. Added enabled, the click would blank the
    ///         preview. The compile is what says so: the plan is still there afterwards.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And the field is the other half, without which the button is a mechanism whose
    ///         caller can only pass the default.</b> An effect row with no way to name a node is a row
    ///         an artist can create and can never make mean anything, which is this workstream's
    ///         commonest defect wearing a hat.
    ///     </para>
    /// </remarks>
    [Fact]
    public void An_added_mask_effect_is_off_until_it_names_a_node() {
        using var fixture = new TexturingFixture();
        var document = Open(fixture, Masked());

        Find<Button>(Panel(fixture), "layer-stack-mask-add-effect").Activate();

        var added = Assert.Single(Mask(document).Effects);

        Assert.False(added.Enabled);
        Assert.Equal("", added.Node);

        // The instrument: a stack whose effect was added enabled compiles to nothing at all.
        Assert.NotNull(LayerStackCompiler.Compile(document.Document, document.Document.Sets[0]).Plan);

        Find<TextBox>(Panel(fixture), "layer-stack-effect-node").Value = "Colour/Levels";

        Assert.Equal("Colour/Levels", Assert.Single(Mask(document).Effects).Node);

        Buttons(Panel(fixture), "layer-stack-effect-delete")[0].Activate();

        Assert.Empty(Mask(document).Effects);
    }

    /// <summary>⚠ An id-less layer's row refuses to be selected, and says why.</summary>
    /// <remarks>
    ///     <para>
    ///         <b><a href="https://github.com/Rikarin/Vixen/issues/966">#966</a>.</b>
    ///         <c>PaintTool.LayerId</c> being empty already means <em>the first paint layer in
    ///         composite order</em> — <c>PaintSurface.Find</c> returns on <c>layerId.Length == 0</c>
    ///         before comparing anything — so clicking an id-less row wrote a value indistinguishable
    ///         from having selected nothing, and on this stack the brush would then aim at
    ///         <c>'lower'</c> while the artist believed they had picked the row above it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Only the select button, and the rest of the row still edits.</b> A single id-less
    ///         layer addresses perfectly well and the compiler accepts it, so disarming the whole row
    ///         the way an ambiguous id does would make a one-layer file uneditable — that is the line
    ///         between this and <a href="https://github.com/Rikarin/Vixen/issues/893">#893</a>.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The named row is activated too, and that is the instrument.</b> A view that had
    ///         simply stopped selecting anything passes every assertion above on its own.
    ///     </para>
    /// </remarks>
    [Fact]
    public void An_id_less_layers_row_refuses_selection_rather_than_aiming_the_brush_elsewhere() {
        using var fixture = new TexturingFixture();
        using UiDocument ui = new(1280f, 800f);

        var document = Open(fixture, TwoPaintLayers());
        PaintTool tool = new();
        LayerStackView view = new(ui.Root, tool);

        view.Show(document);

        // Topmost first, so row 0 is the id-less layer and row 1 is 'lower'.
        var selects = Buttons(view.Root, "layer-stack-select");

        Assert.True(selects[0].Disabled);
        Assert.False(selects[1].Disabled);
        Assert.Contains(LayerStackView.Unnamed, Texts(view.Root, "layer-stack-row-refusal"));

        // ⚠ Activated rather than only inspected, because `Disabled` is a style on some controls and
        // the refusal on others: `ToggleBase.Activate` flips first and asks afterwards. `Button` does
        // not, and this is what says so — a `Disabled` that were decoration leaves this line selecting
        // the row.
        selects[0].Activate();

        Assert.Null(view.Selected);
        Assert.Equal("", tool.LayerId);

        selects[1].Activate();

        Assert.Equal("lower", view.Selected?.Id);
        Assert.Equal("lower", tool.LayerId);
    }

    /// <summary>A fill's colour can be changed from the panel, per channel, and undone.</summary>
    /// <remarks>
    ///     <para>
    ///         <b><a href="https://github.com/Rikarin/Vixen/issues/986">#986</a>.</b> Every other
    ///         property of a layer was editable here and the two that decide what a fill actually
    ///         puts on the surface were not, so a fill added from the panel kept the mid-grey it was
    ///         born with until somebody opened the file in a text editor.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The second channel is what makes this an assertion about the keying.</b>
    ///         <c>Values</c> is a dictionary per usage, so a row that wrote the whole layer's colour
    ///         — or that wrote the first entry whatever row was dragged — would pass on a
    ///         one-channel stack and be wrong on every real one.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_fill_channels_colour_is_edited_a_component_at_a_time() {
        using var fixture = new TexturingFixture();
        var document = Open(fixture, TwoChannels());
        var panel = Panel(fixture);

        var greens = Fields(panel, "layer-stack-fill-green");

        // One row per channel of the set, in the set's own order.
        Assert.Equal(2, greens.Count);
        Assert.Equal(0.25d, greens[0].Number);
        Assert.Equal(0.5d, greens[1].Number);

        greens[1].Number = 0.125d;

        Assert.Equal([0.5f, 0.125f, 0.5f, 1f], Only(document).Values["roughness"]);

        // ⚠ And the channel nobody touched is untouched — the dictionary is rewritten, not rebuilt.
        Assert.Equal([0.25f, 0.25f, 0.25f, 1f], Only(document).Values["baseColor"]);

        Assert.True(document.Stack.Undo());
        fixture.Shell.Document.Effects.Flush();

        Assert.Equal([0.5f, 0.5f, 0.5f, 1f], Only(document).Values["roughness"]);
        Assert.Equal(0.5d, Fields(panel, "layer-stack-fill-green")[1].Number);
    }

    /// <summary>⚠ A colour the file holds outside 0…1 survives a drag of another component.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>What the file holds reaches the panel and comes back out of it unchanged.</b> A
    ///         colour above 1 was a value this panel could hold and not author while the four
    ///         components were 0…1 sliders; it is now a value it can do both with, and this is the
    ///         half that says the round trip does not round.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It is no longer the test that refuses a gathering row, and the claim that it was
    ///         is refuted</b> — <a href="https://github.com/Rikarin/Vixen/issues/1004">#1004</a> says
    ///         "gathering the sliders turns the 4 into a 1 with nothing else in the suite noticing",
    ///         which was true of a <em>slider</em> and is false of a field.
    ///         <c>NumericInput.Number</c> holds what it was given at full precision —
    ///         <c>Decimals</c> rounds the <em>text</em> and not the number — so a row that gathered
    ///         its four fields would write back exactly what it read, and this test passes against
    ///         that row. Sabotage-checked: replacing the per-component write with a gather of the
    ///         four leaves all 46 tests in this suite green.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Which is why the per-component write is now held by
    ///         <see cref="A_refused_component_is_not_written_by_an_edit_to_its_neighbour" />
    ///         instead.</b> A gather is still wrong, for a reason the clamp used to hide: a field
    ///         that is <em>refusing</em> what it holds has a number, and gathering it writes into the
    ///         document exactly the value the refusal exists to keep out.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_component_above_one_survives_a_drag_of_its_neighbour() {
        using var fixture = new TexturingFixture();

        var document = Open(
            fixture,
            Stack(
                [new() { Usage = "baseColor", Default = [0f, 0f, 0f, 1f] }],
                new LayerAsset {
                    Id = "l",
                    Name = "Bright",
                    Kind = LayerKind.Fill,
                    Values = { ["baseColor"] = [4f, 0.25f, 0.25f, 1f] }
                }
            )
        );

        var panel = Panel(fixture);

        // The instrument: the 4 reaches the field intact rather than arriving clamped, which is what
        // #1004 bought and what a 0…1 control could not do.
        Assert.Equal(4d, Fields(panel, "layer-stack-fill-red")[0].Number);

        Fields(panel, "layer-stack-fill-green")[0].Number = 0.75d;

        Assert.Equal([4f, 0.75f, 0.25f, 1f], Only(document).Values["baseColor"]);
    }

    /// <summary>⚠ A component the field is refusing is not written by an edit to another one.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The per-component write, now that a clamp no longer stands in for it.</b> A row
    ///         that gathered its four fields would read <c>Number</c> off a field whose value the
    ///         row itself has just refused — <see cref="A_negative_component_is_refused_rather_than_written" />
    ///         — and write it into the document through the neighbour's edit. The refusal would then
    ///         hold for exactly as long as nobody touched the row again, which is a gate that fails
    ///         open.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The assertion is on red, which was never dragged.</b> Green is supposed to change
    ///         and cannot tell the two designs apart; and both numbers have to be in the document at
    ///         once, because a test that only asserted red would also pass against a row that wrote
    ///         nothing at all.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_refused_component_is_not_written_by_an_edit_to_its_neighbour() {
        using var fixture = new TexturingFixture();

        var document = Open(
            fixture,
            Stack(
                [new() { Usage = "baseColor", Default = [0f, 0f, 0f, 1f] }],
                new LayerAsset {
                    Id = "l",
                    Name = "Bright",
                    Kind = LayerKind.Fill,
                    Values = { ["baseColor"] = [0.5f, 0.25f, 0.25f, 1f] }
                }
            )
        );

        var panel = Panel(fixture);
        var red = Fields(panel, "layer-stack-fill-red")[0];

        red.Number = -1d;

        // The instrument: the row really is refusing this one, so a gathering row would really have
        // a negative to write.
        Assert.False(red.IsValid);
        Assert.Equal(-1d, red.Number);

        Fields(panel, "layer-stack-fill-green")[0].Number = 0.75d;

        Assert.Equal([0.5f, 0.75f, 0.25f, 1f], Only(document).Values["baseColor"]);
    }

    /// <summary>⚠ And a component above 1 can now be <em>authored</em>, which is #1004 itself.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The renderer works in cd/m² and an emissive fill of 4 is an ordinary thing for a
    ///         <c>.vxlayers</c> to hold</b> — so a panel whose only control was a 0…1 slider could
    ///         hold that value and could not produce it, and the file had to be opened in a text
    ///         editor. <a href="https://github.com/Rikarin/Vixen/issues/1004">#1004</a>.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The starting value is inside the range the old control could reach, which is
    ///         what makes this red under it.</b> A fixture that already held a 4 would pass against
    ///         a slider too, because the assertion would then be about the value surviving rather
    ///         than about the value being writable. Writing 4 into a control whose maximum is 1
    ///         leaves it at 1, and the document then holds 1.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And the neighbours are asserted unchanged</b>, so a row that answered by
    ///         widening the write rather than the control cannot pass this either.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_component_above_one_can_be_authored() {
        using var fixture = new TexturingFixture();

        var document = Open(
            fixture,
            Stack(
                [new() { Usage = "emissive", Default = [0f, 0f, 0f, 1f] }],
                new LayerAsset {
                    Id = "l",
                    Name = "Lamp",
                    Kind = LayerKind.Fill,
                    Values = { ["emissive"] = [0.5f, 0.25f, 0.25f, 1f] }
                }
            )
        );

        var panel = Panel(fixture);

        Fields(panel, "layer-stack-fill-red")[0].Number = 4d;

        Assert.Equal([4f, 0.25f, 0.25f, 1f], Only(document).Values["emissive"]);

        // And the file's number comes back to the panel rather than being shown clamped, which is
        // the half a write-only widening would fail.
        Assert.True(document.Stack.Undo());
        fixture.Shell.Document.Effects.Flush();

        Assert.Equal(0.5d, Fields(panel, "layer-stack-fill-red")[0].Number);
    }

    /// <summary>⚠ A negative component is shown, refused, and not written.</summary>
    /// <remarks>
    ///     <b>The floor the field keeps now that it has no ceiling.</b> <c>NumericInput</c> holds and
    ///     reports an out-of-range number rather than clamping it, so the row has to decide what to
    ///     do with one — and a negative radiance is not a quantity. It stays in the field where the
    ///     person can see it and correct it, exactly as <c>PropertyGrid</c>'s numeric rows do, and
    ///     the document keeps the last value that was allowed. ⚠ Without the <c>IsValid</c> gate the
    ///     panel writes the negative straight into the <c>.vxlayers</c>.
    /// </remarks>
    [Fact]
    public void A_negative_component_is_refused_rather_than_written() {
        using var fixture = new TexturingFixture();

        var document = Open(
            fixture,
            Stack(
                [new() { Usage = "baseColor", Default = [0f, 0f, 0f, 1f] }],
                new LayerAsset {
                    Id = "l",
                    Name = "Base",
                    Kind = LayerKind.Fill,
                    Values = { ["baseColor"] = [0.5f, 0.25f, 0.25f, 1f] }
                }
            )
        );

        var panel = Panel(fixture);
        var red = Fields(panel, "layer-stack-fill-red")[0];

        red.Number = -1d;

        Assert.False(red.IsValid);
        Assert.Equal([0.5f, 0.25f, 0.25f, 1f], Only(document).Values["baseColor"]);
    }

    /// <summary>A channel a fill says nothing about can be given a colour, and told to stop.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Presence in <c>Values</c> is a second question from the channel tick, and both
    ///         have to be true.</b> A layer restricting no channels writes all of them
    ///         (<a href="https://github.com/Rikarin/Vixen/issues/807">#807</a> · 2) and an absent
    ///         entry is then the only way a fill says "nothing about this channel" — so the panel has
    ///         to be able to add one and take it away, or half the states a <c>.vxlayers</c> can hold
    ///         are states it cannot reach.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And what it starts from is the channel's own default</b>, which is exactly the
    ///         number #807 · 2 refused as a <em>fallback</em>: read when there is no entry it invents
    ///         a layer's opinion, offered as the first draft of an entry the artist just asked for it
    ///         is the set's own answer.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_channel_with_nothing_to_say_can_be_given_a_colour_and_take_it_back() {
        using var fixture = new TexturingFixture();

        var document = Open(
            fixture,
            Stack(
                [
                    new() { Usage = "baseColor", Default = [0f, 0f, 0f, 1f] },
                    new() { Usage = "roughness", Default = [0.9f, 0.9f, 0.9f, 1f] }
                ],
                Fill("l", "Layer", 0.25f)
            )
        );

        var panel = Panel(fixture);
        var ticks = Controls<CheckBox>(panel, "layer-stack-fill-writes");

        Assert.Equal(2, ticks.Count);
        Assert.True(ticks[0].IsChecked);
        Assert.False(ticks[1].IsChecked);
        Assert.DoesNotContain("roughness", Only(document).Values.Keys);

        ticks[1].Activate();

        Assert.Equal([0.9f, 0.9f, 0.9f, 1f], Only(document).Values["roughness"]);

        Controls<CheckBox>(panel, "layer-stack-fill-writes")[1].Activate();

        Assert.DoesNotContain("roughness", Only(document).Values.Keys);
        Assert.Equal(2, document.Stack.Depth.Value);
    }

    /// <summary>A texture fill names its image per channel, through the same rows.</summary>
    /// <remarks>
    ///     ⚠ <b>The picker is here too, because otherwise the field below it is unreachable.</b>
    ///     Nothing else in this panel could set <c>LayerAsset.Fill</c>, so an image field offered
    ///     only on a texture fill would be a control no route in the editor arrives at — the defect
    ///     this workstream produces more than any other, built on purpose.
    /// </remarks>
    [Fact]
    public void A_texture_fill_names_an_image_for_the_channel_it_writes() {
        using var fixture = new TexturingFixture();
        var document = Open(fixture, TwoChannels());
        var panel = Panel(fixture);
        var source = Find<Select>(panel, "layer-stack-fill-source");

        Assert.Equal(nameof(LayerFillSource.Constant), source.Value);

        source.Value = nameof(LayerFillSource.Texture);

        Assert.Equal(LayerFillSource.Texture, Only(document).Fill);

        Controls<TextBox>(panel, "layer-stack-fill-texture")[0].Value = "Assets/Rust.png";

        Assert.Equal("Assets/Rust.png", Only(document).Textures["baseColor"]);

        // ⚠ Emptied is removed rather than stored as "", because the compiler answers a texture fill
        // naming no image with a refusal either way — a key with an empty string behind it is a
        // state the file can hold and nothing can mean.
        Controls<TextBox>(panel, "layer-stack-fill-texture")[0].Value = "";

        Assert.Empty(Only(document).Textures);
    }

    /// <summary>⚠ A row whose id names two layers is listed and carries no controls.</summary>
    /// <remarks>
    ///     <para>
    ///         <b><a href="https://github.com/Rikarin/Vixen/issues/893">#893</a>'s panel half.</b>
    ///         <c>LayerStackGraph.Duplicates</c> refuses the stack, and a refusal is a message beside
    ///         a list of rows that are still drawn and still clicked — the panel builds its rows from
    ///         the document rather than from a compilation. Against the code before this,
    ///         <c>Buttons(panel, "layer-stack-move-up")[0].Activate()</c> moved the <em>other</em>
    ///         layer, because <c>LayerStackEdit</c> resolves an id to the first match.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Both rows are still there, which is half the assertion.</b> Refusing by dropping
    ///         the row would leave an artist with a file whose shape they cannot see, and the shape
    ///         is the thing they have to fix.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_row_whose_id_names_two_layers_is_listed_without_controls() {
        using var fixture = new TexturingFixture();

        Open(fixture, Shared());

        var panel = Panel(fixture);

        Assert.Equal(2, Texts(panel, "layer-stack-row-name").Count);

        // Not one button, one tick, one slider or one selector between them: every control on this
        // row is addressed by the id that names both layers.
        Assert.Empty(All(panel, "layer-stack-move-up"));
        Assert.Empty(All(panel, "layer-stack-move-down"));
        Assert.Empty(All(panel, "layer-stack-enabled"));
        Assert.Empty(All(panel, "layer-stack-opacity"));
        Assert.Empty(All(panel, "layer-stack-blend"));
        Assert.Empty(All(panel, "layer-stack-select"));

        var refusals = Texts(panel, "layer-stack-row-refusal");

        Assert.Equal(2, refusals.Count);
        Assert.Contains("dup", refusals[0], StringComparison.Ordinal);
        Assert.Equal(LayerStackView.Ambiguity("dup"), refusals[0]);
    }

    /// <summary>⚠ And the same panel over a stack with distinct ids is fully editable.</summary>
    /// <remarks>
    ///     <b>The half that makes the assertion above a finding rather than a description of a panel
    ///     that draws no controls at all.</b> Every <c>Assert.Empty</c> above is satisfied by a build
    ///     in which the rows were never populated, which is what a refusal written one line too high
    ///     in <c>Build</c> would produce.
    /// </remarks>
    [Fact]
    public void A_stack_with_distinct_ids_keeps_every_control() {
        using var fixture = new TexturingFixture();
        var document = Open(fixture, Two());
        var panel = Panel(fixture);

        Assert.Equal(2, Buttons(panel, "layer-stack-move-up").Count);
        Assert.Empty(Texts(panel, "layer-stack-row-refusal"));

        Buttons(panel, "layer-stack-move-up")[1].Activate();

        Assert.Equal(0.25f, TopColour(document));
    }

    /// <summary>⚠ The panel disarms exactly the ids the compiler refuses, and no others.</summary>
    /// <remarks>
    ///     <b>The two are one rule — <c>LayerStackEdit.Ambiguous</c> — and this is what says so.</b>
    ///     A panel with its own copy of the rule can drift into either failure: offering to reorder a
    ///     layer the compiler will not build, or disarming a row of a stack that compiles. The
    ///     third layer here has an id of its own, so the set contains both answers at once.
    /// </remarks>
    [Fact]
    public void The_disarmed_rows_are_the_ones_the_compiler_refuses() {
        using var fixture = new TexturingFixture();
        var document = Open(fixture, Shared("solo"));
        var panel = Panel(fixture);

        var compilation = LayerStackCompiler.Compile(document.Document, document.Document.Sets[0]);

        Assert.Null(compilation.Plan);

        var refused = compilation.Problems
            .Where(problem => problem.Message.Contains("share the id", StringComparison.Ordinal))
            .Select(problem => problem.Layer)
            .ToArray();

        Assert.Equal(["dup"], refused);

        // Three rows, one of which is the layer nothing shares an id with — and it is the only one
        // with a `Move up` on it.
        Assert.Equal(3, Texts(panel, "layer-stack-row-name").Count);
        Assert.Equal(2, Texts(panel, "layer-stack-row-refusal").Count);
        Assert.Single(Buttons(panel, "layer-stack-move-up"));
    }

    /// <summary>⚠ An edit made in the panel is in the file after a save.</summary>
    /// <remarks>
    ///     <b>The sentence <a href="https://github.com/Rikarin/Vixen/issues/819">#819</a> was written
    ///     to avoid</b> — "a panel that let an artist drag a row and quietly dropped it on save would
    ///     be worse than one that does not offer the drag". Every other test here reads the document
    ///     in memory, which is exactly the half a dropped save would still satisfy; this one reads
    ///     the bytes back off disk through the same YAML the next session will.
    /// </remarks>
    [Fact]
    public void An_edit_is_in_the_file_after_a_save() {
        using var fixture = new TexturingFixture();
        var document = Open(fixture, Two());
        var panel = Panel(fixture);

        Buttons(panel, "layer-stack-move-up")[1].Activate();

        document.Save();

        Assert.False(document.IsDirty.Value);

        var written = LayerStackYaml.Read(File.ReadAllText(document.AssetPath));
        var layers = written.Sets[0].Layers;

        Assert.Equal("top", layers[0].Id);
        Assert.Equal("bottom", layers[1].Id);
    }

    /// <summary>⚠ A second stack of the same shape gets its own rows, not the first one's.</summary>
    /// <remarks>
    ///     <b>A shape is not an identity, and the rebuild rule is written on the shape.</b> Every
    ///     control on a row closes over the document it was built for, and two stacks with the same
    ///     layer ids, kinds and channels — which is every pair made from
    ///     <c>LayerStackDocument.Starter</c> — produce the same signature. Without the identity check
    ///     beside it the second stack would be shown with the first one's rows, and every edit would
    ///     land in a file that is no longer open: an edit that appears to do nothing, and a dirty
    ///     flag on the wrong document.
    /// </remarks>
    [Fact]
    public void A_second_stack_of_the_same_shape_gets_its_own_rows() {
        using var fixture = new TexturingFixture();
        var first = Open(fixture, Two());
        var panel = Panel(fixture);

        var second = Another(fixture, "Keel", Two());

        Buttons(panel, "layer-stack-move-up")[1].Activate();

        Assert.Equal(0, first.Stack.Depth.Value);
        Assert.Equal(1, second.Stack.Depth.Value);
        Assert.Equal(0.75f, TopColour(first));
        Assert.Equal(0.25f, TopColour(second));
    }

    /// <summary>The colour the last composite reads, which is what "topmost" means arithmetically.</summary>
    static float TopColour(LayerStackDocument document) {
        var plan = Compile(document);
        var blend = Last(plan, "Blend");

        foreach (var op in plan.Ops) {
            if (string.Equals(op.Kernel, "Uniform", StringComparison.Ordinal) && op.Output == blend.Inputs[1]) {
                return op.Find("red")!.Value.Value;
            }
        }

        Assert.Fail("the last blend's foreground is not a uniform");

        throw new InvalidOperationException("unreachable");
    }

    static LayerAsset Top(LayerStackDocument document) {
        var layers = document.Document.Sets[0].Layers;

        return layers[^1];
    }

    static TexturePlan Compile(LayerStackDocument document) {
        var compilation = LayerStackCompiler.Compile(document.Document, document.Document.Sets[0]);

        Assert.NotNull(compilation.Plan);

        return compilation.Plan;
    }

    static int Count(TexturePlan plan, string kernel) {
        var count = 0;

        foreach (var op in plan.Ops) {
            if (string.Equals(op.Kernel, kernel, StringComparison.Ordinal)) {
                count++;
            }
        }

        return count;
    }

    static TextureOp Last(TexturePlan plan, string kernel) {
        for (var index = plan.Ops.Length - 1; index >= 0; index--) {
            if (string.Equals(plan.Ops[index].Kernel, kernel, StringComparison.Ordinal)) {
                return plan.Ops[index];
            }
        }

        Assert.Fail($"no '{kernel}' op in this plan");

        throw new InvalidOperationException("unreachable");
    }

    static LayerStackAsset Two() =>
        Stack(
            [new() { Usage = "baseColor", Default = [0f, 0f, 0f, 1f] }],
            Fill("bottom", "Bottom", 0.25f),
            Fill("top", "Top", 0.75f)
        );

    /// <summary>Two layers carrying one id, and optionally a third that carries its own.</summary>
    /// <param name="third">
    ///     An id for a layer nothing shares one with, or empty for a stack that is entirely ambiguous.
    /// </param>
    static LayerStackAsset Shared(string third = "") {
        List<LayerAsset> layers = [Fill("dup", "Lower", 0.25f), Fill("dup", "Upper", 0.75f)];

        if (third.Length > 0) {
            layers.Add(Fill(third, "Solo", 0.5f));
        }

        return Stack([new() { Usage = "baseColor", Default = [0f, 0f, 0f, 1f] }], [.. layers]);
    }

    static LayerStackAsset Three() =>
        Stack(
            [new() { Usage = "baseColor", Default = [0f, 0f, 0f, 1f] }],
            Fill("bottom", "Bottom", 0.25f),
            Fill("middle", "Middle", 0.5f),
            Fill("top", "Top", 0.75f)
        );

    static LayerStackAsset TwoChannels() =>
        Stack(
            [
                new() { Usage = "baseColor", Default = [0f, 0f, 0f, 1f] },
                new() { Usage = "roughness", Default = [0f, 0f, 0f, 1f] }
            ],
            new LayerAsset {
                Id = "l",
                Name = "Layer",
                Kind = LayerKind.Fill,
                Values = { ["baseColor"] = [0.25f, 0.25f, 0.25f, 1f], ["roughness"] = [0.5f, 0.5f, 0.5f, 1f] }
            }
        );

    /// <summary>One masked layer, whose mask is a bake.</summary>
    /// <param name="map">
    ///     What the bake measures. ⚠ A parameter so that one test can stage a map this build does
    ///     <em>not</em> bake — the case a picker has to keep on the screen rather than replace.
    /// </param>
    static LayerStackAsset Masked(string map = "curvature") =>
        Stack(
            [new() { Usage = "baseColor", Default = [0f, 0f, 0f, 1f] }],
            new LayerAsset {
                Id = "l",
                Name = "Layer",
                Kind = LayerKind.Fill,
                Values = { ["baseColor"] = [0.25f, 0.25f, 0.25f, 1f] },
                Mask = new() {
                    Source = LayerMaskSource.Bake,
                    Map = map,
                    Layers = [
                        new() { Source = LayerMaskSource.Constant, Value = 0.5f, Blend = LayerBlendMode.Multiply }
                    ]
                }
            }
        );

    /// <summary>Three layers, the middle of which anchors its mask onto the bottom one.</summary>
    /// <remarks>
    ///     ⚠ <b>The middle one, deliberately: for the topmost layer "everything below" and
    ///     "everything but itself" are the same list</b>, so a picker that only excluded self would
    ///     pass a test built on the top row. Here they differ by the layer above.
    /// </remarks>
    static LayerStackAsset Anchored() =>
        Stack(
            [new() { Usage = "baseColor", Default = [0f, 0f, 0f, 1f] }],
            Fill("bottom", "Bottom", 0.25f),
            new LayerAsset {
                Id = "middle",
                Name = "Middle",
                Kind = LayerKind.Fill,
                Values = { ["baseColor"] = [0.5f, 0.5f, 0.5f, 1f] },
                Mask = new() { Source = LayerMaskSource.Anchor, Anchor = "bottom" }
            },
            Fill("top", "Top", 0.75f)
        );

    /// <summary>⚠ The same anchor the wrong way round: the bottom layer reading the top one.</summary>
    /// <remarks>
    ///     A file can say this and the compile refuses it; what it is here for is the picker, whose
    ///     honest answer for the bottom layer of a stack is that there is nothing to offer.
    /// </remarks>
    static LayerStackAsset AnchoredFromTheBottom() =>
        Stack(
            [new() { Usage = "baseColor", Default = [0f, 0f, 0f, 1f] }],
            new LayerAsset {
                Id = "bottom",
                Name = "Bottom",
                Kind = LayerKind.Fill,
                Values = { ["baseColor"] = [0.25f, 0.25f, 0.25f, 1f] },
                Mask = new() { Source = LayerMaskSource.Anchor, Anchor = "top" }
            },
            Fill("top", "Top", 0.75f)
        );

    /// <summary>⚠ A second stack in the same panel does not inherit the first one's chosen set.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>#927's mis-aim in the window a set selector opens.</b> The panel's <c>SetName</c>
    ///         is a copy of the document's <c>PaintSet</c>, and the view outlives the document it is
    ///         showing — so a copy carried across a document switch aims the picker at the previous
    ///         stack while the new stack's <c>PaintSet</c> is still empty and the stroke lands in its
    ///         first set.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Both stacks carry the same set names, which is what makes it silent.</b>
    ///         <c>LayerStackEdit.SetFor</c> resolves "Head" in the second stack rather than refusing
    ///         it, so nothing anywhere reports a disagreement — the picker reads right and the paint
    ///         goes elsewhere. A fixture whose two stacks had different set names would go green
    ///         under the defect, because the stale name would fail to resolve and fall back.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_second_stack_opened_into_the_panel_starts_at_its_own_set() {
        using var fixture = new TexturingFixture();

        var first = Open(fixture, TwoSets());

        Find<Select>(Panel(fixture), "layer-stack-set").Value = "Head";

        Assert.Equal("Head", first.PaintSet);

        fixture.Project.Selection.Set(LayerStackPanelTests.AddStack(fixture, "Keel"));

        Assert.True(fixture.Shell.Commands.Execute(TexturingModule.OpenStackCommand));

        // ⚠ Both documents stay open — the panel is what switches, which is the whole point.
        var second = Assert.IsType<LayerStackDocument>(
            fixture.Project.Documents.Single(document => !ReferenceEquals(document, first))
        );

        second.Document = TwoSets();

        Assert.True(fixture.Shell.Commands.Execute(TexturingModule.OpenStackCommand));

        var panel = Panel(fixture);

        // The picker is aimed at the second stack's own answer, which is its first set.
        Assert.Equal("Body", Find<Select>(panel, "layer-stack-set").Value);
        Assert.Equal("", second.PaintSet);

        // ⚠ And the rows agree with it, which is what says the adoption reached the build rather
        // than only the field: under the defect these read "Head" while `PaintSet` stayed empty.
        Assert.Contains(Texts(panel, "layer-stack-row-name"), row => row.Contains("Body", StringComparison.Ordinal));

        Assert.DoesNotContain(
            Texts(panel, "layer-stack-row-name"),
            row => row.Contains("Head", StringComparison.Ordinal)
        );
    }

    /// <summary>⚠ Two sets that differ in every single thing the panel reads off one.</summary>
    /// <remarks>
    ///     ⚠ <b>A different channel list, a different layer id and a different <c>Mesh</c>.</b> Two
    ///     sets alike in any of those make the corresponding assertion true of a panel that never
    ///     changed set at all — which is what <a href="https://github.com/Rikarin/Vixen/issues/927">
    ///     #927</a> asks a fixture for in so many words.
    /// </remarks>
    static LayerStackAsset TwoSets() =>
        new() {
            Name = "Hull",
            BaseWidth = 32,
            BaseHeight = 32,
            Seed = 7u,
            Sets = [
                new() {
                    Name = "Body",
                    Channels = [new() { Usage = "baseColor", Default = [0f, 0f, 0f, 1f] }],
                    Layers = [Fill("body", "Body", 0.25f)]
                },
                new() {
                    Name = "Head",
                    Mesh = "head",
                    Channels = [
                        new() { Usage = "baseColor", Default = [0f, 0f, 0f, 1f] },
                        new() { Usage = "roughness", Default = [0f, 0f, 0f, 1f] }
                    ],
                    Layers = [Fill("head", "Head", 0.75f)]
                }
            ]
        };

    /// <summary>⚠ Two sets a copy apart: same channels, same layer id, different layer name.</summary>
    /// <remarks>
    ///     ⚠ <b>The state a duplicated material slot is really in</b>, and the one
    ///     <c>LayerStackView.Shape</c> cannot tell apart without the set's own name in it. The layer
    ///     <em>name</em> is what differs because a name is a value rather than structure, so it is
    ///     deliberately absent from the shape — nothing else here can move the comparison.
    /// </remarks>
    static LayerStackAsset TwinSets() {
        List<ChannelAsset> channels = [new() { Usage = "baseColor", Default = [0f, 0f, 0f, 1f] }];

        return new() {
            Name = "Hull",
            BaseWidth = 32,
            BaseHeight = 32,
            Seed = 7u,
            Sets = [
                new() { Name = "Body", Channels = channels, Layers = [Fill("only", "Body layer", 0.25f)] },
                new() { Name = "Head", Channels = channels, Layers = [Fill("only", "Head layer", 0.25f)] }
            ]
        };
    }

    /// <summary>Two paint layers, the upper of which has no id at all.</summary>
    /// <remarks>
    ///     ⚠ <b>Two, and both painted, because one of either makes the defect invisible.</b> With one
    ///     paint layer the brush's fallback reaches the layer the artist clicked and everything looks
    ///     right; with the id-less one at the bottom the fallback reaches it as well. The damage needs
    ///     a second paint layer <em>below</em> the id-less one, which is the one the fallback finds.
    /// </remarks>
    static LayerStackAsset TwoPaintLayers() =>
        Stack(
            [new() { Usage = "baseColor", Default = [0f, 0f, 0f, 1f] }],
            new LayerAsset { Id = "lower", Name = "Lower", Kind = LayerKind.Paint },
            new LayerAsset { Name = "Upper", Kind = LayerKind.Paint }
        );

    /// <summary>⚠ A group, whose second child anchors — the case a flat stack cannot express.</summary>
    /// <remarks>
    ///     ⚠ <b>The anchored layer is <em>inside</em> the group and its target is its own sibling</b>,
    ///     because what separates the post-order walk from a parent-first one is a single option: the
    ///     group. Every other layer here is offered identically by both. <c>'top'</c> is above the
    ///     whole group and <c>'bottom'</c> below it, so the group's boundary is crossed in both
    ///     directions.
    /// </remarks>
    static LayerStackAsset AnchoredInsideAGroup() =>
        Stack(
            [new() { Usage = "baseColor", Default = [0f, 0f, 0f, 1f] }],
            Fill("bottom", "Bottom", 0.2f),
            new LayerAsset {
                Id = "g",
                Name = "Group",
                Kind = LayerKind.Group,
                Children = [
                    Fill("inner", "Inner", 0.4f),
                    new LayerAsset {
                        Id = "child",
                        Name = "Child",
                        Kind = LayerKind.Fill,
                        Values = { ["baseColor"] = [0.6f, 0.6f, 0.6f, 1f] },
                        Mask = new() { Source = LayerMaskSource.Anchor, Anchor = "inner" }
                    }
                ]
            },
            Fill("top", "Top", 0.8f)
        );

    /// <summary>A group with a layer under it and a layer over it, and two children inside.</summary>
    /// <remarks>
    ///     ⚠ <b>A layer on each side of the group, because a stack that is only a group cannot show
    ///     the difference.</b> With nothing below it, the first child's honest answer is the empty
    ///     list — which is also what a walk that had lost the recursion returns, and what the bottom
    ///     layer of any stack returns. The <c>bottom</c> fill is what makes "nothing of its own
    ///     group" a statement about the group rather than about the end of the list.
    /// </remarks>
    static LayerStackAsset AnchoredAroundAGroup() =>
        Stack(
            [new() { Usage = "baseColor", Default = [0f, 0f, 0f, 1f] }],
            Fill("bottom", "Bottom", 0.25f),
            new LayerAsset {
                Id = "g",
                Name = "Group",
                Kind = LayerKind.Group,
                Children = [Fill("lower", "Lower", 0.4f), Fill("upper", "Upper", 0.6f)]
            },
            Fill("top", "Top", 0.75f)
        );

    static LayerStackAsset Grouped() =>
        Stack(
            [new() { Usage = "baseColor", Default = [0f, 0f, 0f, 1f] }],
            new LayerAsset {
                Id = "g",
                Name = "Group",
                Kind = LayerKind.Group,
                Children = [Fill("lower", "Lower", 0.25f), Fill("upper", "Upper", 0.75f)]
            }
        );

    /// <summary>The layer <see cref="TwoChannels" /> makes, read back out of the open document.</summary>
    static LayerAsset Only(LayerStackDocument document) => document.Document.Sets[0].Layers[0];

    static LayerAsset Fill(string id, string name, float grey) =>
        new() {
            Id = id,
            Name = name,
            Kind = LayerKind.Fill,
            Values = { ["baseColor"] = [grey, grey, grey, 1f] }
        };

    static LayerStackAsset Stack(List<ChannelAsset> channels, params LayerAsset[] layers) =>
        new() {
            Name = "Hull",
            BaseWidth = 32,
            BaseHeight = 32,
            Seed = 7u,
            Sets = [new() { Name = "S", Channels = channels, Layers = [.. layers] }]
        };

    /// <summary>Opens a stack through the verb, puts a made one in it, and shows the panel.</summary>
    /// <remarks>
    ///     ⚠ <b>Through the module's own command, twice.</b> Assigning
    ///     <c>LayerStackDocument.Document</c> is what a test can do that a person cannot; running the
    ///     verb again is what puts the assigned stack in front of the panel, and it is the same route
    ///     <c>LayerStackPanelTests</c> takes for the same reason — a view a test constructed would
    ///     pass in an editor where the panel was never registered.
    /// </remarks>
    static LayerStackDocument Open(TexturingFixture fixture, LayerStackAsset stack) {
        fixture.Host.Activate(TexturingModule.ModuleId, TexturingModule.ModuleName, new TexturingModule());
        fixture.Project.Selection.Set(LayerStackPanelTests.AddStack(fixture, "Hull"));

        Assert.True(fixture.Shell.Commands.Execute(TexturingModule.OpenStackCommand));

        var document = Assert.IsType<LayerStackDocument>(fixture.Project.Documents.Single());

        document.Document = stack;

        Assert.True(fixture.Shell.Commands.Execute(TexturingModule.OpenStackCommand));

        return document;
    }

    /// <summary>Opens a second stack in the same panel, the way a person opens a second file.</summary>
    /// <remarks>
    ///     ⚠ <b>The file is written with the stack already in it, and that is what makes the test
    ///     about the identity rather than about the shape.</b> Opening an <em>empty</em>
    ///     <c>.vxlayers</c> gives a document holding <c>LayerStackDocument.Starter</c>'s one layer,
    ///     so assigning the real stack afterwards changes the row shape and forces a rebuild for a
    ///     reason that has nothing to do with which document is open. Reading the second file gives
    ///     the panel two stacks of identical shape back to back, which is the state the check exists
    ///     for.
    /// </remarks>
    static LayerStackDocument Another(TexturingFixture fixture, string name, LayerStackAsset stack) {
        var relative = "Assets/" + name + LayerStackDocument.Extension;

        File.WriteAllText(fixture.Paths.Absolute(relative), LayerStackYaml.Write(stack));

        var report = fixture.Project.Assets.Scan();

        Assert.DoesNotContain(report.Issues, issue => issue.Kind != AssetIssueKind.MetaCreated);
        Assert.True(fixture.Project.Assets.TryGetByPath(relative, out var entry));

        fixture.Project.Selection.Set(entry.Guid);

        Assert.True(fixture.Shell.Commands.Execute(TexturingModule.OpenStackCommand));

        return Assert.IsType<LayerStackDocument>(
            fixture.Project.Documents.Single(open => open.Asset == entry.Guid)
        );
    }

    /// <summary>Opens a stack and puts it in a view this test holds, laid out and framed.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Its own panel, because what these two tests read is the <em>view's</em> and the
    ///         module keeps its own private.</b> Every other test here goes through
    ///         <see cref="Panel" /> and reads the tree, which is the stronger assertion and the one
    ///         this file keeps to — but a zoom and a walk count are not elements, and a module that
    ///         handed its view out would be a seam that exists for xunit.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The first <c>Show</c> is before the layout on purpose.</b> <c>ImageView.Fit</c>
    ///         answers false with no box to fit against, which is the state a panel's first refresh is
    ///         really in; a helper that settled first would hide the retry the caller is asserting on.
    ///     </para>
    /// </remarks>
    static (LayerStackView View, LayerStackDocument Document) Viewed(
        TexturingFixture fixture,
        LayerStackAsset stack
    ) {
        var document = Open(fixture, stack);
        LayerStackView? built = null;

        fixture.Shell.RegisterPanel(
            ViewPanel,
            new StringId("editor.panel." + ViewPanel, "Layers"),
            panel => built = new LayerStackView(panel)
        );

        fixture.Shell.Workspace.Open(ViewPanel);

        Assert.NotNull(built);

        built.Show(document);

        fixture.Shell.Document.Update();
        fixture.Shell.Document.Draw();

        return (built, document);
    }

    static UiElement Panel(TexturingFixture fixture) {
        var panel = fixture.Shell.Workspace.Open(TexturingModule.StackPanel);

        Assert.NotNull(panel);

        return panel;
    }

    static UiElement Element(UiElement root, string tag) {
        var found = All(root, tag);

        Assert.NotEmpty(found);

        return found[0];
    }

    static T Find<T>(UiElement root, string tag) where T : UiElement {
        var found = All(root, tag);

        Assert.NotEmpty(found);

        return Assert.IsType<T>(found[0]);
    }

    /// <summary>The only layer's mask, for a stack made by <see cref="Masked" />.</summary>
    static MaskAsset Mask(LayerStackDocument document) => document.Document.Sets[0].Layers[^1].Mask;

    /// <summary>Every fill-colour component field the panel drew under that class, in layout order.</summary>
    /// <remarks>
    ///     ⚠ <b>By class, where every other finder here walks tags, and the panel is what forced
    ///     it.</b> The four components of a fill colour are the one place in this view that keeps the
    ///     control's own tag — <c>numeric-input</c>, so that the field is styled as a field at all —
    ///     and carries its name as a class instead. A tag walk finds nothing, which is a green
    ///     assertion about an empty list rather than a failure, so the two forms are not
    ///     interchangeable and this one is named for the thing it finds.
    /// </remarks>
    static List<NumericInput> Fields(UiElement root, string className) {
        List<NumericInput> found = [];

        Walk(root);

        Assert.NotEmpty(found);

        return found;

        void Walk(UiElement element) {
            if (element.HasClass(className)) {
                found.Add(Assert.IsType<NumericInput>(element));
            }

            foreach (var child in element.Children) {
                Walk(child);
            }
        }
    }

    /// <summary>Every control of one kind the panel drew under that tag, in layout order.</summary>
    static List<T> Controls<T>(UiElement root, string tag) where T : UiElement {
        List<T> found = [];

        foreach (var element in All(root, tag)) {
            found.Add(Assert.IsType<T>(element));
        }

        return found;
    }

    static List<Button> Buttons(UiElement root, string tag) {
        List<Button> found = [];

        foreach (var element in All(root, tag)) {
            found.Add(Assert.IsType<Button>(element));
        }

        return found;
    }

    static List<CheckBox> Ticks(UiElement root, string tag) {
        List<CheckBox> found = [];

        foreach (var element in All(root, tag)) {
            found.Add(Assert.IsType<CheckBox>(element));
        }

        return found;
    }

    static List<string> Texts(UiElement root, string tag) {
        List<string> found = [];

        foreach (var element in All(root, tag)) {
            found.Add(element.Text ?? "");
        }

        return found;
    }

    /// <summary>Every element with that tag, in the order the panel laid them out.</summary>
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
}
