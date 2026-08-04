---
title: Gameplay modules
slug: gameplay/modules
kind: guide
area: Gameplay
summary: What a game composes — stats, tags, definition types and systems — declared explicitly, with nothing scanned.
api: [T:Vixen.Gameplay.IGameplayModule, T:Vixen.Gameplay.GameplayModuleBuilder, T:Vixen.Gameplay.GameplayConfig, T:Vixen.Gameplay.GameplayComposition, T:Vixen.Gameplay.GameplaySystemRegistration, T:Vixen.Gameplay.DefinitionRegistration, T:Vixen.Gameplay.GameplayKernelModule]
tags: [gameplay, modules, composition, aot]
since: 0.1
status: preview
related: [gameplay/definitions, gameplay/attributes, gameplay/tags]
---

## What it is

An `IGameplayModule` is a unit of gameplay a game chooses to have: items, or combat, or its own guild
ranks. It declares its stats, the tags its own code needs, its definition types and its systems, and
a `GameplayConfig` composes the ones a game used into a `GameplayComposition`.

The engine's modules and a game's own are the same kind of object — doc 16's `NetworkModule`
discipline one level up: build the built-ins out of the primitive users get, so that the extension
point is the one the engine itself uses and therefore the one that works.

## What it is for

Making "which gameplay does this game have" a question with a written answer. A realm builds its
system list from the composition, a content build bakes its tags, and a diagnostic prints the whole
thing — so "why does this game have an auction house" is not a search of the reference graph.

It is also what makes declining a library visible. Doc 28 ships twenty-odd packages precisely so an
extraction shooter does not carry a threat table; a composition that silently pulled in a dependency
would undo that, so a missing dependency is named and refused rather than satisfied.

## Using it

`Use<TModule>()` has a `new()` constraint, so the compiler emits the constructor call at the call
site. Nothing is activated by name and nothing has to survive trimming — an assembly scan reads
metadata a trimmed publish has already deleted, and produces a game that works in development and
ships with no quests.

`Build` is where the composition is checked. Two modules declaring the same stat, two modules
claiming one definition type, and a module whose dependency nobody used are each a composition that
compiles, runs, and is wrong in a way nothing else reports.

⚠ **A definition type needs `[DataContract]`**, because that alias is the `!Tag` a `.vxdef` names it
by. Declaring one without a descriptor is refused here, which is the only place that knows the type
was meant to be authorable at all. Two *types* sharing an alias is refused earlier and harder, by
`TypeRegistry` itself, from a module initializer.

⚠ **Declare a tag your C# asks about.** Most tags reach the table because a definition mentions
them; a tag only code knows — `State.InCombat` — is absent without `builder.Tag(…)`, and every rule
mentioning it then resolves to an empty range and quietly matches nothing.

## Examples

A module:

```csharp compile
using Vixen.Core.Threading;
using Vixen.Ecs.Systems;
using Vixen.Gameplay;

public sealed class CombatModule : IGameplayModule {
    public string Name => "Combat";

    public void Configure(GameplayModuleBuilder builder) =>
        builder
            .DependsOn<GameplayKernelModule>()
            .Attribute("Power", 100f)
            .Attribute("Health", 1000f, minimum: 0f)
            .Attribute("CritChance", 0.05f, minimum: 0f, maximum: 1f)
            .Tag("State.InCombat")
            .System(SystemPhase.Update, static () => new ThreatSystem());
}

public sealed class ThreatSystem : SystemBase {
    public override JobHandle Update(in SystemContext context, JobHandle dependency) => dependency;
}
```

Composing a game, and using what came out:

```csharp compile
using Vixen.Gameplay;

public sealed class InventoryModule : IGameplayModule {
    public int BagSlots { get; set; } = 4;

    public string Name => "Inventory";

    public void Configure(GameplayModuleBuilder builder) => builder.Tag("Item.Soulbound");
}

static class Composition {
    public static GameplayComposition ForThisGame() =>
        new GameplayConfig()
            .Use<GameplayKernelModule>()
            .Use<InventoryModule>(module => module.BagSlots = 5)
            .Build();

    // The stats every module declared, compiled into one layout, is what a subject is made over.
    public static GameplaySubject NewCharacter(GameplayComposition composition) =>
        new(composition.Attributes);

    // And the tags a module's own code needs go into the catalog beside the ones content mentions.
    public static DefinitionCatalog Bake(GameplayComposition composition) {
        var builder = new DefinitionCatalogBuilder();

        foreach (var tag in composition.Tags) {
            builder.AddTag(tag);
        }

        return builder.Build();
    }
}
```

## See also

- [Definitions](gameplay/definitions) — what a module's definition types become in a `.vxdef`.
- [Attributes](gameplay/attributes) — what `Attribute(…)` builds up.
- [Gameplay tags](gameplay/tags) — where `Tag(…)` ends up.
