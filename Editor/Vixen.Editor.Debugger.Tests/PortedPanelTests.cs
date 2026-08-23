// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui;
using Vixen.Ui.Controls;
using Vixen.Ui.Styling;
using Vixen.Ui.Testing;
using Xunit;

namespace Vixen.Editor.Debugger.Tests;

/// <summary>
///     The two panels doc 36 § F7 wave 1b moved into <c>.vxml</c>, asserted through the elements they
///     built rather than through the properties they were handed.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Every assertion here reads an element, which is the whole point of the file.</b> A
///         markup panel draws through effects: something writes a signal, the scheduler runs the
///         binding on the next flush, and "the model was assigned" and "the screen followed" are two
///         different statements. Both panels used to recompute themselves from a hand-written
///         <c>Restate</c>, and a port that replaced the signals with plain fields would pass every
///         test that read a property and would draw its first answer for ever.
///     </para>
///     <para>
///         ⚠ <b>Sabotage-verified.</b> Recorded, because a reactivity test that would pass without the
///         reactivity is the usual way this goes wrong.
///     </para>
///     <list type="bullet">
///         <item>
///             <c>FrameDebuggerView.source</c> as a plain field fails
///             <see cref="A_capture_source_that_arrives_after_the_panel_is_open_lights_the_button" />
///             and nothing else — which is the bug the port fixed rather than translated.
///         </item>
///         <item>
///             <c>FrameDebuggerView.selected</c> as a plain <c>int</c> fails
///             <see cref="Stepping_a_draw_moves_the_status_line_and_the_state_pane" /> and
///             <see cref="The_step_buttons_grey_at_the_ends" />, and nothing else — the two things a
///             step is supposed to move.
///         </item>
///         <item>
///             <c>DeviceManager.devices</c> back as a <c>List</c> fails
///             <see cref="A_discovery_after_the_panel_is_open_reaches_the_status_line" /> and nothing
///             else; the grid still fills, because that half is fed by <c>Changed</c> and always was,
///             which is the line between what this port changed and what it left alone.
///         </item>
///         <item>
///             <c>DeviceManager.Selected</c> back as an auto-property fails
///             <see cref="Choosing_a_device_lights_attach_and_keeps_the_row_highlighted" /> at the
///             button rather than at the highlight — the highlight is the grid's own and survives —
///             and takes <see cref="A_deploy_rule_that_arrives_after_the_panel_is_open_greys_the_button" />
///             with it, because the refusal is a sentence about the chosen device.
///         </item>
///         <item>
///             <c>DeviceManagerView.CanDeploy</c> back as an auto-property fails
///             <see cref="A_deploy_rule_that_arrives_after_the_panel_is_open_greys_the_button" /> and
///             nothing else.
///         </item>
///         <item>
///             <c>RemoteInspectorClient.State</c> back as an auto-property fails
///             <see cref="Attaching_relabels_the_button_and_lights_refresh" />,
///             <see cref="A_tree_that_arrives_reaches_the_status_line_and_the_tree" /> and
///             <see cref="Apply_lights_only_with_a_selection_and_a_member" /> — the three things a
///             connection coming up is supposed to move — and nothing else.
///         </item>
///         <item>
///             <c>RemoteInspectorClient.counters</c> back as a <c>Dictionary</c> written in place
///             fails <see cref="A_counter_that_arrives_and_then_moves_reaches_the_pane" /> and
///             <see cref="Detaching_removes_the_counter_rows_rather_than_parking_them" />, and
///             nothing else. That is the <c>ImmutableDictionary</c> earning its allocation: a map
///             mutated in place is a change no signal can see.
///         </item>
///     </list>
///     <para>
///         ⚠ <b>And one sabotage that fails nothing, recorded rather than dressed up.</b>
///         <c>RemoteInspectorClient.entities</c> back as a plain <c>List</c> leaves all 81 tests
///         green, because with today's protocol every path that changes the entity list also moves
///         <c>IsFetching</c> or <c>State</c> — <c>Refresh</c> sets fetching, <c>TreeComplete</c>
///         clears it, <c>Reset</c> changes the state — and either of those re-runs the status
///         sentence, which then reads the count fresh. The <c>CollectionSignal</c> stays because the
///         count genuinely <i>is</i> a dependency of that sentence and a message that changed the
///         list without touching the other two would otherwise be silently wrong; but "it is
///         load-bearing today" is a claim this file cannot make, so it does not.
///     </para>
/// </remarks>
public sealed class PortedPanelTests : IDisposable {
    readonly UiTest test = UiTest.Create();

    public PortedPanelTests() {
        ControlTheme.Install(test.Document);
        DebuggerTheme.Install(test.Document);
    }

