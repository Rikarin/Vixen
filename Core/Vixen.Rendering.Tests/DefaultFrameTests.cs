// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Graphics;
using Vixen.Graphics.Null;
using Vixen.Rendering.Compositor;
using Xunit;

namespace Vixen.Rendering.Tests;

/// <summary>The frame a project with no compositor of its own is drawn with.</summary>
/// <remarks>
///     ⚠ <b>What makes "a new project renders something" true.</b> A host with no
///     <c>.vxcompositor</c> to load has no frame at all, and the difference between that and a broken
///     renderer is invisible from outside — a black window either way. It moved out of
///     <c>Vixen.App</c> when the editor became the second head that needed it: a game falling back to
///     one frame and an editor to another would make the viewport disagree with the build for exactly
///     the projects most likely to be looking at the viewport to find out what their scene looks like.
/// </remarks>
public sealed class DefaultFrameTests : IDisposable {
    readonly NullDevice device = new();

    public void Dispose() => device.Dispose();

    /// <summary>It builds, which is the whole of what a fallback has to do.</summary>
    [Fact]
    public void TheDefaultFrameBuilds() {
        var system = new RenderSystem();
        var builder = new CompositorBuilder(system);

        builder.Views["Camera"] = new("Camera");

        var compositor = builder.Build(GraphicsCompositorAsset.Default);

        Assert.NotNull(compositor);
    }

    /// <summary>Its stage has a mask, which is what a host extracts against.</summary>
    /// <remarks>
    ///     ⚠ <b>The mask is the stage's index, assigned as the document is built.</b> A host that
    ///     registered its extraction with <see cref="RenderStageMask.None" /> — the value a name
    ///     lookup that missed leaves behind — draws nothing at all, and every counter upstream of the
    ///     draw reads healthy: objects extracted, meshes resolved, effects compiled. So the lookup
    ///     being by name and the name being one the document declares are worth asserting together.
    /// </remarks>
    [Fact]
    public void ItsStageHasAMaskAHostCanExtractAgainst() {
        var system = new RenderSystem();
        var builder = new CompositorBuilder(system);

        builder.Views["Camera"] = new("Camera");
        builder.Build(GraphicsCompositorAsset.Default);

        Assert.True(builder.Stages.TryGetValue("Opaque", out var stage), "the default frame declares no Opaque stage.");
        Assert.False(stage!.Mask.IsEmpty);
    }

    /// <summary>Two defaults are two documents, not one shared array.</summary>
    /// <remarks>
    ///     ⚠ <b>A static field would hand every caller the same <c>Stages</c> array</b>, and one that
    ///     sorted or replaced an element of it would change what every later default is — a mutation
    ///     at a distance, through a property nobody would think of as state.
    /// </remarks>
    [Fact]
    public void EachDefaultIsItsOwnDocument() {
        var first = GraphicsCompositorAsset.Default;
        var again = GraphicsCompositorAsset.Default;

        Assert.NotSame(first, again);
        Assert.NotSame(first.Stages, again.Stages);
    }

    /// <summary>It declares the colour and depth targets its one pass writes.</summary>
    [Fact]
    public void ItDeclaresWhatItsPassWritesInto() {
        var frame = GraphicsCompositorAsset.Default;

        Assert.Contains(frame.Resources, resource => resource.Name == "SceneColour");

        Assert.Contains(
            frame.Resources,
            resource => resource.Name == "SceneDepth" && resource.Usage == TextureUsage.DepthStencilTarget
        );
    }
}
