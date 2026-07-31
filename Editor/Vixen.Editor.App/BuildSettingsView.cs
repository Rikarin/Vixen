// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Editor.Assets.Content;
using Vixen.Editor.Core;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Vixen.Ui.Controls.Advanced;

namespace Vixen.Editor.App;

/// <summary>Doc 20's B7: what a player build is, and the two buttons that run one.</summary>
/// <remarks>
///     <para>
///         <b>Five fields and a list, over <see cref="PlayerBuild" />'s calls.</b> Target,
///         configuration, output path and the scenes that ship are the four doc 20 names; the fifth
///         thing on the window is the sentence saying why Build is greyed, which is the same rule
///         every unimplemented menu line follows and is what stops "it does nothing when I press it".
///     </para>
///     <para>
///         ⚠ <b>No Apply, and this is the one settings surface where that is right.</b> Doc 20's A4
///         asks for an explicit Apply because its two settings cost something to change — lowering the
///         undo depth drops history, changing the content target invalidates an import. Nothing here
///         costs anything until Build is pressed, and Build is the thing that <i>reads</i> these
///         fields: an edit that had not been applied yet would mean the button building something
///         other than what is on screen, which is worse than a file written per keystroke.
///     </para>
///     <para>
///         ⚠ <b>The scene list is the panel's own control rather than the inspector's list drawer.</b>
///         A list of strings drawn generically is a column of text boxes; what this list needs is
///         order (the first entry is what <c>AppConfig.StartupScene</c> defaults to), a picker that
///         offers only scenes that exist, and a column saying which entries a build would refuse.
///         None of those three is expressible as a property attribute.
///     </para>
/// </remarks>
sealed partial class BuildSettingsView : Control {
    readonly List<SceneRow> rows = [];

    PlayerBuildSettings? settings;

    /// <summary>Whether the controls are being filled in rather than typed into.</summary>
    bool loading;

    /// <inheritdoc />
    protected override string TagName => "build-settings";

    /// <inheritdoc />
    protected override bool AcceptsFocus => false;

    /// <summary>The three fields at the top.</summary>
    public UiElement Form { get; private set; } = null!;

    /// <summary>Which platform the player is built for.</summary>
    public Select TargetPicker { get; private set; } = null!;

    /// <summary>Which of doc 17's variants it is built as.</summary>
    public Select VariantPicker { get; private set; } = null!;

    /// <summary>Where the artefact goes.</summary>
    public TextBox Output { get; private set; } = null!;

    /// <summary>What the three fields resolve to, spelled out.</summary>
    public UiElement Note { get; private set; } = null!;

    /// <summary>The scenes that are not in the build yet.</summary>
    public Select ScenePicker { get; private set; } = null!;

    /// <summary>Puts the chosen scene in the build.</summary>
    public Button AddScene { get; private set; } = null!;

    /// <summary>Takes the selected row out of it.</summary>
    public Button RemoveScene { get; private set; } = null!;

    /// <summary>Moves the selected row one earlier.</summary>
    public Button MoveUp { get; private set; } = null!;

    /// <summary>And one later.</summary>
    public Button MoveDown { get; private set; } = null!;

    /// <summary>The scenes that ship, in order.</summary>
    public DataGrid Scenes { get; private set; } = null!;

    /// <summary>Builds without running.</summary>
    public Button BuildButton { get; private set; } = null!;

    /// <summary>Builds and launches what came out.</summary>
    public Button RunButton { get; private set; } = null!;

    /// <summary>Why the two buttons are greyed, or what the last build did.</summary>
    public UiElement Status { get; private set; } = null!;

    /// <summary>Raised when a field or the scene list was edited, so the change can be persisted.</summary>
    public event Action<BuildSettingsView>? Changed;

    /// <summary>Raised by the two buttons. The flag is whether to launch what was built.</summary>
    public event Action<BuildSettingsView, bool>? BuildRequested;

    /// <summary>Every scene the project has, project-relative, or <see langword="null" /> for none.</summary>
    /// <remarks>
    ///     Pulled rather than handed over, because a panel's factory runs again when it is reopened
    ///     and because the answer changes: creating a scene while this panel is open should put it in
    ///     the picker, and it does the next time the list is rebuilt.
    /// </remarks>
    public Func<IEnumerable<string>>? Catalogue { get; set; }

    /// <summary>What is wrong with an entry, or <see langword="null" /> when nothing is.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The two things this list can be wrong about, and a build refuses both.</b> A scene
    ///         deleted or renamed outside the editor leaves an entry naming nothing; a scene with no
    ///         address is in no bundle, so a player could not open it whatever the list said. Either
    ///         one stops the content build, so the row says so here — where somebody is looking —
    ///         rather than only in the console after an import has run.
    ///     </para>
    ///     <para>
    ///         A word rather than a bool, because the two have different fixes: one is restored or
    ///         removed, the other is given an address in the inspector.
    ///     </para>
    /// </remarks>
    public Func<string, string?>? Trouble { get; set; }

