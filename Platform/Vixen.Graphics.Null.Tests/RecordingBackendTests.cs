// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Testing;
using Xunit;
using Xunit.Sdk;

namespace Vixen.Graphics.Null.Tests;

/// <summary>
///     The assertion vocabulary, asserted on.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Every one of these has a red half.</b> An assertion helper is an instrument, and an
///         instrument is worth exactly what it prints on the day the thing it measures is broken —
///         so each assertion here is shown passing on a stream that satisfies it <em>and</em>
///         throwing on one that does not. A helper with only the green half is how a suite of
///         seventy files comes to assert nothing.
///     </para>
///     <para>
///         The interesting cases are the vacuous ones: an empty log, and an ordering question asked
///         about a call that never happened. Both are green under the hand-rolled LINQ these
///         helpers replace.
///     </para>
/// </remarks>
public sealed class RecordingBackendTests : IDisposable {
    readonly NullDevice device = new(new() { Record = true });

    [Fact]
    public void ADrawIsFoundByItsArguments() {
        Frame(list => list.DrawIndexed(36, instanceCount: 4));

        var draw = device.Log().ShouldContainDrawIndexed(36, instances: 4).Command;

        Assert.Equal(36, draw.A);
    }

    /// <summary>What it prints on the day the draw is wrong: the count, and the stream under it.</summary>
    [Fact]
    public void ADrawWithTheWrongCountIsRefusedAndTheStreamIsInTheMessage() {
        Frame(list => list.DrawIndexed(35, instanceCount: 4));

        var failure = Assert.Throws<XunitException>(() => device.Log().ShouldContainDrawIndexed(36, instances: 4));

        Assert.Contains("expected a DrawIndexed indices=36 instances=4", failure.Message, StringComparison.Ordinal);
        Assert.Contains("indices=35", failure.Message, StringComparison.Ordinal);
        Assert.Contains("BeginRenderPass", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     ⚠ The one that matters. `Assert.Empty(log.OfKind(Dispatch))` passes on a frame that never
    ///     ran, and a suite of those is a green report on nothing at all.
    /// </summary>
    [Fact]
    public void ANegativeAssertionOverAnEmptyLogIsRefused() {
        var failure = Assert.Throws<XunitException>(() => device.Log().ShouldNotContain(RecordedCommandKind.Dispatch));

        Assert.Contains("vacuous", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ANegativeAssertionOverAFrameThatRanIsTheClaimItLooksLike() {
        Frame(list => list.Draw(3));

        device.Log().ShouldNotContain(RecordedCommandKind.Dispatch);
    }

    [Fact]
    public void ANegativeAssertionIsRedWhenTheCallIsThere() {
        Frame(list => list.Draw(3));

        Assert.Throws<XunitException>(() => device.Log().ShouldNotContain(RecordedCommandKind.Draw));
    }

    /// <summary>Zero occurrences spelled as a count is the vacuity written the other way round.</summary>
    [Fact]
    public void AnExpectationOfZeroIsRefusedByName() {
        Frame(list => list.Draw(3));

        var failure = Assert.Throws<XunitException>(() => device.Log().ShouldContain(RecordedCommandKind.Draw, 0));

        Assert.Contains("ShouldNotContain", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     ⚠ A device built without `Record = true` is the Null-device trap one layer up. Today that
    ///     is a <see cref="NullReferenceException" /> out of `device.Recorder!`, which reads like a
    ///     bug in the code under test rather than in the fixture.
    /// </summary>
    [Fact]
    public void ADeviceThatIsNotRecordingSaysSo() {
        using var quiet = new NullDevice();

        var failure = Assert.Throws<XunitException>(() => quiet.Log());

        Assert.Contains("Record = true", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     ⚠ The bug the cursor cannot express. A frame that binds Opaque, binds Shadow and then
    ///     draws satisfies "the index of a BindPipeline of Opaque is below the index of the draw",
    ///     which is what the tree's hand-rolled ordering assertions ask — and the draw used Shadow.
    /// </summary>
    [Fact]
    public void APipelineReplacedBeforeTheDrawIsNotThePipelineInForce() {
        var opaque = Pipeline("Opaque");
        var shadow = Pipeline("Shadow");

        Frame(
            list => {
                list.BindPipeline(opaque);
                list.BindPipeline(shadow);
                list.Draw(3);
            }
        );

        device.Log().ShouldContainDraw(3).AfterBinding(shadow);

        var failure = Assert.Throws<XunitException>(() => device.Log().ShouldContainDraw(3).AfterBinding(opaque));

        Assert.Contains("the pipeline in force was", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ADrawUnderNoPipelineAtAllIsRefused() {
        Frame(list => list.Draw(3));

        var failure = Assert.Throws<XunitException>(() => device.Log().ShouldContainDraw(3).AfterBinding(Pipeline()));

        Assert.Contains("no pipeline was bound at all", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     ⚠ `FindIndex` returns −1 for a call that never happened, and −1 is below every index — so
    ///     "the bind came before the draw" is green when nothing bound anything. A cursor only
    ///     exists because a match was found, so the same mistake will not compile.
    /// </summary>
    [Fact]
    public void AnOrderingQuestionAboutACallThatNeverHappenedIsRed() {
        Frame(list => list.Draw(3));

        var failure = Assert.Throws<XunitException>(
            () => device.Log().ShouldContainDraw(3).After(RecordedCommandKind.BindDescriptorSet)
        );

        Assert.Contains("no BindDescriptorSet at all", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OrderIsASubsequenceSoAMarkerBetweenTheTwoDoesNotBreakIt() {
        Frame(
            list => {
                list.BindPipeline(Pipeline());
                list.InsertDebugMarker("Between");
                list.Draw(3);
            }
        );

        device.Log().ShouldRecordInOrder(RecordedCommandKind.BindPipeline, RecordedCommandKind.Draw);
    }

    [Fact]
    public void OrderInReverseIsRed() {
        Frame(
            list => {
                list.BindPipeline(Pipeline());
                list.Draw(3);
            }
        );

        var failure = Assert.Throws<XunitException>(
            () => device.Log().ShouldRecordInOrder(RecordedCommandKind.Draw, RecordedCommandKind.BindPipeline)
        );

        Assert.Contains("Draw → BindPipeline", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnOrderOverNoCallsIsRefusedRatherThanSatisfied() {
        Frame(list => list.Draw(3));

        Assert.Throws<XunitException>(() => device.Log().ShouldRecordInOrder());
    }

    [Fact]
    public void ADrawIsInsideEveryGroupOpenAroundIt() {
        Frame(
            list => {
                list.PushDebugGroup("Opaque");
                list.PushDebugGroup("Instanced");
                list.Draw(3);
                list.PopDebugGroup();
                list.PopDebugGroup();
            }
        );

        device.Log().ShouldContainDraw(3).InsideDebugGroup("Opaque").InsideDebugGroup("Instanced");
    }

    [Fact]
    public void ADrawIsNotInsideAGroupThatClosedBeforeIt() {
        Frame(
            list => {
                list.PushDebugGroup("Shadows");
                list.PopDebugGroup();
                list.Draw(3);
            }
        );

        var failure = Assert.Throws<XunitException>(
            () => device.Log().ShouldContainDraw(3).InsideDebugGroup("Shadows")
        );

        Assert.Contains("no group was open", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ACountThatDisagreesCarriesTheStream() {
        Frame(
            list => {
                list.Draw(3);
                list.Draw(3);
            }
        );

        var failure = Assert.Throws<XunitException>(() => device.Log().ShouldContain(RecordedCommandKind.Draw, 3));

        Assert.Contains("expected 3 × Draw", failure.Message, StringComparison.Ordinal);
        Assert.Contains("Found: 2", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ARenderPassIsFoundByItsName() {
        Frame(list => list.Draw(3));

        device.Log().ShouldContainRenderPass("Opaque").Before(RecordedCommandKind.EndRenderPass);
    }

    public void Dispose() => device.Dispose();

    /// <summary>Records a frame, so every assertion above is about a stream a real list produced.</summary>
    /// <param name="body">What the frame does inside the pass.</param>
    void Frame(Action<ICommandList> body) {
        using var list = device.BeginCommandList(name: "Frame");
        list.BeginRenderPass(new RenderPassDescription(Attachments(), null, "Opaque"));
        body(list);
        list.EndRenderPass();
        list.Finish();
        device.GraphicsQueue.Submit([list]);
    }

    ColourAttachment[] Attachments() {
        var texture = device.CreateTexture(
            new(PixelFormat.Rgba8UNorm, 16, 16, TextureUsage.ColourTarget, Name: "Target")
        );

        return [new ColourAttachment(device.CreateTextureView(texture))];
    }

    PipelineHandle Pipeline(string name = "Triangle") {
        var vertex = device.CreateShader(ShaderStage.Vertex, [1, 2, 3, 4], $"{name}.vs");
        var fragment = device.CreateShader(ShaderStage.Fragment, [5, 6, 7, 8], $"{name}.fs");
        var layout = device.CreatePipelineLayout(new([], Name: "Empty"));

        return device.CreateGraphicsPipeline(
            new(
                vertex,
                fragment,
                layout,
                [new ColourTargetState(PixelFormat.Rgba8UNorm)],
                DepthStencil: DepthStencilState.Disabled,
                Name: name
            )
        );
    }
}
