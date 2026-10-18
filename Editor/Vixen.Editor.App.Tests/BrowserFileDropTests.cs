// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Testing;
using Vixen.Ui;
using Vixen.Ui.Controls.Advanced;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>Doc 20 § Part D — Content: dragging a file in from the OS, which was this row's last ⛔.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The framework half has been finished for four batches and nothing in the editor was
///         listening.</b> <c>DropEvent</c> is routed, hit-tested and bubbled, <c>UiElement.AllowDrop</c>
///         exists and <c>on:drop</c> is a name the binder knows — all of it landed under
///         <see href="https://github.com/Rikarin/Vixen/issues/654">#654</see> — and the only consumer
///         anywhere was a sample. So a folder of textures dragged onto the content browser reached the
///         panel, found no handler, and was indistinguishable from a platform that cannot do it.
///     </para>
///     <para>
///         ⚠ <b>Driven through <c>UiDocument.Dispatch(DropEvent)</c>, which is the method the
///         platform layer calls</b> — <c>PlatformInput</c>'s <c>DropFile</c> arm builds exactly this
///         event and hands it over. A test that called the browser's own event instead would prove
///         the import and nothing about whether a drop can reach it.
///     </para>
/// </remarks>
public class BrowserFileDropTests {
    /// <summary>The gesture, end to end: dropped on the panel, copied in, and indexed.</summary>
    /// <remarks>
    ///     ⚠ <b>Indexed is half the claim.</b> A file copied into <c>Assets/</c> that no scan has seen
    ///     has no GUID, no <c>.meta</c> and no row — it is on disk and not in the project, which is
    ///     the state a person discovers when a reference to it will not save.
    /// </remarks>
    [Fact]
    public void A_file_dropped_on_the_browser_is_copied_into_the_project_and_indexed() {
        using var editor = Started();

        var dropped = Outside(editor, "wood.png");

        Assert.True(Drop(editor, [dropped]), "the drop reached nothing at all");

        editor.Settle();

        var landed = Path.Combine(editor.Project.Paths.Assets, "wood.png");

        Assert.True(File.Exists(landed), "the dropped file was not copied into Assets/");
        Assert.Contains("wood.png", Names(editor));
    }

    /// <summary>A folder of textures, which is the drag doc 20 names and the one that used to fail.</summary>
    /// <remarks>
    ///     ⚠ <b><c>File.Copy</c> over a directory throws</b>, so the dialog path this shares reported
    ///     "could not copy the files" for the input every user tries first — and reported it after
    ///     copying however many single files came before the folder in the list.
    /// </remarks>
    [Fact]
    public void A_dropped_folder_arrives_whole() {
        using var editor = Started();

        var folder = Path.Combine(Temp(editor), "Bark");

        Directory.CreateDirectory(Path.Combine(folder, "Detail"));
        File.WriteAllText(Path.Combine(folder, "bark.png"), "bark");
        File.WriteAllText(Path.Combine(folder, "Detail", "fine.png"), "fine");

        Assert.True(Drop(editor, [folder]), "the drop reached nothing at all");

        editor.Settle();

        Assert.True(File.Exists(Path.Combine(editor.Project.Paths.Assets, "Bark", "bark.png")));
        Assert.True(
            File.Exists(Path.Combine(editor.Project.Paths.Assets, "Bark", "Detail", "fine.png")),
            "the folder came in but its subfolder did not"
        );
    }

    /// <summary>What the drop landed on chooses the destination, which is what a drag says.</summary>
    /// <remarks>
    ///     ⚠ <b>The folder being shown rather than <c>Assets/</c>.</b> A drop on the empty space
    ///     under the last tile is the commonest aim there is, and answering the root there puts files
    ///     somewhere the user is not looking — a mistake nobody notices until the build.
    /// </remarks>
    [Fact]
    public void A_drop_lands_in_the_folder_the_browser_is_showing() {
        using var editor = Started();

        var scenes = Path.Combine(editor.Project.Paths.Assets, "Scenes");

        Assert.True(Directory.Exists(scenes), "the fixture project has no Scenes folder to stand in");

        Stand(editor, "Scenes");

        Assert.True(Drop(editor, [Outside(editor, "grass.png")]), "the drop reached nothing at all");

        editor.Settle();

        Assert.True(File.Exists(Path.Combine(scenes, "grass.png")), "the drop landed outside the folder shown");
        Assert.False(File.Exists(Path.Combine(editor.Project.Paths.Assets, "grass.png")));
    }

