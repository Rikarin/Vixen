// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Vixen.Ai;

/// <summary>One blackboard key an assembly declared.</summary>
/// <param name="Name">What the key is called. The name an asset and a sensor resolve.</param>
/// <param name="Type">What kind of value it holds.</param>
/// <param name="Origin">The assembly that declared it, so an unloaded project's keys can be forgotten.</param>
public readonly record struct BlackboardKeyDeclaration(string Name, BlackboardValueType Type, Assembly Origin);

/// <summary>One agent action an assembly declared.</summary>
/// <param name="Name">What the action is called. The name a compiled asset resolves to an index.</param>
/// <param name="Factory">
///     Builds the action from the layout the host settled on. ⚠ A layout rather than nothing, because
///     most actions take a <see cref="BlackboardKey" /> — and a key is an index into a layout that
///     does not exist until every assembly has declared its keys, so an action cannot be constructed
///     at declaration time.
/// </param>
/// <param name="StateSize">How many bytes of per-agent memory it needs. May be zero.</param>
/// <param name="Origin">The assembly that declared it, so an unloaded project's actions can be forgotten.</param>
public readonly record struct AgentActionDeclaration(
    string Name,
    Func<BlackboardLayout, IAgentAction> Factory,
    int StateSize,
    Assembly Origin
);

/// <summary>
///     What a project's assembly says its agents can do, readable by a host that never ran the
///     project's boot path.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why this exists.</b> An <see cref="IAgentAction" /> is a project's own type, so an
///         <see cref="AgentActionRegistry" /> could only ever be built by code the project ran —
///         which meant the editor's play mode, a headless determinism harness and a plugin could not
///         construct an <see cref="Ecs.AiSystem" /> at all, because they could not construct the
///         registry its constructor takes. Every other project-declared kind already had a
///         declarative route: <c>[GameSystem]</c> into <c>GameSystemRegistry</c>,
///         <c>SceneComponentRegistry.Declare</c>, <c>SerializerRegistry.Register</c>. This is the
///         same shape for agent actions.
///     </para>
///     <para>
///         ⚠ <b><c>[GameSystem]</c>'s route is a generator emitting a <c>[ModuleInitializer]</c> per
///         declaring assembly, not a reflection scan</b> — which is what survives trimming and what
///         lets the editor read a project's frame without running it. So the way in here is a call
///         from the project's own <c>[ModuleInitializer]</c>, which runs when the assembly is loaded
///         and independently of anything calling <c>Game.OnInitialise</c>.
///     </para>
///     <para>
///         <b>The blackboard layout is declared here too, and has to be.</b> It is the second half of
///         <see cref="Ecs.AiSystem" />'s constructor and it had the same problem — built by a
///         <see cref="BlackboardLayoutBuilder" /> in the game's own boot method and nowhere else.
///         Declaring the keys separately is also what makes the two-phase build below possible: every
///         key is known before any action is constructed.
///     </para>
///     <para>
///         ⚠ <b>Ordered by name, never by declaration order.</b> Which assembly's module initialiser
///         runs first is a property of the run, so a registry built in declaration order would hand
///         out different indices in two runs of the same program. <c>SceneComponentRegistry</c>
///         learned this the expensive way — five dump tests that passed locally and failed on all
///         three CI runners at once.
///     </para>
/// </remarks>
public static class AgentDeclarations {
    static readonly ConcurrentDictionary<string, BlackboardKeyDeclaration> DeclaredKeys =
        new(StringComparer.Ordinal);

    static readonly ConcurrentDictionary<string, AgentActionDeclaration> DeclaredActions =
        new(StringComparer.Ordinal);

    /// <summary>Every declared blackboard key, ordered by name.</summary>
    public static IReadOnlyList<BlackboardKeyDeclaration> Keys {
        get {
            var keys = DeclaredKeys.Values.ToArray();

            Array.Sort(keys, static (left, right) => string.CompareOrdinal(left.Name, right.Name));

            return keys;
        }
    }

    /// <summary>Every declared action, ordered by name.</summary>
    public static IReadOnlyList<AgentActionDeclaration> Actions {
        get {
            var actions = DeclaredActions.Values.ToArray();

            Array.Sort(actions, static (left, right) => string.CompareOrdinal(left.Name, right.Name));

            return actions;
        }
    }

    /// <summary>Declares a blackboard key on behalf of the calling assembly.</summary>
    /// <param name="name">What the key is called.</param>
    /// <param name="type">What kind of value it holds.</param>
    /// <returns>Whether this declaration is the one in force; false when the name was already taken.</returns>
    /// <exception cref="ArgumentException"><paramref name="name" /> is null or empty.</exception>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool DeclareKey(string name, BlackboardValueType type) =>
        DeclareKey(name, type, Assembly.GetCallingAssembly());

