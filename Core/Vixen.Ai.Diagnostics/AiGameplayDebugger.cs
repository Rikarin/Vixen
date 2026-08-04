// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using Vixen.Ai.Ecs;
using Vixen.Ai.Perception.Diagnostics;
using Vixen.Ai.Perception.Ecs;
using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Engine.Diagnostics;
using Vixen.Engine.Transforms;

namespace Vixen.Ai.Diagnostics;

/// <summary>
///     One overlay over all three planners and the perception model, drawn through
///     <see cref="DebugDraw" />.
/// </summary>
/// <remarks>
///     <para>
///         <b>Doc 37 § D20's one debug surface.</b> Unreal's answer to "why did my AI do that" is one
///         key, one overlay and numbered categories, and it is the most-used AI feature in that engine
///         because it works in a packaged build rather than only in the editor. This is that, and it
///         is one class rather than three because <see cref="AiSnapshots" /> has already made the
///         three planners agree on a shape.
///     </para>
///     <para>
///         ⚠ <b>It draws lines and text and it is therefore testable with no window at all</b> —
///         <c>ConstraintGizmos</c>'s precedent, and doc 37 § P7's second exit criterion. Everything
///         lands in a <see cref="DebugDraw" />'s three lists, which a test reads directly; there is no
///         device, no font atlas and no render pass between the assertion and the geometry.
///     </para>
///     <para>
///         ⚠ <b>It reads and never simulates.</b> A capture re-scores a utility set without advancing
///         its clock and reads a plan without re-resolving it, so turning the overlay on cannot change
///         what an agent does. A debugger that perturbed the thing it watched would be worse than
///         none, because the bug would move.
///     </para>
///     <para>
///         The cost is a capture per drawn agent per frame, which is why <see cref="AiOverlayStyle" />
///         has a range and a count and why both of them bite before anything is formatted.
///     </para>
/// </remarks>
public sealed class AiGameplayDebugger {
    readonly AiAgentSnapshot snapshot = new();
    readonly List<AiFinding> findings = [];
    readonly List<Entity> candidates = [];
    readonly QueryDescription agents = new QueryDescription().WithAll<AiAgent>();
    readonly StringBuilder line = new();

    /// <summary>Whether anything is drawn. Off is one branch.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>What is drawn, how far and in what colours.</summary>
    public AiOverlayStyle Style { get; set; } = AiOverlayStyle.Default;

    /// <summary>Where the camera is. Agents are drawn nearest first from here.</summary>
    public Vector3 Viewpoint { get; set; }

    /// <summary>One agent to draw whatever the range says, and to draw in the attention colour.</summary>
    public Entity Selected { get; set; }

    /// <summary>The perception system, when the game has one.</summary>
    public PerceptionSystem? Perception { get; set; }

    /// <summary>How many agents the last <see cref="Draw" /> put on the screen.</summary>
    public int DrawnAgents { get; private set; }

    /// <summary>How many rows it drew, across every agent.</summary>
    public int DrawnRows { get; private set; }

    /// <summary>What <see cref="AiDiagnosis" /> made of the log the last time it was asked.</summary>
    public IReadOnlyList<AiFinding> Findings => findings;

    /// <summary>Draws the overlay for one world.</summary>
    /// <param name="draw">Where the geometry goes.</param>
    /// <param name="system">The system running the agents.</param>
    /// <param name="world">Their world.</param>
    /// <param name="time">The clock.</param>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public void Draw(DebugDraw draw, AiSystem system, World world, GameTime time = default) {
        ArgumentNullException.ThrowIfNull(draw);
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(world);

        DrawnAgents = 0;
        DrawnRows = 0;

        if (!Enabled || !draw.Enabled || Style.Categories == AiDebugCategory.None) {
            return;
        }

        if (Style.Shows(AiDebugCategory.Findings)) {
            AiDiagnosis.Analyse(system.Debug, findings);
        } else {
            findings.Clear();
        }

        Gather(world);

