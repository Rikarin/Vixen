// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Reflection;
using Vixen.Ecs.Systems;

namespace Vixen.Gameplay;

/// <summary>What a game composed: the modules it chose and everything they brought.</summary>
/// <remarks>
///     Immutable and produced once at start-up. A realm builds its system list out of
///     <see cref="Systems" />, its content build bakes <see cref="Tags" /> into the tag table, and a
///     diagnostic prints the whole thing so that "why does this game have an auction house" has an
///     answer that is not a search of the reference graph.
/// </remarks>
public sealed class GameplayComposition {
    internal GameplayComposition(
        IReadOnlyList<IGameplayModule> modules,
        AttributeLayout attributes,
        IReadOnlyList<string> tags,
        IReadOnlyList<DefinitionRegistration> definitions,
        IReadOnlyList<GameplaySystemRegistration> systems
    ) {
        Modules = modules;
        Attributes = attributes;
        Tags = tags;
        Definitions = definitions;
        Systems = systems;
    }

    /// <summary>The modules, in the order they were used.</summary>
    public IReadOnlyList<IGameplayModule> Modules { get; }

    /// <summary>Every stat every module declared, compiled.</summary>
    public AttributeLayout Attributes { get; }

    /// <summary>Every tag a module's own code needs, for the content build to bake in.</summary>
    public IReadOnlyList<string> Tags { get; }

    /// <summary>Every definition type, with the <c>!Tag</c> a file names it by.</summary>
    public IReadOnlyList<DefinitionRegistration> Definitions { get; }

    /// <summary>Every system, with the phase it runs in.</summary>
    public IReadOnlyList<GameplaySystemRegistration> Systems { get; }
}

/// <summary>Composes the gameplay modules a game has chosen.</summary>
/// <remarks>
///     <para>
///         Doc 28's <c>RealmConfig.Use&lt;T&gt;()</c>, living here rather than in
///         <c>Vixen.Live.Realm</c> because <c>Gameplay/</c> may not reference <c>Live/</c> and because
///         a single-player game composes modules too. A realm's own configuration forwards to this.
///     </para>
///     <para>
///         <b>What <see cref="Build" /> is for is the refusals.</b> Two modules declaring the same
///         stat with different bounds, two definition types sharing a <c>[DataContract]</c> alias, a
///         module whose dependency nobody used: each is a composition that compiles, runs, and is
///         wrong in a way nothing reports. Named here, with both modules in the message.
///     </para>
/// </remarks>
public sealed class GameplayConfig {
    readonly List<IGameplayModule> modules = [];
    readonly HashSet<Type> used = [];
    readonly AttributeLayoutBuilder attributes = new();
    readonly Dictionary<string, string> attributeOwners = new(StringComparer.Ordinal);
    readonly SortedSet<string> tags = new(StringComparer.Ordinal);
    readonly List<DefinitionRegistration> definitions = [];
    readonly Dictionary<Type, DefinitionRegistration> definitionsByType = [];
    readonly List<GameplaySystemRegistration> systems = [];
    readonly List<(string Module, Type Dependency)> dependencies = [];

    /// <summary>How many modules have been used.</summary>
    public int Count => modules.Count;

    /// <summary>Uses a module.</summary>
    /// <typeparam name="TModule">Its type.</typeparam>
    /// <returns>The config, so uses chain.</returns>
    /// <remarks>
    ///     The <c>new()</c> constraint is what keeps this AOT-clean: the compiler emits the
    ///     constructor call at the call site, so nothing activates a type by name and nothing has to
    ///     survive trimming.
    /// </remarks>
    public GameplayConfig Use<TModule>() where TModule : IGameplayModule, new() => Use(new TModule());

