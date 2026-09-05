// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Vixen.Editor.Core;
using Vixen.Editor.Plugin;
using Vixen.Editor.Ui;
using Xunit;

namespace Vixen.Editor.Texturing.Tests;

/// <summary>Doc 48 § D14's assertion, in the one form in which it can fail.</summary>
/// <remarks>
///     <para>
///         ⚠ <b><c>PluginHost.WaitForCollection</c> on a compiled-in module cannot fail.</b> Its
///         first two lines are <c>if (plugin?.Collectible is null) return true;</c>, and a module
///         activated without a load context has none — so every other suite in this project asserts
///         a <see cref="WeakReference" /> to the module instead, which is the honest form there.
///         This suite is the other half: the same module, loaded into a real collectible
///         <see cref="PluginLoadContext" />, where a registration left behind pins an
///         <see cref="AssemblyLoadContext" /> and <c>WaitForCollection</c> says so.
///     </para>
///     <para>
///         ⚠ <b>The instrument is checked before the claim, because this is the exact shape of test
///         that passes by not running.</b> A context that never loaded anything is collected
///         instantly whatever the module leaked, so <see cref="Activate" /> asserts that the type it
///         instantiated came out of <i>that</i> context and not out of the test's own reference to
///         the same assembly. Without that line the whole file is green against a plugin host that
///         does nothing at all.
///     </para>
///     <para>
///         ⚠ <b>Sabotage-checked.</b> Replacing <c>context.AddPanel</c> in
///         <c>TexturingModule.Activate</c> with <c>context.Shell.RegisterPanel</c> — which is what
///         <c>TerrainModule</c> does today, in five places, with no matching
///         <c>UnregisterPanel</c> — leaves
///         <see cref="The_module_leaves_no_load_context_behind" /> red and nothing else in this
///         project red.
///     </para>
/// </remarks>
public class TexturingCollectionTests {
    /// <summary>The plugin id the loaded copy is activated under.</summary>
    /// <remarks>
    ///     Not <c>TexturingModule.ModuleId</c>, because the constant this test assembly compiled
    ///     against and the constant in the loaded copy are the same string for a reason nobody should
    ///     have to rely on — this suite is about two copies of one assembly, and naming the loaded one
    ///     separately keeps that distinction visible in a failure message.
    /// </remarks>
    const string LoadedId = "vixen.texturing.loaded";

    [Fact]
    public void The_module_activates_out_of_a_collectible_context_and_registers() {
        using var fixture = new TexturingFixture(editors: true);

        var loaded = Activate(fixture);

        Assert.Equal(PluginState.Active, loaded.State);
        Assert.Empty(fixture.Host.Diagnostics);

        Assert.NotNull(fixture.Shell.Commands[TexturingModule.OpenCommand]);
        Assert.Contains(fixture.Shell.Workspace.Panels, panel => panel.Id == TexturingModule.GraphPanel);
        Assert.True(fixture.Editors.TryGetForFile("Assets/Bricks" + TextureGraphDocument.Extension, out _));

        fixture.Host.Unload(LoadedId);
    }

    /// <summary>The claim the whole plugin design rests on, and the one the runtime reports nothing about.</summary>
    /// <remarks>
    ///     A context that cannot be collected unloads on paper, stays in memory in fact, and is not
    ///     noticed until the same plugin is loaded a second time and its static state is not what it
    ///     was. Ten seconds is a hang check rather than a budget: the loop inside
    ///     <c>WaitForCollection</c> returns on the first collection that works.
    /// </remarks>
    [Fact]
    public void The_module_leaves_no_load_context_behind() {
        using var fixture = new TexturingFixture(editors: true);

        Activate(fixture);

        Assert.True(fixture.Host.Unload(LoadedId));

        // ⚠ Nothing left registered, asserted before the collection rather than instead of it. A
        // registration the shell still holds is a lambda over the plugin's own types, which is
        // precisely what a live context looks like from the other side — so a failure here says
        // *which* registration, and the assertion below only says that there was one.
        Assert.Null(fixture.Shell.Commands[TexturingModule.OpenCommand]);
        Assert.Null(fixture.Shell.Commands[EditorShell.PanelCommand(TexturingModule.GraphPanel)]);
        Assert.DoesNotContain(fixture.Shell.Workspace.Panels, panel => panel.Id == TexturingModule.GraphPanel);
        Assert.Empty(fixture.Extensions.All<NewAssetKind>());
        Assert.Equal(0, fixture.Editors.Count);

        Assert.True(
            fixture.Host.WaitForCollection(LoadedId, TimeSpan.FromSeconds(10)),
            "the texturing module's load context was still alive, so something the editor holds points into it."
        );
    }

    /// <summary>Loads a second copy of this assembly into a collectible context and activates it.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The context is made <em>here</em>, not by the caller, and that is not tidiness —
    ///         it is the difference between this test working and not.</b>
    ///         <c>WaitForCollection</c> reads a <see cref="WeakReference" /> to the context object
    ///         itself, and a debug build roots every local for the whole method it is declared in. A
    ///         test that held the context in a variable would report a leak on a module that leaked
    ///         nothing, for ever. The module and its type are here for the same reason.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>PluginLoadContext.LoadPlugin</c> is what makes the entry assembly the
    ///         context's own.</b> Its <c>Load</c> override sends every <c>Vixen.*</c> to the default
    ///         context, so a copy resolved by name would be the host's — and the whole test would
    ///         assert that an empty context collects. The two assertions below are the ones that
    ///         catch it: a different <see cref="Assembly" /> object, in this context.
    ///     </para>
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    static LoadedPlugin Activate(TexturingFixture fixture) {
        var context = new PluginLoadContext(typeof(TexturingModule).Assembly.Location, "texturing-test");
        var assembly = context.LoadPlugin();

        Assert.NotSame(typeof(TexturingModule).Assembly, assembly);
        Assert.Same(context, AssemblyLoadContext.GetLoadContext(assembly));

        var type = assembly.GetType(typeof(TexturingModule).FullName!, throwOnError: true)!;

        // ⚠ Not `typeof(TexturingModule)`: the interface is the *host's* — `PluginLoadContext` sends
        // `Vixen.Editor.Plugin` to the default context on purpose — while the implementation is this
        // context's. A cast that failed here would be the classic "cannot cast IEditorPlugin to
        // IEditorPlugin", and it passing is what proves the sharing rule works for this assembly.
        var module = (IEditorPlugin)Activator.CreateInstance(type)!;

        // ⚠ The line that makes this suite mean anything, and it is not implied by the two above.
        // A context that has *loaded* an assembly nothing instantiates from is collected the instant
        // it is unloaded, whatever the module left behind — checked by handing this host's own
        // `new TexturingModule()` to a host with a context that had loaded the second copy, which is
        // green on a module that leaks its panel. What pins a context is a live object of its types.
        Assert.Same(context, AssemblyLoadContext.GetLoadContext(module.GetType().Assembly));

        return fixture.Host.Activate(LoadedId, "Texturing (loaded)", module, context);
    }
}
