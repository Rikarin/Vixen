// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Globalization;
using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Core.Threading;
using Vixen.Ecs;
using Vixen.Ecs.Systems;
using Vixen.Engine.Frames;
using Vixen.Engine.Scenes;
using Vixen.Engine.Transforms;

namespace Vixen.Samples.EcsStressTest;

/// <summary>
///     The whole of Phase 2 at once, and the exit criteria measured rather than asserted: a hundred
///     thousand entities, a transform hierarchy, a system graph and a frame loop.
/// </summary>
/// <remarks>
///     A console program with no window, because Phase 2 renders nothing — the picture is Phase 4's.
///     What it shows is the shape of a frame at scale: how long the systems take, how many entities
///     the transform pass actually touches, and whether a steady state allocates.
/// </remarks>
public static class Program {
    const int ChildrenPerRoot = 4;

    /// <summary>Runs the stress test.</summary>
    /// <param name="arguments">
    ///     <c>--frames N</c> to run a fixed number of frames, matching the host's own
    ///     <c>--vixen-frames</c> so this is CI-runnable the same way.
    /// </param>
    public static void Main(string[] arguments) {
        var frames = Argument(arguments, "--frames", 600);
        var report = Argument(arguments, "--report-every", 120);
        var roots = Argument(arguments, "--roots", 20_000);

        using var scheduler = new JobScheduler();
        using var loop = new EngineLoop(jobs: scheduler);
        var scenes = new SceneManager(loop.World);
        var level = scenes.Create("stress");

        loop.Add(new OrbitSystem());

        var clock = Stopwatch.StartNew();
        Populate(loop.World, scenes, level, roots);
        var built = clock.Elapsed;

        Write(
            $"{loop.World.EntityCount:N0} entities in {loop.World.Archetypes.Count} archetypes, "
            + $"built in {built.TotalMilliseconds:N1} ms "
            + $"({built.TotalNanoseconds / loop.World.EntityCount:N0} ns each)"
        );

        // Two warm-up frames: the first builds the system graph and the query caches, the second
        // grows the transform pass's per-depth buckets. Measuring those would measure start-up.
        loop.Frame(TimeSpan.FromMilliseconds(16));
        loop.Frame(TimeSpan.FromMilliseconds(16));

        var before = GC.GetAllocatedBytesForCurrentThread();
        var collections = GC.CollectionCount(0);
        var slowest = TimeSpan.Zero;
        var total = TimeSpan.Zero;

        for (var frame = 0; frame < frames; frame++) {
            var start = clock.Elapsed;
            loop.Frame(TimeSpan.FromMilliseconds(16));
            var elapsed = clock.Elapsed - start;

            total += elapsed;

            if (elapsed > slowest) {
                slowest = elapsed;
            }

            if (report > 0 && (frame + 1) % report == 0) {
                Write($"frame {frame + 1,5}: {elapsed.TotalMicroseconds:N0} us");
            }
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Write(
            $"{frames} frames: mean {total.TotalMicroseconds / frames:N0} us, "
            + $"worst {slowest.TotalMicroseconds:N0} us"
        );

        Write($"allocated {allocated:N0} bytes over {frames} frames, {GC.CollectionCount(0) - collections} gen0 collections");

        var unloaded = scenes.Unload(level);
        Write($"unloaded {unloaded:N0} entities; {loop.World.EntityCount} remain");
    }

    static void Populate(World world, SceneManager scenes, SceneHandle level, int roots) {
        for (var index = 0; index < roots; index++) {
            var angle = index * 0.001f;

            var root = scenes.CreateTransform(
                level,
                LocalTransform.At(new(MathF.Cos(angle) * 50f, 0f, MathF.Sin(angle) * 50f))
            );

            world.Add(root, new Orbit { Speed = 0.5f + (index % 16 * 0.05f), Radius = 50f, Angle = angle });

            for (var child = 0; child < ChildrenPerRoot; child++) {
                var leaf = Hierarchy.CreateTransform(world, LocalTransform.At(new(0f, child + 1f, 0f)));
                Hierarchy.SetParent(world, leaf, root);
                scenes.Adopt(level, leaf);
            }
        }
    }

    static int Argument(string[] arguments, string name, int fallback) {
        for (var index = 0; index < arguments.Length - 1; index++) {
            if (arguments[index] == name && int.TryParse(arguments[index + 1], CultureInfo.InvariantCulture, out var value)) {
                return value;
            }
        }

        return fallback;
    }

    static void Write(string line) => Console.Out.WriteLine(line);
}

/// <summary>Moves every root around a circle, which is what makes the transform pass have work.</summary>
/// <remarks>
///     In <see cref="SystemPhase.Update" /> and declaring what it touches, so the runner can see it
///     does not conflict with anything else and the transform pass in <c>PreRender</c> picks up
///     exactly the chunks it wrote.
/// </remarks>
[UpdateInGroup(SystemPhase.Update)]
public sealed class OrbitSystem : SystemBase, IDeclaredAccess {
    readonly QueryDescription orbiting = new QueryDescription().WithAll<Orbit, LocalTransform>();

    /// <inheritdoc />
    public SystemAccess Access { get; } = SystemAccess.Declare()
        .Write<Orbit>()
        .Write<LocalTransform>()
        .Build();

    /// <inheritdoc />
    public override JobHandle Update(in SystemContext context, JobHandle dependency) {
        var delta = context.Time.DeltaSeconds;

        foreach (var chunk in context.World.Chunks(orbiting)) {
            var orbits = chunk.Values<Orbit>();
            var transforms = chunk.Values<LocalTransform>()[..orbits.Length];

            // Bounded by the span's own length, not by chunk.Count: the benchmark measured that as
            // the difference between the fastest form and one 34% slower.
            for (var index = 0; index < orbits.Length; index++) {
                orbits[index].Angle += orbits[index].Speed * delta;

                transforms[index].Position = new(
                    MathF.Cos(orbits[index].Angle) * orbits[index].Radius,
                    0f,
                    MathF.Sin(orbits[index].Angle) * orbits[index].Radius
                );
            }
        }

        return dependency;
    }
}
