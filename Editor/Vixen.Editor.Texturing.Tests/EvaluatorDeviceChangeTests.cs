// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Plugin;
using Vixen.Graphics;
using Xunit;

namespace Vixen.Editor.Texturing.Tests;

/// <summary>The module's evaluator follows the host's device, because it cannot outlive one.</summary>
/// <remarks>
///     <para>
///         <b><a href="https://github.com/Rikarin/Vixen/issues/945">#945</a>, and the shape it
///         corrects is <a href="https://github.com/Rikarin/Vixen/issues/820">#820</a>'s.</b> Sharing
///         one evaluator between the two panes is right and is what
///         <see cref="SharedEvaluatorDeviceTests" /> measures; holding it for the life of the
///         <em>module</em> is what was wrong, because a <c>TexturePlanEvaluator</c> caches a pipeline
///         and a shader module per kernel and output format on the device it was constructed with,
///         and there is no route by which any of that is replayed onto another one.
///     </para>
///     <para>
///         ⚠ <b>That the device can change under an open session was checked before this was
///         written, because the issue's own first question was whether it can.</b> It can:
///         <c>EditorHost</c> answers <c>PlatformEventKind.Suspending</c> with <c>Release</c>, which
///         disposes the <c>VulkanDevice</c> and nulls <c>EditorApplication.GraphicsDevice</c>, and
///         the next <c>Present</c> calls <c>EnsureDevice</c>, which sees a null device and a surface
///         that can present and creates another. Nothing tells the plugin;
///         <c>IEditorGraphics.Device</c> just starts answering differently.
///     </para>
///     <para>
///         ⚠ <b>Nothing reports the defect this is about, which is why it is a counter rather than a
///         picture.</b> A pane dispatching through pipelines of a destroyed device does not draw a
///         wrong image — it draws whatever a driver does with a dangling handle, which is a crash
///         somewhere else and on somebody else's frame.
///     </para>
///     <para>
///         ⚠ <b>Two real adapters, or a loud skip.</b> Without a device there is no evaluator, no
///         count and nothing to compare — and a suite that quietly passed on a host with no adapter
///         would be the instrument that reports success on the day it does not run.
///     </para>
/// </remarks>
public class EvaluatorDeviceChangeTests {
    /// <summary>A second device gets a second evaluator, and the first one is not handed out again.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The old device is deliberately <em>not</em> disposed here, though the editor's own
    ///         route does dispose it.</b> Destroying a <c>VkDevice</c> whose pipelines are still alive
    ///         is undefined by the specification, and modelling that here would make this suite's
    ///         outcome a driver's opinion rather than an assertion. What is asserted is the half a
    ///         module can control — that it stops dispatching through the old one.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>"The module cannot destroy them: it is never told the device is going" was true
    ///         when this was written and is not any more</b> —
    ///         <a href="https://github.com/Rikarin/Vixen/issues/968">#968</a>. It is told, and
    ///         <see cref="The_module_gives_its_pipelines_back_when_the_host_announces_the_loss" />
    ///         is that path. This test keeps the silent route on purpose, because it is what a host
    ///         that announces nothing still does and the module still has to survive it.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_second_device_gets_a_second_evaluator_rather_than_the_first_ones_pipelines() {
        using var first = TexturingDevice.Open();
        using var second = TexturingDevice.Open();

        Swapping graphics = new(first);

        using var fixture = new TexturingFixture(graphics: false);

        fixture.Services.Add<IEditorGraphics>(graphics);

        TexturingModule module = new();

        fixture.Host.Activate(TexturingModule.ModuleId, TexturingModule.ModuleName, module);
        fixture.Project.Selection.Set(LayerStackPanelTests.AddStack(fixture, "Hull"));

        Assert.True(fixture.Shell.Commands.Execute(TexturingModule.OpenStackCommand));
        Assert.NotNull(fixture.Shell.Workspace.Open(TexturingModule.StackPanel));

        // The instrument, before the finding. A pane that never reached the device compiles nothing,
        // and "one evaluator became two" is true of a module that built neither.
        Assert.Equal(1, module.EvaluatorsBuilt);
        Assert.True(
            module.KernelCompilations > 0,
            $"{TexturingDevice.Adapter(first)}: the stack pane compiled no kernel variants, so nothing "
            + "here was dispatched and the counts below are about a pane that did not draw."
        );

        // The window went and came back. This is the whole of what a plugin observes.
        graphics.Device = second;

        Assert.True(fixture.Shell.Commands.Execute(TexturingModule.OpenStackCommand));

        Assert.Equal(2, module.EvaluatorsBuilt);

