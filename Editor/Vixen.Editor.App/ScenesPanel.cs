// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Plugin;
using Vixen.Editor.SceneView;
using Vixen.Editor.Ui;
using Vixen.Platform;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Vixen.Ui.Controls.Advanced;

namespace Vixen.Editor.App;

/// <summary>Doc 20's multi-scene row as <c>SceneModule</c>'s: the Scenes panel, Open Scene Additively and Close Scene.</summary>
/// <remarks>
///     <para>
///         <b>Moved, not rewritten (#1290)</b> — the panel's rows were <c>EditorWorlds.RefreshScenes</c>,
///         the close was <c>EditorWorlds.CloseScene</c> and the picker <c>OpenSceneAdditive</c>. What
///         they do to the open scenes is <see cref="ISceneEditing.OpenAdditively" /> and
///         <see cref="ISceneEditing.Close" />; what is left here is the two questions — which file, and
///         whether to lose unsaved changes — and the rows.
///     </para>
///     <para>
///         ⚠ <b>The eye and the padlock on a row are this panel's, and not undoable.</b> Hiding a scene
///         to work on the one in front of it must not change what ships, so it is not written and it is
///         not on any stack — the rule <c>entity.toggle-hidden</c> follows, which #1290 recorded as
///         decided. They were two properties on the application's per-file record, read by nothing but
///         this panel.
///     </para>
///     <para>
///         ⚠ <b>The picker's answer is polled from the module's update rather than awaited</b>, the
///         shape <c>DiagnosticsModule</c>'s report uses: the platform finishes a picker on whatever
///         thread its event loop ran, and the open touches the document. The close's question is
///         awaited, and its continuation touches only the service, so a panel closed meanwhile is
///         nothing it reaches into (#1413).
///     </para>
/// </remarks>
sealed class ScenesPanel : IDisposable {
    /// <summary>The panel's id, which saved arrangements name.</summary>
    public const string PanelId = "scenes";

    /// <summary>Scene ▸ Close Scene: closes the active scene, asking first if it has changes.</summary>
    public const string CloseCommand = "scene.close-scene";

    readonly EditorShell shell;
    readonly ISceneEditing editing;
    readonly string context;
    readonly Func<string?> folder;
    readonly INativeDialogs? dialogs;
    readonly Action remarked;

    /// <summary>Each open scene's eye and padlock, which outlive the panel.</summary>
    readonly Dictionary<SceneDocument, Marks> marks = [];

    /// <summary>The unsaved marks the rows were last drawn with, so a save redraws them.</summary>
    readonly List<bool> drawn = [];

    UiElement? list;
    Task<string?>? picking;

    /// <summary>Builds the panel over the scene service.</summary>
    /// <param name="shell">The shell, for the context, the question and the toasts.</param>
    /// <param name="editing">The open scenes and what opens and closes them.</param>
    /// <param name="context">The shell context a press in the panel selects.</param>
    /// <param name="dialogs">The OS's file pickers, or <see langword="null" /> where there are none.</param>
    /// <param name="folder">Where the picker starts.</param>
    /// <param name="remarked">Says an eye or a padlock changed, so the outliner restates its rows.</param>
    public ScenesPanel(
        EditorShell shell,
        ISceneEditing editing,
        string context,
        INativeDialogs? dialogs,
        Func<string?> folder,
        Action remarked
    ) {
        this.shell = shell;
        this.editing = editing;
        this.context = context;
        this.dialogs = dialogs;
        this.folder = folder;
        this.remarked = remarked;

        editing.ActiveChanged += Changed;
        editing.ScenesChanged += Changed;
    }

    /// <inheritdoc />
    public void Dispose() {
        editing.ActiveChanged -= Changed;
        editing.ScenesChanged -= Changed;
    }

    /// <summary>Registers the panel, its toggle and the two verbs, through the module's context.</summary>
    /// <param name="module">What the module was activated with.</param>
    public void Register(PluginContext module) {
        module.AddPanel(new PanelDescriptor(PanelId, EditorStrings.PanelScenes, Build) { Closed = () => list = null });

        module.AddCommand(
            new EditorCommand("scene.scenes", EditorStrings.CommandSceneScenes, () => shell.Workspace.Toggle(PanelId)) {
                Category = EditorStrings.CategoryScene
            }
        );

        module.AddCommand(
            new EditorCommand("scene.open-additive", EditorStrings.CommandSceneOpenAdditive, Pick) {
                Category = EditorStrings.CategoryScene,
                Enablement = () => dialogs is not null
            }
        );

        // ⚠ The active scene, and never the first: closing the last one is New Scene, and an editor
        // with no scene at all has no viewport subject, no outliner and nowhere to put a new entity.
        module.AddCommand(
            new EditorCommand(CloseCommand, EditorStrings.CommandSceneCloseScene, () => Close(editing.Active)) {
                Category = EditorStrings.CategoryScene,
                Enablement = () => editing.Scenes.Count > 1 && !ReferenceEquals(editing.Active, editing.Scenes[0])
            }
        );
    }

