// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using Vixen.Editor.AssetEditors;
using Vixen.Editor.Core;
using Vixen.Editor.Plugin;
using Vixen.Editor.Testing;
using Vixen.Editor.Ui.Documents;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>
///     The asset editors as a module's contribution (#1297): the application references none of the
///     assembly, so everything a double-click opens arrives through the module — and leaves with it.
/// </summary>
/// <remarks>
///     ⚠ <b>Reached by effect, through the services a module is handed</b>, because the editor that
///     builds its own registries and the test that builds another are two hosts — a test that asked
///     <c>StandardEditors</c> would agree with itself on the day the module stopped registering.
/// </remarks>
public sealed class AssetEditorsModuleReachTests {
    /// <summary>The module's kinds and views are the editor's, and a double-click on a material opens one.</summary>
    [Fact]
    public void The_kinds_and_views_the_editor_opens_with_are_the_modules() {
        using var session = EditorSession.Start();

        var documents = session.Plugins.Services.Require<IDocumentHost>();
        var workspace = session.Plugins.Services.Require<IWorkspace>();

        Assert.True(documents.TryGetForFile("Assets/hero.vxmat", out _));
        Assert.NotEmpty(workspace.Views);

        Assert.True(session.Plugins.Unload(AssetEditorsModule.ModuleId));

        // ⚠ Nothing of it stays: the application registers no kind of its own, so the editor with
        // the module switched off claims no asset-editor file, which is what a double-click then says.
        Assert.False(documents.TryGetForFile("Assets/hero.vxmat", out _));

        // ⚠ Nothing of *this assembly's*, rather than nothing at all: since #1276 the texturing module
        // is standard and adds its texture-graph and layer-stack views to the same workspace, and they
        // stay when this module goes — which is also what proves the walk below sees views at all.
        var assembly = typeof(AssetEditorsModule).Assembly;

        Assert.DoesNotContain(workspace.Views, view => view.GetType().Assembly == assembly);
        Assert.Contains(workspace.Views, view => view.GetType().Assembly != assembly);
    }

    /// <summary>After an unload nothing the editor still holds reaches the module, even with its documents opened.</summary>
    /// <remarks>
    ///     ⚠ <b>A <see cref="WeakReference" />, on <c>WaterModuleReachTests</c>' terms</b>: a built-in
    ///     has no load context, so <c>PluginHost.WaitForCollection</c> answers true for it whatever
    ///     happens. The fresh instance opens a material and builds its tab first, so the subscriptions
    ///     this module makes on <see cref="IDocumentHost.Opened" /> and <see cref="IWorkspace.Shown" />
    ///     have been exercised before they are asked to let go.
    /// </remarks>
    [Fact]
    public void Nothing_the_editor_holds_reaches_the_module_after_an_unload() {
        using var session = EditorSession.Start();

        Assert.True(session.Plugins.Unload(AssetEditorsModule.ModuleId));

        var reference = ActivateFreshAndOpen(session);

        session.Settle();
        Assert.True(session.Plugins.Unload(AssetEditorsModule.ModuleId));
        session.Settle();

        for (var attempt = 0; attempt < 8 && reference.IsAlive; attempt++) {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        Assert.False(reference.IsAlive, "something the editor still holds is pointing at the asset editors module.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static WeakReference ActivateFreshAndOpen(EditorSession session) {
        var module = new AssetEditorsModule();

        Assert.Equal(
            PluginState.Active,
            session.Plugins.Activate(AssetEditorsModule.ModuleId, AssetEditorsModule.ModuleName, module).State
        );

        session.Run("assets.create-material").Settle();

        // The material's tab was built by the module's view, so its join ran.
        Assert.Contains(session.Project.Documents, document => document.GetType().Name == "MaterialDocument");

        return new WeakReference(module);
    }
}
