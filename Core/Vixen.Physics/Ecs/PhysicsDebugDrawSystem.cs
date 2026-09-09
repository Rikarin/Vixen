// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Threading;
using Vixen.Ecs;
using Vixen.Ecs.Systems;
using Vixen.Engine.Diagnostics;
using Vixen.Engine.Diagnostics.Overlays;
using Vixen.Physics.Bodies;
using Vixen.Physics.Diagnostics;

namespace Vixen.Physics.Ecs;

/// <summary>Draws the scene's colliders and contacts into a <see cref="DebugDraw" />.</summary>
/// <remarks>
///     <para>
///         In <see cref="SystemPhase.PreRender" /> and after the interpolation pass, so a collider is
///         drawn where the body is rather than where the smoothed transform says — which is the whole
///         point of a physics overlay. The two disagree by up to one step, and when they do, the one
///         being investigated is the body.
///     </para>
///     <para>
///         Off by default. The overlay is a few thousand lines for a modest scene and there is no
///         cost at all to a system whose <see cref="Enabled" /> is <see langword="false" />.
///     </para>
///     <para>
///         ⚠ <b>An <see cref="IDiagnosticOverlay" /> as well as a system, and that is what makes it
///         reachable.</b> The geometry is world-space and so cannot come out of
///         <see cref="IDiagnosticOverlay.Draw" /> — which is why this is a system in the first place —
///         but a system has no name, and every other overlay is turned on by one: <c>overlay physics
///         on</c> in the console, <c>--vixen-overlay physics</c> on the command line,
///         <c>AppConfig.EnabledOverlays</c> in a head. Answering to both means the one
///         <see cref="Enabled" /> flag is what the console writes and what <see cref="Update" />
///         reads, rather than a second flag to keep in step.
///     </para>
///     <para>
///         ⚠ <b>Wire it with <see cref="PhysicsSystems.AddPhysicsOverlay" />.</b> For a long time
///         everything below this line was written, tested and added by nothing outside one test — the
///         overlay doc 13 § Diagnostic overlays asks for existed and no build could switch it on.
///     </para>
/// </remarks>
[UpdateInGroup(SystemPhase.PreRender)]
[UpdateAfter(typeof(PhysicsInterpolationSystem))]
public sealed class PhysicsDebugDrawSystem(PhysicsScene scene, DebugDraw draw)
    : SystemBase, IDeclaredAccess, IDiagnosticOverlay {
    /// <summary>The name the console, the flag and the config all use.</summary>
    public const string OverlayName = "physics";

    const int Rows = 3;

    static readonly QueryDescription Bodies = new QueryDescription().WithAll<PhysicsBody>();

    readonly PhysicsDebugDraw renderer = new();
    readonly List<BodyHandle> handles = [];

    /// <summary>Whether the overlay is drawn at all.</summary>
    public bool Enabled { get; set; }

    /// <summary>What is drawn.</summary>
    public PhysicsDebugOverlay Overlay {
        get => renderer.Overlay;
        set => renderer.Overlay = value;
    }

    /// <inheritdoc />
    public string Name => OverlayName;

    /// <inheritdoc />
    public OverlayAnchor Anchor { get; set; } = OverlayAnchor.TopRight;

    /// <summary>How wide the counts panel is, in pixels.</summary>
    public float Width { get; set; } = 190f;

    /// <inheritdoc />
    public SystemAccess Access { get; } = SystemAccess.Declare().Read<PhysicsBody>().Build();

    /// <summary>Draws the counts panel that says what the wireframes are of.</summary>
    /// <param name="surface">Where to draw.</param>
    /// <param name="time">The frame's clock.</param>
    /// <exception cref="ArgumentNullException"><paramref name="surface" /> is null.</exception>
    /// <remarks>
    ///     ⚠ <b>Three numbers, and the point of them is the awake count.</b> A scene whose bodies are
    ///     all asleep looks identical in a wireframe to a scene that is not being stepped at all —
    ///     the same lines in the same places, frame after frame — and those are two different bugs
    ///     with two different fixes. The step count separates them: a simulation that is running says
    ///     so even when nothing in it moves.
    /// </remarks>
    public void Draw(OverlaySurface surface, in GameTime time) {
        ArgumentNullException.ThrowIfNull(surface);

        var region = surface.Panel(Anchor, Width, Rows, "PHYSICS");

        if (region.IsEmpty) {
            return;
        }

        var theme = surface.Theme;
        var world = scene.World;

        Span<char> buffer = stackalloc char[48];

        region.Text(0, "bodies awake/all", theme.Text);

        if (buffer.TryWrite($"{world.ActiveBodyCount}/{world.BodyCount}", out var length)) {
            region.TextRight(0, buffer[..length], theme.Text);
        }

        region.Text(1, "constraints", theme.Text);

        if (buffer.TryWrite($"{world.ConstraintCount}", out length)) {
            region.TextRight(1, buffer[..length], theme.Text);
        }

        region.Text(2, "contacts/steps", theme.Text);

        if (buffer.TryWrite($"{scene.Contacts.Length}/{world.StepCount}", out length)) {
            region.TextRight(2, buffer[..length], theme.Text);
        }
    }

    /// <inheritdoc />
    public override JobHandle Update(in SystemContext context, JobHandle dependency) {
        if (!Enabled || !draw.Enabled) {
            return dependency;
        }

        handles.Clear();

        foreach (var chunk in context.World.Chunks(Bodies)) {
            var bodies = chunk.ReadValues<PhysicsBody>();

            for (var index = 0; index < chunk.Count; index++) {
                handles.Add(bodies[index].Handle);
            }
        }

        renderer.Draw(scene.World, draw, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(handles));
        return dependency;
    }
}
