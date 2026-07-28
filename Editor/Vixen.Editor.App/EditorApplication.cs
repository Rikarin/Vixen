// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Ui;
using Vixen.Input;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Vixen.Ui.Controls.Advanced;

namespace Vixen.Editor.App;

/// <summary>The editor as an application: which panels exist, which layouts, and what persists.</summary>
/// <remarks>
///     <para>
///         <b>The half of the editor that is not chrome.</b> <see cref="EditorShell" /> is a menu
///         bar, a docking workspace, a palette and a status bar with nothing in them;
///         <see cref="EditorHost" /> is a window, a device and a frame loop. This is the list of what
///         goes in the panels, what the layouts are called, and where the arrangement is written when
///         the window closes — which is the part a game team would fork and the other two are not.
///     </para>
///     <para>
///         ⚠ <b>The panels are placeholders and are meant to be read as such.</b>
///         <c>Vixen.Editor.SceneView</c>, <c>.Inspector</c>, <c>.NodeGraph</c> and <c>.Profiler</c>
///         are separate assemblies in doc 11's tree and none of them exists yet; what is here is the
///         hierarchy and the inspector built out of the two advanced controls that do, so that the
///         shell is exercised by something real rather than by four empty boxes. Each becomes a
///         one-line change when its assembly lands, because a panel is an id and a factory.
///     </para>
///     <para>
///         ⚠ <b>Every user-facing decision is written on the way out, and nothing on the way in is
///         required.</b> A first run has no layout, no keymap and no theme file, and the editor
///         opens on the Default preset in dark — the same argument <c>ProjectSettingsStore</c> makes
///         about a missing settings file meaning the defaults.
///     </para>
/// </remarks>
sealed class EditorApplication : IDisposable {
    readonly EditorUserStore store;

    /// <summary>Builds the editor's interface into a new document.</summary>
    /// <param name="width">The surface's width in device-independent pixels.</param>
    /// <param name="height">Its height.</param>
    /// <param name="directory">Where the user's layouts, keymap and preferences live.</param>
    public EditorApplication(float width, float height, string directory) {
        store = new EditorUserStore(directory);
        Shell = new EditorShell(width, height);

        Panels();
        Layouts();
        Commands();

        // ⚠ In this order and no other. The keymap has to be loaded after the commands that own its
        // defaults, or every override in the file lands on a command with no default and the file
        // rewrites itself with the whole map in it. The layout has to be applied after the panels
        // are registered, or a saved arrangement names panels the workspace cannot build.
        if (store.Read(EditorUserStore.KeyMapFile) is { } keymap) {
            Shell.Keys.Load(keymap);
        }

        Shell.Theme.LoadTokens(store.Read("theme.yaml"));

        if (store.LoadLayout(EditorUserStore.CurrentLayout) is { } layout) {
            Shell.Workspace.Load(layout);
        } else {
            Shell.Workspace.Reset();
        }

        Shell.Status = "Ready";
    }

    /// <summary>The interface.</summary>
    public EditorShell Shell { get; }

    /// <summary>Whether the editor has been asked to close.</summary>
    public bool IsClosing { get; private set; }

    /// <summary>Writes what the user changed.</summary>
    /// <remarks>
    ///     Called on the way down rather than on every change: a splitter drag raises
    ///     <c>LayoutChanged</c> per mouse-move, and an editor that wrote a file per frame of a drag
    ///     would be one whose window layout is the noisiest thing on the disk.
    /// </remarks>
    public void Persist() {
        store.SaveLayout(EditorUserStore.CurrentLayout, Shell.Workspace.Save());
        store.Write(EditorUserStore.KeyMapFile, Shell.Keys.Save());
    }

    /// <inheritdoc />
    public void Dispose() => Shell.Dispose();

