// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Editor.Assets;
using Vixen.Editor.Core;
using Vixen.Editor.Plugin;
using Vixen.Editor.Testing;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>
///     The editor's own Create ▸ kinds and reference verbs as a module over <see cref="IContentLibrary" /> (doc 50 § D3,
///     #1292), asserted by effect in an editor opened the way the host opens one.
/// </summary>
public class LibraryModuleTests {
    /// <summary>
    ///     Every kind the application registered before the move, by id — ⚠ the set, so a kind lost on the way
    ///     into the module is a red line here rather than a Create ▸ entry nobody notices is gone.
    /// </summary>
    static readonly string[] BuiltIn = [
        "assets.create-scene",
        "assets.create-material",
        "assets.create-shader-graph",
        "assets.create-frame",
        "assets.create-addressable-group",
        "assets.create-vfx",
        "assets.create-animation",
        "assets.create-animation-graph",
        "assets.create-sequence",
        "assets.create-input",
        "assets.create-mixer",
        "assets.create-font",
        "assets.create-spline",
        "assets.create-move-set",
        "assets.create-proxy-shapes",
        "assets.create-shape-vocabulary",
        "assets.create-priorities",
        "assets.create-constraint-template",
        "assets.create-harness"
    ];

    /// <summary>The kinds are all there, in the order Create ▸ showed them, and switching the module off takes every one.</summary>
    /// <remarks>
    ///     ⚠ <b>The off half is what proves ownership.</b> A copy of the list left behind in the application
    ///     would keep the first assertion green; only an editor whose Create ▸ empties of the built-ins when
    ///     the module unloads has one place they come from.
    /// </remarks>
    [Fact]
    public void The_editors_own_kinds_are_the_modules_and_leave_with_it() {
        using var editor = EditorSession.Start();

        Assert.Equal(PluginState.Active, editor.Plugins.Find(LibraryModule.ModuleId)!.State);

        string[] Shown() => [.. editor.Extensions.All<NewAssetKind>().OrderBy(static kind => kind.Order).Select(static kind => kind.Id)];

        Assert.Equal(BuiltIn, Shown().Where(BuiltIn.Contains));

        editor.Plugins.Disable(LibraryModule.ModuleId);

        Assert.DoesNotContain(Shown(), BuiltIn.Contains);
        Assert.Null(editor.Shell.Commands["assets.find-references"]);
        Assert.Null(editor.Shell.Commands["edit.find-references"]);
        Assert.Null(editor.Shell.Commands["assets.select-dependencies"]);
        Assert.Null(editor.Shell.Commands["assets.delete"]);
        Assert.Null(editor.Shell.Commands["assets.show-in-explorer"]);
        Assert.Null(editor.Shell.Commands["assets.reimport-all"]);
        Assert.Null(editor.Shell.Commands["assets.open"]);

        editor.Plugins.Enable(LibraryModule.ModuleId);

        Assert.Equal(BuiltIn, Shown().Where(BuiltIn.Contains));
    }

    /// <summary>A Create ▸ line makes the file through the service a plugin gets, and Find References selects through it.</summary>
    [Fact]
    public void Create_and_find_references_reach_the_published_service() {
        using var editor = EditorSession.Start();

        var library = editor.Plugins.Services.Require<IContentLibrary>();
        var arrived = 0;

        library.Arrived += () => arrived++;

        editor.Run("assets.create-move-set").Settle();

        Assert.Equal(1, arrived);
        Assert.True(editor.Project.Assets.TryGetByPath("Assets/New Move Set.vxmoveset", out var made));

        File.WriteAllText(
            editor.Project.Paths.Absolute("Assets/Uses.yaml"),
            "uses: " + new AssetReference(made.Guid) + "\n"
        );

        editor.Plugins.Services.Require<IProjectSession>().Rescan();

        var user = editor.Project.Assets.Entries.Single(entry => entry.Name == "Uses.yaml").Guid;

        editor.Project.Selection.Set([made.Guid]);
        editor.Run("assets.find-references");

        Assert.Equal([user], editor.Project.Selection);

        editor.Run("assets.select-dependencies");

        Assert.Equal([made.Guid], editor.Project.Selection);
    }

    /// <summary>Open opens the selection through the published document host, in a panel of its own (#1292).</summary>
    /// <remarks>
    ///     ⚠ <b>Counted on the published <see cref="IDocumentHost" />.</b> The command is the module's and asks
    ///     the published workspace, whose open is the host's <c>TryOpen</c> — so the document a plugin's
    ///     <c>Opened</c> handler hears about is the one the command opened. The file is written and scanned
    ///     rather than made by a Create ▸ line, which opens what it makes and would be counted first.
    /// </remarks>
    [Fact]
    public void Open_reaches_the_published_document_host() {
        using var editor = EditorSession.Start();

        var host = editor.Plugins.Services.Require<IDocumentHost>();
        List<EditorDocument> opened = [];

        host.Opened += opened.Add;

        File.WriteAllText(editor.Project.Paths.Absolute("Assets/Walk.vxmoveset"), "name: walk\n");
        editor.Run("assets.refresh");

        Assert.True(editor.Project.Assets.TryGetByPath("Assets/Walk.vxmoveset", out var walk));

        editor.Project.Selection.Set([walk.Guid]);
        editor.Run("assets.open");

        Assert.Equal(walk.Guid, Assert.Single(opened).Asset);
        Assert.True(editor.Shell.Workspace.IsOpen("asset." + walk.Guid));
    }

    /// <summary>Delete asks, then deletes through the service a plugin gets — once, with the sidecar.</summary>
    /// <remarks>
    ///     ⚠ <b>Counted on the published object.</b> A Delete left registered in the application beside the
    ///     module's would delete the file just as well, and only the service's event can tell the two apart.
    /// </remarks>
    [Fact]
    public void Delete_asks_and_then_deletes_through_the_published_service() {
        using var editor = EditorSession.Start();

        var library = editor.Plugins.Services.Require<IContentLibrary>();
        var rearranged = 0;

        library.Rearranged += () => rearranged++;

        File.WriteAllText(editor.Project.Paths.Absolute("Assets/crate.png"), "pixels");
        editor.Run("assets.refresh");

        Assert.True(editor.Project.Assets.TryGetByPath("Assets/crate.png", out var crate));

        editor.Project.Selection.Set([crate.Guid]);
        editor.Run("assets.delete");

        Assert.True(editor.IsAsking, "Delete did not ask first");
        Assert.Equal(0, rearranged);

        editor.Answer("Delete");

        Assert.Equal(1, rearranged);
        Assert.False(editor.Project.Assets.TryGetByGuid(crate.Guid, out _));
        Assert.False(File.Exists(editor.Project.Paths.Absolute("Assets/crate.png.meta")));
    }
}