    /// <summary>Why a build cannot be started, or <see langword="null" /> when it can.</summary>
    public Func<string?>? Refusal { get; set; }

    /// <summary>Points the panel at a project's build settings.</summary>
    /// <param name="value">The settings.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value" /> is null.</exception>
    /// <remarks>
    ///     ⚠ <b>The three fields are filled with <see cref="loading" /> set, and that is not
    ///     defensiveness.</b> Writing to a control raises its own change event, which here means
    ///     "the user edited this" and therefore "write the file" — so a panel simply being opened
    ///     would mark the project dirty and put a settings file into a checkout that had none.
    /// </remarks>
    public void Show(PlayerBuildSettings value) {
        ArgumentNullException.ThrowIfNull(value);

        settings = value;
        loading = true;

        try {
            TargetPicker.Value = value.Target.Length > 0 ? value.Target : HostChoice;
            VariantPicker.Value = value.Variant;
            Output.Value = value.OutputPath;
        } finally {
            loading = false;
        }

        Rebuild();
    }

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        Form = Part("build-form");

        TargetPicker = Field<Select>("Target");

        // ⚠ The host's own target is an *option* rather than the absence of one, and the setting
        // behind it is still empty. A project checked out on a Mac and built on a Windows machine
        // has to build for the machine it is on unless somebody said otherwise, which is what an
        // empty setting means — but a picker with nothing chosen reads as a picker nobody has got
        // to yet, so this row says which machine it would be.
        TargetPicker.AddOption(HostChoice, $"This machine ({ProjectWorkspace.HostTarget})");

        foreach (var target in PlayerBuild.Targets) {
            TargetPicker.AddOption(target);
        }

        TargetPicker.SelectionChanged += (_, value) => {
            if (settings is { } current) {
                current.Target = value is null or HostChoice ? string.Empty : value;
                Edited();
            }
        };

        VariantPicker = Field<Select>("Configuration");

        foreach (var variant in PlayerBuild.Variants) {
            VariantPicker.AddOption(variant);
        }

        VariantPicker.SelectionChanged += (_, value) => {
            if (settings is { } current && value is { Length: > 0 }) {
                current.Variant = value;
                Edited();
            }
        };

        Output = Field<TextBox>("Output");
        Output.Placeholder = "Build/<target>";

        Output.ValueChanged += (_, value) => {
            if (settings is { } current) {
                current.OutputPath = value ?? string.Empty;
                Edited();
            }
        };

        Note = Part("build-note");

        Part("build-heading").Text = "Scenes in Build";

        var bar = Part("build-scene-bar");

        ScenePicker = bar.Add<Select>();
        ScenePicker.Placeholder = "Choose a scene…";

        AddScene = Command(bar, "Add", () => {
            if (settings is { } current && ScenePicker.Value is { Length: > 0 } path && !current.Scenes.Contains(path)) {
                current.Scenes.Add(path);
                Edited();
                Rebuild();
            }
        });

        RemoveScene = Command(bar, "Remove", () => Mutate(index => settings!.Scenes.RemoveAt(index)));
        MoveUp = Command(bar, "Move Up", () => Move(-1));
        MoveDown = Command(bar, "Move Down", () => Move(1));

        Scenes = Part<DataGrid>();
        Scenes.MultiSelect = false;

        Scenes.AddColumn("#", item => ((SceneRow)item).Order.ToString(CultureInfo.InvariantCulture)).Width = 36f;
        Scenes.AddColumn("Scene", item => ((SceneRow)item).Path).Width = 320f;
        Scenes.AddColumn("State", item => ((SceneRow)item).State).Width = 120f;

        Scenes.SelectionChanged += _ => Restate();

        var actions = Part("build-actions");

        // ⚠ A growing element rather than an alignment on the row, which is `SettingsView`'s own
        // idiom for the same job — see `settings-spacer`. It matters because the two are not the
        // same rule the day something is added to the *left* of the strip: a spacer keeps whatever
        // is before it left and whatever is after it right, and a container-wide alignment pushes
        // the lot over.
        actions.Add<UiElement>("build-spacer");

        BuildButton = Command(actions, "Build", () => BuildRequested?.Invoke(this, false), ControlVariant.Default);
        RunButton = Command(actions, "Build and Run", () => BuildRequested?.Invoke(this, true), ControlVariant.Primary);

        Status = Part("build-status");