    void Panels() {
        Shell.RegisterPanel(
            "hierarchy",
            new StringId("editor.panel.hierarchy", "Hierarchy"),
            panel => {
                var tree = panel.Add<TreeView>();
                var scene = tree.Root.Add("Scene");

                scene.Add("Directional Light");
                scene.Add("Main Camera");
                scene.Add("Ground");

                tree.Refresh();
                tree.Expand(scene);
            }
        );

        Shell.RegisterPanel(
            "project",
            new StringId("editor.panel.project", "Project"),
            panel => {
                var tree = panel.Add<TreeView>();
                var assets = tree.Root.Add("Assets");

                assets.Add("Materials");
                assets.Add("Scenes");
                assets.Add("Shaders");

                tree.Refresh();
                tree.Expand(assets);
            }
        );

        Shell.RegisterPanel(
            "scene",
            new StringId("editor.panel.scene", "Scene"),
            panel => panel.Add<EmptyState>().Title = "The viewport lands with Vixen.Editor.SceneView."
        );

        Shell.RegisterPanel(
            "inspector",
            new StringId("editor.panel.inspector", "Inspector"),
            panel => panel.Add<PropertyGrid>()
        );

        Shell.RegisterPanel(
            "console",
            new StringId("editor.panel.console", "Console"),
            panel => panel.Add<TextBlock>().Text = "Nothing logged yet."
        );
    }

    void Layouts() {
        // The five doc 11 names. They differ in which panels they show and how the middle is split,
        // which is the whole of what a layout preset is — the shapes come from `LayoutPresets` and
        // the panel lists are this application's.
        Shell.RegisterLayout(
            "Default",
            new StringId("editor.layout.default", "Default"),
            () => LayoutPresets.Standard(["hierarchy", "project"], ["scene"], ["inspector"], ["console"])
        );

        Shell.RegisterLayout(
            "Scene",
            new StringId("editor.layout.scene", "Scene"),
            () => LayoutPresets.Standard(["hierarchy"], ["scene"], ["inspector"])
        );

        Shell.RegisterLayout(
            "Shading",
            new StringId("editor.layout.shading", "Shading"),
            () => LayoutPresets.Standard(["project"], ["scene"], ["inspector"], ["console"])
        );

        Shell.RegisterLayout(
            "Animation",
            new StringId("editor.layout.animation", "Animation"),
            () => LayoutPresets.Standard(["hierarchy"], ["scene"], ["inspector"], ["console"])
        );

        Shell.RegisterLayout(
            "Debug",
            new StringId("editor.layout.debug", "Debug"),
            () => LayoutPresets.Split(["scene"], ["console"], 0.6f)
        );

        Shell.Workspace.DefaultPreset = "Default";
    }

    void Commands() {
        Shell.Commands.Add(
            new EditorCommand("file.exit", EditorStrings.CommandExit, () => IsClosing = true) {
                Category = EditorStrings.CategoryFile
            }
        );

        Shell.Commands.Add(
            new EditorCommand("view.save-layout", EditorStrings.CommandSaveLayout, SaveLayout) {
                Category = EditorStrings.CategoryView
            }
        );

        Shell.Commands.Add(
            new EditorCommand("help.about", EditorStrings.CommandAbout, About) {
                Category = EditorStrings.CategoryHelp
            }
        );

        Shell.Keys.SetDefault("file.exit", new KeyChord(InputKey.Q, ModifierKeys.Control));

        Shell.Toolbar.Show("view.palette", null, "view.reset-layout", "view.toggle-theme");

        // The saved arrangements are a palette source rather than a menu, because there is no bound
        // on how many of them somebody makes and a menu with forty lines in it is a list nobody
        // reads.
        Shell.Palette.AddSource(
            new DelegatePaletteSource(
                "Layout",
                () => store.Layouts(),
                name => {
                    if (store.LoadLayout(name) is { } layout) {
                        Shell.Workspace.Load(layout);
                    }
                }
            )
        );
    }

    void SaveLayout() {
        // Named after the preset it came from until there is a dialog to ask. A "Save Layout…" that
        // silently overwrote the arrangement the user is looking at would be worse than one that
        // has an obvious placeholder name.
        var name = Shell.Workspace.DefaultPreset + " (custom)";

        store.SaveLayout(name, Shell.Workspace.Save());
        Shell.Notifications.Success(name);
    }

    void About() =>
        Shell.Notifications.Show(
            "Vixen Editor",
            NotificationSeverity.Info,
            typeof(EditorApplication).Assembly.GetName().Version?.ToString() ?? "development build"
        );
}