    public void Dispose() => test.Dispose();

    // ═══════════════════════════════════════════════════ The frame debugger

    /// <summary>A host with no recording backend gets a sentence rather than an empty tree.</summary>
    [Fact]
    public void With_nothing_to_capture_with_the_panel_says_so() {
        var view = Debugger();

        Assert.True(view.CaptureButton.Disabled);
        Assert.True(view.Previous.Disabled);
        Assert.True(view.Next.Disabled);
        Assert.Equal("Nothing here can take a capture.", TextOf(view.Status));
        Assert.Empty(view.Tree.Root.Children);

        // ⚠ Not an empty pane: `DrawState.Rows` always says what the target and the pipeline are,
        // and for an empty capture that is "(outside a pass)" and "(none)". The C# did the same —
        // its `OnCreated` ended in a `Restate` — and a port that drew nothing here would be the
        // change in behaviour rather than this.
        Assert.Contains(Tagged(view, "key-value-value"), part => TextOf(part) == "(outside a pass)");
    }

    /// <summary>
    ///     ⚠ The assertion that <c>Source</c> is a signal, and the one that says this port fixed a bug
    ///     rather than moving it. <c>DiagnosticsModule</c> assigns the delegate <i>after</i>
    ///     <c>panel.Add&lt;FrameDebuggerView&gt;()</c> returns, and the C# computed the button's state
    ///     once in <c>OnCreated</c> — so the only thing that would have un-greyed it was a capture,
    ///     which is what the button takes.
    /// </summary>
    [Fact]
    public void A_capture_source_that_arrives_after_the_panel_is_open_lights_the_button() {
        var view = Debugger();

        Assert.True(view.CaptureButton.Disabled);

        view.Source = Frame;
        test.Frames(2);

        Assert.False(view.CaptureButton.Disabled);
        Assert.Equal("Press Capture Frame.", TextOf(view.Status));
    }

    /// <summary>And the reason a host gives for having none reaches the same line.</summary>
    [Fact]
    public void A_reason_that_arrives_after_the_panel_is_open_reaches_the_status_line() {
        var view = Debugger();

        view.Unavailable = "This host records into a real command buffer.";
        test.Frames(2);

        Assert.Equal("This host records into a real command buffer.", TextOf(view.Status));
    }

    /// <summary>A capture fills the tree, the state pane and the line above them.</summary>
    [Fact]
    public void A_capture_fills_the_tree_and_the_state_pane() {
        var view = Debugger();

        view.Show(Frame());
        test.Frames(2);

        Assert.Equal(2, view.Tree.Root.Children.Count);
        Assert.NotEmpty(Tagged(view, "key-value-row"));
        Assert.Contains("test —", TextOf(view.Status), StringComparison.Ordinal);

        // The pane is grouped, and a group heading is a row of the same list rather than something
        // else parented beside them — which is what keeps `:nth-child` striping honest.
        Assert.Contains(Tagged(view, "key-value-row"), row => row.HasClass("heading"));
    }

    /// <summary>
    ///     ⚠ Stepping is what the panel is <i>for</i>, and both halves of what it moves are bindings.
    ///     The state pane is a keyed <c>@for</c> over an immutable snapshot, so a step that changed the
    ///     pipeline handle has to produce different rows and not the same rows showing the old value.
    /// </summary>
    [Fact]
    public void Stepping_a_draw_moves_the_status_line_and_the_state_pane() {
        var view = Debugger();

        view.Show(Frame());
        test.Frames(2);

        var first = TextOf(view.Status);
        var pipeline = Pipeline(view);

        Assert.Contains("draw 1.", first, StringComparison.Ordinal);

        view.Next.Activate();
        test.Frames(2);

        Assert.Contains("draw 2.", TextOf(view.Status), StringComparison.Ordinal);
        Assert.NotEqual(pipeline, Pipeline(view));
    }

    /// <summary>The two step buttons grey at the ends of the stream, and only there.</summary>
    [Fact]
    public void The_step_buttons_grey_at_the_ends() {
        var view = Debugger();

        view.Show(Frame());
        test.Frames(2);

        Assert.True(view.Previous.Disabled);
        Assert.False(view.Next.Disabled);

        view.Next.Activate();
        test.Frames(2);

        Assert.False(view.Previous.Disabled);
        Assert.True(view.Next.Disabled);
    }

    // ═══════════════════════════════════════════════════ The device manager

