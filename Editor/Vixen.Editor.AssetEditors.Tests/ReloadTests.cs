// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Editor.AssetEditors.Code;
using Vixen.Editor.AssetEditors.Frame;
using Vixen.Editor.Core;
using Vixen.Rendering.PostFx;
using Xunit;

namespace Vixen.Editor.AssetEditors.Tests;

/// <summary>The two documents that can read their file again, doing it.</summary>
/// <remarks>
///     ⚠ <b>The half of doc 148 that would otherwise be a mechanism with no implementor.</b>
///     <c>ExternalEdits</c> routes a change to a document and the document is what has to act on it;
///     a <c>CanReload</c> that nothing overrode would be a seam that is built and never fed, which is
///     this tree's commonest defect.
/// </remarks>
public class ReloadTests : IDisposable {
    const string Knobs = """
        version: 2
        game: !StandardFrame
          quality: High
          shadows: Cascades
          look: !Look
            settings:
              ev100: 13
        """;

    const string Changed = """
        version: 2
        game: !StandardFrame
          quality: Low
          shadows: Off
          look: !Look
            settings:
              ev100: 7
        """;

    /// <summary>A tag no binder knows, which is what a half-written or hand-broken file looks like.</summary>
    const string Broken = """
        version: 2
        game: !ThereIsNoSuchNodeKind
          nonsense: true
        """;

    readonly EditorFixture fixture = new();

    public void Dispose() {
        fixture.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void A_frame_document_says_it_can_read_its_file_again() {
        var document = new StandardFrameDocument(
            fixture.Project,
            AssetId.New(),
            fixture.Write("Assets/Frame.vxcompositor", Knobs)
        );

        Assert.True(document.CanReload);
    }

    /// <summary>The knobs, the expansion and the live-apply event all follow the file.</summary>
    [Fact]
    public void A_frame_edited_on_disk_reaches_the_knobs_and_the_expansion() {
        var path = fixture.Write("Assets/Frame.vxcompositor", Knobs);
        var document = new StandardFrameDocument(fixture.Project, AssetId.New(), path);
        var announced = 0;

        document.Changed += _ => announced++;

        Assert.Equal(FrameQualityChoice.High, document.Settings.Quality);

        File.WriteAllText(path, Changed);

        Assert.True(document.Reload());
        Assert.Equal(FrameQualityChoice.Low, document.Settings.Quality);
        Assert.Equal(ShadowMode.Off, document.Settings.Shadows);
        Assert.Equal(7f, document.Look.Ev100);

        // ⚠ The same event an in-editor knob raises, which is what makes a viewport hosting the
        // document follow a change on disk without a second subscription.
        Assert.Equal(1, announced);
    }

    /// <summary>
    ///     ⚠ The entries described the previous contents of the file, so they go — and the document is
    ///     then what is on disk, which is what clean means.
    /// </summary>
    [Fact]
    public void Reloading_a_frame_discards_the_history_and_leaves_it_clean() {
        var path = fixture.Write("Assets/Frame.vxcompositor", Knobs);
        var document = new StandardFrameDocument(fixture.Project, AssetId.New(), path);

        document.Settings.Quality = FrameQualityChoice.Low;
        document.Apply();
        document.Save();
        document.Settings.Shadows = ShadowMode.Off;
        document.Apply();

        File.WriteAllText(path, Changed);

        Assert.True(document.Reload());
        Assert.Equal(0, document.Stack.Depth.Value);
        Assert.False(document.IsDirty.Value);
        Assert.False(document.IsStale.Value);
    }

    /// <summary>
    ///     ⚠ <c>Reframe</c>'s trade. A tool that writes a file in two passes would otherwise blank the
    ///     panel on the first of them.
    /// </summary>
    [Fact]
    public void A_frame_that_will_not_parse_leaves_the_one_on_screen_standing() {
        var path = fixture.Write("Assets/Frame.vxcompositor", Knobs);
        var document = new StandardFrameDocument(fixture.Project, AssetId.New(), path);

        File.WriteAllText(path, Broken);

        Assert.False(document.Reload());
        Assert.NotNull(document.Node);
        Assert.Equal(FrameQualityChoice.High, document.Settings.Quality);
        Assert.NotEmpty(document.Diagnostics);
    }

    /// <summary>
    ///     ⚠ And opening one still does the opposite, which is the reason the two paths are a
    ///     parameter rather than one behaviour. A broken file has to open, or its diagnostics have
    ///     nowhere to be read.
    /// </summary>
    [Fact]
    public void Opening_a_broken_frame_still_produces_a_document_to_look_at() {
        var document = new StandardFrameDocument(
            fixture.Project,
            AssetId.New(),
            fixture.Write("Assets/Frame.vxcompositor", Broken)
        );

        Assert.Null(document.Node);
        Assert.False(document.CanEdit);
        Assert.NotEmpty(document.Diagnostics);
    }

    /// <summary>
    ///     ⚠ Exploding rewrites the file into a form that has no knobs, so the knob-turn entries on
    ///     the stack would undo a <c>!StandardFrame</c> back over the expansion.
    /// </summary>
    [Fact]
    public void Exploding_leaves_no_history_over_the_file_it_wrote() {
        var path = fixture.Write("Assets/Frame.vxcompositor", Knobs);
        var document = new StandardFrameDocument(fixture.Project, AssetId.New(), path);

        // A real entry on the stack, because the knob mirrors are not themselves undoable — an
        // inspector's write is, and this stands in for one.
        document.Stack.Execute(new DelegateCommand("Turn a knob", _ => { }, _ => { }));

        document.Settings.Quality = FrameQualityChoice.Low;
        document.Apply();

        Assert.Equal(1, document.Stack.Depth.Value);
        Assert.True(document.IsDirty.Value);

        document.Explode();

        Assert.Null(document.Node);
        Assert.Equal(0, document.Stack.Depth.Value);
        Assert.False(document.IsDirty.Value);
    }

    /// <summary>A shader edited by another program reaches the buffer.</summary>
    [Fact]
    public void A_text_asset_edited_on_disk_reaches_the_buffer() {
        var path = fixture.Write("Assets/hero.rvn", "shader Hero {\n}\n");
        var document = new ShaderDocument(fixture.Project, AssetId.New(), path);

        Assert.True(document.CanReload);

        File.WriteAllText(path, "shader Hero {\n  // somebody else\n}\n");

        Assert.True(document.Reload());
        Assert.Contains("somebody else", document.Buffer.Text, StringComparison.Ordinal);
    }

    /// <summary>
    ///     ⚠ The reload is not typing. An entry for it would put the version it just replaced back one
    ///     Ctrl+Z later, and the caller has already decided that version is gone.
    /// </summary>
    [Fact]
    public void Reloading_a_text_asset_pushes_no_undo_entry() {
        var path = fixture.Write("Assets/hero.rvn", "one\n");
        var document = new ShaderDocument(fixture.Project, AssetId.New(), path);

        document.Buffer.Insert(new(0, 3), "!");
        document.Save();

        File.WriteAllText(path, "two\n");

        Assert.True(document.Reload());
        Assert.Equal("two\n", document.Buffer.Text);
        Assert.Equal(0, document.Stack.Depth.Value);
        Assert.False(document.Stack.CanUndo.Value);
        Assert.False(document.IsDirty.Value);
    }
}