    /// <summary>A file the project already holds is refused rather than duplicated.</summary>
    /// <remarks>
    ///     ⚠ <b>A row dragged out of the browser onto the browser means a move</b>, and copying would
    ///     mint a second GUID for the same bytes — which is the state the reference index cannot
    ///     repair. The refusal is said out loud, because a drag that does nothing is one people
    ///     repeat.
    /// </remarks>
    [Fact]
    public void A_path_already_in_the_project_is_not_copied_over_itself() {
        using var editor = Started();

        var already = Directory.EnumerateFiles(editor.Project.Paths.Assets, "*", SearchOption.AllDirectories)
            .First(path => !path.EndsWith(".meta", StringComparison.Ordinal));

        var before = Directory.EnumerateFiles(editor.Project.Paths.Assets, "*", SearchOption.AllDirectories).Count();

        Assert.True(Drop(editor, [already]), "the drop reached nothing at all");

        editor.Settle();

        Assert.Equal(
            before,
            Directory.EnumerateFiles(editor.Project.Paths.Assets, "*", SearchOption.AllDirectories).Count()
        );

        Assert.Contains(
            editor.Shell.Notifications.History,
            entry => entry.Message.Contains("already in this project", StringComparison.Ordinal)
        );
    }

    /// <summary>An in-app drag is not an import, and it carries no files to prove it.</summary>
    /// <remarks>
    ///     ⚠ <b>Rows dragged inside the panel arrive here too</b> — the browser's own tree is where
    ///     most drags in this panel start — and an empty <c>Files</c> is the whole difference between
    ///     "somebody brought something in" and "somebody moved something about".
    /// </remarks>
    [Fact]
    public void An_in_app_drag_with_no_files_imports_nothing() {
        using var editor = Started();

        var before = Directory.EnumerateFiles(editor.Project.Paths.Assets, "*", SearchOption.AllDirectories).Count();

        Drop(editor, []);
        editor.Settle();

        Assert.Equal(
            before,
            Directory.EnumerateFiles(editor.Project.Paths.Assets, "*", SearchOption.AllDirectories).Count()
        );
    }

    /// <summary>The SDL-shaped bracket, through the bridge the host calls, lands as one gesture.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Through <c>PlatformInput.Dispatch</c> and not <c>UiDocument.Dispatch</c></b>,
    ///         because SDL does not deliver a <c>DropEvent</c>: it delivers <c>DropBegin</c>, one
    ///         <c>DropFile</c> per path and <c>DropComplete</c>, and the bridge is what groups them.
    ///         The tests above start one step later than the platform does.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>No marshalling, and that is a property of the host rather than an omission.</b>
    ///         <c>EditorHost.Pump</c> drains <c>platform.PumpEvents()</c> in the same loop that calls
    ///         <c>editor.Update</c>, on the thread it makes <c>ReactiveGraph.OwningThread</c>; SDL
    ///         queues drop events and they are read where they are polled. So a drop is on the UI
    ///         thread by construction, which is what this test does too.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_platform_drop_bracket_lands_as_one_gesture() {
        using var editor = Started();

        var bounds = editor.Panel("project").Bounds;
        var at = new System.Numerics.Vector2(bounds.X + (bounds.Width / 2), bounds.Y + (bounds.Height / 2));

        var first = Outside(editor, "moss.png");
        var second = Outside(editor, "rock.png");

        Vixen.Platform.Ui.PlatformInput.Dispatch(
            editor.Document,
            Vixen.Platform.PlatformEvent.Drop(Vixen.Platform.PlatformEventKind.DropBegin, 1, 0, string.Empty, at)
        );

        Vixen.Platform.Ui.PlatformInput.Dispatch(
            editor.Document,
            Vixen.Platform.PlatformEvent.Drop(Vixen.Platform.PlatformEventKind.DropFile, 1, 0, first, at)
        );

        Vixen.Platform.Ui.PlatformInput.Dispatch(
            editor.Document,
            Vixen.Platform.PlatformEvent.Drop(Vixen.Platform.PlatformEventKind.DropFile, 1, 0, second, at)
        );

        // Nothing has landed until the bracket closes.
        Assert.False(File.Exists(Path.Combine(editor.Project.Paths.Assets, "moss.png")));

        Vixen.Platform.Ui.PlatformInput.Dispatch(
            editor.Document,
            Vixen.Platform.PlatformEvent.Drop(Vixen.Platform.PlatformEventKind.DropComplete, 1, 0, string.Empty, at)
        );

        editor.Settle();

        Assert.True(File.Exists(Path.Combine(editor.Project.Paths.Assets, "moss.png")));
        Assert.True(File.Exists(Path.Combine(editor.Project.Paths.Assets, "rock.png")));