        // ⚠ And the evaluator the module is *now* holding has compiled kernels of its own, which is
        // a second instrument rather than a second finding: `KernelCompilations` asks whichever
        // evaluator the module holds, so a rebuild that produced one nothing ever dispatched through
        // would read zero here while the count above still said two.
        Assert.True(
            module.KernelCompilations > 0,
            $"{TexturingDevice.Adapter(second)}: the module rebuilt its evaluator and that evaluator has "
            + "compiled nothing, so the pane did not draw through the replacement."
        );
    }

    /// <summary>And the same device twice is still one evaluator, which is #820 unchanged.</summary>
    /// <remarks>
    ///     ⚠ <b>The half a per-device cache is easiest to get wrong in the other direction.</b> A
    ///     module that rebuilt on every call would draw correctly and pay a Raven parse, a shader
    ///     module and a compute pipeline per kernel per refresh — a cost nothing reports, which is
    ///     exactly why #820 needed a counter rather than a picture.
    /// </remarks>
    [Fact]
    public void The_same_device_twice_is_still_one_evaluator() {
        using var device = TexturingDevice.Open();

        Swapping graphics = new(device);

        using var fixture = new TexturingFixture(graphics: false);

        fixture.Services.Add<IEditorGraphics>(graphics);

        TexturingModule module = new();

        fixture.Host.Activate(TexturingModule.ModuleId, TexturingModule.ModuleName, module);
        fixture.Project.Selection.Set(LayerStackPanelTests.AddStack(fixture, "Hull"));

        Assert.True(fixture.Shell.Commands.Execute(TexturingModule.OpenStackCommand));
        Assert.NotNull(fixture.Shell.Workspace.Open(TexturingModule.StackPanel));
        Assert.True(fixture.Shell.Commands.Execute(TexturingModule.OpenStackCommand));
        Assert.True(fixture.Shell.Commands.Execute(TexturingModule.OpenStackCommand));

        Assert.Equal(1, module.EvaluatorsBuilt);
        Assert.True(
            module.KernelCompilations > 0,
            $"{TexturingDevice.Adapter(device)}: nothing was dispatched, so one evaluator is the count of "
            + "a module that built none."
        );
    }

    /// <summary>Told that the device is going, the module hands its pipelines back before it does.</summary>
    /// <remarks>
    ///     <para>
    ///         <b><a href="https://github.com/Rikarin/Vixen/issues/968">#968</a>, from the plugin's
    ///         side.</b> The two tests above are what a module can do when it is told nothing: notice,
    ///         afterwards, and drop what it cannot legally destroy. <c>PluginContext.OnDeviceLost</c>
    ///         is raised while the device is still valid, so here the pipelines, shader modules and
    ///         <c>EffectLoader</c> go back to the device that owns them.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The oracle is that the device does <em>not</em> change, and that is what makes the
    ///         assertion about the announcement rather than about the comparison.</b>
    ///         <c>TexturingModule.Evaluator</c> already rebuilds when it is handed a device that is
    ///         not the one it built on — so a module that ignored the announcement entirely would
    ///         still read two evaluators in a test that swapped the device, and would read one here.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>KernelCompilations</c> is asked before the rebuild for the same reason.</b> It
    ///         reads whichever evaluator the module is holding, so zero is the module holding none —
    ///         a release that dropped the reference without disposing it, or one that never ran,
    ///         cannot both produce zero here and a second build below.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_module_gives_its_pipelines_back_when_the_host_announces_the_loss() {
        using var device = TexturingDevice.Open();

        Swapping graphics = new(device);

        using var fixture = new TexturingFixture(graphics: false);

        fixture.Services.Add<IEditorGraphics>(graphics);

        TexturingModule module = new();

        fixture.Host.Activate(TexturingModule.ModuleId, TexturingModule.ModuleName, module);
        fixture.Project.Selection.Set(LayerStackPanelTests.AddStack(fixture, "Hull"));

        Assert.True(fixture.Shell.Commands.Execute(TexturingModule.OpenStackCommand));
        Assert.NotNull(fixture.Shell.Workspace.Open(TexturingModule.StackPanel));

        Assert.Equal(1, module.EvaluatorsBuilt);
        Assert.True(
            module.KernelCompilations > 0,
            $"{TexturingDevice.Adapter(device)}: the stack pane compiled no kernel variants, so there is "
            + "nothing for the release below to give back."
        );

        // What the host now says before it stops answering with the device — and the device is still
        // the same one afterwards, which is what the module's own mismatch check cannot see.
        fixture.Host.DeviceLost(device);

        Assert.Equal(0, module.KernelCompilations);

        Assert.True(fixture.Shell.Commands.Execute(TexturingModule.OpenStackCommand));

        Assert.Equal(2, module.EvaluatorsBuilt);
    }

    /// <summary>A host whose device answer moves, which is what <c>PluginGraphics</c> really is.</summary>
    /// <remarks>
    ///     ⚠ <b>Its own double rather than <c>RecordingGraphics</c>, whose device is fixed at
    ///     construction.</b> The property under test is precisely that the answer changes, so a
    ///     recorder that could not change it would make this suite unwritable — and widening the
    ///     shared recorder for one suite is how a harness grows a knob nothing else uses.
    /// </remarks>
    sealed class Swapping(IGraphicsDevice? device) : IEditorGraphics {
        /// <summary>What the host answers now.</summary>
        public IGraphicsDevice? Device { get; set; } = device;

        /// <inheritdoc />
        public IEditorImage? Upload(int width, int height, ReadOnlySpan<byte> rgba) => null;

        /// <inheritdoc />
        public bool Update(IEditorImage image, int x, int y, int width, int height, ReadOnlySpan<byte> rgba) =>
            false;
    }
}
