// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Editor.Core;
using Vixen.Editor.Testing;
using Vixen.Engine.Transforms;
using Vixen.Ui.Controls;
using Vixen.Ui.Controls.Advanced;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>Doc 11's end-to-end scenario, and doc 20's E1 exit criterion.</summary>
/// <remarks>
///     <para>
///         <b>The one test that is about the editor rather than about a part of it.</b> Every panel
///         here has a suite of its own and all of them passed while the join between two of them did
///         not exist; what this presses on is a whole session — put something in the project, put it
///         in the scene, change it, take the change back, write it, close the editor, open it again,
///         and find it there.
///     </para>
///     <para>
///         ⚠ <b>Restarting is the step nothing else can fake.</b> "Save, reopen, assert" is a claim
///         about what reached the disk, and an in-process reload that kept the same objects alive
///         would pass for a scene that was never written.
///     </para>
/// </remarks>
public class ScenarioTests {
    /// <summary>Doc 11's scenario, verb by verb.</summary>
    [Fact]
    public void A_session_survives_being_closed_and_opened_again() {
        using var editor = EditorSession.Start();

        // ── create project ──────────────────────────────────────────────────────────────────────
        // Opening the editor with no project is what makes one: a scratch project under the data
        // directory, scanned, with a scene seeded and written. The first launch is the create.
        editor.Step("open a project").Open("project");

        Assert.True(Directory.Exists(editor.ProjectRoot));
        Assert.NotEmpty(editor.Project.Assets.Entries);

        // ── import asset ────────────────────────────────────────────────────────────────────────
        editor.Step("import an asset");

        var crate = Import(editor, "Assets/Textures/crate.png");

        Assert.True(editor.Project.Assets.TryGetByGuid(crate, out var imported));
        Assert.Equal("Assets/Textures/crate.png", imported.Path);

        // ── put it in the scene ─────────────────────────────────────────────────────────────────
        // ⚠ Through the Entity menu rather than by dragging the row, and the reason is a gap rather
        // than a preference: no runtime component carries an `AssetId`, so there is nothing for an
        // entity to hold a texture *in* and a drop that made one would be the editor pretending. The
        // scenario's shape — something goes from the browser into the scene and is then edited — is
        // what this step is for, and it is what the gap will be closed against.
        editor.Step("put something in the scene").Open("hierarchy");
        editor.Menu("Entity", "3D Object", "Cube");

        var cube = editor.Scene.Entities.First(entity => editor.Scene.NameOf(entity) == "Cube");

        Assert.Contains("Cube", EditorSession.Labels(editor.Hierarchy));

        // ── edit a property ─────────────────────────────────────────────────────────────────────
        editor.Step("edit a property").ClickRow(editor.Hierarchy, "Cube");

        var position = editor.Inspector.Rows.Single(row => row.Field.Member.Name == "Position");

        Assert.True(position.Field.Write(new Vector3(4f, 5f, 6f)));
        editor.Settle();

        Assert.Equal(new Vector3(4f, 5f, 6f), new Transform(editor.Scene.World, cube).Position);

        // ── undo ────────────────────────────────────────────────────────────────────────────────
        editor.Step("undo it").Run("edit.undo");

        Assert.NotEqual(new Vector3(4f, 5f, 6f), new Transform(editor.Scene.World, cube).Position);

        // And put it back, so that what is saved is what was typed rather than what undo left.
        editor.Run("edit.redo");
        Assert.Equal(new Vector3(4f, 5f, 6f), new Transform(editor.Scene.World, cube).Position);

        // ── save ────────────────────────────────────────────────────────────────────────────────
        editor.Step("save").Run("file.save");

        Assert.False(editor.Scene.IsDirty.Value);

        // ── reopen ──────────────────────────────────────────────────────────────────────────────
        editor.Step("close the editor and open it again").Restart();

        // ── assert ──────────────────────────────────────────────────────────────────────────────
        editor.Step("find everything where it was left");
        editor.ExpandAll(editor.Hierarchy);

        Assert.Contains("Cube", EditorSession.Labels(editor.Hierarchy));

        var reopened = editor.Scene.Entities.First(entity => editor.Scene.NameOf(entity) == "Cube");

        Assert.Equal(new Vector3(4f, 5f, 6f), new Transform(editor.Scene.World, reopened).Position);

        // The asset came back with it, under the same identity — which is what everything else in
        // the project refers to it by.
        Assert.True(editor.Project.Assets.TryGetByGuid(crate, out var still));
        Assert.Equal("Assets/Textures/crate.png", still.Path);
    }

