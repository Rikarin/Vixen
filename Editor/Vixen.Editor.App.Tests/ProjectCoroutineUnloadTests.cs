// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Vixen.Editor.Core;
using Vixen.Editor.Plugin.Tests;
using Vixen.Engine.Behaviors;
using Vixen.Engine.Frames;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>
///     The end of the chain: a behaviour compiled into a collectible context, running a coroutine,
///     detached — and the context actually leaving memory.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The claim nothing else reports on.</b> A collectible context that cannot be collected
///         produces no exception, no log line and no symptom until the tenth reload of a session is
///         holding ten copies of the project's assembly. <c>BehaviorTests</c> and
///         <c>CoroutineTests</c> assert the first links of this chain — the store lets go of a
///         detached behaviour, the scheduler lets go of its coroutine — because those are
///         deterministic; this asserts the end of it, which is the thing anybody actually cares
///         about.
///     </para>
///     <para>
///         ⚠ <b>Everything that could pin the context other than the coroutine is already released
///         by the time the assertion runs.</b> <see cref="ProjectAssemblies.Unload" /> evicts the
///         five registries; the loop, the world, the store and the behaviour are locals of a frame
///         that has returned. What is left is the scheduler, which before this fix held the
///         coroutine's continuation — a delegate over a state machine whose type is in the context —
///         for ever.
///     </para>
///     <para>
///         ⚠ <b>Bounded by attempts rather than by a clock.</b> A deadline in seconds is a test that
///         reports the machine's load; the property here is that a fixed number of full collections
///         is enough, which is true when nothing holds the context and never becomes true when
///         something does. <c>PluginHost.WaitForCollection</c> spends a timeout because it is a tool
///         an editor calls; a test can say how much work it is willing to do instead.
///     </para>
/// </remarks>
public sealed class ProjectCoroutineUnloadTests : IDisposable {
    /// <summary>
    ///     A behaviour of the kind a project author writes: a coroutine started in <c>Start</c> that
    ///     never finishes on its own.
    /// </summary>
    /// <remarks>
    ///     ⚠ No <c>[DataContract]</c> and no generator, so nothing registers this type anywhere. That
    ///     is deliberate: the registries have their own eviction test, and a fixture that also
    ///     exercised them would not say which of the two holds the context when it fails.
    /// </remarks>
    const string Source = """
        using Vixen.Engine.Behaviors;
        using Vixen.Engine.Coroutines;

        namespace GameCode;

        public sealed class Patrol : Behavior {
            protected override void Start() => Run(Body());

            async Coroutine Body() {
                while (true) {
                    await Seconds(30f);
                }
            }
        }
        """;

    static readonly TimeSpan Sixtieth = TimeSpan.FromTicks(TimeSpan.TicksPerSecond / 60);

    readonly PluginFolder folder = new();

    /// <summary>The scene the editor keeps open across a reload of the project's code.</summary>
    /// <remarks>
    ///     ⚠ <b>A field, and that is the whole difference between this test and one that proves
    ///     nothing.</b> Written first with the loop as a local of the helper below, it passed against
    ///     the defect: the scheduler died with the frame that made it, so of course nothing was left
    ///     holding the context. What the editor actually does is keep the scene — its world, its
    ///     behaviour store and its coroutine scheduler — and swap the assembly underneath it. A
    ///     harness looser than the runtime looks thorough and asserts nothing.
    /// </remarks>
    readonly EngineLoop loop = new();

    /// <inheritdoc />
    public void Dispose() {
        loop.Dispose();
        folder.Dispose();
    }

    [Fact]
    public void A_detached_behaviours_coroutine_does_not_pin_the_projects_load_context() {
        var weak = LoadRunAndDetach();

        // Two collections per attempt, because a finalizer that drops the last reference only runs
        // between them — the arrangement every collectible-context sample uses.
        for (var attempt = 0; attempt < 10 && weak.IsAlive; attempt++) {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        Assert.False(
            weak.IsAlive,
            "the project's load context was not collected: a detached behaviour's coroutine is still "
            + "holding a state machine of a type inside it"
        );
    }

    /// <summary>
    ///     Compiles, loads, runs, detaches and unloads — in a frame that returns, so the only
    ///     reference the caller could still find is one the engine kept.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Not inlined, and every reference into the context is a local of this frame.</b> A
    ///     jitted method that kept the assembly or the behaviour alive in a register past the
    ///     collection would report a leak for a fix that works — the same reason
    ///     <c>PluginHost.Deactivate</c> is marked the same way.
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    WeakReference LoadRunAndDetach() {
        var library = folder.WriteLibrary("gamecode", "GameCode", Source);
        var assemblies = new ProjectAssemblies(new ProjectPaths(folder.Root));
        var assembly = assemblies.Load(library);

        var context = AssemblyLoadContext.GetLoadContext(assembly);

        Assert.NotNull(context);
        Assert.True(context.IsCollectible, "the project's assembly is not in a collectible context");

        var weak = new WeakReference(context);
        var behavior = (Behavior) Activator.CreateInstance(assembly.GetType("GameCode.Patrol")!)!;

        loop.Behaviors.Add(loop.World.Create(), behavior);

        // Twice: `Start` runs in the first frame's lifecycle drain, and the coroutine's first wait
        // is expressed there — so it is suspended and held by the scheduler from here on.
        loop.Frame(Sixtieth);
        loop.Frame(Sixtieth);

        Assert.Equal(1, loop.Coroutines.RunningCount);

        // The editor's hot-reload path, verbatim: `SaveProjectBehaviors` detaches every authored
        // behaviour and `ProjectAssemblies.Reload` unloads the context, with no frame in between.
        Assert.True(loop.Behaviors.Remove(behavior));
        Assert.True(assemblies.Unload());

        return weak;
    }
}
