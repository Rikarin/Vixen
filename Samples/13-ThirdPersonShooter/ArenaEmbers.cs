// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Vfx;

namespace Vixen.Samples.ThirdPersonShooter;

/// <summary>The embers that rise off a sodium floodlight, as a compiled particle graph.</summary>
/// <remarks>
///     <para>
///         <b>Written in code because there is no other way to write one.</b> <c>.vxvfx</c> is a node
///         graph the editor authors and compiles <em>in the editor process</em> — <c>docs/overview.md</c>
///         lists it among the formats whose runtime compiler is still owed, so a game cannot load one
///         by address. <c>VfxCompiledGraph</c> is the artefact both backends read and it is plain data,
///         which is what makes writing it here a shortcut rather than a workaround.
///     </para>
///     <para>
///         <b>One graph per lamp, and the reason is that the opcodes are world-space.</b> There is no
///         emitter transform anywhere in <c>Vixen.Vfx</c>: a particle's position is whatever
///         <c>PositionInSphere</c>'s vector said, in world coordinates. So the lamp's position is baked
///         into the initializer and fifteen lamps are fifteen graphs. That is fifteen small arrays
///         rather than fifteen simulations of any weight — each holds sixty-four particles.
///     </para>
///     <para>
///         ⚠ <b>Seeded from the lamp's index, never from a clock.</b> This sample asserts that two runs
///         of <c>--vixen-frames 8</c> produce the same frames, and <c>VfxRandom</c> is a hash of the
///         particle's identifier, the system's seed and the operation's salt — so a seed that varied
///         per run would make the picture vary per run. It is the same argument
///         <c>LampFlicker.Offset</c> makes, one system along.
///     </para>
/// </remarks>
static class ArenaEmbers {
    /// <summary>How many particles one lamp's graph has room for.</summary>
    /// <remarks>
    ///     Sixty-four against a spawn rate of six a second and a life of at most nine — a steady state
    ///     of about forty, so the buffer never fills and nothing is dropped at the moment somebody
    ///     walks up to a lamp. The buffer is allocated once for the capacity whatever the count, so
    ///     this is fifteen lamps × 64 × the particle stride, which is a few tens of kilobytes for the
    ///     whole level.
    /// </remarks>
    const int Capacity = 64;

    /// <summary>How far from the lamp's centre a spark can be born.</summary>
    /// <remarks>
    ///     The globe's own radius, near enough — the <c>!Light</c> in the scene gives each lamp a
    ///     <c>radius: 0.14</c>, and a spark born outside the glass would read as floating rather than
    ///     as coming off the lamp.
    /// </remarks>
    const float Source = 0.16f;

    /// <summary>How far a lamp's embers can drift before they are outside the render object's bound.</summary>
    /// <remarks>
    ///     ⚠ <b>The bound is what the frustum culls against, and a particle outside it disappears with
    ///     the whole effect rather than on its own.</b> Nothing recomputes it — the render object's
    ///     bounding sphere is written once, when the lamp is found — so it has to cover where the
    ///     drift can reach: a rise of 0.35 m/s against a life of nine seconds is a little over three
    ///     metres, plus the turbulence's sideways wander. Four is that with room to spare, and an
    ///     effect bound that is slightly too large costs one frustum test that comes back true.
    /// </remarks>
    public const float Reach = 4f;

    /// <summary>The graph for a lamp at a point.</summary>
    /// <param name="lamp">Where the lamp is, in world space.</param>
    /// <returns>The compiled graph, ready for a <see cref="VfxSystem" />.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>The colour is the lamp's, in the units the rest of the level is lit in.</b> These are
    ///         1 900 K sodium floodlights, so an ember off one is the same orange — and the value
    ///         carried here is a <em>ratio</em>, with the brightness in
    ///         <c>ParticleSprite.emissive</c>. Splitting them that way is what lets one number make the
    ///         sparks bloom without touching their hue.
    ///     </para>
    ///     <para>
    ///         <b>Gravity points up.</b> An ember is hot air with a bit of carbon in it: it rises, and
    ///         it rises faster the longer it has been rising, until the drag catches it. That is a
    ///         positive <c>y</c> acceleration and an exponential drag, which between them give the
    ///         float-then-settle a real one has — and it is the whole reason the two are separate
    ///         opcodes rather than one velocity.
    ///     </para>
    ///     <para>
    ///         <b>The turbulence is what makes it look alive.</b> Without it fifteen lamps produce
    ///         fifteen identical vertical columns, which reads as a bug in the emitter rather than as
    ///         still air. Curl noise at a low frequency and a slow drift is a draught.
    ///     </para>
    /// </remarks>
    public static VfxCompiledGraph Graph(Vector3 lamp) =>
        VfxCompiledGraph.Compile(
            // A trickle rather than bursts. A burst is a shower of sparks — something struck, something
            // broken — and these are meant to be the constant faint drift off a hot lamp.
            [VfxSpawner.AtRate(6f)],
            [
                new(VfxOpcode.PositionInSphere, new Vector4(lamp.X, lamp.Y, lamp.Z, Source)),

                // Up, in a wide cone, slowly. The half-angle is generous because the initial direction
                // is almost immediately overwhelmed by the buoyancy and the turbulence — what it
                // actually decides is how wide the column is at the bottom.
                new(VfxOpcode.VelocityInCone, new Vector4(0f, 1f, 0f, 0.7f)) { B = new(0.12f, 0.35f, 0f, 0f) },
                new(VfxOpcode.SetLifetime, new Vector4(4.5f, 9f, 0f, 0f)),

                // Two to five centimetres. Smaller than a texel at any distance worth mentioning, which
                // is the point: what makes it visible is the brightness, not the area, and a spark that
                // covers several pixels reads as a firefly.
                new(VfxOpcode.SetSize, new Vector4(0.02f, 0.05f, 0f, 0f)),

                // The lamp's own colour as a ratio — see the remarks. Alpha is the particle's opacity
                // and starts at one, so the fade below is a fade of the whole thing.
                new(VfxOpcode.SetColour, new Vector4(1f, 0.58f, 0.16f, 1f))
            ],
            [
                new(VfxOpcode.Gravity, new Vector4(0f, 0.35f, 0f, 0f)),
                new(VfxOpcode.Drag, new Vector4(0.55f, 0f, 0f, 0f)),

                // Frequency, then strength in `w`, then the drift per second in `B.x` — the term that
                // makes the field itself move, so a stationary particle in still air still wanders.
                new(VfxOpcode.Turbulence, new Vector4(0.45f, 0.45f, 0.45f, 0.5f)) { B = new(0.2f, 2f, 0f, 0f) },

                // Cooling: bright orange to a dim red, and the alpha to nothing so nothing pops out of
                // existence. `ColourOverLife` lerps A to B across the whole life, so the fade is the
                // last thing the sizes and colours here say.
                new(VfxOpcode.ColourOverLife, new Vector4(1f, 0.58f, 0.16f, 1f)) { B = new(0.7f, 0.16f, 0.03f, 0f) },
                new(VfxOpcode.SizeOverLife, new Vector4(0.045f, 0.012f, 0f, 0f)),

                // ⚠ Last, always. The forces above write velocity and this is what turns velocity into
                // position — put first, every particle would move on the previous step's forces, which
                // is a whole frame of lag in something that lives for a few seconds.
                new(VfxOpcode.Integrate, Vector4.Zero)
            ],
            Capacity,

            // Unsorted, because the blend is additive and addition commutes. Sorting would cost a sort
            // per effect per frame to produce the same pixels — see `VfxRenderer.Billboard`, which says
            // so from the other side.
            VfxRenderer.Billboard
        );
}
