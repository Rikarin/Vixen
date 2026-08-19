// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Threading;
using Vixen.Ecs;
using Vixen.Ecs.Systems;
using Vixen.Engine.Diagnostics;
using Vixen.Engine.Transforms;

namespace Vixen.Water.Physics;

/// <summary>The frame that joins <c>water.showBuoyancy</c> to <see cref="BuoyancyDebugDraw" />.</summary>
/// <remarks>
///     <para>
///         <b>The half of the sixth verb that was owed.</b> The lines were written and tested and the
///         verb was registered, and nothing joined them: no host copied <c>WaterDebug.ShowBuoyancy</c>
///         into <see cref="BuoyancyDebugDraw.Enabled" /> and no host called
///         <see cref="BuoyancyDebugDraw.Draw" />. So the toggle set a bool that nothing consumed, in
///         the editor's Water menu as well as the console.
///     </para>
///     <para>
///         ⚠ <b><see cref="Show" /> is a delegate because the flag is in an assembly this one must
///         not reference.</b> <c>WaterDebug</c> lives in <c>Vixen.Rendering.Water</c>, and § D1 puts
///         the physics join in its own assembly precisely so that nothing linking Jolt is linked by a
///         renderer — reaching for the flag from here would drag a graphics device into the path a
///         dedicated server runs. So a host that has both writes the join once, at registration:
///         <c>Show = () =&gt; WaterDebug.ShowBuoyancy</c>. Left null, <see cref="Draw" />'s own
///         <c>Enabled</c> is honoured, which is what a headless build or a test drives it with.
///     </para>
///     <para>
///         ⚠ <b>Read once a frame rather than subscribed to.</b> The flag is a static a console verb
///         writes at any moment; a system that cached it would draw one frame behind every toggle,
///         and the frame somebody is looking at when they press the key is the one that matters.
///     </para>
///     <para>
///         ⚠ <b><see cref="SystemPhase.PreRender" />, and both neighbours decide it.</b>
///         <c>TransformSystem</c> writes the <c>WorldTransform</c> the pontoons are placed from in
///         <c>LateUpdate</c>, so anything earlier draws the spheres where the body was; and the
///         accumulator is drained during <c>Render</c> and aged after it, so anything later draws
///         into a frame that has already been recorded. Neither failure says anything — the picture
///         is a lag or an empty screen.
///     </para>
/// </remarks>
/// <param name="buoyancy">The solver, whose <c>Forces</c> hold the last body stepped.</param>
/// <param name="into">Where the geometry goes. A host's own <c>DebugDraw</c>.</param>
[UpdateInGroup(SystemPhase.PreRender)]
public sealed class BuoyancyDebugSystem(BuoyancySystem buoyancy, DebugDraw into) : SystemBase, IDeclaredAccess {
    readonly BuoyancySystem buoyancy = buoyancy ?? throw new ArgumentNullException(nameof(buoyancy));

    readonly DebugDraw into = into ?? throw new ArgumentNullException(nameof(into));

    /// <summary>Where the host's <c>water.showBuoyancy</c> is, or null to drive <see cref="Draw" />
    ///     directly.</summary>
    public Func<bool>? Show { get; set; }

    /// <summary>The lines themselves, so a host can change the arrow scale.</summary>
    public BuoyancyDebugDraw Draw { get; } = new();

    /// <summary>How many frames this has drawn into the accumulator.</summary>
    /// <remarks>
    ///     ⚠ <b>The counter that says the join is live</b>, on <c>WaterImmersionSystem.Swimming</c>'s
    ///     terms. A verb switched on over a scene with no floating body draws nothing and looks
    ///     exactly like a verb nothing consumes, which is the defect this class exists to close — and
    ///     the two are only distinguishable from outside by a number that moves.
    /// </remarks>
    public int Frames { get; private set; }

    /// <inheritdoc />
    /// <remarks>
    ///     Read-only, and every component it touches is one <c>BuoyancySystem</c> or
    ///     <c>TransformSystem</c> has already finished with this frame.
    /// </remarks>
    public SystemAccess Access { get; } = SystemAccess.Declare()
        .Read<BuoyancyState>()
        .Read<WorldTransform>()
        .Build();

    /// <inheritdoc />
    public override JobHandle Update(in SystemContext context, JobHandle dependency) {
        // The pontoon spheres are placed from WorldTransform and the states were written in
        // FixedUpdate; nothing scheduled may still be writing either.
        dependency.Complete();

        Step(context.World);

        return dependency;
    }

    /// <summary>Copies the flag across and draws one frame's worth.</summary>
    /// <param name="world">The world the bodies are in.</param>
    /// <exception cref="ArgumentNullException"><paramref name="world" /> is null.</exception>
    /// <remarks>Public so a test can drive it without standing up a runner.</remarks>
    public void Step(World world) {
        ArgumentNullException.ThrowIfNull(world);

        if (Show is { } flag) {
            Draw.Enabled = flag();
        }

        if (!Draw.Enabled) {
            return;
        }

        Draw.Draw(into, world, buoyancy);
        Frames++;
    }
}
