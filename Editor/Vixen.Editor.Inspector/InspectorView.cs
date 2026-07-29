// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Core;
using Vixen.Ui;
using Vixen.Ui.Controls;

namespace Vixen.Editor.Inspector;

/// <summary>One member's row: its name, its editor, and the buttons that put it back.</summary>
public sealed class InspectorRow : Control {
    /// <inheritdoc />
    protected override string TagName => "inspector-row";

    /// <inheritdoc />
    protected override bool AcceptsFocus => false;

    /// <summary>The member this row edits, bound to what is being inspected.</summary>
    public InspectorField Field { get; internal set; } = null!;

    /// <summary>What drew the editor.</summary>
    public IPropertyDrawer Drawer { get; internal set; } = null!;

    /// <summary>What the drawer built.</summary>
    public UiElement Editor { get; internal set; } = null!;

    /// <summary>The name on the left.</summary>
    public UiElement Label { get; private set; } = null!;

    /// <summary>Where the editor goes.</summary>
    public UiElement Slot { get; private set; } = null!;

    /// <summary>The button that puts the member back to its type's default.</summary>
    public IconButton Reset { get; private set; } = null!;

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        Label = Part("inspector-label");
        Slot = Part("inspector-editor");

        Reset = Part<IconButton>();
        Reset.LeadingIcon.Geometry = ControlIcons.Close;
        Reset.Variant = ControlVariant.Subtle;
        Reset.Label = "Reset";
        Reset.Size = ControlSize.Small;
        Reset.TabIndex = -1;
    }
}

/// <summary>The inspector: editors generated from what a type's attributes say about it.</summary>
/// <remarks>
///     <para>
///         <b>Everything it draws came from a generator.</b> The member list, the attribute metadata
///         and the accessors are compile-time facts registered by module initializers — no reflection
///         pass, no assembly scan, and a member that could not be described was a build warning
///         rather than a row that quietly never appears.
///     </para>
///     <para>
///         <b>Several targets at once, which is what an inspector is for.</b> Selecting twenty
///         objects and setting one field on all of them is the operation; showing the first one's
///         values and silently editing only that is the bug. Where the targets disagree the editors
///         say so, and typing into one writes to every one of them —
///         <see cref="InspectorField.Read" /> and <see cref="InspectorField.Write" /> are where that
///         lives, so a third-party drawer gets it for free.
///     </para>
///     <para>
///         <b>Every edit is an <c>IEditorCommand</c> on the document's stack</b>, produced by the
///         field rather than by the drawer, so undo works without per-drawer effort and a drawer that
///         wrote a member directly is a visible mistake rather than an invisible one.
///     </para>
/// </remarks>
public sealed class InspectorView : Control {
    readonly List<InspectorRow> rows = [];
    readonly List<object> targets = [];

    /// <inheritdoc />
    protected override string TagName => "inspector";

    /// <inheritdoc />
    protected override bool AcceptsFocus => false;

    /// <summary>Which drawer edits which member.</summary>
    public DrawerRegistry Drawers { get; set; } = DrawerRegistry.Default;

    /// <summary>Where copy-property puts things.</summary>
    public PropertyClipboard Clipboard { get; set; } = PropertyClipboard.Default;

    /// <summary>Where edits are recorded.</summary>
    /// <remarks>
    ///     Not called <c>Document</c>: a <c>UiElement</c> already has one of those and it is the
    ///     interface tree this control lives in, which is a different thing entirely.
    /// </remarks>
    public EditorDocument? EditedDocument { get; set; }

    /// <summary>What the inspected objects were made from, for override marks and revert.</summary>
    public IPrefabSource? Prefab { get; set; }

    /// <summary>The search box above the rows.</summary>
    public SearchBox Search { get; private set; } = null!;

    /// <summary>Where the rows go.</summary>
    public UiElement Body { get; private set; } = null!;

    /// <summary>Shown instead of the rows when the selection has nothing in common.</summary>
    public EmptyState Empty { get; private set; } = null!;

