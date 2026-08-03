// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.IO;
using Vixen.Core.IO.Watch;
using Vixen.Editor.Core;
using Vixen.Editor.Plugin;
using Vixen.Editor.Ui;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Vixen.Ui.Controls.Advanced;

namespace Vixen.Editor.Scripts;

/// <summary>The editor's own tier-three producer: a project's <c>Editor/</c> folder.</summary>
/// <remarks>
///     <para>
///         <b>Doc 36 § D2's third producer and § P5's whole phase.</b> The other two are a source
///         generator at compile time and a plugin's <c>Activate</c> at load time; this is the one that
///         runs when a project opens, and it is the only one whose input is a file somebody is typing
///         into right now.
///     </para>
///     <para>
///         ⚠ <b>A module, so <c>Vixen.Editor.App</c> never references it.</b> It asks the host for the
///         project and for the plugin host, registers a rebuild verb, a panel and a per-frame drain,
///         and is named in <c>EditorModules</c> beside Terrain and Blockout. The assembly hosting a C#
///         compiler being reachable from the editor's application would be P3's exit list undone for
///         the sake of a feature that is, by construction, optional.
///     </para>
///     <para>
///         ⚠ <b>The watcher is drained on the frame thread, not raised on the platform's.</b>
///         <c>FileWatcher</c> is pull-based for exactly this reason — compiling and loading an
///         assembly from a file-system callback would run a plugin's <c>Activate</c> on whichever
///         thread the OS chose, and every registration it makes is the shell's.
///     </para>
/// </remarks>
public sealed class ScriptsModule : IEditorPlugin, IDisposable {
    /// <summary>What the host activates it under.</summary>
    public const string ModuleId = "vixen.scripts";

    /// <summary>What a plugin-management panel calls it.</summary>
    public const string ModuleName = "Editor Scripts";

    /// <summary>The panel that lists what the compiler said.</summary>
    public const string PanelId = "scripts";

    /// <summary>The verb that compiles the folder again.</summary>
    public const string RebuildCommand = "scripts.rebuild";

    readonly List<FileChange> drained = [];

    EditorScripts? scripts;
    IFileWatcher? watcher;
    UiElement? list;

    /// <summary>What the last build produced, for a panel and for a test.</summary>
    public ScriptState State => scripts?.State ?? new(ScriptBuild.None, Loaded: false, 0, 0);

    /// <inheritdoc />
    public void Activate(PluginContext context) {
        ArgumentNullException.ThrowIfNull(context);

        var project = context.Services.Require<EditorProject>();
        var host = context.Services.Require<PluginHost>();

        scripts = new EditorScripts(host, project.Paths.Root, Path.Combine(project.Paths.Library, "EditorScripts"));
        scripts.Rebuilt += _ => Show();

        context.AddCommand(
            RebuildCommand,
            new StringId("editor.command.scripts.rebuild", "Rebuild Editor Scripts"),
            () => scripts?.Rebuild()
        );

        context.AddPanel(PanelId, new StringId("editor.panel.scripts", "Editor Scripts"), Build);

        // ⚠ Once, at activation, before anything is watching. A project whose scripts already exist
        // has to come up with its menus in place — a first build that waited for a file to change
        // would mean every session started without the project's own tools until somebody touched
        // something.
        scripts.Rebuild();

        Watch(project);
        context.OnUpdate(_ => Poll());
        context.OnUnload(Dispose);
    }

    /// <inheritdoc />
    public void Deactivate() => Dispose();

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>The watcher first, so nothing arrives during the unload.</b> A change drained after
    ///     the scripts were dropped would compile and load an assembly into a host that is shutting
    ///     down, which is the shape of leak this whole path is arranged to avoid.
    /// </remarks>
    public void Dispose() {
        watcher?.Dispose();
        watcher = null;

        scripts?.Unload();
        scripts = null;

        GC.SuppressFinalize(this);
    }

    void Watch(EditorProject project) {
        try {
            watcher = new FileWatcher(project.Paths.Root, VirtualPath.Root);
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
            // ⚠ Not fatal. A project on a share, on a full inotify budget, or in a sandbox that
            // refuses the handle still gets its scripts — it gets them on the rebuild verb rather
            // than on a save, which is the feature degrading instead of the editor failing.
            watcher = null;
        }
    }

    /// <summary>Rebuilds if anything under an <c>Editor/</c> folder changed since the last frame.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Filtered to <c>.cs</c> under an <c>Editor</c> folder, because the watcher covers
    ///         the project.</b> A texture import writes hundreds of files and every one of them would
    ///         otherwise be a compile — and there is only one watcher per project worth having, so it
    ///         is the consumer that narrows rather than the watch.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>An overflow rebuilds rather than being ignored.</b> The watcher says it lost
    ///         events; the only correct response to that is to assume the worst, which here costs one
    ///         compile of a folder of a dozen files.
    ///     </para>
    /// </remarks>
    void Poll() {
        if (watcher is null || scripts is null) {
            return;
        }

        var overflowed = watcher.HasOverflowed;

        if (overflowed) {
            watcher.ClearOverflow();
        }

        drained.Clear();
        watcher.Drain(drained);

        if (overflowed || drained.Any(IsScript)) {
            scripts.Rebuild();
        }
    }

    static bool IsScript(FileChange change) {
        var path = change.Path.ToString();

        return path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
            && path.Contains("/" + ScriptCompiler.FolderName + "/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Builds the panel that says what the compiler said.</summary>
    /// <remarks>
    ///     ⚠ <b>A panel and not a dialog, which is the whole of P5's second exit criterion.</b> A
    ///     compile error arrives while somebody is typing, several times a minute; anything modal
    ///     would make the workflow unusable and anything that took the editor down would lose their
    ///     scene. What it is instead is a list that is empty when everything compiles.
    /// </remarks>
    void Build(DockPanel panel) {
        list = panel.Add("script-diagnostics");
        Show();
    }

    void Show() {
        if (list is null) {
            return;
        }

        while (list.Children.Count > 0) {
            list.Children[^1].Remove();
        }

        var state = State;

        if (state.Build.Sources == 0) {
            Say("This project has no Editor/ folder, so there are no editor scripts to build.");
            return;
        }

        foreach (var diagnostic in state.Build.Diagnostics) {
            Say(diagnostic.ToString()).AddClass(diagnostic.IsError ? "error" : "warning");
        }

        if (state.Build.Diagnostics.Count == 0) {
            Say($"{state.Build.Sources} file(s) compiled. {state.Menus} menu item(s), {state.Plugins} plugin(s).");
        }
    }

    TextBlock Say(string text) {
        var line = list!.Add<TextBlock>();

        line.AddClass("script-diagnostic");
        line.Text = text;

        return line;
    }
}