    /// <summary>
    ///     E1's second exit scenario: the asset is renamed and the scene still resolves it.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Doc 20 calls a naive rename "the fastest way to corrupt a project", and this is the
    ///     test of which naivety.</b> Not a stale path — doc 08 chose a GUID over one, so a referrer
    ///     needs nothing done to it. The corruption is leaving the sidecar behind: the next scan
    ///     finds an asset with no identity, mints a new one, and every reference dangles with nothing
    ///     having reported an error. That failure is invisible until a scene is opened, which is
    ///     exactly why it is asserted after a restart rather than before one.
    /// </remarks>
    [Fact]
    public void An_asset_can_be_renamed_and_what_points_at_it_still_resolves() {
        using var editor = EditorSession.Start();

        editor.Step("import an asset and point the scene at it").Open("project");

        var crate = Import(editor, "Assets/Textures/crate.png");

        // A referrer written the way every asset in the project writes one: the GUID in a prefixed
        // scalar, which is what `ReferenceIndex` finds and what a scene file carries.
        var material = Path.Combine(editor.ProjectRoot, "Assets", "Materials", "Crate.vxmat");

        Directory.CreateDirectory(Path.GetDirectoryName(material)!);
        File.WriteAllText(material, $"albedo: vx:{crate}\n");

        editor.Run("assets.refresh");

        var referrer = Guid(editor, "Assets/Materials/Crate.vxmat");

        Assert.Contains(referrer, editor.Project.References.ReferrersOf(crate));

        // ── rename it, through the browser ──────────────────────────────────────────────────────
        editor.Step("rename it in the browser");
        editor.ExpandAll(editor.Assets);
        editor.ClickRow(editor.Assets, "crate.png");

        Assert.True(editor.CanRun("assets.rename"));
        editor.Run("assets.rename");

        var box = Find<TextBox>(editor.Panel("project"))
            ?? throw editor.Fail("Rename did not open an editor on the row.");

        box.Value = "barrel";

        // Enter, through the box the rename opened rather than through the model — the commit is
        // `TreeView`'s and it is what raises the event the browser turns into an operation.
        editor.Ui.Get("textbox").First().PressKey(Vixen.Input.InputKey.Enter);
        editor.Settle();

        Assert.True(editor.Project.Assets.TryGetByGuid(crate, out var renamed));
        Assert.Equal("Assets/Textures/barrel.png", renamed.Path);

        // ── and it still resolves, after a restart ──────────────────────────────────────────────
        editor.Step("reopen and check what points at it");
        editor.Restart();

        Assert.True(editor.Project.Assets.TryGetByGuid(crate, out var survived));
        Assert.Equal("Assets/Textures/barrel.png", survived.Path);

        // The reference was never rewritten and never needed to be. What had to be true is that the
        // sidecar moved with the file, which is the only thing that keeps the GUID attached to it.
        Assert.Contains(crate, editor.Project.References.ReferencesFrom(Guid(editor, "Assets/Materials/Crate.vxmat"))
            .Select(reference => reference.Asset));
    }