    /// <summary>Declares a blackboard key, naming the assembly it belongs to.</summary>
    /// <param name="name">What the key is called.</param>
    /// <param name="type">What kind of value it holds.</param>
    /// <param name="origin">The assembly the declaration belongs to, for <see cref="Evict" />.</param>
    /// <returns>Whether this declaration is the one in force; false when the name was already taken.</returns>
    /// <exception cref="ArgumentException"><paramref name="name" /> is null or empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="origin" /> is null.</exception>
    public static bool DeclareKey(string name, BlackboardValueType type, Assembly origin) {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(origin);

        return DeclaredKeys.TryAdd(name, new(name, type, origin));
    }

    /// <summary>Declares an action on behalf of the calling assembly.</summary>
    /// <param name="name">What the action is called.</param>
    /// <param name="factory">Builds it, given the layout the host settled on.</param>
    /// <param name="stateSize">How many bytes of per-agent memory it needs.</param>
    /// <returns>Whether this declaration is the one in force; false when the name was already taken.</returns>
    /// <exception cref="ArgumentException"><paramref name="name" /> is null or empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="factory" /> is null.</exception>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool DeclareAction(string name, Func<BlackboardLayout, IAgentAction> factory, int stateSize = 0) =>
        DeclareAction(name, factory, stateSize, Assembly.GetCallingAssembly());

    /// <summary>Declares an action, naming the assembly it belongs to.</summary>
    /// <param name="name">What the action is called.</param>
    /// <param name="factory">Builds it, given the layout the host settled on.</param>
    /// <param name="stateSize">How many bytes of per-agent memory it needs.</param>
    /// <param name="origin">The assembly the declaration belongs to, for <see cref="Evict" />.</param>
    /// <returns>Whether this declaration is the one in force; false when the name was already taken.</returns>
    /// <exception cref="ArgumentException"><paramref name="name" /> is null or empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="factory" /> or <paramref name="origin" /> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="stateSize" /> is negative.</exception>
    /// <remarks>
    ///     ⚠ <b>First declaration wins, silently, exactly as <c>GameSystemRegistry.Declare</c> does.</b>
    ///     That is what an assembly loaded twice into two contexts needs — the alternative doubles
    ///     every action in the registry. The return value is how a test or a tool can see that a
    ///     second declaration lost; a module initialiser normally ignores it.
    /// </remarks>
    public static bool DeclareAction(
        string name,
        Func<BlackboardLayout, IAgentAction> factory,
        int stateSize,
        Assembly origin
    ) {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentOutOfRangeException.ThrowIfNegative(stateSize);

        return DeclaredActions.TryAdd(name, new(name, factory, stateSize, origin));
    }

    /// <summary>Builds the layout every declared key is in.</summary>
    /// <returns>The layout, with the keys in name order.</returns>
    public static BlackboardLayout BuildLayout() {
        var builder = new BlackboardLayoutBuilder();

        foreach (var key in Keys) {
            builder.Add(key.Name, key.Type);
        }

        return builder.Build();
    }

    /// <summary>Builds a registry holding every declared action.</summary>
    /// <param name="layout">The layout the actions resolve their keys against.</param>
    /// <returns>The registry, with the actions in name order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="layout" /> is null.</exception>
    /// <exception cref="InvalidOperationException">A factory returned null.</exception>
    /// <remarks>
    ///     A registry of its own each time, and not a cached one: an
    ///     <see cref="AgentActionRegistry" /> is read-only once built and an action instance is shared
    ///     by every agent running it, so two worlds wanting two sets of action objects is a legitimate
    ///     thing to ask for and a shared instance would be a surprise.
    /// </remarks>
    public static AgentActionRegistry BuildRegistry(BlackboardLayout layout) {
        ArgumentNullException.ThrowIfNull(layout);

        var registry = new AgentActionRegistry();

        foreach (var declaration in Actions) {
            var action = declaration.Factory(layout)
                ?? throw new InvalidOperationException(
                    $"The factory declared for the action '{declaration.Name}' returned null."
                );

            registry.Register(declaration.Name, action, declaration.StateSize);
        }

        return registry;
    }

    /// <summary>Forgets everything an assembly declared.</summary>
    /// <param name="assembly">The assembly being unloaded.</param>
    /// <returns>How many declarations were forgotten.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="assembly" /> is null.</exception>
    /// <remarks>
    ///     ⚠ <b>Only for a collectible context, and it is not optional there.</b> An action
    ///     declaration holds a delegate over the project's code, so one left behind keeps the
    ///     unloaded context alive and the next Play builds its agents out of the previous build's
    ///     assembly — the same failure <c>GameSystemRegistry.Evict</c> exists for.
    /// </remarks>
    public static int Evict(Assembly assembly) {
        ArgumentNullException.ThrowIfNull(assembly);

        var evicted = 0;

        foreach (var (name, declaration) in DeclaredKeys.ToArray()) {
            if (declaration.Origin == assembly && DeclaredKeys.TryRemove(name, out _)) {
                evicted++;
            }
        }

        foreach (var (name, declaration) in DeclaredActions.ToArray()) {
            if (declaration.Origin == assembly && DeclaredActions.TryRemove(name, out _)) {
                evicted++;
            }
        }

        return evicted;
    }
}
