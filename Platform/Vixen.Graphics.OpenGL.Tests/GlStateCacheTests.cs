// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Graphics.OpenGL.Tests;

/// <summary>The shadow state, which is half of what makes a GL backend viable.</summary>
public sealed class GlStateCacheTests {
    /// <summary>Setting the same thing twice costs one call.</summary>
    [Fact]
    public void ElidesARedundantSet() {
        var gl = new RecordingGlApi();
        var cache = new GlStateCache(gl);

        cache.UseProgram(7);
        cache.UseProgram(7);
        cache.UseProgram(7);

        Assert.Equal(1, gl.Count("UseProgram"));
    }

    /// <summary>The first set after construction always writes.</summary>
    /// <remarks>
    ///     Every field starts at a value GL cannot be in, deliberately. Guessing GL's defaults right
    ///     works until a driver disagrees, and then the call that would have fixed the picture is the
    ///     one that was elided.
    /// </remarks>
    [Fact]
    public void NeverAssumesGlIsAlreadyAtTheDefault() {
        var gl = new RecordingGlApi();
        var cache = new GlStateCache(gl);

        cache.UseProgram(0);
        cache.BindVertexArray(0);
        cache.BindDrawFramebuffer(0);

        Assert.Equal(1, gl.Count("UseProgram"));
        Assert.Equal(1, gl.Count("BindVertexArray"));
        Assert.Equal(1, gl.Count("BindFramebuffer"));
    }

    /// <summary>Invalidating makes the next set write again.</summary>
    [Fact]
    public void InvalidateForgetsEverything() {
        var gl = new RecordingGlApi();
        var cache = new GlStateCache(gl);

        cache.UseProgram(7);
        cache.Invalidate();
        cache.UseProgram(7);

        Assert.Equal(2, gl.Count("UseProgram"));
    }

    /// <summary>The viewport is converted from the RHI's top-left origin to GL's bottom-left one.</summary>
    /// <remarks>
    ///     Clip control changes which way clip space maps into the viewport rectangle, not where the
    ///     rectangle is measured from — so this conversion is needed on every profile, including the
    ///     one that has <c>glClipControl</c>.
    /// </remarks>
    [Fact]
    public void FlipsTheViewportOrigin() {
        var gl = new RecordingGlApi();
        var cache = new GlStateCache(gl) { TargetHeight = 256 };

        // The RHI's top-left quadrant of a 256-tall target.
        cache.SetViewport(0, 0, 128, 128, 0f, 1f);

        var call = gl.Single("Viewport");
        Assert.Equal([0, 128, 128, 128], call.Arguments);
    }

    /// <summary>So is the scissor rectangle, by the same conversion.</summary>
    /// <remarks>Two separate conversions and one shared misunderstanding; doing one of them is the
    /// version of this bug that ships.</remarks>
    [Fact]
    public void FlipsTheScissorOrigin() {
        var gl = new RecordingGlApi();
        var cache = new GlStateCache(gl) { TargetHeight = 256 };

        cache.SetScissor(16, 32, 64, 64);

        var call = gl.Single("Scissor");
        Assert.Equal([16, 160, 64, 64], call.Arguments);
    }

    /// <summary>An indexed buffer bind drops the shadow of the general target.</summary>
    /// <remarks>
    ///     <c>glBindBufferRange</c> also sets the general binding on every driver, so a cache that
    ///     kept its shadow would elide the next <c>glBindBuffer</c> to the same target and leave the
    ///     wrong buffer bound for a copy.
    /// </remarks>
    [Fact]
    public void DropsTheGeneralShadowWhenBindingARange() {
        var gl = new RecordingGlApi();
        var cache = new GlStateCache(gl);

        cache.BindBuffer(GlConstants.UniformBuffer, 4);
        cache.BindBufferRange(GlConstants.UniformBuffer, 0, 9, 0, 64);
        gl.Clear();
        cache.BindBuffer(GlConstants.UniformBuffer, 4);

        Assert.Equal(1, gl.Count("BindBuffer"));
    }

    /// <summary>The index buffer is not cached against the context.</summary>
    /// <remarks>
    ///     It is vertex-array state: the same target holds a different buffer depending on which VAO
    ///     is bound, so caching it against the context elides exactly the binds a pipeline change
    ///     makes necessary.
    /// </remarks>
    [Fact]
    public void TracksTheIndexBufferSeparately() {
        var gl = new RecordingGlApi();
        var cache = new GlStateCache(gl);

        cache.BindBuffer(GlConstants.ElementArrayBuffer, 3);
        cache.BindBuffer(GlConstants.ArrayBuffer, 3);

        Assert.Equal(2, gl.Count("BindBuffer"));
    }

