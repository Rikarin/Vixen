// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Threading;
using Vixen.Ecs.Systems;
using Vixen.Engine.Frames;
using Xunit;

namespace Vixen.Engine.Tests;

/// <summary>
///     What a project declaring its frame buys, and what it costs when a service is not there.
/// </summary>
/// <remarks>
///     ⚠ <b>The declarations these read were made by the generator, not by this file.</b> The systems
///     below carry <c>[GameSystem]</c>, <c>Vixen.Engine.Generators</c> saw them at compile time, and
///     the <c>[ModuleInitializer]</c> it emitted for this assembly ran before the first test did. A
///     test that had to call <c>Declare</c> by hand would be proving the registry works and saying
///     nothing about whether a project's frame ever reaches it.
/// </remarks>
public sealed class GameSystemTests {
    [Fact]
    public void TheGeneratorDeclaresTheSystemsThisAssemblyMarked() {
        var declared = Declarations();

        Assert.Contains(declared, declaration => declaration.SystemType == typeof(NeedsNothing));
        Assert.Contains(declared, declaration => declaration.SystemType == typeof(NeedsOne));
        Assert.Contains(declared, declaration => declaration.SystemType == typeof(NeedsTwo));
    }

    /// <summary>
    ///     The constructor is the whole of how a system says what it needs — no second list to keep
    ///     in step with it.
    /// </summary>
    [Fact]
    public void AConstructorsParametersAreTheServicesItAsksFor() {
        var two = Single(typeof(NeedsTwo));

        Assert.Equal([typeof(Ledger), typeof(IClerk)], two.Requires);

        // ⚠ The static parameter type, not the instance's. `NeedsTwo` asks for an `IClerk`; what is
        // registered is a `Clerk`. Keying on the implementation would make a system that took an
        // interface unsatisfiable, which is the ordinary way to write one.
        Assert.Equal(typeof(IClerk), two.Requires[1]);

        Assert.Empty(Single(typeof(NeedsNothing)).Requires);
    }

    [Fact]
    public void ASystemWhoseServicesAreAllThereIsBuiltAndAdded() {
        var services = new ServiceRegistry();
        services.Add(new Ledger());
        services.Add<IClerk>(new Clerk());

        using var loop = new EngineLoop();
        var frame = loop.AddDeclaredSystems(services, Ours);

        Assert.Equal(["NeedsNothing", "NeedsOne", "NeedsTwo"], frame.Running);
        Assert.Empty(frame.Missing);

        // Added to the graph, not merely constructed: the loop is what proves it.
        loop.Frame(TimeSpan.FromMilliseconds(16));

        Assert.True(NeedsTwo.Ran);
    }

    /// <summary>
    ///     ⚠ <b>The rule the whole feature is built on.</b> A declared system whose service nobody
    ///     provided is named. Silently skipping it moves the failure out of the report, where it can
    ///     be read, and into the game, where it presents as a script that stopped working.
    /// </summary>
    [Fact]
    public void ASystemWhoseServiceIsAbsentIsNamedRatherThanSkipped() {
        var services = new ServiceRegistry();
        services.Add(new Ledger());

        using var loop = new EngineLoop();
        var frame = loop.AddDeclaredSystems(services, Ours);

        // The two that only wanted a Ledger ran; the one that also wanted an IClerk did not.
        Assert.Equal(["NeedsNothing", "NeedsOne"], frame.Running);

        var absent = Assert.Single(frame.Missing);
        Assert.Contains("NeedsTwo", absent, StringComparison.Ordinal);
        Assert.Contains(nameof(IClerk), absent, StringComparison.Ordinal);
    }

    [Fact]
    public void NothingProvidedMeansEverythingWithARequirementIsNamed() {
        using var loop = new EngineLoop();
        var frame = loop.AddDeclaredSystems(new ServiceRegistry(), Ours);

        Assert.Equal(["NeedsNothing"], frame.Running);
        Assert.Equal(2, frame.Missing.Count);
    }

