// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Texturing.Layers;
using Vixen.Editor.Texturing.Painting;
using Xunit;

namespace Vixen.Editor.Texturing.Tests;

/// <summary>Neither pane owns an evaluator: both ask the lease, every time they evaluate.</summary>
/// <remarks>
///     <para>
///         <b><a href="https://github.com/Rikarin/Vixen/issues/820">#820</a>'s sentence at the seam
///         the panes actually have, and it is the sentence no fixture in this assembly made</b>
///         (<a href="https://github.com/Rikarin/Vixen/issues/988">#988</a>).
///         <c>SharedEvaluatorDeviceTests</c> measures what a session costs with both panels open,
///         through the module; this is the property that makes that possible — a
///         <c>LayerStackPreview</c> and a <c>TextureGraphPreview</c> handed one lender take one
///         evaluator between them, because each of them asks on every evaluation rather than
///         constructing its own.
///     </para>
///     <para>
///         ⚠ <b>Two counters, because either one alone can only fail in the harmless direction.</b>
///         <c>LentEvaluator.Built</c> catches a lender that quietly builds one per call — which is
///         what it was written for, and until now nothing read it. It cannot catch the opposite: a
///         pane that stopped asking and built its own leaves the lender at one build and every
///         assertion about it green. So <c>Asks</c> is counted beside it and expected exactly, which
///         is the shape <a href="https://github.com/Rikarin/Vixen/issues/978">#978</a> settled one
///         type along — count the questions, not how they were answered.
///     </para>
///     <para>
///         ⚠ <b>Exact and not a floor, and the numbers are the shape of this script.</b> Two
///         evaluations of the stack and one of the graph is three asks; a change to either pane's
///         evaluation moves that, and a run that updates it has to leave <c>Built</c> at one. A
///         floor is what let #978's equivalent through.
///     </para>
///     <para>
///         ⚠ <b>A real adapter or a loud skip.</b> Both previews return before they reach the lease
///         when <c>IEditorGraphics.Device</c> is null, so on a host with no device every count here
///         is zero — and a suite that asserted a floor would then pass on nothing at all. The asks
///         are expected exactly, so the no-device run is red rather than quiet, and
///         <c>TexturingDevice.Open</c> skips loudly before it gets that far.
///     </para>
/// </remarks>
public class PreviewLeaseTests {
    const int Side = 32;

    /// <summary>One lender, two panes, three evaluations, one evaluator and three questions.</summary>
    [Fact]
    public void Both_panes_take_their_evaluator_from_the_lease_on_every_evaluation() {
        using var device = TexturingDevice.Open();
        using var fixture = new TexturingFixture(device);

        PaintCanvasStore canvases = new();

        using LentEvaluator evaluators = new();
        using LayerStackPreview stacks = new(fixture.Graphics!, evaluators.Lease, canvases);
        using TextureGraphPreview graphs = new(fixture.Graphics!, evaluators.Lease, canvases);

        // The instrument, before the claim: nothing has been evaluated, so a lender that built one
        // in its constructor — or a pane that took one on the way in — is already visible here.
        Assert.Equal(0, evaluators.Built);
        Assert.Equal(0, evaluators.Asks);

        LayerStackDocument stack = new(
            fixture.Project,
            LayerStackPanelTests.AddStack(fixture, "Hull"),
            fixture.Paths.Absolute("Assets/Hull" + LayerStackDocument.Extension)
        ) {
            Document = LayerStackDocument.Starter("Hull") with { BaseWidth = Side, BaseHeight = Side }
        };

        var painted = stacks.Evaluate(stack);

        Assert.True(
            painted.Image is not null,
            $"{TexturingDevice.Adapter(device)}: the layers pane drew nothing, so it never reached the lease "
            + $"and every count below is about a pane that did not run. It said: {painted.Status}"
        );

        Assert.Equal(1, evaluators.Asks);

        // ⚠ The same pane again, which is the half that separates "asked once, on the way in" from
        // "asks every time" — `LayerStackPreview` holds no evaluator field, and this is what says so.
        Assert.NotNull(stacks.Evaluate(stack).Image);
        Assert.Equal(2, evaluators.Asks);

        var asset = fixture.AddGraph("Bricks");
        TextureGraphDocument graph = new(
            fixture.Project,
            asset,
            fixture.Paths.Absolute("Assets/Bricks" + TextureGraphDocument.Extension)
        );

        var drawn = graphs.Evaluate(graph);

        Assert.True(
            drawn.Image is not null,
            $"{TexturingDevice.Adapter(device)}: the graph pane drew nothing, so the third ask never "
            + $"happened. It said: {drawn.Status}"
        );

        // The whole claim, as the two numbers it is: three questions and one evaluator. A pane that
        // built its own reads fewer asks; a lender that built one per call reads three builds.
        Assert.Equal(3, evaluators.Asks);
        Assert.Equal(1, evaluators.Built);
    }
}