    /// <summary>A pipeline's state block is applied once and not again.</summary>
    [Fact]
    public void AppliesAPipelineStateBlockOnce() {
        var gl = new RecordingGlApi();
        var cache = new GlStateCache(gl);
        using var device = new GlDevice(new(gl));
        var pipeline = Pipelines.Graphics(device, gl, BlendState.AlphaBlend, DepthStencilState.Default);

        gl.Clear();
        cache.ApplyPipeline(pipeline);
        var first = gl.Calls.Count;

        cache.ApplyPipeline(pipeline);
        Assert.Equal(first, gl.Calls.Count);
        Assert.True(first > 0, "the first apply should have written the whole block");
    }

    /// <summary>Two pipelines differing only in blend state re-send only the blend state.</summary>
    /// <remarks>
    ///     The case a material system produces by the hundred, and the reason the state cache earns
    ///     its place: the program, the vertex array, the cull mode and the depth comparison are all
    ///     already right.
    /// </remarks>
    [Fact]
    public void SendsOnlyWhatDiffersBetweenTwoPipelines() {
        var gl = new RecordingGlApi();
        var cache = new GlStateCache(gl);
        using var device = new GlDevice(new(gl));

        var opaque = Pipelines.Graphics(device, gl, BlendState.Opaque, DepthStencilState.Default);
        var blended = Pipelines.Graphics(device, gl, BlendState.AlphaBlend, DepthStencilState.Default);

        cache.ApplyPipeline(opaque);
        gl.Clear();
        cache.ApplyPipeline(blended);

        Assert.Equal(0, gl.Count("DepthFunc"));
        Assert.Equal(0, gl.Count("CullFace"));
        Assert.Equal(1, gl.Count("BlendFuncSeparate"));
    }

    /// <summary>Changing the stencil reference re-sends the pipeline's comparison with it.</summary>
    /// <remarks>
    ///     GL has no "set reference" call: the reference, the comparison and the read mask go in
    ///     together through <c>glStencilFuncSeparate</c>, so a dynamic reference change has to carry
    ///     the comparison the pipeline chose. One of the places GL's state model and the RHI's do not
    ///     divide the same way.
    /// </remarks>
    [Fact]
    public void CarriesTheComparisonWhenTheStencilReferenceChanges() {
        var gl = new RecordingGlApi();
        var cache = new GlStateCache(gl);
        using var device = new GlDevice(new(gl));

        var stencilled = Pipelines.Graphics(
            device,
            gl,
            BlendState.Opaque,
            DepthStencilState.Default with {
                StencilTest = true,
                Front = new(CompareFunction.Equal),
                Back = new(CompareFunction.Equal)
            }
        );

        cache.ApplyPipeline(stencilled);
        gl.Clear();
        cache.SetStencilReference(3);

        var calls = gl.Named("StencilFuncSeparate");
        Assert.Equal(2, calls.Count);
        Assert.Equal(GlConstants.Equal, calls[0].Arguments[1]);
        Assert.Equal(3, calls[0].Arguments[2]);
    }

    /// <summary>Preparing to clear opens the write masks and forgets the pipeline's.</summary>
    /// <remarks>
    ///     A GL clear goes through the same fixed-function path a draw does, so a pass that cleared
    ///     with the previous pipeline's depth mask off would clear no depth at all — with no error,
    ///     and looking exactly like a depth comparison set the wrong way round.
    /// </remarks>
    [Fact]
    public void OpensTheWriteMasksBeforeAClear() {
        var gl = new RecordingGlApi();
        var cache = new GlStateCache(gl);
        using var device = new GlDevice(new(gl));
        var pipeline = Pipelines.Graphics(device, gl, BlendState.Opaque, DepthStencilState.Disabled);

        cache.ApplyPipeline(pipeline);
        gl.Clear();
        cache.PrepareClear();

        Assert.Equal([true], gl.Single("DepthMask").Arguments);
        Assert.Equal([true, true, true, true], gl.Single("ColorMask").Arguments);

        // And the pipeline's state is re-sent afterwards rather than assumed intact.
        gl.Clear();
        cache.ApplyPipeline(pipeline);
        Assert.Equal(1, gl.Count("DepthMask"));
    }
}
