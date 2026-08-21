// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Vixen.Ecs.Systems;

namespace Vixen.Engine.Frames;

/// <summary>Says a system belongs to this project's frame, so anything that runs the project runs it.</summary>
/// <remarks>
///     <para>
///         <b>The thing a project had no way to say.</b> A game's system set was imperative code in
///         its own <c>Game.OnInitialise</c> — <c>loop.Add(new NavigationSystem(crowd))</c> and a
///         dozen more — so the set existed only as the side effect of running the game's boot path.
///         Nothing could read it: not the editor, which is why Play mode could name a project's
///         systems by reflection and not run any of them, and not a test, and not a tool. This
///         attribute is the declaration that was missing, and it is deliberately the only new thing
///         a project has to write.
///     </para>
///     <para>
///         ⚠ <b>It carries nothing, because everything it might carry is already said elsewhere.</b>
///         A system's phase is <c>[UpdateInGroup]</c> and its order is <c>[UpdateBefore]</c> /
///         <c>[UpdateAfter]</c>; what it reads and writes is <c>[Reads]</c> / <c>[Writes]</c> or
///         <c>IDeclaredAccess</c>. And what it *needs* is its constructor — see
///         <see cref="GameSystemDeclaration.Requires" />, which is the parameter list and nothing
///         more. An attribute that repeated any of those would be a second opinion that can go stale.
///     </para>
///     <para>
///         ⚠ <b>Opt-in, and the engine's own systems do not carry it.</b> A system in
///         <c>Vixen.Rendering</c> or <c>Vixen.Physics</c> is added by whatever built the service it
///         runs against — the app host, or an <c>IPlaySystems</c> contribution in the editor —
///         because that owner is the only thing that knows the service's lifetime. This is for the
///         half the *project* owns.
///     </para>
///     <para>
///         <b>Additive.</b> Nothing stops a project from carrying on constructing its systems by
///         hand, and a declared system and a hand-constructed one are the same thing to
///         <c>SystemGraph</c>. What a project must not do is both for one system, because nothing
///         dedupes and the frame would run it twice — which includes calling
///         <see cref="GameSystems.AddDeclaredSystems" /> from its own <c>OnInitialise</c>, since
///         <c>VixenApplication.Initialise</c> already calls it as soon as that returns.
///     </para>
/// </remarks>
/// <example>
///     <code language="csharp" no-compile="a fragment; ScrapSystem and Warehouse are the project's own">
///     [GameSystem]
///     [UpdateInGroup(SystemPhase.Update)]
///     public sealed class ScrapSystem(Warehouse warehouse) : SystemBase { … }
///
///     // In Game.OnInitialise: the service, not the system. The host adds the system as soon as
///     // OnInitialise returns, and the editor's play mode does the same against its session.
///     Services.Registry.Add(new Warehouse());
///     </code>
/// </example>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class GameSystemAttribute : Attribute;

/// <summary>One system a project declared: what it is, what it needs, and how to build it.</summary>
/// <remarks>
///     ⚠ <b>The factory is emitted, not reflected.</b> <c>GameSystemGenerator</c> writes a lambda
///     that calls the constructor with the arguments cast to their declared types, so there is no
///     <c>ConstructorInfo.Invoke</c> anywhere in this path. That is the same reason
///     <c>ServiceRegistry</c> is not a DI container: under NativeAOT the reflective version does not
///     work at all, and the emitted one is shorter as well as faster.
/// </remarks>
public sealed class GameSystemDeclaration {
    readonly Func<object[], ISystem> factory;

    /// <summary>The system's type.</summary>
    public Type SystemType { get; }

    /// <summary>Its short name, which is what a report calls it.</summary>
    public string Name => SystemType.Name;

    /// <summary>The services its constructor takes, in order, by the type it declared them as.</summary>
    /// <remarks>
    ///     ⚠ <b>The static parameter type is the key, not the instance's type.</b> A system taking an
    ///     <c>ITerrainScene</c> is asking for whatever was registered <em>as</em> an
    ///     <c>ITerrainScene</c> — which is what <c>ServiceRegistry.Add&lt;T&gt;</c> and
    ///     <c>PlaySession.Provide&lt;T&gt;</c> both already key on, so the three agree without any of
    ///     them being told about the others.
    /// </remarks>
    public IReadOnlyList<Type> Requires { get; }

