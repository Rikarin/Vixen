// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Texturing.Layers;
using Xunit;

namespace Vixen.Editor.Texturing.Tests;

/// <summary>Both panes dispatch through one evaluator, so a kernel they share is compiled once.</summary>
/// <remarks>
///     <para>
///         <b><a href="https://github.com/Rikarin/Vixen/issues/820">#820</a>, and it is a defect with
///         no symptom.</b> Nothing reported two evaluators: both panes drew correctly, and the cost
///         was memory plus the second panel's first-open latency. A
///         <c>TexturePlanEvaluator</c>'s variant cache is an <em>instance</em> field, so two of them
///         over one device are two Raven parses, two shader modules, two compute pipelines and two
///         descriptor-set-layout cache entries per kernel and format the panes share — held for the
///         session.
///     </para>
///     <para>
///         ⚠ <b>Measured rather than asserted structurally, because "one evaluator" is bookkeeping
///         and the compilations are the thing.</b> A module that held one evaluator and rebuilt its
///         variants would satisfy a <c>ReferenceEquals</c> and cost exactly what the issue describes.
///         So the claim below is a differential taken on this machine at this moment: opening both
///         panes compiles strictly fewer variants than opening each of them alone adds up to.
///     </para>
///     <para>
///         ⚠ <b>A real adapter or a loud skip.</b> Without one there is no device, no evaluator and
///         no compilation at all — every number below would be zero and every comparison between
///         zeros would be true, which is precisely the instrument that reports success on the day it
///         does not run. The counts are asserted non-zero first.
///     </para>
/// </remarks>
public class SharedEvaluatorDeviceTests {
    [Fact]
    public void Both_panes_share_one_evaluator_and_the_kernels_they_share_are_compiled_once() {
        using var device = TexturingDevice.Open();

        var graphAlone = Compilations(device, graph: true, stack: false, out var builtForGraph);
        var stackAlone = Compilations(device, graph: false, stack: true, out var builtForStack);
        var together = Compilations(device, graph: true, stack: true, out var builtForBoth);

        // ⚠ The instrument, before the finding. All three of these are zero on a run where nothing
        // was evaluated, and zero is smaller than zero plus zero is false — so the comparison below
        // would fail rather than pass on a run with no device. Said anyway, because what it would
        // fail on is unreadable and this is not.
        Assert.True(
            graphAlone > 0 && stackAlone > 0,
            $"{TexturingDevice.Adapter(device)}: the panes compiled {graphAlone} and {stackAlone} kernel "
            + "variants alone. Nothing was dispatched, so there is nothing here to compare."
        );

        // ⚠ Before the differential and not after it, because the differential is only readable
        // while this holds. `KernelCompilations` asks the evaluator the module is *currently*
        // holding, so under two evaluators it reports one of them and undercounts — a number that
        // could make the comparison below pass for the wrong reason. Assert the count first and the
        // reading is well defined; this is the ordering, not decoration.
        Assert.Equal(1, builtForGraph);
        Assert.Equal(1, builtForStack);

        // The whole of the fix, said as the number it is worth: one evaluator, however many panes.
        Assert.Equal(1, builtForBoth);

        Assert.True(
            together < graphAlone + stackAlone,
            $"{TexturingDevice.Adapter(device)}: with both panes open the module compiled {together} kernel "
            + $"variants, and the panes need {graphAlone} and {stackAlone} on their own. Sharing bought "
            + "nothing, which is what two evaluators over one device look like."
        );

        // And it did not buy it by drawing less: a shared cache can only ever be the union.
        Assert.True(together >= Math.Max(graphAlone, stackAlone));
    }

    /// <summary>Opens the named panes in a fresh module and says what its evaluator compiled.</summary>
    /// <param name="device">The device the fixture publishes.</param>
    /// <param name="graph">Whether to open a texture graph.</param>
    /// <param name="stack">Whether to open a layer stack.</param>
    /// <param name="built">How many evaluators the module built.</param>
    /// <returns>The kernel variants compiled.</returns>
    /// <remarks>
    ///     Through the verbs and the workspace rather than by constructing a preview, because what is
    ///     being measured is what a session costs — and a preview built directly is one the module
    ///     never lent anything to.
    /// </remarks>
    static int Compilations(Vixen.Graphics.Vulkan.VulkanDevice device, bool graph, bool stack, out int built) {
        using var fixture = new TexturingFixture(device);

        TexturingModule module = new();

        fixture.Host.Activate(TexturingModule.ModuleId, TexturingModule.ModuleName, module);

        if (graph) {
            fixture.Project.Selection.Set(fixture.AddGraph("Bricks"));

            Assert.True(fixture.Shell.Commands.Execute(TexturingModule.OpenCommand));
            Assert.NotNull(fixture.Shell.Workspace.Open(TexturingModule.GraphPanel));
        }

        if (stack) {
            fixture.Project.Selection.Set(LayerStackPanelTests.AddStack(fixture, "Hull"));

            Assert.True(fixture.Shell.Commands.Execute(TexturingModule.OpenStackCommand));
            Assert.NotNull(fixture.Shell.Workspace.Open(TexturingModule.StackPanel));
        }

        built = module.EvaluatorsBuilt;

        var compiled = module.KernelCompilations;

        // ⚠ Read before the unload, because `Release` disposes the evaluator and the counter goes
        // with it. Reading after would report zero for every arrangement, equally.
        Assert.True(fixture.Host.Unload(TexturingModule.ModuleId));

        return compiled;
    }
}
