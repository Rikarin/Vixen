// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Vixen.Core;
using Vixen.Core.Threading;
using Vixen.Ecs.Systems;
using Vixen.Engine.Frames;
using Xunit;

namespace Vixen.Cli.Tests;

/// <summary>`vixen doctor systems`, run against this assembly's own declared frame.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The declarations these read were emitted by <c>GameSystemGenerator</c>, not written
///         here.</b> The systems below carry <c>[GameSystem]</c>, the generator is wired into this
///         project as an analyzer, and the <c>[ModuleInitializer]</c> it produced ran before the
///         first test did. A fixture that called <c>GameSystemRegistry.Declare</c> by hand would
///         prove the registry works and say nothing about whether a real project's frame ever
///         reaches the command.
///     </para>
///     <para>
///         <b>The frame below is deliberately broken, in four different ways at once</b>, because a
///         doctor that cannot be made to complain has not been tested. There is an
///         <c>[UpdateAfter]</c> naming a system that does not exist; an <c>[UpdateBefore]</c>
///         reaching across a phase boundary, which the scheduler silently drops; a constructor
///         asking for a value type, which <c>ServiceRegistry</c> can never satisfy; and a system
///         with no <c>[GameSystem]</c> at all.
///     </para>
/// </remarks>
public sealed class DoctorSystemsTests : IDisposable {
    readonly StringWriter output = new();

    /// <summary>This assembly, which is the built game assembly under examination.</summary>
    static string ThisAssembly => Assembly.GetExecutingAssembly().Location;

    public void Dispose() => output.Dispose();

    async Task<int> RunAsync(params string[] args) =>
        await VixenCommand.Create(output, output).Parse(args).InvokeAsync();

    [Fact]
    public async Task It_reports_the_resolved_run_order_by_phase() {
        var code = await RunAsync("doctor", "systems", "--assembly", ThisAssembly);
        var text = output.ToString();

        // EarlyUpdate before Update, because a phase's position is the enum's.
        Assert.Contains("EarlyUpdate 1/1: BookkeepingSystem", text, StringComparison.Ordinal);

        // ⚠ The order, not the registration order. GameSystemRegistry.Declared is sorted by type
        // name, so HaulSystem is offered to the sort first; [UpdateAfter(typeof(ScrapSystem))] is
        // what puts it second, and that is the whole thing this command exists to show.
        Assert.Contains("Update 1/2: ScrapSystem", text, StringComparison.Ordinal);
        Assert.Contains("Update 2/2: HaulSystem", text, StringComparison.Ordinal);

        // A value type nothing can register is broken, so the command fails.
        Assert.Equal((int)ExitCode.Failed, code);
    }

    /// <summary>
    ///     The finding that cannot be got any other way. <c>SystemGraph</c> drops an edge naming a
    ///     system it does not have, deliberately and without a word.
    /// </summary>
    [Fact]
    public async Task It_names_an_ordering_attribute_that_does_nothing() {
        await RunAsync("doctor", "systems", "--assembly", ThisAssembly);
        var text = output.ToString();

        Assert.Contains(
            "StraySystem's [UpdateAfter(typeof(GhostSystem))] does nothing: no GhostSystem is in this set",
            text,
            StringComparison.Ordinal
        );
    }

    /// <summary>The subtler one: a constraint that is real, and reaches across a phase boundary.</summary>
    [Fact]
    public async Task It_tells_a_cross_phase_constraint_apart_from_an_absent_one() {
        await RunAsync("doctor", "systems", "--assembly", ThisAssembly);
        var text = output.ToString();

        Assert.Contains(
            "BookkeepingSystem's [UpdateBefore(typeof(ScrapSystem))] does nothing: ScrapSystem is in "
            + "the Update phase and BookkeepingSystem is in EarlyUpdate",
            text,
            StringComparison.Ordinal
        );
    }

    /// <summary>Task #334, reported and not solved.</summary>
    [Fact]
    public async Task A_value_typed_dependency_is_broken_because_the_registry_keys_on_reference_types() {
        var code = await RunAsync("doctor", "systems", "--assembly", ThisAssembly);
        var text = output.ToString();

        Assert.Contains("broken  ClockSystem: asks for a GameTime, which is a value type", text, StringComparison.Ordinal);
        Assert.Equal((int)ExitCode.Failed, code);
    }

    [Fact]
    public async Task It_lists_what_each_system_needs() {
        await RunAsync("doctor", "systems", "--assembly", ThisAssembly);
        var text = output.ToString();

        Assert.Contains("ScrapSystem: needs Warehouse.", text, StringComparison.Ordinal);
        Assert.Contains("BookkeepingSystem: needs no services.", text, StringComparison.Ordinal);

        // It says what it cannot know rather than implying the frame is fine.
        Assert.Contains("is not knowable here", text, StringComparison.Ordinal);
    }

