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
}
