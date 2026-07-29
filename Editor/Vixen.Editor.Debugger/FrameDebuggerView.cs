// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Vixen.Ui.Controls.Advanced;

namespace Vixen.Editor.Debugger;

/// <summary>A frame's command stream, steppable, with the state at each call beside it.</summary>
/// <remarks>
///     <para>
///         <b>Doc 20's E4 exit criterion: "a draw call can be stepped and its render target
///         inspected".</b> The first half is here in full — the tree, the two step buttons, and a
///         state pane replayed to whichever call is selected. The second half is honest about what it
///         has: a capture taken from a recording backend holds the state and not the pixels, and the
///         target pane says which attachments the draw wrote rather than showing an image that would
///         be a fabrication.
///     </para>
///     <para>
///         ⚠ <b>Stepping moves between draws, not between commands.</b> A frame is a few thousand
///         calls and forty of them per draw are binds; a step that advanced one command would take
///         forty presses to reach the next thing that put a pixel anywhere.
///         <see cref="FrameCapture.Work" /> is the index that makes it one press.
///     </para>
///     <para>
///         ⚠ <b>The tree is rebuilt on capture rather than every frame.</b> A capture does not change
///         while it is on screen — that is the whole point of capturing one — so the only work per
///         frame here is none.
///     </para>
/// </remarks>
public sealed partial class FrameDebuggerView : Control {
    readonly List<UiElement> stateRows = [];

    FrameCapture capture = FrameCapture.Empty;
    int selected;

    /// <inheritdoc />
    protected override string TagName => "frame-debugger";

    /// <inheritdoc />
    protected override bool AcceptsFocus => false;

    /// <summary>The strip along the top.</summary>
    public UiElement Toolbar { get; private set; } = null!;

    /// <summary>What takes a capture, when the host has given it something to take one with.</summary>
    public Button CaptureButton { get; private set; } = null!;

    /// <summary>Steps back one draw.</summary>
    public Button Previous { get; private set; } = null!;

    /// <summary>Steps forward one draw.</summary>
    public Button Next { get; private set; } = null!;

    /// <summary>What the capture holds, and where in it the selection is.</summary>
    public UiElement Status { get; private set; } = null!;

    /// <summary>The stream, as passes and groups.</summary>
    public TreeView Tree { get; private set; } = null!;

    /// <summary>What is bound at the selected call.</summary>
    public UiElement StatePane { get; private set; } = null!;

    /// <summary>What the capture is taken with, or <see langword="null" /> when nothing can.</summary>
    /// <remarks>
    ///     A delegate rather than a device, because what a capture is <i>of</i> is the host's
    ///     arbitration — the editor's own frame, or a running game's — and this assembly deliberately
    ///     does not know which.
    /// </remarks>
    public Func<FrameCapture>? Source { get; set; }

    /// <summary>Why a capture cannot be taken, shown when <see cref="Source" /> is null.</summary>
    public string? Unavailable { get; set; }

    /// <summary>What it is showing.</summary>
    public FrameCapture Capture => capture;

    /// <summary>Which call the state pane is replayed to.</summary>
    public int Sequence => selected;

    /// <summary>Takes a fresh capture, if there is anything to take one with.</summary>
    public void Take() {
        if (Source?.Invoke() is { } taken) {
            Show(taken);
        }
    }

    /// <summary>Shows a capture somebody else took.</summary>
    /// <param name="frame">The capture.</param>
    /// <exception cref="ArgumentNullException"><paramref name="frame" /> is null.</exception>
    public void Show(FrameCapture frame) {
        ArgumentNullException.ThrowIfNull(frame);

        capture = frame;
        selected = frame.Work.Count > 0 ? frame.Work[0] : 0;

        Rebuild();
        Restate();
    }

    /// <summary>Moves the selection to a call.</summary>
    /// <param name="sequence">Which call. Out-of-range values clamp.</param>
    public void StepTo(int sequence) {
        selected = capture.IsEmpty ? 0 : Math.Clamp(sequence, 0, capture.Commands.Count - 1);
        Restate();
    }

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        Toolbar = Part("debugger-toolbar");

        CaptureButton = Toolbar.Add<Button>();
        CaptureButton.Size = ControlSize.Small;
        CaptureButton.Variant = ControlVariant.Primary;
        CaptureButton.Label = "Capture Frame";
        CaptureButton.Clicked += _ => Take();