        // One gesture, one report — not one per file.
        Assert.Single(
            editor.Shell.Notifications.History,
            entry => entry.Message.Contains("imported into", StringComparison.Ordinal)
        );
    }

    /// <summary>A file named like one the folder already holds is refused, and the one there is untouched.</summary>
    /// <remarks>
    ///     ⚠ Two textures called <c>wood.png</c> from two folders is the ordinary case, and replacing
    ///     the one in the project is a change nothing records and undo cannot reach.
    /// </remarks>
    [Fact]
    public void A_name_the_folder_already_holds_is_refused_and_the_original_kept() {
        using var editor = Started();

        Assert.True(Drop(editor, [Outside(editor, "wood.png")]), "the drop reached nothing at all");
        editor.Settle();

        var landed = Path.Combine(editor.Project.Paths.Assets, "wood.png");
        var original = File.ReadAllText(landed);

        var elsewhere = Path.Combine(Temp(editor), "Other");
        Directory.CreateDirectory(elsewhere);

        var clash = Path.Combine(elsewhere, "wood.png");
        File.WriteAllText(clash, "a different wood");

        Assert.True(Drop(editor, [clash]), "the drop reached nothing at all");
        editor.Settle();

        Assert.Equal(original, File.ReadAllText(landed));
        Assert.Contains(
            editor.Shell.Notifications.History,
            entry => entry.Message.Contains("already in the project", StringComparison.Ordinal)
        );
    }

    /// <summary>A file no importer reads is copied and indexed rather than refused.</summary>
    /// <remarks>
    ///     ⚠ <b>Deliberately not a refusal.</b> <c>Assets/</c> is a file view, and a file with no
    ///     importer is still a file the project holds — a licence, a note, a source file a later
    ///     importer will read. The database gives it a GUID like any other; what an import makes of
    ///     it is the import task's report, not the drop's. Pinned so that a change to either is a
    ///     decision rather than a drift.
    /// </remarks>
    [Fact]
    public void A_file_no_importer_reads_is_copied_and_indexed_not_refused() {
        using var editor = Started();

        Assert.True(Drop(editor, [Outside(editor, "notes.xyzzy")]), "the drop reached nothing at all");
        editor.Settle();

        Assert.True(File.Exists(Path.Combine(editor.Project.Paths.Assets, "notes.xyzzy")));
        Assert.Contains("notes.xyzzy", Names(editor));
        Assert.DoesNotContain(
            editor.Shell.Notifications.History,
            entry => entry.Message.Contains("Could not copy", StringComparison.Ordinal)
        );
    }

    static EditorSession Started() {
        var editor = EditorSession.Start();
        editor.Open("project");

        return editor;
    }

    /// <summary>Walks the browser into a folder, the way a person does: by clicking it.</summary>
    static void Stand(EditorSession editor, string folder) {
        var view = Descendants(editor.Panel("project"))
                .OfType<TreeView>()
                .FirstOrDefault(candidate => candidate.HasClass("browser-folders"))
            ?? throw editor.Fail("the browser has no folder tree");

        var node = Walk(view.Root).FirstOrDefault(candidate => candidate.Text == folder)
            ?? throw editor.Fail($"the folder tree has no '{folder}'");

        view.Select(node);
        editor.Settle();
    }

    static IEnumerable<TreeNode> Walk(TreeNode node) {
        foreach (var child in node.Children) {
            yield return child;

            foreach (var found in Walk(child)) {
                yield return found;
            }
        }
    }

    static IEnumerable<UiElement> Descendants(UiElement element) {
        foreach (var child in element.Children) {
            yield return child;

            foreach (var found in Descendants(child)) {
                yield return found;
            }
        }
    }

    /// <summary>Sends a drop at the middle of the browser panel, the way the platform layer does.</summary>
    static bool Drop(EditorSession editor, IReadOnlyList<string> files) {
        var bounds = editor.Panel("project").Bounds;

        var args = new DropEvent {
            X = bounds.X + (bounds.Width / 2),
            Y = bounds.Y + (bounds.Height / 2),
            Files = files
        };

        return editor.Document.Dispatch(args) is not null;
    }

    /// <summary>A file on the user's disk, outside the project — which is what an OS drag carries.</summary>
    static string Outside(EditorSession editor, string name) {
        var path = Path.Combine(Temp(editor), name);
        File.WriteAllText(path, name);

        return path;
    }

    static string Temp(EditorSession editor) {
        var path = Path.Combine(editor.DataDirectory, "Desktop");
        Directory.CreateDirectory(path);

        return path;
    }

    static IReadOnlyList<string> Names(EditorSession editor) =>
        [.. editor.Project.Assets.Entries.Select(entry => entry.Name)];
}