    /// <summary>Uses a module that has already been constructed and configured.</summary>
    /// <typeparam name="TModule">Its type.</typeparam>
    /// <param name="module">The module.</param>
    /// <param name="configure">Anything to set on it first.</param>
    /// <returns>The config, so uses chain.</returns>
    /// <exception cref="InvalidOperationException">The same module type is used twice.</exception>
    public GameplayConfig Use<TModule>(TModule module, Action<TModule>? configure = null)
        where TModule : IGameplayModule {
        ArgumentNullException.ThrowIfNull(module);

        if (!used.Add(module.GetType())) {
            throw new InvalidOperationException(
                $"{module.GetType().Name} is used twice. A module declares stats and definition types, "
                + "so using one twice is a composition that would have to refuse itself a moment later."
            );
        }

        configure?.Invoke(module);
        modules.Add(module);
        module.Configure(new(this, module.Name));

        return this;
    }

    /// <summary>Uses a module with no state, configuring it as it is made.</summary>
    /// <typeparam name="TModule">Its type.</typeparam>
    /// <param name="configure">Anything to set on it.</param>
    /// <returns>The config, so uses chain.</returns>
    public GameplayConfig Use<TModule>(Action<TModule> configure) where TModule : IGameplayModule, new() =>
        Use(new TModule(), configure);

    /// <summary>Checks the composition and produces it.</summary>
    /// <returns>The composition.</returns>
    /// <exception cref="InvalidOperationException">A module's dependency was not used.</exception>
    public GameplayComposition Build() {
        foreach (var (module, dependency) in dependencies) {
            if (!used.Contains(dependency)) {
                throw new InvalidOperationException(
                    $"{module} depends on {dependency.Name}, which this game did not use. Add "
                    + $".Use<{dependency.Name}>() before it, or stop using {module} — the composition "
                    + "will not pull it in for you, because declining a library is the point of there "
                    + "being twenty of them."
                );
            }
        }

        return new(
            [.. modules],
            attributes.Build(),
            [.. tags],
            [.. definitions],
            [.. systems]
        );
    }

    internal void DeclareAttribute(
        string module,
        string name,
        float @default,
        float minimum,
        float maximum,
        AttributeRounding rounding
    ) {
        if (attributeOwners.TryGetValue(name, out var owner)) {
            throw new InvalidOperationException(
                $"{module} and {owner} both declare the stat '{name}'. Two declarations mean two sets "
                + "of bounds and defaults for one number, and the one that loses is whichever ran "
                + "second."
            );
        }

        attributeOwners.Add(name, module);
        attributes.Add(name, @default, minimum, maximum, rounding);
    }

    internal void DeclareTag(string module, string name) {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _ = module;

        tags.Add(name);
    }

    internal void DeclareDefinition<TDefinition>(string module) where TDefinition : Definition {
        var type = typeof(TDefinition);

        var tag = TypeRegistry.TryGet(type, out var descriptor)
            ? descriptor.Alias
            : throw new InvalidOperationException(
                $"{type.Name} has no type descriptor, so no .vxdef can name it. Give it [DataContract], "
                + "which is what makes the !Tag resolvable."
            );

        // ⚠ Two *types* sharing an alias is not checked here, and that is not an omission:
        // TypeRegistry.Register already refuses it, from a module initializer, before any composition
        // exists to complain. What is left for this to catch is two modules each claiming the same
        // definition type — harmless to the content build and a sign that one of them is about to be
        // deleted along with a type the other needs.
        if (definitionsByType.TryGetValue(type, out var existing)) {
            throw new InvalidOperationException(
                $"{module} and {existing.Module} both declare {type.Name}. One of them owns it; the "
                + "other should depend on that module rather than redeclare its content."
            );
        }

        var registration = new DefinitionRegistration(module, type, tag);
        definitionsByType.Add(type, registration);
        definitions.Add(registration);
    }

    internal void DeclareSystem(string module, SystemPhase phase, Func<ISystem> create) {
        ArgumentNullException.ThrowIfNull(create);

        systems.Add(new(module, phase, create));
    }

    internal void DeclareDependency(string module, Type dependency) => dependencies.Add((module, dependency));
}