        Previous = Toolbar.Add<Button>();
        Previous.Size = ControlSize.Small;
        Previous.Variant = ControlVariant.Subtle;
        Previous.Label = "◀ Draw";

        Previous.Clicked += _ => {
            if (capture.PreviousWork(selected - 1) is { } target) {
                StepTo(target);
            }
        };

        Next = Toolbar.Add<Button>();
        Next.Size = ControlSize.Small;
        Next.Variant = ControlVariant.Subtle;
        Next.Label = "Draw ▶";

        Next.Clicked += _ => {
            if (capture.NextWork(selected + 1) is { } target) {
                StepTo(target);
            }
        };

        Status = Part("debugger-status");

        var body = Part("debugger-body");

        Tree = body.Add<TreeView>();

        Tree.SelectionChanged += tree => {
            foreach (var node in tree.Selection) {
                if (node.Tag is CaptureNode captured) {
                    StepTo(captured.Command.Sequence);
                    return;
                }
            }
        };

        StatePane = body.Add<UiElement>("debugger-state");

        Restate();
    }

    /// <summary>Rebuilds the tree from the capture.</summary>
    /// <remarks>
    ///     ⚠ <b>Passes and groups are expanded and their contents are not.</b> A frame's structure is
    ///     what somebody is looking for; every bind under every draw expanded on open is a tree with
    ///     two thousand visible rows and no structure at all.
    /// </remarks>
    void Rebuild() {
        while (Tree.Root.Children.Count > 0) {
            Tree.Root.Remove(Tree.Root.Children[^1]);
        }

        foreach (var root in capture.Roots) {
            Add(Tree.Root, root);
        }

        Tree.Refresh();
    }

    void Add(TreeNode into, CaptureNode captured) {
        var node = into.Add(new TreeNode(Label(captured), captured));

        foreach (var child in captured.Children) {
            Add(node, child);
        }

        // ⚠ After the children, because `TreeView.Expand` is what maintains the flattened visible
        // list and a node expanded while it was still empty is one the list has no rows for.
        if (captured.Command.Kind is CaptureCommandKind.BeginPass or CaptureCommandKind.PushGroup) {
            Tree.Expand(node);
        }
    }

    static string Label(CaptureNode node) =>
        node.Command.Kind is CaptureCommandKind.BeginPass or CaptureCommandKind.PushGroup
            ? string.Create(CultureInfo.InvariantCulture, $"{node.Label} — {node.WorkCount} draw(s)")
            : node.Label;

    void Restate() {
        var steppable = capture.WorkCount > 0;

        CaptureButton.Disabled = Source is null;
        Previous.Disabled = !steppable || capture.PreviousWork(selected - 1) is null;
        Next.Disabled = !steppable || capture.NextWork(selected + 1) is null;

        Status.Text = Sentence();
        ShowState();
    }

    string Sentence() {
        if (capture.IsEmpty) {
            return Unavailable ?? (Source is null
                ? "Nothing here can take a capture."
                : "Press Capture Frame.");
        }

        var index = 0;

        for (var work = 0; work < capture.Work.Count; work++) {
            if (capture.Work[work] <= selected) {
                index = work + 1;
            }
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{capture.Name} — {capture.Commands.Count:N0} call(s), {capture.WorkCount:N0} draw(s). "
            + $"At command {selected:N0}, draw {index:N0}."
        );
    }

    void ShowState() {
        var rows = capture.StateAt(selected).Rows();
        var slot = 0;
        string? group = null;

        foreach (var row in rows) {
            if (!string.Equals(group, row.Group, StringComparison.Ordinal)) {
                group = row.Group;
                Row(ref slot, "state-heading", row.Group, null);
            }

            Row(ref slot, "state-row", row.Label, row.Value);
        }

        for (var index = slot; index < stateRows.Count; index++) {
            stateRows[index].AddClass("parked");
        }
    }

    void Row(ref int slot, string className, string label, string? value) {
        while (stateRows.Count <= slot) {
            var built = StatePane.Add<UiElement>("state-line");

            built.Add<UiElement>("state-label");
            built.Add<UiElement>("state-value");

            stateRows.Add(built);
        }

        var row = stateRows[slot++];

        row.RemoveClass("parked");
        row.RemoveClass(className == "state-row" ? "state-heading" : "state-row");
        row.AddClass(className);

        row.Children[0].Text = label;
        row.Children[1].Text = value;
    }
}
