// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Inspector;
using Vixen.Ui;
using Vixen.Ui.Controls;

namespace Vixen.Editor.AssetEditors.Importing;

/// <summary>One cell of the matrix: a tick that says whether this target overrides, and the editor.</summary>
public sealed class OverrideCell : Control {
    /// <inheritdoc />
    protected override string TagName => "override-cell";

    /// <inheritdoc />
    protected override bool AcceptsFocus => false;

    /// <summary>The tick, absent in the base column.</summary>
    public CheckBox? Toggle { get; internal set; }

    /// <summary>The member this cell edits, bound to one row's settings object.</summary>
    public InspectorField Field { get; internal set; } = null!;

    /// <summary>What drew the editor.</summary>
    public IPropertyDrawer Drawer { get; internal set; } = null!;

    /// <summary>What the drawer built.</summary>
    public UiElement Editor { get; internal set; } = null!;
}

/// <summary>The per-target import settings, as a grid of settings against build targets.</summary>
/// <remarks>
///     <para>
///         <b>Doc 08's <c>overrides</c> block, drawn.</b> A row is one setting, a column is one
///         target, and the leftmost column is the base every target starts from. That orientation is
///         the one that answers the question people actually have — "which platforms disagree about
///         compression" is read across a row — and it is why this is a grid rather than a group per
///         target.
///     </para>
///     <para>
///         <b>The cells are the inspector's own drawers.</b> The matrix knows what a member is and
///         nothing about what an enum, a bool or an int looks like; a cell is an
///         <see cref="InspectorField" /> over one target's settings object and whatever
///         <see cref="DrawerRegistry" /> resolves for it. So a setting added to an importer appears
///         here with its right editor, and a custom drawer a plugin registers works in the matrix
///         without knowing the matrix exists.
///     </para>
///     <para>
///         ⚠ <b>An unticked cell is still an editor, and it is still live.</b> It shows the value
///         the target will build with, which is the base's — so ticking a box and typing is two
///         actions rather than three, and untying them would mean a column of blanks whose meaning
///         is "look left". Typing into an unticked cell writes the row's own object and does
///         nothing to the build until the box is ticked, which is the same shape as every
///         override editor that has this problem.
///     </para>
///     <para>
///         ⚠ <b>Rebuilt whenever the rows change, and only then.</b>
///         <see cref="ImportSettingsDocument.OverridesChanged" /> is raised for a target appearing or
///         a tick moving; an ordinary value edit is not one, because a grid that rebuilt itself on
///         every keystroke would take the focus out of the field being typed into.
///     </para>
/// </remarks>
public sealed class TargetOverrideMatrix : Control {
    readonly List<OverrideCell> cells = [];
    readonly Dictionary<UiElement, string> removals = [];

    ImportSettingsDocument? document;
    InspectorDescriptor? descriptor;

    /// <inheritdoc />
    protected override string TagName => "override-matrix";

    /// <inheritdoc />
    protected override bool AcceptsFocus => false;

    /// <summary>Which drawer edits which member.</summary>
    public DrawerRegistry Drawers { get; set; } = DrawerRegistry.Default;

    /// <summary>Where the header and the setting rows go.</summary>
    public UiElement Body { get; private set; } = null!;

    /// <summary>The field a new target's name is typed into.</summary>
    public TextBox TargetName { get; private set; } = null!;

    /// <summary>The button that adds the typed target.</summary>
    public Button AddTarget { get; private set; } = null!;

    /// <summary>Shown instead of the grid when the settings type has nothing to override.</summary>
    public EmptyState Empty { get; private set; } = null!;

    /// <summary>Every cell in the grid, in row-major order.</summary>
    public IReadOnlyList<OverrideCell> Cells => cells;

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        Body = Part("override-body");

        var bar = Part("override-bar");

        TargetName = bar.Add<TextBox>();
        TargetName.Placeholder = "Android, or Android/Vulkan";

        AddTarget = bar.Add<Button>();
        AddTarget.Label = "Add target";

        Empty = Part<EmptyState>();
        Empty.AddClass("hidden");

