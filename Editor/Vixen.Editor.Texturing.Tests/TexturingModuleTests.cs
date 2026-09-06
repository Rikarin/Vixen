// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Core;
using Vixen.Editor.Plugin;
using Vixen.Editor.Ui;
using Xunit;

namespace Vixen.Editor.Texturing.Tests;

/// <summary>Doc 48 § D14: the texture graph is a plugin, and that is the test.</summary>
public class TexturingModuleTests {
    [Fact]
    public void It_registers_its_command_its_panel_and_its_create_entry() {
        using var fixture = new TexturingFixture();

        var loaded = fixture.Host.Activate(TexturingModule.ModuleId, TexturingModule.ModuleName, new TexturingModule());

        Assert.Equal(PluginState.Active, loaded.State);
        Assert.Empty(fixture.Host.Diagnostics);

        Assert.NotNull(fixture.Shell.Commands[TexturingModule.OpenCommand]);
        Assert.NotNull(fixture.Shell.Commands[TexturingModule.OpenStackCommand]);

        // ⚠ The third verb, on the same list rather than in a roll call of its own —
        // <a href="https://github.com/Rikarin/Vixen/issues/887">#887</a>. It was gated separately
        // while two agents were in this file at once, which was the right call then and the wrong
        // shape to leave: the point of a roll call is that it is *one* list, and a second one is a
        // second place to forget the fourth.
        Assert.NotNull(fixture.Shell.Commands[TexturingModule.PaintCommand]);

        Assert.Contains(fixture.Shell.Workspace.Panels, panel => panel.Id == TexturingModule.GraphPanel);
        Assert.Contains(fixture.Shell.Workspace.Panels, panel => panel.Id == TexturingModule.StackPanel);

        // ⚠ Both documents this plugin owns, named rather than counted —
        // <a href="https://github.com/Rikarin/Vixen/issues/806">#806</a>. This was `Assert.Single`,
        // which is what caught the second kind arriving; a count grown to two would have said nothing
        // about *which* two, and the whole finding was that one of them had never been registered.
        Assert.Equal(
            [Layers.LayerStackDocument.Extension, TextureGraphDocument.Extension],
            fixture.Extensions.All<NewAssetKind>().Select(kind => kind.Extension).Order(StringComparer.Ordinal)
        );

        // ⚠ And neither opens, which is not a nicety: this fixture publishes no `AssetEditorRegistry`,
        // and `Opens` is derived from whether there is one to claim the extension in. A kind with
        // `Opens: true` here would put "No editor claims that file" on screen every time somebody
        // made one.
        Assert.All(fixture.Extensions.All<NewAssetKind>(), kind => Assert.False(kind.Opens));
    }

    /// <summary>
    ///     ⚠ The one a host that publishes nothing must see: refused by name, not a null reference.
    /// </summary>
    [Fact]
    public void A_host_with_no_project_refuses_it_with_a_sentence() {
        using var shell = new EditorShell(1280f, 800f);
        var host = new PluginHost(shell);

        var loaded = host.Activate(TexturingModule.ModuleId, TexturingModule.ModuleName, new TexturingModule());

        Assert.Equal(PluginState.Failed, loaded.State);
        Assert.Contains(host.Diagnostics, entry => entry.Message.Contains("EditorProject", StringComparison.Ordinal));

        // Rolled back completely: half a module is harder to spot than half a plugin.
        Assert.Null(shell.Commands[TexturingModule.OpenCommand]);
    }

    [Fact]
    public void Unloading_takes_out_everything_it_registered() {
        using var fixture = new TexturingFixture();

        fixture.Host.Activate(TexturingModule.ModuleId, TexturingModule.ModuleName, new TexturingModule());

        Assert.True(fixture.Host.Unload(TexturingModule.ModuleId));

        Assert.Null(fixture.Shell.Commands[TexturingModule.OpenCommand]);
        Assert.Null(fixture.Shell.Commands[TexturingModule.OpenStackCommand]);
        Assert.Null(fixture.Shell.Commands[TexturingModule.PaintCommand]);
        Assert.DoesNotContain(fixture.Shell.Workspace.Panels, panel => panel.Id == TexturingModule.GraphPanel);
        Assert.DoesNotContain(fixture.Shell.Workspace.Panels, panel => panel.Id == TexturingModule.StackPanel);
        Assert.Empty(fixture.Extensions.All<NewAssetKind>());

        // ⚠ The panel's *command* too, which is the half that is easy to leave: `RegisterPanel` makes
        // two registrations and `UnregisterPanel` is what takes both out. A View-menu line that
        // toggles nothing is the visible symptom; the invisible one is a lambda over the module.
        // ⚠ Both panels, because #806's whole shape was a second document registered nowhere — and a
        // roll call that names only the first cannot catch the second going unregistered *or*
        // un-unregistered.
        Assert.Null(fixture.Shell.Commands[EditorShell.PanelCommand(TexturingModule.GraphPanel)]);
        Assert.Null(fixture.Shell.Commands[EditorShell.PanelCommand(TexturingModule.StackPanel)]);
    }

    /// <summary>
    ///     Doc 48 § D14 asks for <c>PluginHost.WaitForCollection</c> reporting no leak, and this is
    ///     the honest form of that assertion for a module.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b><c>WaitForCollection</c> on a module cannot fail, so asserting it would prove
    ///         nothing.</b> Its first two lines are <c>if (plugin?.Collectible is null) return
    ///         true;</c> and a built-in has no <c>AssemblyLoadContext</c> — the plugin README says so
    ///         in as many words, because claiming otherwise would report a leak for every feature the
    ///         editor ships. A test that called it here would be green against a module that
    ///         registered its panel the long way and leaked it, which is the defect this repository
    ///         keeps shipping under the name "a test that cannot fail".
    ///     </para>
    ///     <para>
    ///         <b>What can fail is the property underneath it.</b> A registration left behind is a
    ///         lambda over the module, and a lambda over the module is a reference the host holds
    ///         into it — the exact thing that pins a plugin's assembly when there is one. So the
    ///         assertion is that the module instance itself is collectable once it has been unloaded,
    ///         which is false the moment any registration is not undone.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Sabotage-checked.</b> Replacing <c>context.AddPanel</c> in
    ///         <c>TexturingModule.Activate</c> with <c>shell.RegisterPanel</c> — which is what
    ///         <c>TerrainModule</c> does today — leaves this red and leaves nothing else in the suite
    ///         red.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Nothing_of_it_is_left_holding_the_module_after_an_unload() {
        using var fixture = new TexturingFixture();

        var reference = Activate(fixture);

        Assert.True(fixture.Host.Unload(TexturingModule.ModuleId));

        // Two collections per attempt, because a finalizer that drops the last reference only runs
        // between them — the arrangement `PluginHost.WaitForCollection` uses, for the same reason.
        for (var attempt = 0; attempt < 8 && reference.IsAlive; attempt++) {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        Assert.False(reference.IsAlive, "something the editor still holds is pointing at the module.");
    }

    /// <summary>Activates a module and keeps only a weak reference to it.</summary>
    /// <remarks>
    ///     ⚠ <b>In a method of its own, and not inlined into the test.</b> A local holding the module
    ///     stays rooted for the whole method in a debug build — the JIT's liveness tracking is not
    ///     what a debug build does — so a test that constructed it in the same frame it collects in
    ///     would fail for a reason that has nothing to do with the module.
    /// </remarks>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static WeakReference Activate(TexturingFixture fixture) {
        var module = new TexturingModule();

        fixture.Host.Activate(TexturingModule.ModuleId, TexturingModule.ModuleName, module);

        return new WeakReference(module);
    }
}
