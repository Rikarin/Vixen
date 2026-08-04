// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ai.Perception.Ecs;
using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Engine.Transforms;

namespace Vixen.Ai.Perception.Tests;

/// <summary>A world with a perception system in it, stepped by hand.</summary>
/// <remarks>
///     The frame is a tenth of a second and the default interval is a tenth of a second, so one
///     <see cref="Step" /> is one pass for every listener — which keeps a test about what a sense
///     found from also being a test about the schedule. The tests that <i>are</i> about the schedule
///     say so and set their own interval.
/// </remarks>
sealed class Fleet {
    int frame;

    public Fleet(PerceptionConfig? config = null) {
        World = new World("perception-test");
        System = new PerceptionSystem();
        Config = System.Configs.Add(config ?? Everything());
    }

    public World World { get; }

    public PerceptionSystem System { get; }

    public int Config { get; }

    /// <summary>Sight and hearing at full range, no cone, nothing blocking, no jitter.</summary>
    /// <remarks>⚠ Deviation zero on purpose: a schedule a test cannot predict is a flaky test.</remarks>
    public static PerceptionConfig Everything(SenseMask senses = SenseMask.All) => new() {
        Senses = senses,
        Sight = new() { Radius = 20f, LoseSightRadius = 25f, ConeDegrees = 360f, Occlusion = false },
        RandomDeviation = 0f
    };

    public Entity Listener(Vector3 at, byte team = 0, int? config = null) =>
        World.Create(AiPerception.Sensing(config ?? Config, team), LocalTransform.At(at));

    public Entity Source(Vector3 at, byte team = 1, SenseMask senses = SenseMask.All) =>
        World.Create(AiStimuliSource.Perceivable(team, senses), LocalTransform.At(at));

    public Entity Both(Vector3 at, byte team, int? config = null) {
        var entity = Listener(at, team, config);

        World.Add(entity, AiStimuliSource.Perceivable(team));

        return entity;
    }

    public void MoveTo(Entity entity, Vector3 position) => World.Get<LocalTransform>(entity).Position = position;

    public void Face(Entity entity, Quaternion rotation) => World.Get<LocalTransform>(entity).Rotation = rotation;

    public PerceivedTargets Perceived(Entity listener) => System.PerceivedBy(World, listener)!;

    public void Step(int count = 1) {
        for (var index = 0; index < count; index++) {
            System.Step(World, Frame(frame++));
        }
    }

    public static GameTime Frame(int index) => new(
        TimeSpan.FromSeconds((index + 1) * 0.1),
        TimeSpan.FromSeconds(0.1),
        TimeSpan.FromSeconds(0.1),
        index,
        1f
    );
}