        AddHandler<ClickEvent>(static (element, args) => ((TargetOverrideMatrix) element).Chosen(args));
    }

    /// <summary>Shows a document's overrides.</summary>
    /// <param name="settings">The document.</param>
    public void Show(ImportSettingsDocument settings) {
        ArgumentNullException.ThrowIfNull(settings);

        if (document is { } previous) {
            previous.OverridesChanged -= Reload;
        }

        document = settings;
        descriptor = InspectorRegistry.Find(settings.Settings.GetType());

        settings.OverridesChanged += Reload;
        Rebuild();
    }

    /// <summary>Rebuilds the grid from the document as it stands.</summary>
    public void Rebuild() {
        while (Body.Children.Count > 0) {
            Body.Children[^1].Remove();
        }

        cells.Clear();

        if (document is not { } settings || descriptor is not { } type || type.Members.Count == 0) {
            Empty.RemoveClass("hidden");
            Empty.Title = "No per-target settings";

            Empty.Description = "This importer's settings type declares nothing an override could change.";

            return;
        }

        Empty.AddClass("hidden");

        removals.Clear();

        var header = Body.Add("override-row", null, "header");
        header.Add("override-name").Text = "Setting";
        Column(header, "Base");

        foreach (var row in settings.Overrides) {
            var column = Column(header, row.Target);

            // The remove button lives in the header rather than in a cell, because removing a target
            // takes the whole column and a button inside one of its cells would read as removing
            // that setting's override.
            var remove = column.Add<IconButton>();
            remove.LeadingIcon.Geometry = ControlIcons.Close;
            remove.Variant = ControlVariant.Subtle;
            remove.Size = ControlSize.Small;
            remove.Label = "Remove " + row.Target;

            // The button is remembered by identity rather than carrying its target as data, because
            // a UiElement has no user-data slot and inventing one on a control set for a panel's
            // convenience would be the wrong assembly to change.
            removals[remove] = row.Target;
        }

        foreach (var member in type.Members) {
            var line = Body.Add("override-row");
            line.Add("override-name").Text = member.DisplayName;

            Cell(line, settings, member, target: null);

            foreach (var row in settings.Overrides) {
                Cell(line, settings, member, row);
            }
        }
    }

    /// <summary>Reads every cell back from the objects behind it, without rebuilding.</summary>
    public void Reload() {
        foreach (var cell in cells) {
            using (cell.Field.Refreshing()) {
                cell.Drawer.Show(cell.Field, cell.Editor);
            }
        }
    }

    void Reload(ImportSettingsDocument changed) => Rebuild();

    static UiElement Column(UiElement header, string title) {
        var column = header.Add("override-column");
        column.Add("override-title").Text = title;

        return column;
    }

    void Cell(UiElement line, ImportSettingsDocument settings, InspectorMember member, TargetOverride? target) {
        var cell = line.Add<OverrideCell>();
        var owner = target?.Settings ?? settings.Settings;

        if (target is not null) {
            var toggle = cell.Add<CheckBox>();
            toggle.IsChecked = target.IsOverridden(member.Name);

            toggle.CheckedChanged += (_, value) => {
                settings.SetOverridden(target, member.Name, value);
                Restate(cell, target, member);
            };

            cell.Toggle = toggle;
        }

        cell.Field = new(descriptor!, member, [owner], settings);

        var drawer = Drawers.Resolve(member);

        if (drawer is null) {
            return;
        }

        cell.Drawer = drawer;
        cell.Editor = drawer.Build(cell.Field, cell);

        using (cell.Field.Refreshing()) {
            drawer.Show(cell.Field, cell.Editor);
        }

        if (target is not null) {
            Restate(cell, target, member);
        }

        cells.Add(cell);
    }

    static void Restate(OverrideCell cell, TargetOverride target, InspectorMember member) {
        // The one thing a cell says about itself: whether this column decides the value or merely
        // repeats the base's. Muted rather than hidden, so the column stays a column.
        if (target.IsOverridden(member.Name)) {
            cell.AddClass("overridden");
        } else {
            cell.RemoveClass("overridden");
        }
    }

    void Chosen(ClickEvent args) {
        if (document is not { } settings) {
            return;
        }

        for (var element = args.Source; element is not null; element = element.Parent) {
            if (ReferenceEquals(element, AddTarget)) {
                Add(settings);
                args.Handled = true;

                return;
            }

            if (removals.TryGetValue(element, out var target)) {
                settings.RemoveTarget(target);
                args.Handled = true;

                return;
            }
        }
    }

    void Add(ImportSettingsDocument settings) {
        var target = (TargetName.Value ?? string.Empty).Trim();

        // A blank or duplicate target is refused quietly by doing nothing, because the field is
        // still there with the text in it — an error toast for "you have not typed anything yet"
        // is noise.
        if (target.Length == 0 || settings.Find(target) is not null) {
            return;
        }

        settings.AddTarget(target);
        TargetName.Value = string.Empty;
    }
}