    /// <summary>Declares a system. Called by generated code.</summary>
    /// <param name="systemType">The system's type.</param>
    /// <param name="requires">Its constructor's parameter types, in order.</param>
    /// <param name="factory">Builds it from services resolved in that order.</param>
    public GameSystemDeclaration(Type systemType, Type[] requires, Func<object[], ISystem> factory) {
        ArgumentNullException.ThrowIfNull(systemType);
        ArgumentNullException.ThrowIfNull(requires);
        ArgumentNullException.ThrowIfNull(factory);

        SystemType = systemType;
        Requires = requires;
        this.factory = factory;
    }

    /// <summary>Builds the system, if everything it needs is there.</summary>
    /// <param name="services">Where to look the services up.</param>
    /// <param name="system">The system, if it could be built.</param>
    /// <param name="missing">The first service that was not there, if it could not.</param>
    /// <returns>Whether it was built.</returns>
    /// <remarks>
    ///     ⚠ <b><paramref name="missing" /> is an answer, not an error.</b> A declared system whose
    ///     service nobody provided is a real and ordinary situation — the editor has no
    ///     <c>PhysicsScene</c> until something makes one — and the rule this whole feature is built
    ///     on is that such a system is *named* rather than quietly skipped. Throwing instead would
    ///     make one absent service a Play button that does not work.
    /// </remarks>
    public bool TryCreate(
        IServiceProvider services,
        [NotNullWhen(true)] out ISystem? system,
        [NotNullWhen(false)] out Type? missing
    ) {
        ArgumentNullException.ThrowIfNull(services);

        var arguments = Requires.Count == 0 ? [] : new object[Requires.Count];

        for (var index = 0; index < Requires.Count; index++) {
            if (services.GetService(Requires[index]) is not { } service) {
                system = null;
                missing = Requires[index];

                return false;
            }

            arguments[index] = service;
        }

        system = factory(arguments);
        missing = null;

        return true;
    }
}

/// <summary>Every system the loaded assemblies declared with <see cref="GameSystemAttribute" />.</summary>
/// <remarks>
///     <para>
///         <b>Filled at compile time and read at run time, exactly as <c>SceneBehaviorRegistry</c>
///         is.</b> <c>GameSystemGenerator</c> emits one <c>[ModuleInitializer]</c> per assembly that
///         declares anything, so the set is whatever a generator saw in the source — it survives
///         trimming, and it is the same set in the editor and in a shipped game.
///     </para>
///     <para>
///         ⚠ <b>Which is also why the editor can read a project's frame without running the
///         project.</b> <c>ProjectAssemblies.Load</c> runs the module constructor of the assembly it
///         loads, so the declarations are here the moment the project's code is loaded — before, and
///         independently of, anything calling <c>Game.OnInitialise</c>.
///     </para>
/// </remarks>
public static class GameSystemRegistry {
    static readonly ConcurrentDictionary<Type, GameSystemDeclaration> ByType = new();

    /// <summary>Every declared system, ordered by type name so a report is stable.</summary>
    public static IReadOnlyList<GameSystemDeclaration> Declared =>
        ByType.Values.OrderBy(declaration => declaration.SystemType.FullName, StringComparer.Ordinal).ToArray();

    /// <summary>Declares a system. Called by generated code.</summary>
    /// <param name="systemType">The system's type.</param>
    /// <param name="requires">Its constructor's parameter types, in order.</param>
    /// <param name="factory">Builds it from services resolved in that order.</param>
    /// <remarks>
    ///     Idempotent for the same type: a type declared twice — which an assembly loaded twice into
    ///     two contexts produces — keeps the first declaration rather than doubling the frame.
    ///     Annotating the system is the ordinary way in; this stays public because generated code
    ///     lives in the declaring assembly, and for a test that wants one declared now.
    /// </remarks>
    public static void Declare(Type systemType, Type[] requires, Func<object[], ISystem> factory) {
        var declaration = new GameSystemDeclaration(systemType, requires, factory);

        ByType.TryAdd(declaration.SystemType, declaration);
    }