    /// <summary>A panel nobody has pointed at a manager says so rather than showing an empty grid.</summary>
    [Fact]
    public void With_no_manager_the_device_panel_says_so() {
        var view = test.Document.Root.Add<DeviceManagerView>();
        test.Frames(2);

        Assert.Equal("No device providers.", TextOf(view.Status));
        Assert.True(view.DeployButton.Disabled);
        Assert.True(view.AttachButton.Disabled);
    }

    /// <summary>A manager with a device in it fills the grid and counts it.</summary>
    [Fact]
    public void A_manager_fills_the_grid_and_the_count() {
        var view = Devices(out _);

        Assert.Single(view.Devices.Items);
        Assert.Equal("1 device(s) from 1 provider(s).", TextOf(view.Status));
    }

    /// <summary>
    ///     ⚠ The assertion that <c>DeviceManager.devices</c> is a collection signal: a discovery that
    ///     finds something new while the panel is open has to change what the line says. The grid half
    ///     would pass either way — it is fed by <c>Changed</c>, which is the surface this port left
    ///     alone on purpose.
    /// </summary>
    [Fact]
    public void A_discovery_after_the_panel_is_open_reaches_the_status_line() {
        var view = Devices(out var manager);

        Assert.Equal("1 device(s) from 1 provider(s).", TextOf(view.Status));

        manager.Add(new StubProvider("second", "A Phone"));
        manager.Discover();
        test.Frames(2);

        Assert.Equal("2 device(s) from 2 provider(s).", TextOf(view.Status));
        Assert.Equal(2, view.Devices.Items.Count);
    }

    /// <summary>
    ///     ⚠ <b>The regression this port exists to name.</b> <c>Restate</c> refilled the grid, and
    ///     <see cref="Vixen.Ui.Controls.Advanced.DataGrid.SetItems" /> clears the selection — so before
    ///     the port, clicking a device left <c>Devices.Selection</c> empty and no row checked while the
    ///     buttons lit up for a device nothing on screen said was chosen. Measured: <c>Select(0)</c>
    ///     gave <c>Selection.Count == 0</c>, <c>checked == 0</c>, <c>manager.Selected == "local"</c>.
    /// </summary>
    [Fact]
    public void Choosing_a_device_lights_attach_and_keeps_the_row_highlighted() {
        var view = Devices(out var manager);

        Assert.True(view.AttachButton.Disabled);

        view.Devices.Select(0);
        test.Frames(2);

        Assert.Equal("local", manager.Selected?.Id);
        Assert.Single(view.Devices.Selection);
        Assert.Contains(view.Devices.Rows, row => row.State.HasFlag(ElementState.Checked));
        Assert.False(view.AttachButton.Disabled);
    }

    /// <summary>
    ///     ⚠ <c>CanDeploy</c> is assigned by <c>DiagnosticsModule</c> after the panel is built, which
    ///     is why the C# setter had to re-run <c>Restate</c> by hand and guard against a panel with no
    ///     buttons yet. A binding needs neither, and this is what says so.
    /// </summary>
    [Fact]
    public void A_deploy_rule_that_arrives_after_the_panel_is_open_greys_the_button() {
        var view = Devices(out _);

        view.Devices.Select(0);
        test.Frames(2);

        Assert.True(view.DeployButton.Disabled);
        Assert.Equal("Nothing here knows how to build for a device yet.", TextOf(view.Status));

        view.CanDeploy = _ => null;
        test.Frames(2);

        Assert.False(view.DeployButton.Disabled);
        Assert.Equal("1 device(s) from 1 provider(s).", TextOf(view.Status));

        view.CanDeploy = _ => "No SDK for that.";
        test.Frames(2);

        Assert.True(view.DeployButton.Disabled);
        Assert.Equal("No SDK for that.", TextOf(view.Status));
    }

    /// <summary>A provider that fails is reported, and the report outranks the count.</summary>
    [Fact]
    public void A_provider_that_throws_is_named_on_the_status_line() {
        var view = Devices(out var manager);

        manager.Add(new BrokenProvider());
        manager.Discover();
        test.Frames(2);

        Assert.Contains("cable is out", TextOf(view.Status), StringComparison.Ordinal);
    }

    // ══════════════════════════════════════════════════ The remote inspector

    /// <summary>A panel with no client says so, and offers nothing that would do nothing.</summary>
    [Fact]
    public void With_no_connection_the_remote_inspector_says_so() {
        var view = test.Document.Root.Add<RemoteInspectorView>();

        test.Frames(2);

        Assert.Equal("No connection configured.", TextOf(view.Status));
        Assert.True(view.RefreshButton.Disabled);
        Assert.True(view.Apply.Disabled);

        // ⚠ "Detach", and that is the panel this replaces rather than the port. The label reads
        // `State is Detached ? "Attach" : "Detach"`, and a null client is not `Detached` — so a
        // panel with no connection at all offers to detach from it. Preserved deliberately: the
        // whole-tree dump of the hand-written panel says the same word in the same place, and a
        // port that changes behaviour is a defect until argued for. Worth its own commit.
        Assert.Equal("Detach", view.AttachButton.Label);
    }

