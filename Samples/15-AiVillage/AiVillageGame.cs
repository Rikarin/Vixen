// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.Logging;
using Vixen.Ai.Diagnostics;
using Vixen.App;
using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Engine.Cameras;
using Vixen.Engine.Transforms;

namespace Vixen.Samples.AiVillage;

/// <summary>The AI stack, in a game: three agents deciding about one intruder.</summary>
/// <remarks>
///     <para>
///         <b>What this sample exists to prove is that the runtime half of <c>Vixen.Ai</c> works
///         outside a test fixture</b>, which nothing had shown: before this project,
///         <c>grep -rl "Vixen.Ai" Samples/</c> was empty, and every consumer of <c>AiSystem</c>,
///         <c>PerceptionSystem</c> and all three planners in the whole repository was a
///         <c>*.Tests</c> assembly stepping them by hand.
///     </para>
///     <para>
///         ⚠ <b>The whole visual is the AI overlay</b>, and that is not a shortcut — it is doc 37 §
///         D20's <c>AiGameplayDebugger</c>, which was built with nine tests and <i>registered by no
///         application</i>. A sample that drew three capsules would have shown the agents moving and
///         left the overlay exactly as unreached as it was.
///     </para>
///     <para>
///         ⚠ <b><c>Graphics.Overlays</c> is the switch, and it is off by default.</b> It is what
///         builds <c>AppGraphics.Debug</c> and the compositor node that drains it; without it
///         <c>AiOverlaySystem</c> writes lines into an accumulator that does not exist. It is set
///         here rather than left to <c>--vixen-overlays</c> because a sample whose picture depends
///         on a flag the reader has to know about is a sample that looks broken.
///     </para>
///     <para>
///         ⚠ <b>And <c>DebugDrawSystem</c> is deliberately <i>not</i> added.</b> It ages the
///         accumulator in <c>PostRender</c>, which under <c>VixenApplication</c> runs before the GPU
///         frame is recorded — so it would delete every line one call before the node drew it, with
///         every counter still reading correct. <c>AppGraphics.AdvanceDebug</c> already does the
///         ageing, at the only point in the frame where it is right.
///     </para>
/// </remarks>
public sealed class AiVillageGame : Game {
    /// <summary>Where the camera stands, and what it looks at.</summary>
    static readonly Vector3 Eye = new(24f, 30f, -16f);

    static readonly Vector3 Subject = new(20f, 0f, 20f);

    ILogger? log;
    Village? village;
    DecisionLog? decisions;
    AiGameplayDebugger? debugger;
    int reported;
    bool announced;

    /// <inheritdoc />
    protected override void OnConfigure(AppConfig config) {
        ArgumentNullException.ThrowIfNull(config);

        config.Name = "AI Village";

        // ⚠ `IsVisible` follows `Headless`, which the other samples do not do and should.
        // `AppConfig.Apply` reads the command line *before* this hook — deliberately, so that a game
        // can override it — so a sample that assigns `IsVisible = true` unconditionally shows a
        // window on a run that asked for none. Every sample in this tree does exactly that, which is
        // why `--vixen-headless` still puts a window on the screen.
        config.Window = new() {
            Title = "Vixen — AI Village",
            Size = new(1280, 720),
            IsVisible = !config.Headless
        };

        // The switch that makes the overlay visible at all — see this class's remarks.
        config.Graphics.Overlays = true;
    }