    /// <summary>Forgets every system an assembly declared.</summary>
    /// <param name="assembly">The assembly being unloaded.</param>
    /// <returns>How many were forgotten.</returns>
    /// <remarks>
    ///     ⚠ <b>Only for a collectible context.</b> A declaration left behind names a type in an
    ///     unloaded context and holds a delegate over it, so the context never collects and the next
    ///     Play builds a system out of the previous build's code. The editor calls this from
    ///     <c>ProjectAssemblies.Unload</c>, beside the four registries that already needed it;
    ///     nothing else should.
    /// </remarks>
    public static int Evict(Assembly assembly) {
        ArgumentNullException.ThrowIfNull(assembly);

        var evicted = 0;

        foreach (var (type, _) in ByType.ToArray()) {
            if (type.Assembly == assembly && ByType.TryRemove(type, out _)) {
                evicted++;
            }
        }

        return evicted;
    }
}

/// <summary>What <see cref="GameSystems.AddDeclaredSystems" /> did, and what it could not do.</summary>
/// <param name="Running">The systems that were added, by name.</param>
/// <param name="Missing">
///     The declared systems that were not, one readable line each. ⚠ <b>Read this out.</b> A frame
///     that is missing a system is a game whose scrap never spawns or whose crowd never moves, and a
///     caller that dropped this on the floor turns an unregistered service into a gameplay bug.
/// </param>
public readonly record struct FrameActivation(IReadOnlyList<string> Running, IReadOnlyList<string> Missing);

/// <summary>Runs the systems a project declared.</summary>
public static class GameSystems {
    /// <summary>Adds every declared system whose services are all available, and names the rest.</summary>
    /// <param name="loop">The graph to add them to.</param>
    /// <param name="services">Where a system's constructor parameters are looked up.</param>
    /// <param name="where">
    ///     Which declarations to consider, or <see langword="null" /> for all of them. The editor
    ///     passes the project's own assembly, because an engine extension's systems are that
    ///     extension's business — see <see cref="GameSystemAttribute" />.
    /// </param>
    /// <returns>What ran and what did not.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Order is not decided here.</b> The systems are added in a stable name order and
    ///         <c>SystemGraph</c> sorts them by their <c>[UpdateInGroup]</c> phase and their
    ///         <c>[UpdateBefore]</c> / <c>[UpdateAfter]</c> edges, exactly as it does for a system
    ///         added by hand. A declared system and an imperative one are the same thing to the
    ///         runner, which is the property that makes this additive.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A constructor that throws is named, not propagated.</b> One system that cannot
    ///         stand itself up must not take the other nine down with it — the same rule
    ///         <c>PlayModeController.Refused</c> follows, and for the same reason: a machine missing
    ///         one native library should lose one system, not the frame.
    ///     </para>
    /// </remarks>
    public static FrameActivation AddDeclaredSystems(
        this EngineLoop loop,
        IServiceProvider services,
        Func<GameSystemDeclaration, bool>? where = null
    ) {
        ArgumentNullException.ThrowIfNull(loop);
        ArgumentNullException.ThrowIfNull(services);

        List<string> running = [];
        List<string> missing = [];

        foreach (var declaration in GameSystemRegistry.Declared) {
            if (where is not null && !where(declaration)) {
                continue;
            }

            ISystem? system;
            Type? absent;

            try {
                if (!declaration.TryCreate(services, out system, out absent)) {
                    missing.Add($"{declaration.Name} — nothing provides a {absent.Name}");
                    continue;
                }
            } catch (Exception failure) {
                missing.Add($"{declaration.Name} — its constructor threw: {failure.Message}");
                continue;
            }

            loop.Add(system);
            running.Add(declaration.Name);
        }

        return new(running, missing);
    }
}
