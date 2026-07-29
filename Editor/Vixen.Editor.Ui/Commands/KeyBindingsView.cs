// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Input;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Vixen.Ui.Controls.Advanced;
using Vixen.Ui.Styling;

namespace Vixen.Editor.Ui;

/// <summary>One line of the keybinding editor: a command, and where its chord came from.</summary>
/// <param name="Id">The command's id, which is what a keymap file names.</param>
/// <param name="Title">What it is called on screen.</param>
/// <param name="Category">Where the palette files it.</param>
/// <remarks>
///     ⚠ <b>The chord is <i>not</i> on this record and is read from the map every time a cell is
///     filled.</b> A row that carried its chord would be a second copy of the binding, and the way
///     that goes wrong is a rebind that changes the map and leaves the grid showing the old key until
///     something happened to rebuild it.
/// </remarks>
public sealed record KeyBindingRow(string Id, string Title, string Category);

/// <summary>The panel doc 20's A5 asks for: every command, its chord, and where the chord came from.</summary>
/// <remarks>
///     <para>
///         <b><see cref="KeyMap" /> has had conflict detection, per-command overrides, a
///         defaults-versus-overrides split and reset since it was written, and no way to reach any of
///         it.</b> Doc 11 flags that and doc 20 spells out the panel: a grid of command / category /
///         binding / source, a filter box, a "press a key" capture, conflict reporting inline,
///         per-row and global reset, and import and export of a keymap file.
///     </para>
///     <para>
///         ⚠ <b>The preset dropdown is the point and the grid is the consolation.</b> Doc 20's own
///         note is that "presets matter more than they look" — a Unity user and an Unreal user
///         disagree about most of the bar and both are certain — so the one control that turns a week
///         of friction into a choice is the <see cref="Presets" /> picker, and it is first on the
///         strip for that reason.
///     </para>
///     <para>
///         ⚠ <b>Capture is a mode with a visible end, not a modal.</b> While it is on, every key the
///         panel sees is a candidate binding — including Escape, which is what cancels it, and which
///         is therefore the one chord this panel will not let you bind. The alternative is a dialog,
///         and a dialog that swallows keystrokes to record them cannot be driven by the automation
///         harness or screenshotted, which is <see cref="DialogService" />'s own argument turned round.
///     </para>
///     <para>
///         ⚠ <b>Import and export are events rather than file calls.</b> This assembly has no
///         <c>INativeDialogs</c> and is deliberately not allowed one — the shell is a
///         <c>UiDocument</c> and nothing else — so the panel says what the user asked for and the
///         application, which has the picker, answers. The same arrangement
///         <see cref="ConsoleView.Activated" /> uses and for the same reason.
///     </para>
/// </remarks>
public sealed partial class KeyBindingsView : Control {
    readonly List<KeyBindingRow> rows = [];

    CommandRegistry? commands;
    KeyMap? keys;

    string? filter;

    /// <inheritdoc />
    protected override string TagName => "keybindings-view";

    /// <inheritdoc />
    protected override bool AcceptsFocus => true;

    /// <summary>The strip along the top.</summary>
    public UiElement Toolbar { get; private set; } = null!;

    /// <summary>The filter box.</summary>
    public SearchBox Search { get; private set; } = null!;

    /// <summary>Which keymap preset is in force.</summary>
    public Select Presets { get; private set; } = null!;

    /// <summary>The button that puts the panel into capture mode.</summary>
    public Button Record { get; private set; } = null!;

    /// <summary>The button that asks for a keymap file to be read in.</summary>
    /// <remarks>
    ///     Exposed so that a host with no file picker can disable it rather than leaving a button
    ///     that does nothing — which is doc 20's second bar, and the one a panel breaks the second
    ///     time it is used.
    /// </remarks>
    public Button Import { get; private set; } = null!;

    /// <inheritdoc cref="Import" />
    public Button Export { get; private set; } = null!;

    /// <summary>The grid.</summary>
    public DataGrid Grid { get; private set; } = null!;

    /// <summary>The line under it that reports a conflict, or says what capture is waiting for.</summary>
    public UiElement Status { get; private set; } = null!;

    /// <summary>Whether the next chord pressed is a binding rather than a shortcut.</summary>
    public bool IsCapturing { get; private set; }

    /// <summary>Which command the grid has selected, or <see langword="null" />.</summary>
    public string? Selected =>
        Grid.Selection.Count == 1 && Grid.Items.ElementAtOrDefault(Grid.Selection.First()) is KeyBindingRow row
            ? row.Id
            : null;

    /// <summary>Raised when the user asks to read a keymap in from a file.</summary>
    public event Action<KeyBindingsView>? ImportRequested;

    /// <summary>Raised when they ask to write one out.</summary>
    public event Action<KeyBindingsView>? ExportRequested;