    /// <inheritdoc />
    protected override void OnInitialise() {
        log = Services.LoggerFactory.CreateLogger("AiVillage");

        // Headless with no engine is legitimate: `--vixen-frames 1` on a machine with no GPU is how
        // CI proves the process starts and stops, and there is nothing to decide in it.
        if (Services.Engine is not { } loop) {
            SampleLog.NoEngine(log);

            return;
        }

        village = new Village(loop.World);
        village.Register(loop);
        decisions = new DecisionLog(village);

        // ⚠ On, and the sample says why: the recorder is off by default because it costs a ring
        // buffer per agent, and doc 37 § P7's diagnosis reads nothing else. A village that ran
        // correctly and reported a symptom would be worse than one that reported nothing.
        village.Agents.Debug.Enabled = true;

        Camera(loop.World);

        // ── doc 37 § P7's overlay, registered by an application for the first time ───────────
        if (Services.Graphics?.Debug is { } draw) {
            debugger = new AiGameplayDebugger {
                // Everything: every category, and `Range = 0` so nothing is culled by distance.
                // The default style is `Agent | Shapes` within forty metres of `Viewpoint`, and a
                // viewpoint left at the origin with the village forty metres away draws nothing at
                // all — which looks exactly like an overlay that does not work.
                Style = AiOverlayStyle.Everything,
                Viewpoint = Eye,
                Perception = village.Perception
            };

            loop.Add(new AiOverlaySystem(debugger, village.Agents, draw));
            SampleLog.OverlayRegistered(log);
        } else {
            SampleLog.NoOverlay(log);
        }

    }

    /// <inheritdoc />
    protected override void OnUpdate(GameTime time) {
        if (village is null || decisions is null || log is null) {
            return;
        }

        // ⚠ After the first frame and not from `OnInitialise`. `AiSystem.Population` is written by
        // `Join`, which runs inside the first `Step` — so a line logged at initialise time reports
        // that the village has no agents in it, which is a sentence somebody would go and debug.
        if (!announced) {
            announced = true;
            SampleLog.VillageBuilt(log, village.Agents.Population, village.Registry.Count, Intrusion.Duration);
        }

        // After `EngineLoop.Frame`, which `VixenApplication` runs before this hook — so what is read
        // here is this frame's decision rather than the last one's.
        decisions.Observe(time.FrameCount, village.Script.Elapsed);

        // Streamed as they happen rather than dumped at the end, because a run stopped by
        // `--vixen-frames` mid-script should still have said everything it saw.
        for (; reported < decisions.Changes.Count; reported++) {
            var change = decisions.Changes[reported];

            SampleLog.Decided(
                log,
                change.Frame,
                change.Seconds,
                change.Agent,
                change.Planner,
                change.From,
                change.To,
                change.Distance
            );
        }
    }

    /// <inheritdoc />
    protected override void OnShutdown() {
        if (village is null || decisions is null || log is null) {
            return;
        }

        var findings = new List<AiFinding>();
        var symptoms = AiDiagnosis.Analyse(village.Agents.Debug, findings);

        var guard = decisions.For("guard").Count();
        var villager = decisions.For("villager").Count();
        var scavenger = decisions.For("scavenger").Count();

        SampleLog.RunSummary(
            log,
            decisions.Changes.Count,
            guard,
            villager,
            scavenger,
            village.Script.Elapsed,
            symptoms
        );

        var (atGuard, atVillager) = (village.Where(village.Guard), village.Where(village.Villager));
        var (atScavenger, atIntruder) = (village.Where(village.Scavenger), village.Where(village.Intruder));

        SampleLog.WhereTheyEnded(log, atGuard, atVillager, atScavenger, atIntruder);

        if (debugger is { } drawn) {
            SampleLog.OverlayDrew(log, drawn.DrawnAgents, drawn.DrawnRows);
        }
    }

    /// <summary>An eye to see the village through, because world lines need one.</summary>
    /// <remarks>
    ///     ⚠ <b><c>WorldTransform</c> as well as <c>LocalTransform</c>.</b> An entity created in code
    ///     carries only what it was created with, and extraction reads the world matrix — so a
    ///     camera with only a local transform is a camera nothing renders through, which looks like
    ///     a black frame rather than like a missing component.
    /// </remarks>
    static void Camera(World world) {
        var placement = new LocalTransform {
            Position = Eye,
            Rotation = Transform.LookRotation(Vector3.Normalize(Subject - Eye), Vector3.UnitY),
            Scale = Vector3.One
        };

        world.Create(
            placement,
            new WorldTransform { Value = placement.ToMatrix() },
            Vixen.Engine.Cameras.Camera.Perspective
        );
    }
}