    /// <summary>Applies the picker's answer, and redraws the rows when a scene was saved, once a frame.</summary>
    public void Update() {
        if (picking is { IsCompleted: true } picked) {
            picking = null;
            Picked(picked);
        }

        if (list is not null && !drawn.SequenceEqual(editing.Scenes.Select(scene => scene.HasUnsavedChanges.Value))) {
            Refresh();
        }
    }

    /// <summary>Closes an open scene, asking first if it has changes.</summary>
    /// <param name="scene">Which one. The first, or one not open, is left alone.</param>
    internal void Close(SceneDocument scene) {
        if (!editing.Scenes.Contains(scene) || ReferenceEquals(scene, editing.Scenes[0])) {
            return;
        }

        if (!scene.HasUnsavedChanges.Value) {
            editing.Close(scene);

            return;
        }

        _ = Ask();

        async Task Ask() {
            if (await shell.Dialogs.ConfirmAsync(
                        "Discard unsaved changes?",
                        Path.GetFileName(FileOf(scene)) + " has changes that have not been written.",
                        "Discard",
                        danger: true
                    )
                    .ConfigureAwait(true)) {
                editing.Close(scene);
            }
        }
    }

    void Build(DockPanel panel) {
        panel.WhenPressedIn(() => shell.Context = context);

        list = panel.Add("scene-list");
        Refresh();
    }

    void Changed(ISceneEditing _) {
        foreach (var closed in marks.Keys.Where(scene => !editing.Scenes.Contains(scene)).ToList()) {
            marks.Remove(closed);
        }

        Refresh();
    }

    void Pick() {
        if (dialogs is null || picking is not null) {
            return;
        }

        picking = dialogs.OpenFileAsync(
                new FileDialogOptions {
                    Title = "Open Scene Additively",
                    InitialDirectory = folder(),
                    Filters = [new FileFilter("Vixen scene", SceneSerializer.Extension.TrimStart('.'))]
                }
            )
            .AsTask();
    }

    void Picked(Task<string?> picked) {
        if (!picked.IsCompletedSuccessfully) {
            shell.Notifications.Show(
                "Could not open the scene",
                NotificationSeverity.Error,
                picked.Exception?.GetBaseException().Message
            );

            return;
        }

        if (picked.Result is { } path) {
            editing.OpenAdditively(path);
        }
    }

    /// <summary>The file a scene writes, or its title when it writes none.</summary>
    /// <remarks>
    ///     ⚠ <b>The file it writes <i>now</i>.</b> The application's record kept the path a scene was
    ///     opened from, so after Save As or Open Scene the main scene's row went on naming the file it
    ///     had left; the writer is what #1416 made every "which file is this" question ask.
    /// </remarks>
    static string FileOf(SceneDocument scene) =>
        scene.Writer is SceneFileWriter writer ? writer.Path : scene.Title.Peek();

    /// <summary>Rebuilds the panel's rows.</summary>
    void Refresh() {
        if (list is null) {
            return;
        }

        while (list.Children.Count > 0) {
            list.Children[^1].Remove();
        }

        drawn.Clear();

        var scenes = editing.Scenes;

        for (var index = 0; index < scenes.Count; index++) {
            var open = scenes[index];
            var state = marks.TryGetValue(open, out var found) ? found : marks[open] = new();

            drawn.Add(open.HasUnsavedChanges.Value);

            var row = list.Add("scene-row");
            if (ReferenceEquals(open, editing.Active)) {
                row.AddClass("selected");
            }

            var name = row.Add("scene-name");
            name.Text = Path.GetFileNameWithoutExtension(FileOf(open)) + (open.HasUnsavedChanges.Value ? " *" : string.Empty);

            var visible = row.Add<CheckBox>();
            visible.IsChecked = state.IsVisible;

            visible.CheckedChanged += (_, on) => {
                state.IsVisible = on;

                foreach (var entity in open.Roots) {
                    open.SetHidden(entity, !on);
                }

                remarked();
            };

            var locked = row.Add<CheckBox>();
            locked.IsChecked = state.IsLocked;

            locked.CheckedChanged += (_, on) => {
                state.IsLocked = on;

                foreach (var entity in open.Roots) {
                    open.SetLocked(entity, on);
                }

                remarked();
            };

            row.AddHandler<PointerEvent>(
                (_, args) => {
                    if (args.Action == PointerAction.Pressed) {
                        editing.Activate(open);
                    }
                }
            );

            if (index == 0) {
                continue;
            }

            var close = row.Add<Button>();

            close.Label = "Close";
            close.Size = ControlSize.Small;
            close.Variant = ControlVariant.Subtle;
            close.Clicked += _ => Close(open);
        }
    }

    /// <summary>One scene's two view marks.</summary>
    sealed class Marks {
        /// <summary>Whether its entities are drawn.</summary>
        public bool IsVisible { get; set; } = true;

        /// <summary>Whether its entities can be selected or edited.</summary>
        public bool IsLocked { get; set; }
    }
}