    /// <summary>What is being inspected.</summary>
    public IReadOnlyList<object> Targets => targets;

    /// <summary>The rows, in order.</summary>
    public IReadOnlyList<InspectorRow> Rows => rows;

    /// <summary>The type every target has in common, if they have one.</summary>
    public InspectorDescriptor? Descriptor { get; private set; }

    /// <summary>Raised after an editor writes a member.</summary>
    public event Action<InspectorView, InspectorMember>? ValueChanged;

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        Search = Part<SearchBox>();
        Search.Placeholder = "Search";
        Search.ValueChanged += (_, _) => Filter();

        Body = Part("inspector-body");

        Empty = Part<EmptyState>();
        Empty.AddClass("hidden");

        AddHandler<ClickEvent>(static (element, args) => ((InspectorView) element).Chosen(args));
    }

    /// <summary>Shows the members of some objects.</summary>
    /// <param name="objects">What to inspect. All of one type, or nothing is shown.</param>
    public void Inspect(params ReadOnlySpan<object> objects) {
        targets.Clear();

        foreach (var target in objects) {
            ArgumentNullException.ThrowIfNull(target);
            targets.Add(target);
        }

        Descriptor = InspectorRegistry.CommonType(targets) is { } type ? InspectorRegistry.Find(type) : null;
        Rebuild();
    }

    /// <summary>Shows nothing.</summary>
    public void Clear() => Inspect([]);

    /// <summary>Reads every editor back from the objects, without rebuilding anything.</summary>
    /// <remarks>
    ///     What a gizmo dragging an object calls, forty times a second. The rows already exist and
    ///     their handlers are already wired; all that changed is the numbers, and rebuilding would
    ///     take the focus out of whatever the user was typing into.
    /// </remarks>
    public void Reload() {
        foreach (var row in rows) {
            Show(row);
        }
    }

    /// <summary>Copies a row's value.</summary>
    /// <param name="row">The row.</param>
    /// <returns>Whether there was one value to copy.</returns>
    public bool Copy(InspectorRow row) {
        Owned(row);

        return Clipboard.Copy(row.Field);
    }

    /// <summary>Pastes into a row.</summary>
    /// <param name="row">The row.</param>
    /// <returns>Whether anything changed.</returns>
    public bool Paste(InspectorRow row) {
        Owned(row);

        if (!Clipboard.Paste(row.Field)) {
            return false;
        }

        Show(row);
        return true;
    }

    /// <summary>Puts a row back to its type's default.</summary>
    /// <param name="row">The row.</param>
    /// <returns>Whether anything changed.</returns>
    public bool Reset(InspectorRow row) {
        Owned(row);

        if (!row.Field.Reset()) {
            return false;
        }

        row.Field.Seal();
        Show(row);

        return true;
    }

    /// <summary>Puts a row back to the prefab's value.</summary>
    /// <param name="row">The row.</param>
    /// <returns>Whether anything changed.</returns>
    public bool RevertToPrefab(InspectorRow row) {
        Owned(row);

        if (!row.Field.RevertToPrefab()) {
            return false;
        }

        row.Field.Seal();
        Show(row);

        return true;
    }

    /// <summary>Refuses a row that belongs to another inspector.</summary>
    /// <remarks>
    ///     A row holds the field it edits, so acting on a foreign one would succeed and would write
    ///     to whatever <i>that</i> inspector had selected. Cheap to check and impossible to debug
    ///     from the far end.
    /// </remarks>
    void Owned(InspectorRow row) {
        ArgumentNullException.ThrowIfNull(row);

        if (!rows.Contains(row)) {
            throw new ArgumentException(
                "That row belongs to a different inspector, and acting on it would edit whatever that "
                + "inspector has selected rather than this one's.",
                nameof(row)
            );
        }
    }

    void Rebuild() {
        while (Body.Children.Count > 0) {
            Body.Children[^1].Remove();
        }

        rows.Clear();

        if (Descriptor is not { } descriptor) {
            Empty.RemoveClass("hidden");

            Empty.Title = targets.Count == 0
                ? "Nothing selected"
                : "Mixed selection";

            Empty.Description = targets.Count == 0
                ? null
                : "These objects have no type in common, so there is no set of editors that fits all of them.";

            return;
        }

        Empty.AddClass("hidden");

        UiElement section = Body;

        foreach (var member in descriptor.Members) {
            // A heading belongs to the member that follows it, so a new one starts a section and
            // everything after it lands there until the next heading. Sections are therefore in
            // member order, which is the order the type's author wrote them in.
            if (member.Header is { } header) {
                section = Group(header);
            }

            var field = new InspectorField(descriptor, member, targets, EditedDocument, Prefab);

            if (!field.IsVisible) {
                continue;
            }

            var drawer = Drawers.Resolve(member);

            if (drawer is null) {
                continue;
            }

            var row = section.Add<InspectorRow>();
            row.Field = field;

            // The row restates itself from whatever wrote the member — its own drawer, a paste, a
            // gizmo. Subscribing here rather than having each write path call back is what makes the
            // reset button and the override bar impossible to leave stale.
            field.Changed += edited => {
                Restate(row);
                ValueChanged?.Invoke(this, edited.Member);
            };

            row.Drawer = drawer;
            row.Label.Text = member.DisplayName;

            if (member.Tooltip is { } text) {
                // Attached to the label rather than the row, so hovering the editor does not cover
                // the thing being edited with a description of it.
                var tooltip = row.Add<Tooltip>();
                tooltip.Label = text;
                tooltip.Attach(row.Label);
            }

            row.Editor = drawer.Build(field, row.Slot);

            Show(row);
            rows.Add(row);
        }

        Filter();
    }

    UiElement Group(string title) {
        var expander = Body.Add<Expander>();
        expander.Label = title;
        expander.IsExpanded = true;

        return expander.Content;
    }

    static void Show(InspectorRow row) {
        using (row.Field.Refreshing()) {
            row.Drawer.Show(row.Field, row.Editor);
        }

        Restate(row);
    }

    /// <summary>Shows the reset button only when there is something to reset to.</summary>
    /// <remarks>
    ///     ⚠ <b>Two different marks, and they are not the same question.</b> "Differs from the type's
    ///     default" is what the reset button answers; "differs from the prefab this came from" is
    ///     what the override bar answers, and an object can be either without being the other. An
    ///     inspector that collapsed them into one indicator is one where reverting to prefab looks
    ///     available on a scene object that never came from one.
    /// </remarks>
    static void Restate(InspectorRow row) {
        if (row.Field.CanReset && row.Field.IsModified) {
            row.Reset.RemoveClass("hidden");
        } else {
            row.Reset.AddClass("hidden");
        }

        if (row.Field.IsOverridden) {
            row.AddClass("overridden");
        } else {
            row.RemoveClass("overridden");
        }
    }

    void Chosen(ClickEvent args) {
        if (args.Source is not IconButton button) {
            return;
        }

        foreach (var row in rows) {
            if (ReferenceEquals(row.Reset, button)) {
                Reset(row);
                args.Handled = true;

                return;
            }
        }
    }

    /// <summary>Hides the rows whose names do not match what is in the search box.</summary>
    /// <remarks>
    ///     ⚠ <b>Hidden rather than removed</b>, so clearing the box costs a restyle instead of
    ///     rebuilding every editor — and so a field somebody was halfway through typing into survives
    ///     a stray keystroke in the search box.
    /// </remarks>
    void Filter() {
        var text = Search.Value;

        foreach (var row in rows) {
            var matches = string.IsNullOrEmpty(text)
                || row.Field.Member.DisplayName.Contains(text, StringComparison.OrdinalIgnoreCase)
                || row.Field.Member.Name.Contains(text, StringComparison.OrdinalIgnoreCase);

            if (matches) {
                row.RemoveClass("filtered");
            } else {
                row.AddClass("filtered");
            }
        }
    }
}
