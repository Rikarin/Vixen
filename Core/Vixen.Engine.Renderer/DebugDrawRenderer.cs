// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Engine.Diagnostics;
using Vixen.Graphics;
using Vixen.Rendering;

namespace Vixen.Engine.Renderer;

/// <summary>
///     Draws a frame's <see cref="DebugDraw" />: the world geometry every subsystem has been
///     producing, and the diagnostic overlays on top of it.
/// </summary>
/// <remarks>
///     <para>
///         <b>The half of <c>DebugDraw</c> that was owed since Phase 2.</b> The accumulator has been
///         collecting lines from physics, navigation and anything else that wanted to show its
///         working, with nothing to draw them; this is what drains it. A subsystem written against
///         the accumulator needs no change to become visible.
///     </para>
///     <para>
///         <b>Two <see cref="LineRenderer" />s, and they are not interchangeable.</b> One draws the
///         world with the view-projection and, by default, the depth test — so a collider behind a
///         wall is behind the wall. The other draws the screen with a pixel-to-clip matrix and never
///         has a depth test, because an overlay that could be occluded by the scene is an overlay
///         that disappears exactly when something has gone wrong in front of it.
///     </para>
///     <para>
///         ⚠ <b><see cref="Upload" /> writes buffers and must be called outside a render pass.</b>
///         Vulkan forbids a transfer inside one, which is why <c>ScenePresenter</c> and
///         <c>UiRenderer</c> both upload before their pass is declared rather than in it.
///     </para>
///     <para>
///         ⚠ <b>Nothing here ages the geometry.</b> <c>DebugDrawSystem</c> does, in
///         <c>PostRender</c> — after this has drained. Calling <c>Advance</c> before a frame is
///         recorded is how a line asked for during that frame is never seen.
///     </para>
/// </remarks>
public sealed class DebugDrawRenderer : IDisposable {
    readonly LineRenderer world;
    readonly LineRenderer screen;
    readonly DebugGeometry geometry = new();

    Matrix4x4 screenProjection = Matrix4x4.Identity;
    bool hasScreen;
    bool disposed;

    /// <summary>Builds the two pipelines a frame's debug geometry is drawn with.</summary>
    /// <param name="device">The device.</param>
    /// <param name="shaders">The two stages, shared by both pipelines.</param>
    /// <param name="output">What they draw into.</param>
    /// <param name="worldCapacity">How many world-space vertices one frame may hold.</param>
    /// <param name="screenCapacity">How many screen-space vertices one frame may hold.</param>
    public DebugDrawRenderer(
        IGraphicsDevice device,
        LineShaders shaders,
        RenderOutput output,
        int worldCapacity = 1 << 16,
        int screenCapacity = 1 << 16
    ) {
        ArgumentNullException.ThrowIfNull(device);

        world = new(device, shaders, output, worldCapacity);
        screen = new(device, shaders, output, screenCapacity);
    }

    /// <summary>Whether world geometry is hidden by scene geometry in front of it.</summary>
    /// <remarks>
    ///     On by default, because a debug line that ignored depth would float over the level and be
    ///     impossible to place. Off is what an investigation into something buried wants, and it is
    ///     one field rather than a second buffer because the pipeline pair already exists inside
    ///     <see cref="LineRenderer" />.
    /// </remarks>
    public bool DepthTested { get; set; } = true;

    /// <summary>How many world vertices the last upload held.</summary>
    public int WorldCount => world.Count;

    /// <summary>How many screen vertices the last upload held.</summary>
    public int ScreenCount => screen.Count;

    /// <summary>How many vertices the last upload dropped for want of room, across both buffers.</summary>
    /// <remarks>
    ///     Non-zero means an overlay or a subsystem is producing more than the capacity allows and
    ///     the picture is missing its end. Counted rather than thrown for — see
    ///     <see cref="LineRenderer.Dropped" />, which explains why growing mid-frame is not an option.
    /// </remarks>
    public int Dropped => world.Dropped + screen.Dropped;