    /// <summary>
    ///     One system that cannot stand itself up must not take the others down with it — the rule
    ///     <c>PlayModeController.Refused</c> already follows.
    /// </summary>
    [Fact]
    public void AConstructorThatThrowsIsNamedAndTheRestStillRun() {
        var services = new ServiceRegistry();
        services.Add(new Ledger());
        services.Add<IClerk>(new Clerk());
        services.Add(new Tantrum());

        using var loop = new EngineLoop();
        var frame = loop.AddDeclaredSystems(services, declaration => Ours(declaration) || Throws(declaration));

        Assert.Contains("NeedsTwo", frame.Running);

        var refused = Assert.Single(frame.Missing);
        Assert.Contains(nameof(ThrowsOnConstruction), refused, StringComparison.Ordinal);
    }

    /// <summary>
    ///     ⚠ <b>A dependency that is a value, which until <c>ServiceRegistry.AddValue</c> was
    ///     declarable and unsatisfiable.</b> The generator never had a problem with it — it emits
    ///     <c>(Ticket) services[0]</c> for a struct parameter exactly as it does for a class, and
    ///     reports no diagnostic — so <c>NeedsAValue</c> compiled clean, declared itself, and then
    ///     sat in <c>Missing</c> for the life of every process, because <c>Add&lt;T&gt;</c> is
    ///     <c>where T : class</c> and nothing else writes to the table.
    /// </summary>
    [Fact]
    public void ASystemWhoseDependencyIsAValueIsBuiltFromTheValueThatWasRegistered() {
        var services = new ServiceRegistry();
        services.AddValue(new Ticket(7));

        using var loop = new EngineLoop();
        var frame = loop.AddDeclaredSystems(services, Valued);

        Assert.Equal(["NeedsAValue"], frame.Running);
        Assert.Empty(frame.Missing);

        // The box the registry made, unboxed by the emitted cast. A registry that keyed on
        // anything but the static parameter type resolves nothing and this reads as Missing.
        Assert.Equal(new Ticket(7), NeedsAValue.Built);

        Assert.True(services.TryGetValue<Ticket>(out var read));
        Assert.Equal(new Ticket(7), read);
    }

    /// <summary>
    ///     ⚠ <b>And the rule survives the new shape.</b> A value nobody registered is named in the
    ///     report, exactly as an absent service is — the alternative is a system that silently is
    ///     not in the frame, which is what <c>FrameActivation.Missing</c> exists to make impossible.
    /// </summary>
    [Fact]
    public void AValueNobodyRegisteredIsNamedLikeAnAbsentService() {
        using var loop = new EngineLoop();
        var frame = loop.AddDeclaredSystems(new ServiceRegistry(), Valued);

        Assert.Empty(frame.Running);

        var absent = Assert.Single(frame.Missing);
        Assert.Contains("NeedsAValue", absent, StringComparison.Ordinal);
        Assert.Contains(nameof(Ticket), absent, StringComparison.Ordinal);
    }

    /// <summary>
    ///     <see langword="default" /> is a value like any other, and a registry that treated it as
    ///     "not registered" would put a system asking for a zeroed handle in <c>Missing</c> forever.
    /// </summary>
    [Fact]
    public void TheDefaultValueIsRegisteredRatherThanAbsent() {
        var services = new ServiceRegistry();
        services.AddValue(default(Ticket));

        Assert.True(services.TryGetValue<Ticket>(out var read));
        Assert.Equal(default, read);

        using var loop = new EngineLoop();

        Assert.Empty(loop.AddDeclaredSystems(services, Valued).Missing);
    }

    [Fact]
    public void AValueRegisteredTwiceIsRefusedRatherThanReplaced() {
        var services = new ServiceRegistry();
        services.AddValue(new Ticket(1));

        Assert.Throws<ArgumentException>(() => services.AddValue(new Ticket(2)));
    }

