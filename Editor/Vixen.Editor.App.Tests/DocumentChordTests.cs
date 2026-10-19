// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.AssetEditors.Frame;
using Vixen.Editor.Core;
using Vixen.Editor.Testing;
using Vixen.Input;
using Vixen.Rendering.PostFx;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>
///     <c>document.save</c>, <c>document.revert</c> and <c>document.close</c>, reached the way a person
///     reaches them: a chord pressed with the focus in a tab (#1494).
/// </summary>
/// <remarks>
///     ⚠ <b>Every asset tab answered the three ids on the route and no gesture asked it.</b> The
///     editor's keymap named <c>file.save</c>, which saves the scene, so Ctrl+S in a dirty material tab
///     saved the scene and left the material dirty. The chords are the route's ids now, and the
///     registry's commands of the same ids are what a focus in no document's panel falls back to.
/// </remarks>
public sealed class DocumentChordTests {
    const string Knobs = """
        version: 2
        game: !StandardFrame
          quality: High
          shadows: Cascades
          look: !Look
            settings:
              ev100: 13

        """;

    [Fact]
    public void Ctrl_s_in_a_dirty_asset_tab_saves_that_document_and_not_the_scene() {
        using var session = EditorSession.Start();

        var (document, path, tab) = Editing(session);

        session.Scene.Create("Unsaved", Vixen.Engine.Transforms.LocalTransform.Identity);
        Dirty(document);

        Assert.True(session.Scene.HasUnsavedChanges.Value);
        Assert.True(document.IsDirty.Value);

        FocusIn(session, tab);
        Chord(session, InputKey.S);

        Assert.False(document.IsDirty.Value, "Ctrl+S in the tab did not save the tab's document");
        Assert.NotEqual(Knobs, File.ReadAllText(path));
        Assert.True(session.Scene.HasUnsavedChanges.Value, "Ctrl+S in an asset tab saved the scene instead");
    }

    /// <summary>The fallback: with the focus in no document's panel the chord is the scene's save, as it always was.</summary>
    [Fact]
    public void Ctrl_s_with_the_focus_in_no_document_saves_the_scene() {
        using var session = EditorSession.Start();

        var (document, _, _) = Editing(session);

        session.Scene.Create("Unsaved", Vixen.Engine.Transforms.LocalTransform.Identity);
        Dirty(document);

        session.Document.Focus(null);
        Chord(session, InputKey.S);

        Assert.False(session.Scene.HasUnsavedChanges.Value, "Ctrl+S outside any tab did not save the scene");
        Assert.True(document.IsDirty.Value, "Ctrl+S outside the tab saved the tab's document");
    }

    /// <summary>Ctrl+W in a tab is that document's close, and the tab is what closes.</summary>
    [Fact]
    public void Ctrl_w_in_an_asset_tab_closes_that_tab() {
        using var session = EditorSession.Start();

        var (_, _, tab) = Editing(session);

        Assert.True(session.Shell.Workspace.IsOpen(tab));

        FocusIn(session, tab);
        Chord(session, InputKey.W);
        session.Settle();

        Assert.False(session.Shell.Workspace.IsOpen(tab), "Ctrl+W in the tab left it open");
    }

    /// <summary>Ctrl+W in a dirty tab asks first, and Cancel keeps the tab and every edit in it (#1501).</summary>
    /// <remarks>
    ///     ⚠ The tab used to close on the request and leave its document dirty in the project with
    ///     nothing showing it — Save All and the quit prompt still named it, but the person who closed
    ///     the tab could not see which of keeping or discarding they had chosen.
    /// </remarks>
    [Fact]
    public void Ctrl_w_in_a_dirty_asset_tab_asks_and_cancel_keeps_the_tab_and_its_edits() {
        using var session = EditorSession.Start();

        var (document, path, tab) = Editing(session);

        Dirty(document);
        FocusIn(session, tab);
        Chord(session, InputKey.W);

        Assert.True(session.IsAsking, "Ctrl+W in a tab with unsaved edits did not ask");
        Assert.True(session.Shell.Workspace.IsOpen(tab), "the tab closed before the question was answered");

        session.Answer(ControlStrings.DialogCancel.Text);

        Assert.True(session.Shell.Workspace.IsOpen(tab), "Cancel closed the tab");
        Assert.True(document.IsOpen);
        Assert.True(document.IsDirty.Value, "Cancel threw the edits away");
        Assert.Equal(AntialiasingMode.Off, document.Settings.Antialiasing);
        Assert.Equal(Knobs, File.ReadAllText(path));
    }