    /// <summary>A system in the assembly that nothing declares. Not an error — but not invisible.</summary>
    [Fact]
    public async Task It_names_a_system_that_the_declared_frame_does_not_contain() {
        await RunAsync("doctor", "systems", "--assembly", ThisAssembly);

        Assert.Contains(
            "check   HandRolledSystem: is a system and carries no [GameSystem]",
            output.ToString(),
            StringComparison.Ordinal
        );
    }

    /// <summary>
    ///     ⚠ The failure this command exists to prevent, in its own shape: an assembly that is not
    ///     there must not read as a clean frame.
    /// </summary>
    [Fact]
    public async Task An_assembly_that_is_not_there_is_broken_rather_than_skipped() {
        var absent = Path.Combine(Path.GetTempPath(), "no-such-game-" + Guid.NewGuid().ToString("N") + ".dll");
        var code = await RunAsync("doctor", "systems", "--assembly", absent);

        Assert.Contains("there is nothing at", output.ToString(), StringComparison.Ordinal);
        Assert.Equal((int)ExitCode.Failed, code);
    }

    [Fact]
    public async Task Naming_no_assembly_at_all_is_a_usage_error() {
        var code = await RunAsync("doctor", "systems");

        Assert.Contains("Name at least one built game assembly", output.ToString(), StringComparison.Ordinal);
        Assert.Equal((int)ExitCode.UsageError, code);
    }

    /// <summary>
    ///     The plan is the runner's own sort, so the two cannot disagree about an order. Asserted by
    ///     building the same systems and comparing, rather than by trusting the refactor.
    /// </summary>
    [Fact]
    public void The_planned_order_is_the_order_the_runner_would_run() {
        using var scrap = new ScrapSystem(new());
        using var haul = new HaulSystem(new());

        var built = SystemGraph.Build([haul, scrap]).InPhase(SystemPhase.Update).Select(node => node.Name);
        var planned = SystemGraph.Plan([typeof(HaulSystem), typeof(ScrapSystem)])
            .InPhase(SystemPhase.Update)
            .Select(placement => placement.Name);

        Assert.Equal(["ScrapSystem", "HaulSystem"], built);
        Assert.Equal(["ScrapSystem", "HaulSystem"], planned);
    }
}

/// <summary>A service a declared system can actually be given.</summary>
public sealed class Warehouse;

/// <summary>Runs first, and reaches across a phase boundary it cannot reach across.</summary>
[GameSystem]
[UpdateInGroup(SystemPhase.EarlyUpdate)]
[UpdateBefore(typeof(ScrapSystem))]
public sealed class BookkeepingSystem : SystemBase {
    /// <inheritdoc />
    public override JobHandle Update(in SystemContext context, JobHandle dependency) => dependency;
}

/// <summary>The one everything else in Update is arranged around.</summary>
[GameSystem]
[UpdateInGroup(SystemPhase.Update)]
public sealed class ScrapSystem(Warehouse warehouse) : SystemBase {
    /// <summary>The service it was given.</summary>
    public Warehouse Warehouse { get; } = warehouse;

    /// <inheritdoc />
    public override JobHandle Update(in SystemContext context, JobHandle dependency) => dependency;
}

/// <summary>Ordered after <see cref="ScrapSystem" />, and declared before it.</summary>
[GameSystem]
[UpdateInGroup(SystemPhase.Update)]
[UpdateAfter(typeof(ScrapSystem))]
public sealed class HaulSystem(Warehouse warehouse) : SystemBase {
    /// <summary>The service it was given.</summary>
    public Warehouse Warehouse { get; } = warehouse;

    /// <inheritdoc />
    public override JobHandle Update(in SystemContext context, JobHandle dependency) => dependency;
}

/// <summary>Ordered after a system that does not exist.</summary>
[GameSystem]
[UpdateInGroup(SystemPhase.LateUpdate)]
[UpdateAfter(typeof(GhostSystem))]
public sealed class StraySystem : SystemBase {
    /// <inheritdoc />
    public override JobHandle Update(in SystemContext context, JobHandle dependency) => dependency;
}

/// <summary>Named by <see cref="StraySystem" /> and never declared, which is the point of it.</summary>
public sealed class GhostSystem : SystemBase {
    /// <inheritdoc />
    public override JobHandle Update(in SystemContext context, JobHandle dependency) => dependency;
}

/// <summary>Asks for a struct, which <c>ServiceRegistry.Add&lt;T&gt;</c> cannot hold. See task #334.</summary>
[GameSystem]
[UpdateInGroup(SystemPhase.PostRender)]
public sealed class ClockSystem(GameTime time) : SystemBase {
    /// <summary>What it was given.</summary>
    public GameTime Time { get; } = time;

    /// <inheritdoc />
    public override JobHandle Update(in SystemContext context, JobHandle dependency) => dependency;
}

/// <summary>A system a project would add by hand, with no declaration for a tool to read.</summary>
public sealed class HandRolledSystem : SystemBase {
    /// <inheritdoc />
    public override JobHandle Update(in SystemContext context, JobHandle dependency) => dependency;
}