    /// <summary>
    ///     The imperative path is untouched: a hand-constructed system and a declared one are the
    ///     same thing to the runner, and a game may use either or both.
    /// </summary>
    [Fact]
    public void AHandConstructedSystemStillWorksBesideTheDeclaredOnes() {
        var services = new ServiceRegistry();
        services.Add(new Ledger());

        using var loop = new EngineLoop();
        var counter = new CountingSystem();

        loop.Add(counter);
        loop.AddDeclaredSystems(services, Ours);
        loop.Frame(TimeSpan.FromMilliseconds(16));

        Assert.Equal(1, counter.Ran);
    }

    [Fact]
    public void DeclaringTheSameSystemTwiceDoesNotDoubleTheFrame() {
        var before = Declarations().Count;

        GameSystemRegistry.Declare(typeof(NeedsNothing), [], static _ => new NeedsNothing());

        Assert.Equal(before, Declarations().Count);
    }

    static IReadOnlyList<GameSystemDeclaration> Declarations() => GameSystemRegistry.Declared;

    static GameSystemDeclaration Single(Type system) =>
        Assert.Single(Declarations(), declaration => declaration.SystemType == system);

    /// <summary>The three well-behaved ones, so a test is not at the mercy of the others in here.</summary>
    static bool Ours(GameSystemDeclaration declaration) =>
        declaration.SystemType == typeof(NeedsNothing)
        || declaration.SystemType == typeof(NeedsOne)
        || declaration.SystemType == typeof(NeedsTwo);

    static bool Valued(GameSystemDeclaration declaration) =>
        declaration.SystemType == typeof(NeedsAValue);

    static bool Throws(GameSystemDeclaration declaration) =>
        declaration.SystemType == typeof(ThrowsOnConstruction);

    sealed class CountingSystem : SystemBase {
        public int Ran { get; private set; }

        public override JobHandle Update(in SystemContext context, JobHandle dependency) {
            Ran++;
            return dependency;
        }
    }
}

/// <summary>A service a declared system asks for by its own type.</summary>
public sealed class Ledger;

/// <summary>A service a declared system asks for by an interface.</summary>
public interface IClerk;

/// <summary>Its implementation, which is deliberately not what the system names.</summary>
public sealed class Clerk : IClerk;

/// <summary>A service nothing but the throwing system wants.</summary>
public sealed class Tantrum;

/// <summary>A dependency that is a value — the shape `ServiceRegistry.AddValue` exists for.</summary>
public readonly record struct Ticket(int Number);

[GameSystem]
public sealed class NeedsNothing : SystemBase {
    public override JobHandle Update(in SystemContext context, JobHandle dependency) => dependency;
}

[GameSystem]
public sealed class NeedsOne(Ledger ledger) : SystemBase {
    public Ledger Ledger { get; } = ledger;

    public override JobHandle Update(in SystemContext context, JobHandle dependency) => dependency;
}

[GameSystem]
public sealed class NeedsTwo(Ledger ledger, IClerk clerk) : SystemBase {
    public static bool Ran { get; private set; }

    public Ledger Ledger { get; } = ledger;

    public IClerk Clerk { get; } = clerk;

    public override JobHandle Update(in SystemContext context, JobHandle dependency) {
        Ran = true;
        return dependency;
    }
}

[GameSystem]
public sealed class NeedsAValue : SystemBase {
    public NeedsAValue(Ticket ticket) => Built = ticket;

    /// <summary>What the last one built was handed. The unboxing is what is under test.</summary>
    public static Ticket Built { get; private set; }

    public override JobHandle Update(in SystemContext context, JobHandle dependency) => dependency;
}

[GameSystem]
public sealed class ThrowsOnConstruction : SystemBase {
    public ThrowsOnConstruction(Tantrum tantrum) =>
        throw new InvalidOperationException("this system's native library is not on this machine");

    public override JobHandle Update(in SystemContext context, JobHandle dependency) => dependency;
}