        Rebuild();
    }

    /// <summary>Rebuilds the scene list and the picker beside it.</summary>
    public void Rebuild() {
        var chosen = Selected;

        rows.Clear();

        if (settings is { } current) {
            for (var index = 0; index < current.Scenes.Count; index++) {
                var path = current.Scenes[index];

                rows.Add(
                    new SceneRow(
                        index + 1,
                        path,

                        // ⚠ Trouble first, because "first" is a fact about the list and the other two
                        // are facts about the project — and a row that is both has to show the one
                        // somebody has to act on.
                        Trouble?.Invoke(path)
                        ?? (index == 0 ? "Startup" : "In build")
                    )
                );
            }
        }

        Scenes.SetItems(rows);

        if (chosen is not null) {
            var index = rows.FindIndex(row => string.Equals(row.Path, chosen, StringComparison.Ordinal));

            if (index >= 0) {
                Scenes.Select(index);
            }
        }

        // ⚠ Cleared with the options. A value left pointing at a scene that is no longer offered —
        // because it has just been added to the build — is an Add button that would add it twice if
        // the guard above it ever moved.
        ScenePicker.ClearOptions();
        ScenePicker.Value = null;

        foreach (var path in Catalogue?.Invoke() ?? []) {
            if (settings?.Scenes.Contains(path) != true) {
                ScenePicker.AddOption(path);
            }
        }

        Restate();
    }

    /// <summary>Which scene the grid has selected, or <see langword="null" />.</summary>
    public string? Selected =>
        Scenes.Selection.Count == 1 && Scenes.Items.ElementAtOrDefault(Scenes.Selection.First()) is SceneRow row
            ? row.Path
            : null;

    /// <summary>Brings the buttons and the two sentences up to date.</summary>
    void Restate() {
        var refusal = Refusal?.Invoke();
        var index = IndexOfSelection();

        BuildButton.Disabled = refusal is not null;
        RunButton.Disabled = refusal is not null || !Runnable;

        AddScene.Disabled = ScenePicker.Options.Count == 0;
        RemoveScene.Disabled = index < 0;
        MoveUp.Disabled = index <= 0;
        MoveDown.Disabled = index < 0 || index >= rows.Count - 1;

        Note.Text = settings is { } current
            ? $"{Resolved} · compiled as {PlayerBuild.ConfigurationFor(current.Variant)} · "
            + (current.OutputPath.Length > 0 ? current.OutputPath : $"Build/{Resolved}")
            : string.Empty;

        // ⚠ What reads the list, spelled out, because doc 20's bar is that a field nothing reads
        // teaches people the settings do not work. Both halves are true now: the build refuses an
        // entry it could not ship, and the first entry is what the player opens with.
        Status.Text = refusal
            ?? "A build packs every scene here and refuses one it could not ship. The player opens "
            + "the first of them unless the game names its own in OnConfigure.";
    }

    /// <summary>Whether the chosen target produces something this machine could launch.</summary>
    bool Runnable => PlayerBuild.TryDescribe(Resolved, out var shape) && shape.Runnable;

    /// <summary>Which target a build would actually use.</summary>
    string Resolved =>
        settings is { Target.Length: > 0 } current ? current.Target : ProjectWorkspace.HostTarget;

    int IndexOfSelection() =>
        Selected is { } path ? rows.FindIndex(row => string.Equals(row.Path, path, StringComparison.Ordinal)) : -1;

    void Move(int delta) =>
        Mutate(index => {
            var wanted = index + delta;

            if (wanted < 0 || wanted >= settings!.Scenes.Count) {
                return;
            }

            (settings.Scenes[index], settings.Scenes[wanted]) = (settings.Scenes[wanted], settings.Scenes[index]);
        });

    void Mutate(Action<int> change) {
        var index = IndexOfSelection();

        if (settings is null || index < 0) {
            return;
        }

        change(index);
        Edited();
        Rebuild();
    }

    void Edited() {
        if (loading) {
            return;
        }

        Changed?.Invoke(this);
        Restate();
    }

    /// <summary>Adds a labelled row to the form.</summary>
    T Field<T>(string label) where T : UiElement, new() {
        var row = Form.Add<UiElement>("build-row");

        row.Add<TextBlock>().Text = label;

        return row.Add<T>();
    }

    static Button Command(UiElement parent, string label, Action run, ControlVariant variant = ControlVariant.Subtle) {
        var button = parent.Add<Button>();

        button.Label = label;
        button.Variant = variant;
        button.Size = ControlSize.Small;
        button.Clicked += _ => run();

        return button;
    }

    /// <summary>What the target picker's first option means, which is an empty setting.</summary>
    /// <remarks>
    ///     A word rather than the empty string the setting holds. A <c>Select</c> whose chosen option
    ///     has no value is indistinguishable from one nobody has chosen from, and the difference here
    ///     is the difference between "build for whatever machine this is" and "unset".
    /// </remarks>
    const string HostChoice = "host";

    /// <summary>One row of the scenes-in-build list.</summary>
    /// <param name="Order">Its one-based position, which is what makes "first" visible.</param>
    /// <param name="Path">The project-relative path.</param>
    /// <param name="State">Whether it resolves, and whether it is the first.</param>
    sealed record SceneRow(int Order, string Path, string State);
}