    /// <summary>
    ///     ⚠ <b>The assertion that <c>RemoteInspectorClient.State</c> is a signal.</b> Attaching is
    ///     something the <i>client</i> does, from a poll, and nothing tells the panel — so a plain
    ///     property here leaves the button saying "Attach" over an attached connection until the next
    ///     thing that happened to raise <c>Changed</c>.
    /// </summary>
    [Fact]
    public void Attaching_relabels_the_button_and_lights_refresh() {
        using var far = new LoopbackBuild();
        var view = Inspector(far);

        Assert.Equal("Attach", view.AttachButton.Label);
        Assert.True(view.RefreshButton.Disabled);

        far.Client.Attach();
        far.Settle();
        test.Frames(2);

        Assert.Equal("Detach", view.AttachButton.Label);
        Assert.False(view.RefreshButton.Disabled);
    }

    /// <summary>
    ///     ⚠ <b>The assertion that <c>Entities</c> is a <c>CollectionSignal</c>.</b> The status line
    ///     counts them, and counting a collection signal inside a binding is what subscribes to it —
    ///     a <c>List</c> here draws "0 entit(ies)" for ever, while the tree beside it fills, because
    ///     the tree is fed by <c>Changed</c> and always was. That is the line between what this port
    ///     changed and what it left alone.
    /// </summary>
    [Fact]
    public void A_tree_that_arrives_reaches_the_status_line_and_the_tree() {
        using var far = new LoopbackBuild();
        var view = Inspector(far);

        far.Client.Attach();
        far.Settle();
        test.Frames(2);

        Assert.Contains("3 entit(ies)", TextOf(view.Status), StringComparison.Ordinal);
        Assert.Single(view.Tree.Root.Children);
    }

    /// <summary>
    ///     ⚠ <b>The assertion that the counters are a signal and that the pane is a keyed loop.</b>
    ///     A counter arriving is the one thing that happens most often and moves least — and a
    ///     <c>Dictionary</c> written in place is a change no signal can see, which is the hole a
    ///     revision counter gets invented for.
    /// </summary>
    [Fact]
    public void A_counter_that_arrives_and_then_moves_reaches_the_pane() {
        using var far = new LoopbackBuild();
        var view = Inspector(far);

        far.Client.Attach();
        far.Settle();
        test.Frames(2);

        Assert.Empty(Tagged(view.CounterPane, "remote-counter"));

        far.Report("fps", 59.5);
        far.Settle();
        test.Frames(2);

        var row = Assert.Single(Tagged(view.CounterPane, "remote-counter"));

        Assert.Equal("fps59.5", TextOf(row));

        // ⚠ And the second reading, which is the half a stable key would have broken: the row's
        // value is in its key, so 61.25 is a different key and the region is rebuilt with it.
        far.Report("fps", 61.25);
        far.Settle();
        test.Frames(2);

        Assert.Equal("fps61.25", TextOf(Assert.Single(Tagged(view.CounterPane, "remote-counter"))));
    }

    /// <summary>
    ///     ⚠ <b>Apply needs a selection <i>and</i> a member name, and both now arrive by the same
    ///     route.</b> Wave 6 wrote the selection through a <c>SelectionChanged</c> subscription in
    ///     <c>OnComposed</c>, because <c>change:Selection</c> threw — the read-only view over a
    ///     <c>HashSet</c> is not a <c>[UiProperty]</c> and could not have been one. It is
    ///     <c>change:SelectedNodes</c> now, beside the <c>change:Value</c> on the member box, and
    ///     this test is what says the port kept the behaviour: both halves have to land for the
    ///     button to light, so each is asserted greyed on its own.
    /// </summary>
    [Fact]
    public void Apply_lights_only_with_a_selection_and_a_member() {
        using var far = new LoopbackBuild();
        var view = Inspector(far);

        far.Client.Attach();
        far.Settle();
        test.Frames(2);

        Assert.True(view.Apply.Disabled);

        view.Member.Value = "Transform.Position";
        test.Frames(2);

        // A member with nothing selected is still nothing to write.
        Assert.True(view.Apply.Disabled);

        view.Tree.Select(view.Tree.Root.Children[0]);
        test.Frames(2);

        Assert.False(view.Apply.Disabled);

        view.Member.Value = string.Empty;
        test.Frames(2);

        Assert.True(view.Apply.Disabled);
    }