    /// <summary>Points the panel at a registry and the map that binds it.</summary>
    /// <param name="registry">Every command the editor knows.</param>
    /// <param name="map">What binds them.</param>
    public void Show(CommandRegistry registry, KeyMap map) {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(map);

        Detach();

        commands = registry;
        keys = map;

        // ⚠ Both, because either can change what a row says. A plugin loading adds commands; a
        // preset chosen from this very panel changes two hundred chords at once, and a grid that
        // only listened to one of the two would be right about half of that.
        map.Changed += KeysChanged;
        registry.Changed += CommandsChanged;

        Presets.Value = map.PresetName;
        Rebuild();
    }

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        Toolbar = Part("keybindings-toolbar");

        Search = Toolbar.Add<SearchBox>();
        Search.Placeholder = EditorStrings.KeysFilter.Text;

        Search.ValueChanged += (_, value) => {
            filter = string.IsNullOrWhiteSpace(value) ? null : value;
            Rebuild();
        };

        Presets = Toolbar.Add<Select>();
        Presets.Size = ControlSize.Small;

        foreach (var name in KeyMapPresets.Names) {
            Presets.AddOption(name, name);
        }

        Presets.SelectionChanged += (_, value) => Choose(value);

        Record = Button(EditorStrings.KeysRecord, () => Capture(!IsCapturing));
        Button(EditorStrings.KeysClear, () => Rebind(KeyChord.None, replace: true));
        Button(EditorStrings.KeysResetRow, ResetRow);
        Button(EditorStrings.KeysResetAll, () => keys?.Reset());
        Import = Button(EditorStrings.KeysImport, () => ImportRequested?.Invoke(this));
        Export = Button(EditorStrings.KeysExport, () => ExportRequested?.Invoke(this));

        Grid = Part<DataGrid>();
        Grid.MultiSelect = false;

        Column(EditorStrings.KeysColumnCommand.Text, 240f, row => row.Title);
        Column(EditorStrings.KeysColumnCategory.Text, 120f, row => row.Category);
        Column(EditorStrings.KeysColumnBinding.Text, 150f, row => Describe(row.Id));
        Column(EditorStrings.KeysColumnSource.Text, 90f, row => SourceOf(row.Id));

        Grid.SelectionChanged += _ => {
            // A selection change while waiting for a key is the user changing their mind about which
            // row they are binding, and silently binding the next keystroke to the new row is how a
            // capture mode surprises somebody.
            Capture(false);
            Restate();
        };

        // Double-clicking a row is how every keybinding editor starts a capture, and it is the one
        // gesture somebody tries before reading the strip.
        Grid.Activated += (_, _) => Capture(true);

        Status = Part("keybindings-status");

        // ⚠ On the capture leg and before the dispatcher, which is the whole of what capture mode
        // is. `CommandDispatcher` is attached to the document and would otherwise run whatever the
        // pressed chord is already bound to — so recording Ctrl+S would save the scene.
        AddHandler<KeyEvent>(
            static (element, args) => ((KeyBindingsView) element).Keyed(args),
            RoutingStrategy.Capture,
            handledEventsToo: true
        );