    /// <summary>
    ///     ⚠ <b>A delete that does not say what it breaks is the one that loses somebody a day.</b>
    ///     The reference index has answered "what points at this" since it was written; until now
    ///     nothing asked it, and the browser's rows could not be deleted at all.
    /// </summary>
    [Fact]
    public void Deleting_a_referenced_asset_says_what_would_break_before_it_happens() {
        using var editor = EditorSession.Start();

        editor.Step("import an asset and point something at it").Open("project");

        var crate = Import(editor, "Assets/Textures/crate.png");
        var material = Path.Combine(editor.ProjectRoot, "Assets", "Materials", "Crate.vxmat");

        Directory.CreateDirectory(Path.GetDirectoryName(material)!);
        File.WriteAllText(material, $"albedo: vx:{crate}\n");

        editor.Run("assets.refresh");

        editor.Step("delete it");
        editor.ExpandAll(editor.Assets);
        editor.ClickRow(editor.Assets, "crate.png");

        editor.Run("assets.delete");

        Assert.True(editor.IsAsking, "deleting a referenced asset did not ask first");

        var asked = editor.Shell.Dialogs.Current
            ?? throw editor.Fail("the confirmation is not on screen");

        var said = string.Join(" ", Texts(asked));

        Assert.Contains("Crate.vxmat", said, StringComparison.Ordinal);

        // ── and Cancel means cancel ─────────────────────────────────────────────────────────────
        editor.Answer("Cancel");

        Assert.True(editor.Project.Assets.TryGetByGuid(crate, out _));

        // ── while Delete takes the sidecar with it ──────────────────────────────────────────────
        editor.Step("delete it for real").Run("assets.delete");
        editor.Answer("Delete");

        Assert.False(editor.Project.Assets.TryGetByGuid(crate, out _));
        Assert.False(File.Exists(Path.Combine(editor.ProjectRoot, "Assets", "Textures", "crate.png.meta")));
    }

    [Fact]
    public void A_new_folder_appears_in_the_browser_and_an_asset_can_be_moved_into_it() {
        using var editor = EditorSession.Start();

        editor.Step("make a folder").Open("project");

        var crate = Import(editor, "Assets/Textures/crate.png");

        editor.ExpandAll(editor.Assets);
        editor.ClickRow(editor.Assets, "Assets");

        editor.Run("assets.new-folder");
        editor.ExpandAll(editor.Assets);

        Assert.Contains("New Folder", EditorSession.Labels(editor.Assets));

        editor.Step("move the asset into it");

        var folder = Guid(editor, "Assets/New Folder");

        Assert.True(AssetOperations.Move(editor.Project, crate, "Assets/New Folder").Ok);

        editor.Run("assets.refresh");
        editor.ExpandAll(editor.Assets);

        Assert.True(editor.Project.Assets.TryGetByGuid(crate, out var moved));
        Assert.Equal("Assets/New Folder/crate.png", moved.Path);
        Assert.True(editor.Project.Assets.TryGetByGuid(folder, out _));
    }

    /// <summary>Writes a file into the project and makes the editor notice it.</summary>
    /// <remarks>
    ///     ⚠ <b>Through the editor's own Refresh rather than by poking the database.</b> The scan is
    ///     what mints the GUID and writes the sidecar, and a test that wrote its own would be testing
    ///     a second implementation of the thing the scenario is about.
    /// </remarks>
    static AssetId Import(EditorSession editor, string path) {
        var absolute = Path.Combine(editor.ProjectRoot, path.Replace('/', Path.DirectorySeparatorChar));

        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        File.WriteAllText(absolute, "not really a png, and the database does not care");

        editor.Run("assets.refresh");
        return Guid(editor, path);
    }

    static AssetId Guid(EditorSession editor, string path) {
        if (!editor.Project.Assets.TryGetByPath(path, out var entry)) {
            throw editor.Fail($"'{path}' is not in the project's index.");
        }

        return entry.Guid;
    }

    static IEnumerable<string> Texts(Vixen.Ui.UiElement element) {
        if (element.Text is { Length: > 0 } text) {
            yield return text;
        }

        foreach (var child in element.Children) {
            foreach (var found in Texts(child)) {
                yield return found;
            }
        }
    }

    static T? Find<T>(Vixen.Ui.UiElement element) where T : Vixen.Ui.UiElement {
        if (element is T match) {
            return match;
        }

        foreach (var child in element.Children) {
            if (Find<T>(child) is { } found) {
                return found;
            }
        }

        return null;
    }
}
