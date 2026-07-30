// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Graphics.Null;
using Vixen.Rendering;
using Vixen.Rendering.Compositor;
using Vixen.Rendering.IrradianceFields;
using Vixen.Shaders;
using Xunit;

namespace Tests;

/// <summary>
///     The half of the refinement policy that knows what a renderer is.
/// </summary>
/// <remarks>
///     <para>
///         <see cref="IrradianceRefinementPolicy" /> takes boxes and is tested against closed forms
///         with no scene in sight. What it cannot say anything about is where those boxes come from,
///         and that is the part with a way to be silently wrong: a node reading the visible objects
///         rather than all of them, or reading none at all, produces a field that looks plausible and
///         is coarse exactly where the light comes from.
///     </para>
///     <para>
///         A <see cref="NullDevice" />, because nothing here draws. The node's build declares a pass
///         and refines before it; only the second half is under test.
///     </para>
/// </remarks>
public sealed class IrradianceRefinementTests : IDisposable {
    readonly NullDevice device = new();

    public void Dispose() => device.Dispose();

    /// <summary>A field coarse enough that refining it is visible.</summary>
    static IrradianceField Field() {
        var field = new IrradianceField(new BoundingBox(new(-4f), new(4f)), new(8), new(new(8)));

        field.AllocateAll(8);

        return field;
    }

    /// <summary>A scene with one object of a given radius at a given place.</summary>
    static RenderSystem Scene(Vector3 at, float radius) {
        var system = new RenderSystem();
        var stage = system.AddStage(new("Opaque"));

        system.Objects.Add(new() { Bounds = new(at, radius), Stages = stage.Mask });

        return system;
    }

    [Fact]
    public void WithoutAPolicyTheFieldKeepsItsAllocation() {
        var field = Field();
        using var system = Scene(new(-3.2f), 0.2f);
        using var node = new IrradianceFieldRenderer { Name = "Field", Field = field, Device = device };

        node.Build(new(system) { FrameSize = new(16, 16) }, Frame());

        Assert.Equal(0, node.Refined);
        Assert.Equal(1, field.BrickCount);
    }

    /// <summary>
    ///     A node with a policy refines around the objects the scene holds.
    /// </summary>
    [Fact]
    public void TheSceneIsWhereTheBoundsComeFrom() {
        var field = Field();
        using var system = Scene(new(-3.2f), 0.2f);

        using var node = new IrradianceFieldRenderer {
            Name = "Field",
            Field = field,
            Device = device,
            Refinement = new() { Bands = { new(0f, 1) } }
        };

        node.Build(new(system) { FrameSize = new(16, 16) }, Frame());

        Assert.True(node.Refined > 0, "the node refined nothing, so it read no bounds");

        // And it refined *there*. A node that handed the policy the whole world would leave no coarse
        // bricks, which is the failure that looks like success.
        var near = new BoundingBox(new(-4f), new(-2.9f));
        var coarse = 0;

        foreach (var brick in field.Bricks) {
            if (brick.Size == 1) {
                Assert.True(field.BrickBounds(brick).Intersects(near), $"a brick at {brick.Cell} is fine and far away");
            } else {
                coarse++;
            }
        }

        Assert.True(coarse > 0, "every brick was refined, so the bounds cannot have been one object's");
    }

    /// <summary>
    ///     <b>And an object the frustum rejected still refines the field.</b>
    /// </summary>
    /// <remarks>
    ///     The one that would be easy to get wrong and hard to notice. Indirect light comes from
    ///     geometry the camera cannot see — that is the whole reason a field exists rather than a
    ///     screen-space gather — so a node refining only what survived culling would coarsen the wall
    ///     that is bouncing light onto what the camera <i>can</i> see, one frame after it left the
    ///     view. The scene here has no views at all, which is the strongest form of "nothing is
    ///     visible".
    /// </remarks>
    [Fact]
    public void GeometryNoViewCanSeeStillRefinesTheField() {
        var field = Field();
        using var system = Scene(new(-3.2f), 0.2f);

        system.SetViews([]);

        using var node = new IrradianceFieldRenderer {
            Name = "Field",
            Field = field,
            Device = device,
            Refinement = new() { Bands = { new(0f, 1) } }
        };

        node.Build(new(system) { FrameSize = new(16, 16) }, Frame());

        Assert.True(node.Refined > 0, "a scene nothing can see refined nothing");
    }

    /// <summary>And a second build over a scene that did not move makes nothing.</summary>
    /// <remarks>
    ///     What makes this safe to run per frame. The count is cumulative, so it standing still across
    ///     two builds is the assertion — a policy that kept splitting would show up as a number that
    ///     climbs forever and a pool that fills.
    /// </remarks>
    [Fact]
    public void AStillSceneSettles() {
        var field = Field();
        using var system = Scene(new(-3.2f), 0.2f);

        using var node = new IrradianceFieldRenderer {
            Name = "Field",
            Field = field,
            Device = device,
            Refinement = new() { Bands = { new(0f, 1) } }
        };

        var compositor = new GraphicsCompositor(system) { FrameSize = new(16, 16) };

        node.Build(compositor, Frame());

        var first = node.Refined;

        node.Build(compositor, Frame());

        Assert.True(first > 0);
        Assert.Equal(first, node.Refined);
    }

    /// <summary>A frame to declare into. Nothing executes it.</summary>
    CompositorFrame Frame() =>
        new() {
            Graph = new(device, new(device)),
            Effects = new(),
            Device = device,
            Size = new(16, 16)
        };
}