        foreach (var entity in candidates) {
            if (DrawnAgents == Style.Agents) {
                break;
            }

            if (!AiSnapshots.Take(system, world, entity, snapshot, time)) {
                continue;
            }

            snapshot.Position = PositionOf(world, entity);
            snapshot.Located = true;

            if (Perception is { } perception) {
                PerceptionSnapshots.Describe(perception, world, entity, snapshot);
                PerceptionSnapshots.Add(perception, world, entity, snapshot);
            }

            Agent(draw, world, snapshot);
            DrawnAgents++;
        }
    }

    /// <summary>Which agents are near enough and close enough to be worth drawing, nearest first.</summary>
    /// <remarks>
    ///     ⚠ <b>Sorted by distance and not by chunk order.</b> A cap that took whichever agents the
    ///     query happened to walk first would show a different sixteen every time anything spawned,
    ///     which is the one behaviour that makes an overlay untrustworthy: the agent you were watching
    ///     disappears and you cannot tell whether it died or was merely dropped.
    /// </remarks>
    void Gather(World world) {
        candidates.Clear();

        foreach (var chunk in world.Chunks(agents)) {
            var entities = chunk.Entities;

            for (var index = 0; index < chunk.Count; index++) {
                var entity = entities[index];

                if (entity == Selected) {
                    candidates.Add(entity);

                    continue;
                }

                if (Style.Range > 0f
                    && Vector3.DistanceSquared(PositionOf(world, entity), Viewpoint) > Style.Range * Style.Range) {
                    continue;
                }

                candidates.Add(entity);
            }
        }

        var viewpoint = Viewpoint;
        var selected = Selected;

        candidates.Sort(
            (left, right) => {
                // The selected agent first, always, so that "why is this one doing that" is never
                // answered by the seventeenth label being off the bottom of the cap.
                if (left == selected != (right == selected)) {
                    return left == selected ? -1 : 1;
                }

                return Vector3.DistanceSquared(PositionOf(world, left), viewpoint)
                    .CompareTo(Vector3.DistanceSquared(PositionOf(world, right), viewpoint));
            }
        );
    }

    /// <summary>Draws one agent: its marker, its heading, its readout and its senses.</summary>
    void Agent(DebugDraw draw, World world, AiAgentSnapshot state) {
        var colour = state.Entity == Selected ? Style.Attention : Style.ColourOf(state.Status);
        var at = state.Position;

        if (Style.Shows(AiDebugCategory.Agent)) {
            draw.Cross(at, 0.35f, colour);
            draw.Line(at, at + (Vector3.UnitY * Style.Headroom), colour);
        }

        if (Style.Shows(AiDebugCategory.Shapes)) {
            Shapes(draw, world, state.Entity, at, colour);
        }

        var cursor = at + (Vector3.UnitY * Style.Headroom);
        var size = Style.Text;

        if (Style.Shows(AiDebugCategory.Agent)) {
            line.Clear();
            line.Append(CultureInfo.InvariantCulture, $"{state.Planner} {state.Asset}");
            line.Append(CultureInfo.InvariantCulture, $"\n{state.Action} — {state.Status}");

            if (state.Reason.Length > 0) {
                line.Append(CultureInfo.InvariantCulture, $"\n{state.Reason}");
            }

            draw.Text(cursor, line.ToString(), colour, size);
            cursor -= Vector3.UnitY * (size * 3f);
            DrawnRows += 3;
        }

        if (Style.Shows(AiDebugCategory.Findings)) {
            foreach (var finding in findings) {
                if (finding.Entity != state.Entity) {
                    continue;
                }

                draw.Text(cursor, finding.ToString(), Style.Attention, size);
                cursor -= Vector3.UnitY * size;
                DrawnRows++;
            }
        }

        var section = AiDebugSection.Doing;
        var started = false;
        var shown = 0;

        foreach (var row in state.Rows) {
            if (!started || row.Section != section) {
                started = true;
                section = row.Section;
                shown = 0;
            }

            if (!Style.Shows(AiDebugCategories.For(row.Section)) || shown >= Style.RowsPerSection) {
                continue;
            }

            draw.Text(cursor, row.ToString(), row.Active ? Style.Live : Style.Quiet, size);
            cursor -= Vector3.UnitY * size;
            shown++;
            DrawnRows++;
        }
    }

    /// <summary>The sight cone, the hearing radius, and a line to everything being sensed.</summary>
    void Shapes(DebugDraw draw, World world, Entity entity, Vector3 at, Color4 colour) {
        if (Perception is not { } perception || !world.IsAlive(entity) || !world.Has<AiPerception>(entity)) {
            return;
        }

        ref readonly var listener = ref world.Read<AiPerception>(entity);

        if (listener.Config >= perception.Configs.Count) {
            return;
        }

        var config = perception.Configs[listener.Config];
        var eye = at + (Vector3.UnitY * config.Sight.EyeHeight);
        var forward = Forward(world, entity);

        // A cone drawn as its own geometry rather than as a circle, because "can it see me" is a
        // question about a direction and a circle answers a different one.
        var half = float.DegreesToRadians(config.Sight.ConeDegrees) * 0.5f;
        var reach = config.Sight.Radius;

        draw.Cone(eye, forward * reach, MathF.Tan(MathF.Min(half, 1.5533f)) * reach, colour);

        // ⚠ The lose-sight radius drawn as well as the acquire radius, because the gap between them
        // is the whole of why a guard keeps following somebody it should have lost — and it is
        // invisible in every debugger that draws one circle.
        draw.Circle(at, Vector3.UnitY, config.Sight.LoseSightRadius, Style.Quiet);
        draw.Circle(at, Vector3.UnitY, config.Hearing.Range, Style.Quiet);

        if (!Style.Shows(AiDebugCategory.Senses) || perception.PerceivedBy(world, entity) is not { } perceived) {
            return;
        }

        foreach (var target in perceived.Targets) {
            draw.Line(eye, target.LastKnownLocation, target.Current ? Style.Live : Style.Quiet);
        }
    }

    /// <summary>Where an entity is, or the origin when it has no transform.</summary>
    static Vector3 PositionOf(World world, Entity entity) =>
        world.IsAlive(entity) && world.Has<LocalTransform>(entity)
            ? world.Read<LocalTransform>(entity).Position
            : Vector3.Zero;

    /// <summary>Which way it is facing, or <c>+Z</c> when it has no transform.</summary>
    static Vector3 Forward(World world, Entity entity) {
        if (!world.IsAlive(entity) || !world.Has<LocalTransform>(entity)) {
            return Vector3.UnitZ;
        }

        var forward = Quaternion.Transform(Vector3.UnitZ, world.Read<LocalTransform>(entity).Rotation);

        return forward.LengthSquared() > MathUtil.ZeroTolerance ? Vector3.Normalize(forward) : Vector3.UnitZ;
    }
}
