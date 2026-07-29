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

    /// <summary>The row a secondary click landed on, which is what the context menu acts upon.</summary>
    InspectorRow? aimed;

    ContextMenu? menu;
    MenuItem copy = null!;
    MenuItem paste = null!;
    MenuItem reset = null!;
    MenuItem revert = null!;

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

    /// <summary>The strip above the rows: the search box and the lock.</summary>
    public UiElement Header { get; private set; } = null!;

    /// <summary>The search box above the rows.</summary>
    public SearchBox Search { get; private set; } = null!;

    /// <summary>Holds the inspector on what it is showing while the selection moves on.</summary>
    /// <remarks>
    ///     ⚠ <b>The verb every editor has and the one people reach for without being taught.</b>
    ///     Dragging an asset into a field on object A means selecting object B to find it, at which
    ///     point A is gone. Locking is what makes the two-handed operation possible at all.
    /// </remarks>
    public ToggleButton Lock { get; private set; } = null!;

    /// <summary>The region the rows scroll in.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The header is outside it and has to be.</b> A search box that scrolled away with
    ///         the rows is one you cannot reach at the moment you want it — the panel is long, which
    ///         is the only reason it scrolls, which is the only reason you are filtering it.
    ///     </para>
    ///     <para>
    ///         <b><see cref="ScrollView.Content" /> is public so that a host can put its own sections
    ///         under the rows and have them scroll with them.</b> The application's component
    ///         foldouts are the case: they are deliberately not part of this view — see
    ///         <c>ComponentsView</c> — but a panel with two independent scroll regions in it is one
    ///         where half the answer is always off screen.
    ///     </para>
    /// </remarks>
    public ScrollView Scroll { get; private set; } = null!;

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

    /// <summary>Whether the inspector is held on what it is showing.</summary>
    /// <remarks>
    ///     ⚠ <b>A locked inspector ignores <see cref="Inspect" /> and does not clear.</b> Anything
    ///     softer — following the selection but remembering the old one, say — is a panel whose
    ///     contents depend on which of two rules fired last.
    /// </remarks>
    public bool IsLocked {
        get => Lock.IsChecked;
        set => Lock.IsChecked = value;
    }

    /// <summary>Raised after an editor writes a member.</summary>
    public event Action<InspectorView, InspectorMember>? ValueChanged;

    /// <summary>Raised when the lock is turned on or off.</summary>
    /// <remarks>
    ///     What a host listens to so that it can push the selection in the moment the lock comes off
    ///     — the inspector refused everything while it was on, so it is showing something stale and
    ///     nothing else would tell it.
    /// </remarks>
    public event Action<InspectorView>? LockChanged;

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        Header = Part("inspector-header");

        Search = Header.Add<SearchBox>();
        Search.Placeholder = "Search";
        Search.ValueChanged += (_, _) => Filter();

        // ⚠ A word rather than a padlock, and deliberately. `ControlIcons` says in its own remarks
        // that it is not an icon set — it is the handful of shapes without which the controls here
        // cannot be drawn — and a padlock is not one of them. Inventing an icon set in the inspector
        // for one button would be the wrong place for it; the toolbar's set lives in the shell,
        // which this assembly does not and should not reference.
        Lock = Header.Add<ToggleButton>();
        Lock.Label = "Lock";
        Lock.Size = ControlSize.Small;
        Lock.Variant = ControlVariant.Subtle;
        Lock.AddClass("inspector-lock");
        Lock.CheckedChanged += (_, _) => LockChanged?.Invoke(this);

        Scroll = Part<ScrollView>();
        Body = Scroll.Content.Add<UiElement>("inspector-body");

        Empty = Part<EmptyState>();
        Empty.AddClass("hidden");

        AddHandler<ClickEvent>(static (element, args) => ((InspectorView) element).Chosen(args));

        // ⚠ On the capture leg, so the row is known before the menu this belongs to opens. A handler
        // that ran after the menu's own would decide which row the menu is about from whatever the
        // pointer had been over previously.
        AddHandler<PointerEvent>(
            static (element, args) => {
                if (args is { Action: PointerAction.Pressed, Button: PointerButton.Secondary }) {
                    ((InspectorView) element).aimed = ((InspectorView) element).RowAt(args.X, args.Y);
                }
            },
            RoutingStrategy.Capture,
            handledEventsToo: true
        );
    }

    /// <summary>Shows the members of some objects.</summary>
    /// <param name="objects">What to inspect. All of one type, or nothing is shown.</param>
    public void Inspect(params ReadOnlySpan<object> objects) {
        if (IsLocked) {
            return;
        }

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

            var row = InspectorRows.Add(
                section,
                field,
                Drawers,

                // The row restates itself from whatever wrote the member — its own drawer, a paste,
                // a gizmo. Subscribing here rather than having each write path call back is what
                // makes the reset button and the override bar impossible to leave stale.
                made => field.Changed += edited => {
                    InspectorRows.Restate(made);
                    ValueChanged?.Invoke(this, edited.Member);
                }
            );

            if (row is not null) {
                rows.Add(row);
            }
        }

        Filter();
    }

    UiElement Group(string title) {
        var expander = Body.Add<Expander>();
        expander.Label = title;
        expander.IsExpanded = true;

        return expander.Content;
    }

    static void Show(InspectorRow row) => InspectorRows.Show(row);

    /// <summary>Puts a Copy / Paste / Reset / Revert menu on the rows.</summary>
    /// <returns>The menu, so that a host can add lines of its own to it.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Built on demand rather than in <c>OnCreated</c>, and this is not laziness.</b> A
    ///         menu is a child of the document root, and a control's parts are made before it has a
    ///         document — so building one there would either throw or make an overlay attached to
    ///         nothing.
    ///     </para>
    ///     <para>
    ///         <b>Four lines, and every one of them already existed as a method nothing could
    ///         reach.</b> <see cref="Copy" />, <see cref="Paste" />, <see cref="Reset" /> and
    ///         <see cref="RevertToPrefab" /> have been on this type since it was written; the reset
    ///         button is the only one that had an affordance, and paste — the one that saves the most
    ///         time — had none at all.
    ///     </para>
    /// </remarks>
    public ContextMenu Contextualise() {
        if (menu is not null) {
            return menu;
        }

        menu = Document.Root.Add<ContextMenu>();

        copy = Line("Copy", () => aimed is { } row && Copy(row));
        paste = Line("Paste", () => aimed is { } row && Paste(row));

        menu.AddSeparator();

        reset = Line("Reset to Default", () => aimed is { } row && Reset(row));
        revert = Line("Revert to Prefab", () => aimed is { } row && RevertToPrefab(row));

        // ⚠ Asked on the way open rather than kept in step with every write. Whether there is
        // something on the clipboard, whether this member differs from its default and whether it
        // came from a prefab are three questions with three different answers per row, and pushing
        // them would mean four flags maintained by every path that writes anything.
        menu.OpenChanged += (_, isOpen) => {
            if (isOpen) {
                Enable();
            }
        };

        menu.Attach(Body);
        return menu;

        MenuItem Line(string label, Func<bool> run) {
            var item = menu.AddItem(label);

            item.Clicked += _ => run();
            return item;
        }
    }

    void Enable() {
        var field = aimed?.Field;

        copy.Disabled = field is null;
        paste.Disabled = field is null || !field.CanWrite || !Clipboard.CanPaste(field);
        reset.Disabled = field is null || !field.CanReset || !field.IsModified;
        revert.Disabled = field is null || !field.IsOverridden;
    }

    /// <summary>The row under a point, if any.</summary>
    InspectorRow? RowAt(float x, float y) {
        foreach (var row in rows) {
            var bounds = row.Bounds;

            if (x >= bounds.X && x < bounds.X + bounds.Width && y >= bounds.Y && y < bounds.Y + bounds.Height) {
                return row;
            }
        }

        return null;
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