        Restate();
    }

    /// <inheritdoc />
    protected override void OnRemoved() {
        Detach();
        base.OnRemoved();
    }

    /// <summary>Rebuilds the row list from the registry, honouring the filter.</summary>
    public void Rebuild() {
        rows.Clear();

        if (commands is { } registry) {
            foreach (var command in registry.Commands) {
                var category = command.Category.Id is null ? string.Empty : command.Category.Text;

                if (!Matches(command.Id, command.Title.Text, category)) {
                    continue;
                }

                rows.Add(new KeyBindingRow(command.Id, command.Title.Text, category));
            }
        }

        rows.Sort(static (left, right) => string.CompareOrdinal(left.Id, right.Id));

        var chosen = Selected;

        Grid.SetItems(rows);

        // `SetItems` clears the selection, and a rebuild triggered by the very rebind the user just
        // made would otherwise take the row out from under them.
        if (chosen is not null) {
            var index = rows.FindIndex(row => string.Equals(row.Id, chosen, StringComparison.Ordinal));

            if (index >= 0) {
                Grid.Select(index);
            }
        }

        Restate();
    }

    /// <summary>Reads every cell again without rebuilding the list.</summary>
    public void Refresh() {
        Presets.Value = keys?.PresetName ?? KeyMapPresets.Vixen;

        Grid.Refresh();
        Restate();
    }

    /// <summary>Turns capture mode on or off.</summary>
    /// <param name="on">Which.</param>
    public void Capture(bool on) {
        var wanted = on && Selected is not null;

        if (IsCapturing == wanted) {
            return;
        }

        IsCapturing = wanted;

        if (wanted) {
            Document.Focus(this);
        }

        Restate();
    }

    /// <summary>Gives the selected command a chord.</summary>
    /// <param name="chord">The chord, or <see cref="KeyChord.None" /> to unbind it.</param>
    /// <param name="replace">Whether to take it off whatever has it.</param>
    /// <returns>What happened, or <see cref="BindResult.Unchanged" /> when no row is selected.</returns>
    /// <remarks>
    ///     ⚠ <b>A conflict is reported and <i>not</i> applied, and the second press is what replaces
    ///     it.</b> Silently taking a chord off another command is how somebody loses Ctrl+S without
    ///     ever being told; refusing outright would make the panel unable to express a swap at all.
    ///     Saying who has it and letting the same key be pressed again is what every editor that got
    ///     this right does.
    /// </remarks>
    public BindResult Rebind(KeyChord chord, bool replace = false) {
        if (keys is not { } map || Selected is not { } id) {
            return BindResult.Unchanged;
        }

        var result = map.Bind(id, chord, replace);

        Conflict = result == BindResult.Conflict ? map.CommandFor(chord, ContextOf(id)) : null;
        Pending = result == BindResult.Conflict ? chord : KeyChord.None;

        Restate();
        return result;
    }

    /// <summary>Which command holds the chord the user just tried to take, if the bind was refused.</summary>
    public string? Conflict { get; private set; }

    /// <summary>The chord that was refused, so pressing it again can mean "take it".</summary>
    public KeyChord Pending { get; private set; }

    void ResetRow() {
        if (keys is { } map && Selected is { } id) {
            map.ResetBinding(id);
        }
    }

    void Choose(string? name) {
        if (keys is not { } map || string.Equals(name, map.PresetName, StringComparison.Ordinal)) {
            return;
        }

        if (!map.UsePreset(name)) {
            Status.Text = string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                EditorStrings.KeysUnknownPreset.Text,
                name
            );
        }
    }

    void Keyed(KeyEvent args) {
        if (!IsCapturing || args.Action != KeyAction.Pressed) {
            return;
        }

        // ⚠ Escape leaves capture rather than being bound to anything. A panel that let Escape be
        // recorded would be one whose capture mode has no way out for whoever just bound it.
        if (args.Key == InputKey.Escape) {
            Capture(false);
            args.Handled = true;

            return;
        }

        var chord = KeyChord.Of(args);

        if (!chord.IsBound) {
            // A modifier held on its own. Swallowed anyway, so that holding Ctrl while reaching for
            // the letter does not run whatever Ctrl alone would have.
            args.Handled = true;
            return;
        }

        // ⚠ Back into the portable vocabulary before it is stored. `KeyChord.ForPlatform` is an
        // involution — see its own remarks — so a ⌘S pressed on a Mac becomes the Ctrl+S every
        // keymap file, menu model and preset in this editor is written in.
        chord = chord.ForPlatform();

        var replace = chord == Pending;

        if (Rebind(chord, replace) != BindResult.Conflict) {
            Capture(false);
        }

        args.Handled = true;
    }

    Button Button(StringId label, Action run) {
        var button = Toolbar.Add<Button>();

        button.Label = label.Text;
        button.Variant = ControlVariant.Subtle;
        button.Size = ControlSize.Small;
        button.Clicked += _ => run();

        return button;
    }

    void Column(string header, float width, Func<KeyBindingRow, string> value) {
        var column = Grid.AddColumn(header, item => item is KeyBindingRow row ? value(row) : string.Empty);

        column.Width = width;
    }

    string Describe(string id) => keys?.ChordFor(id).Describe() is { Length: > 0 } text ? text : "—";

    string SourceOf(string id) =>
        keys?.SourceOf(id) switch {
            BindingSource.User => EditorStrings.KeysSourceUser.Text,
            BindingSource.Preset => keys.PresetName,
            _ => EditorStrings.KeysSourceDefault.Text
        };

    string? ContextOf(string id) => commands is { } registry && registry.TryGet(id, out var command)
        ? command.Context
        : null;

    bool Matches(string id, string title, string category) =>
        filter is null
        || id.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || title.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || category.Contains(filter, StringComparison.OrdinalIgnoreCase);

    void Restate() {
        Record.Disabled = Selected is null;
        Record.Label = IsCapturing ? EditorStrings.KeysRecording.Text : EditorStrings.KeysRecord.Text;

        if (IsCapturing) {
            Record.State |= ElementState.Checked;
        } else {
            Record.State &= ~ElementState.Checked;
        }

        Status.RemoveClass("conflict");

        if (Conflict is { } holder) {
            Status.AddClass("conflict");

            Status.Text = string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                EditorStrings.KeysConflict.Text,
                Pending.Describe(),
                commands is { } registry && registry.TryGet(holder, out var command) ? command.Title.Text : holder
            );

            return;
        }

        Status.Text = IsCapturing
            ? EditorStrings.KeysWaiting.Text
            : Selected is null
                ? EditorStrings.KeysPickRow.Text
                : EditorStrings.KeysReady.Text;
    }

    /// <inheritdoc cref="MessageLogView.Detach" />
    void Detach() {
        if (keys is not null) {
            keys.Changed -= KeysChanged;
        }

        if (commands is not null) {
            commands.Changed -= CommandsChanged;
        }
    }

    void KeysChanged(KeyMap _) => Refresh();

    void CommandsChanged(CommandRegistry _) => Rebuild();
}