    /// <summary>Don't Save closes the tab and the document, and the file is what it was (#1501).</summary>
    [Fact]
    public void Dont_save_closes_the_tab_and_its_document_and_leaves_the_file_alone() {
        using var session = EditorSession.Start();

        var (document, path, tab) = Editing(session);

        Dirty(document);
        FocusIn(session, tab);
        Chord(session, InputKey.W);

        Assert.True(session.IsAsking, "Ctrl+W in a tab with unsaved edits did not ask");

        session.Answer(ControlStrings.DocumentDiscard.Text);

        Assert.False(session.Shell.Workspace.IsOpen(tab), "Don't Save left the tab open");
        Assert.False(document.IsOpen, "Don't Save left the dirty document open with no tab showing it");
        Assert.DoesNotContain(document, session.Project.Documents);
        Assert.Equal(Knobs, File.ReadAllText(path));
    }

    /// <summary>Save writes the file and then closes the tab (#1501).</summary>
    [Fact]
    public void Save_writes_the_file_and_closes_the_tab() {
        using var session = EditorSession.Start();

        var (document, path, tab) = Editing(session);

        Dirty(document);
        FocusIn(session, tab);
        Chord(session, InputKey.W);

        Assert.True(session.IsAsking, "Ctrl+W in a tab with unsaved edits did not ask");

        session.Answer(ControlStrings.DocumentSave.Text);

        Assert.NotEqual(Knobs, File.ReadAllText(path));
        Assert.False(session.Shell.Workspace.IsOpen(tab), "Save left the tab open");
        Assert.False(document.IsOpen);
    }

    /// <summary>
    ///     ⚠ Revert in a tab still asks first. <c>DocumentCommands</c>' own answer reverts without a
    ///     question — the framework has no dialog — and the tab's is the editor's, which has always
    ///     asked before it threw away the only copy of somebody's edits.
    /// </summary>
    [Fact]
    public void Revert_in_an_asset_tab_asks_before_it_throws_edits_away() {
        using var session = EditorSession.Start();

        var (document, _, tab) = Editing(session);
        var saved = document.Settings.Antialiasing;

        Dirty(document);
        FocusIn(session, tab);

        Assert.True(CommandRoute.Execute(session.Document, DocumentCommands.Revert));
        session.Settle();

        Assert.True(session.IsAsking, "reverting a tab with unsaved edits did not ask first");
        Assert.True(document.IsDirty.Value);

        session.Answer("Revert");

        Assert.False(document.IsDirty.Value);
        Assert.Equal(saved, document.Settings.Antialiasing);
    }

    static (StandardFrameDocument Document, string Path, string Tab) Editing(EditorSession session) {
        var name = "Frame" + StandardFrameDocument.Extension;
        var path = Path.Combine(session.Project.Paths.Assets, name);

        Directory.CreateDirectory(session.Project.Paths.Assets);
        File.WriteAllText(path, Knobs);
        session.Project.Assets.Scan();

        Assert.True(session.Project.Assets.TryGetByPath("Assets/" + name, out var entry));

        session.Editor.OpenAsset(entry.Guid);
        session.Settle();

        return (session.Project.Documents.OfType<StandardFrameDocument>().Single(), path, "asset." + entry.Guid);
    }

    /// <summary>A real change to what the file will hold, on the document's own stack.</summary>
    static void Dirty(StandardFrameDocument document) {
        document.Settings.Antialiasing = AntialiasingMode.Off;
        Assert.True(document.Apply());
        document.Stack.Execute(new DelegateCommand("Turn a knob", _ => { }, _ => { }));
    }

    /// <summary>Puts the focus on something inside a tab, which is where the route starts.</summary>
    static void FocusIn(EditorSession session, string tab) {
        var target = Descendants(session.Panel(tab)).FirstOrDefault(element => element.Focusable)
            ?? throw session.Fail($"Nothing in the '{tab}' tab takes the focus.");

        Assert.True(session.Document.Focus(target));
        session.Settle();
    }

    static void Chord(EditorSession session, InputKey key) {
        session.Ui.Hold(ModifierKeys.Control);
        session.Ui.PressKey(key);
        session.Ui.Hold(ModifierKeys.None);
        session.Settle();
    }

    static IEnumerable<UiElement> Descendants(UiElement element) {
        foreach (var child in element.Children) {
            yield return child;

            foreach (var found in Descendants(child)) {
                yield return found;
            }
        }
    }
}