    /// <summary>Detaching empties the pane rather than parking rows in it.</summary>
    /// <remarks>
    ///     ⚠ The one dumped state where the port is not byte-identical to the panel it replaces, and
    ///     it is the pool being gone: the C# left two <c>remote-counter</c> elements behind under
    ///     <c>.parked</c>, still reading the last numbers the build sent.
    /// </remarks>
    [Fact]
    public void Detaching_removes_the_counter_rows_rather_than_parking_them() {
        using var far = new LoopbackBuild();
        var view = Inspector(far);

        far.Client.Attach();
        far.Settle();
        far.Report("fps", 59.5);
        far.Settle();
        test.Frames(2);

        Assert.NotEmpty(Tagged(view.CounterPane, "remote-counter"));

        far.Client.Detach();
        test.Frames(2);

        Assert.Empty(Tagged(view.CounterPane, "remote-counter"));
        Assert.Equal("Detached.", TextOf(view.Status));
    }

    // ════════════════════════════════════════════════════════════ Harness

    RemoteInspectorView Inspector(LoopbackBuild far) {
        var view = test.Document.Root.Add<RemoteInspectorView>();

        view.Show(far.Client);
        test.Frames(2);

        return view;
    }

    FrameDebuggerView Debugger() {
        var view = test.Document.Root.Add<FrameDebuggerView>();
        test.Frames(2);

        return view;
    }

    DeviceManagerView Devices(out DeviceManager manager) {
        manager = new DeviceManager();
        manager.Add(new LocalDeviceProvider("127.0.0.1:7777"));
        manager.Discover();

        var view = test.Document.Root.Add<DeviceManagerView>();
        view.Show(manager);
        test.Frames(2);

        return view;
    }

    /// <summary>Two passes, two draws, and a different pipeline bound in each.</summary>
    static FrameCapture Frame() =>
        new(
            "test",
            [
                new(0, CaptureCommandKind.BeginPass, "shadow pass", 0, 1, 0),
                new(0, CaptureCommandKind.BindPipeline, null, 7, 0, 0),
                new(0, CaptureCommandKind.Draw, null, 300, 1, 0),
                new(0, CaptureCommandKind.EndPass, null, 0, 0, 0),
                new(0, CaptureCommandKind.BeginPass, "ui", 1, 0, 0),
                new(0, CaptureCommandKind.BindPipeline, null, 9, 0, 0),
                new(0, CaptureCommandKind.Draw, null, 6, 1, 0)
            ]
        );

    /// <summary>
    ///     What the state pane says is bound, as it is written on screen.
    /// </summary>
    /// <remarks>
    ///     The heading is excluded, because a group named "Pipeline" and a row labelled "Pipeline"
    ///     are both rows of the same list — which is <see cref="Vixen.Ui.Controls.KeyValueList" />'s
    ///     own rule and the reason a heading is a class rather than a different control.
    /// </remarks>
    static string Pipeline(FrameDebuggerView view) =>
        TextOf(
            Assert.Single(
                Tagged(view, "key-value-row"),
                row => !row.HasClass("heading")
                    && Tagged(row, "key-value-key").Any(part => TextOf(part) == "Pipeline")
            )
        );

    static UiElement[] Tagged(UiElement root, string tag) => [.. Descendants(root).Where(element => element.Tag == tag)];

    static UiElement[] Tagged(Control view, string tag) => Tagged((UiElement) view, tag);

    /// <summary>
    ///     A walk rather than a read, because markup text is its own element: an interpolation emits a
    ///     <c>text</c> child rather than setting the parent's own string.
    /// </summary>
    static string TextOf(UiElement element) {
        var text = element.Text ?? string.Empty;

        foreach (var child in Descendants(element)) {
            text += child.Text ?? string.Empty;
        }

        return text;
    }

    static IEnumerable<UiElement> Descendants(UiElement root) {
        foreach (var child in root.Children) {
            yield return child;

            foreach (var nested in Descendants(child)) {
                yield return nested;
            }
        }
    }

    /// <summary>A provider that reports one made-up device.</summary>
    sealed class StubProvider(string id, string name) : IDeviceProvider {
        public string Name => name;

        public IEnumerable<DeviceEntry> Discover() {
            yield return new(id, name, DeviceKind.Mobile, "Android 14");
        }
    }

    /// <summary>A provider whose external tool is not there, which is the ordinary case.</summary>
    sealed class BrokenProvider : IDeviceProvider {
        public string Name => "adb";

        public IEnumerable<DeviceEntry> Discover() => throw new IOException("the cable is out");
    }
}
