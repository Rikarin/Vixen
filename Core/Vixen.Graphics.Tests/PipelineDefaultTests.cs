// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Graphics.Tests;

/// <summary>
///     The documented defaults, asserted to actually be the documented defaults.
/// </summary>
/// <remarks>
///     <para>
///         This exists because they were not. On a record struct whose primary-constructor parameters
///         are all optional, <c>new()</c> binds the <em>implicit parameterless struct constructor</em>
///         — zero-initialising and never running the primary constructor — so
///         <c>public static BlendState Opaque =&gt; new();</c> produced a blend state with
///         <see cref="ColourWriteMask.None" />, and <c>RasterizerState.Default</c> culled nothing
///         while its own documentation said back faces.
///     </para>
///     <para>
///         Nothing catches this. It compiles, it reads correctly, the XML docs describe the intent,
///         and the only symptom is a Vulkan pipeline that draws an entirely untouched attachment with
///         no complaint from the API, the validation layers or the driver. It cost an afternoon to
///         find; these assertions are what stop it costing another one.
///     </para>
/// </remarks>
public sealed class PipelineDefaultTests {
    [Fact]
    public void RasterizerDefaultCullsBackFaces() {
        var state = RasterizerState.Default;

        Assert.Equal(CullMode.Back, state.Cull);
        Assert.Equal(FrontFace.CounterClockwise, state.FrontFace);
        Assert.Equal(FillMode.Solid, state.Fill);
        Assert.False(state.DepthClamp);
    }

    [Fact]
    public void RasterizerTwoSidedCullsNothing() =>
        Assert.Equal(CullMode.None, RasterizerState.TwoSided.Cull);

    /// <summary>Reversed-Z: <see cref="CompareFunction.Greater" />, not <c>Less</c>.</summary>
    [Fact]
    public void DepthStencilDefaultTestsAndWritesWithReversedDepth() {
        var state = DepthStencilState.Default;

        Assert.True(state.DepthTest);
        Assert.True(state.DepthWrite);
        Assert.Equal(CompareFunction.Greater, state.DepthCompare);
        Assert.False(state.StencilTest);
        Assert.Equal(0xFF, state.StencilReadMask);
        Assert.Equal(0xFF, state.StencilWriteMask);
    }

    [Fact]
    public void DepthStencilTestOnlyTestsWithoutWriting() {
        var state = DepthStencilState.TestOnly;

        Assert.True(state.DepthTest);
        Assert.False(state.DepthWrite);
        Assert.Equal(CompareFunction.Greater, state.DepthCompare);
    }

    [Fact]
    public void DepthStencilDisabledDoesNeither() {
        var state = DepthStencilState.Disabled;

        Assert.False(state.DepthTest);
        Assert.False(state.DepthWrite);
        Assert.Equal(CompareFunction.Always, state.DepthCompare);
    }

    /// <summary>The one that turned a whole backend into a blank screen.</summary>
    [Fact]
    public void OpaqueBlendWritesEveryChannel() {
        var state = BlendState.Opaque;

        Assert.False(state.Enabled);
        Assert.Equal(ColourWriteMask.All, state.WriteMask);
        Assert.Equal(BlendFactor.One, state.SourceColour);
        Assert.Equal(BlendFactor.Zero, state.DestinationColour);
        Assert.Equal(BlendOperation.Add, state.ColourOperation);
    }

    [Fact]
    public void TheBlendPresetsWriteEveryChannel() {
        Assert.Equal(ColourWriteMask.All, BlendState.AlphaBlend.WriteMask);
        Assert.Equal(ColourWriteMask.All, BlendState.PremultipliedAlpha.WriteMask);
        Assert.Equal(ColourWriteMask.All, BlendState.Additive.WriteMask);

        Assert.True(BlendState.AlphaBlend.Enabled);
        Assert.True(BlendState.PremultipliedAlpha.Enabled);
        Assert.True(BlendState.Additive.Enabled);
    }

    [Fact]
    public void TheSamplerPresetsKeepTheirDocumentedFilters() {
        Assert.Equal(FilterMode.Linear, SamplerDescription.LinearRepeat.MinFilter);
        Assert.Equal(AddressMode.Repeat, SamplerDescription.LinearRepeat.AddressU);

        Assert.Equal(FilterMode.Linear, SamplerDescription.LinearClamp.MinFilter);
        Assert.Equal(AddressMode.ClampToEdge, SamplerDescription.LinearClamp.AddressU);

        Assert.Equal(FilterMode.Nearest, SamplerDescription.PointClamp.MinFilter);
        Assert.Equal(AddressMode.ClampToEdge, SamplerDescription.PointClamp.AddressU);

        Assert.Equal(CompareFunction.GreaterEqual, SamplerDescription.Shadow.Compare);
        Assert.Equal(BorderColour.OpaqueWhite, SamplerDescription.Shadow.Border);
    }

    /// <summary>
    ///     A colour target with no blend state stated writes every channel. C# cannot give a struct
    ///     parameter any default but all-zeros, and an all-zero write mask writes nothing — so the RHI
    ///     resolves it rather than leaving each backend to.
    /// </summary>
    [Fact]
    public void AColourTargetWithNoBlendStatedIsOpaque() {
        var target = new ColourTargetState(PixelFormat.Rgba8UNorm);

        Assert.Equal(ColourWriteMask.None, target.Blend.WriteMask);
        Assert.Equal(ColourWriteMask.All, target.EffectiveBlend.WriteMask);
        Assert.False(target.EffectiveBlend.Enabled);
    }

    /// <summary>
    ///     A blend state the caller actually built is passed through untouched, including one that
    ///     deliberately writes nothing — which differs from <c>default</c> in its blend factors.
    /// </summary>
    [Fact]
    public void AStatedBlendIsNotSecondGuessed() {
        var alpha = new ColourTargetState(PixelFormat.Rgba8UNorm, BlendState.AlphaBlend);
        Assert.Equal(BlendState.AlphaBlend, alpha.EffectiveBlend);

        var writesNothing = new ColourTargetState(
            PixelFormat.Rgba8UNorm,
            new BlendState(WriteMask: ColourWriteMask.None)
        );

        Assert.Equal(ColourWriteMask.None, writesNothing.EffectiveBlend.WriteMask);
    }
}