    /// <summary>How many labels the last upload turned into strokes.</summary>
    public int LabelCount => geometry.LabelCount;

    /// <summary>How many draws the last record issued: zero, one or two.</summary>
    public int Draws { get; private set; }

    /// <summary>Builds a frame's vertices and writes them into the next region of each ring.</summary>
    /// <param name="draw">The accumulator to drain.</param>
    /// <param name="view">The world-to-view matrix, for facing labels at the camera.</param>
    /// <param name="viewport">The render target's size in pixels, for the screen projection.</param>
    public void Upload(DebugDraw draw, in Matrix4x4 view, Vector2 viewport) {
        ArgumentNullException.ThrowIfNull(draw);
        ObjectDisposedException.ThrowIf(disposed, this);

        geometry.Build(draw, DebugView.FromView(view));

        world.Upload(geometry.World);
        screen.Upload(geometry.Screen);

        hasScreen = viewport.X > 0f && viewport.Y > 0f;

        if (hasScreen) {
            screenProjection = ScreenProjection(viewport);
        }
    }

    /// <summary>Draws what the last upload wrote.</summary>
    /// <param name="commands">Where to record.</param>
    /// <param name="viewProjection">The world-to-clip matrix of the view being drawn.</param>
    /// <remarks>
    ///     World first and the screen after, because the screen pass is an overlay in the ordinary
    ///     sense of the word: with no depth test, the paint order is the record order.
    /// </remarks>
    public void Record(ICommandList commands, in Matrix4x4 viewProjection) {
        ArgumentNullException.ThrowIfNull(commands);
        ObjectDisposedException.ThrowIf(disposed, this);

        Draws = 0;

        world.Record(commands, viewProjection, DepthTested);
        Draws += world.Draws;

        if (hasScreen) {
            screen.Record(commands, screenProjection, depthTested: false);
            Draws += screen.Draws;
        }
    }

    /// <summary>Draws only the screen-space half — the overlays, with no camera involved.</summary>
    /// <param name="commands">Where to record.</param>
    /// <remarks>
    ///     For a pass that composites after the scene, or a frame with no camera at all: a build that
    ///     is failing to load its level still has frame stats and a log worth reading, and requiring a
    ///     view-projection to show them would be requiring the thing that is broken.
    /// </remarks>
    public void RecordScreen(ICommandList commands) {
        ArgumentNullException.ThrowIfNull(commands);
        ObjectDisposedException.ThrowIf(disposed, this);

        Draws = 0;

        if (hasScreen) {
            screen.Record(commands, screenProjection, depthTested: false);
            Draws = screen.Draws;
        }
    }

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;

        world.Dispose();
        screen.Dispose();
    }

    /// <summary>Pixels with y running down, onto clip space with y running up.</summary>
    /// <param name="viewport">The target's size in pixels.</param>
    /// <returns>The matrix the screen pass pushes.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>y is flipped, and that is the engine's convention rather than the API's.</b>
    ///         Vulkan's raw clip space has +y down, but nothing here ever sees it: the command list
    ///         submits a negative-height viewport so that +y up holds everywhere
    ///         (<c>Core/Vixen.Core.Mathematics/Conventions.md</c>). This is the same mapping
    ///         <c>UiRenderer</c> pushes, for the same reason.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>z is a half and not a zero.</b> The depth <i>test</i> is off, but depth
    ///         <i>clipping</i> is not — a vertex outside [0, 1] is clipped whatever the test says, and
    ///         under the engine's reversed convention zero is the far plane and exactly on it.
    ///     </para>
    /// </remarks>
    static Matrix4x4 ScreenProjection(Vector2 viewport) =>
        new(
            2f / viewport.X, 0f, 0f, 0f,
            0f, -2f / viewport.Y, 0f, 0f,
            0f, 0f, 0f, 0f,
            -1f, 1f, 0.5f, 1f
        );
}
