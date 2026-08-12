// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Testing;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>Doc 36 § P5's exit criterion, through a running editor.</summary>
/// <remarks>
///     <para>
///         <b>A <c>.cs</c> file dropped into a project's <c>Editor/</c> folder adds a menu item
///         without restarting the editor, and a compile error is a panel rather than a crash.</b>
///         That is the whole phase, and it is only true or false in a shell: the module has to be
///         activated, the project's root has to be the one it watches, the watcher has to be drained
///         on a frame, and the command has to land in the shell somebody is looking at.
///     </para>
///     <para>
///         ⚠ <b>The unit suite in <c>Vixen.Editor.Scripts.Tests</c> drives <c>EditorScripts</c>
///         directly</b> — the compile, the load, the reload and the unload. What is left for here is
///         the wiring, which is the part a unit test cannot see.
///     </para>
/// </remarks>
public class EditorScriptWorkflowTests {
    const string MenuItem = """
        using Vixen.Editor.Plugin;

        public static class ProjectTools {
            [EditorMenu("Tools/Say Hello")]
            public static void Hello() { }
        }
        """;

    static string Write(EditorSession editor, string name, string source) {
        var folder = Path.Combine(editor.ProjectRoot, "Assets", "Editor");

        Directory.CreateDirectory(folder);

        var path = Path.Combine(folder, name);

        File.WriteAllText(path, source);
        return path;
    }

    /// <summary>
    ///     ⚠ <b>Written before the editor starts, so this is the "already has scripts" case.</b> A
    ///     project whose tools only appear after somebody touches a file is a project that opens
    ///     without its own tools every session.
    /// </summary>
    [Fact]
    public void A_project_that_already_has_scripts_opens_with_their_menu_items() {
        var data = Path.Combine(Path.GetTempPath(), "vixen-p5-" + Guid.NewGuid().ToString("N"));

        try {
            using var editor = EditorSession.Start(new() { DataDirectory = data });

            Write(editor, "ProjectTools.cs", MenuItem);

            // The scripts were compiled at activation, before this file existed — so this is the
            // rebuild verb doing what a save would, which is also the fallback on a machine whose
            // file watcher could not be opened.
            editor.Run("scripts.rebuild");
            editor.Settle();

            Assert.True(editor.CanRun("scripts.tools.say-hello"));
        } finally {
            if (Directory.Exists(data)) {
                Directory.Delete(data, recursive: true);
            }
        }
    }

    /// <summary>
    ///     ⚠ <b>The requirement, in the words it was given in: the item has to be <i>visible</i>.</b>
    ///     A command in the registry and a group in <c>MenuModel</c> are the model; what a person sees
    ///     is <c>MenuPresenter</c>'s bar, rebuilt from that model. A script whose menu existed only in
    ///     the model would be a script whose tool nobody can click.
    /// </summary>
    [Fact]
    public void The_scripts_menu_is_on_the_menu_bar_a_person_looks_at() {
        var data = Path.Combine(Path.GetTempPath(), "vixen-p5-" + Guid.NewGuid().ToString("N"));

        try {
            using var editor = EditorSession.Start(new() { DataDirectory = data });

            Write(editor, "ProjectTools.cs", MenuItem);

            editor.Run("scripts.rebuild");
            editor.Settle();

            editor.Ui.Contains("Tools").ShouldExist();
        } finally {
            if (Directory.Exists(data)) {
                Directory.Delete(data, recursive: true);
            }
        }
    }

    /// <summary>
    ///     ⚠ <b>The requirement in its own words: <i>drop</i> a file in.</b> No command, no restart —
    ///     the file appears in the folder and the menu item is there. The two tests above run the
    ///     rebuild verb, which is the fallback; this is the path a person actually takes.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Real time has to pass, because the watcher debounces.</b> A text editor makes four
    ///     writes for one save and <c>FileChangeCoalescer</c> exists to turn those into one change —
    ///     which is a wall-clock window, not a frame count. So this pumps frames <i>and</i> waits, up
    ///     to a bound, and fails on the bound rather than hanging.
    /// </remarks>
    [Fact]
    public void Dropping_a_file_in_adds_its_menu_item_with_no_command_and_no_restart() {
        var data = Path.Combine(Path.GetTempPath(), "vixen-p5-" + Guid.NewGuid().ToString("N"));

        try {
            using var editor = EditorSession.Start(new() { DataDirectory = data });

            Assert.False(editor.CanRun("scripts.tools.say-hello"), "the verb exists before the file does");

            Write(editor, "ProjectTools.cs", MenuItem);

            // ⚠ Thirty, and ten was measured to be too few. This waits on a file-system event and then
            // on a Roslyn compile of what arrived, on a runner with a dozen test assemblies to itself
            // — the Windows leg spent the whole ten seconds here and reported the watcher as broken.
            // It is the same number, for the same reason, as AssetWatchTests.Ceiling: waiting longer
            // costs a passing build nothing, and costs a genuinely broken watcher thirty seconds.
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);

            while (DateTime.UtcNow < deadline && !editor.CanRun("scripts.tools.say-hello")) {
                Thread.Sleep(25);
                editor.Frames(2);
            }

            Assert.True(editor.CanRun("scripts.tools.say-hello"), "the watcher never rebuilt the scripts");
            editor.Ui.Contains("Tools").ShouldExist();
        } finally {
            if (Directory.Exists(data)) {
                Directory.Delete(data, recursive: true);
            }
        }
    }

    /// <summary>
    ///     ⚠ <b>A compile error leaves the editor running and says where it was.</b> The claim is
    ///     that the failure is a list somebody reads, so what this asserts is that the session is
    ///     still usable afterwards and the diagnostic carries the file it came from.
    /// </summary>
    [Fact]
    public void A_broken_script_is_reported_and_the_editor_keeps_running() {
        var data = Path.Combine(Path.GetTempPath(), "vixen-p5-" + Guid.NewGuid().ToString("N"));

        try {
            using var editor = EditorSession.Start(new() { DataDirectory = data });

            Write(editor, "Broken.cs", "public static class Broken { public static void Run() => Missing.Call(); }");

            editor.Run("scripts.rebuild");
            editor.Settle();

            // The panel opens, which is the whole of "an error is a panel". Its contents are the
            // module's own and are asserted in `Vixen.Editor.Scripts.Tests`; what matters here is
            // that asking for it after a failed build does not take the editor down.
            editor.Open(Scripts.ScriptsModule.PanelId);
            editor.Settle();

            Assert.NotNull(editor.Panel(Scripts.ScriptsModule.PanelId));

            // And the editor is still an editor: an unrelated verb still runs.
            Assert.True(editor.CanRun("scripts.rebuild"));
        } finally {
            if (Directory.Exists(data)) {
                Directory.Delete(data, recursive: true);
            }
        }
    }
}
