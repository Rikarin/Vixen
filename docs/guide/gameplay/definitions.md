---
title: Definitions and the content walk
slug: gameplay/definitions
kind: guide
area: Gameplay
summary: Authored content addressed by a hash nobody maintains, baked into a catalog, and reloaded live when the change is additive.
api: [T:Vixen.Gameplay.DefId, T:Vixen.Gameplay.Definition, T:Vixen.Gameplay.DefinitionCatalog, T:Vixen.Gameplay.DefinitionCatalogBuilder, T:Vixen.Gameplay.IDefinitionRegistry, T:Vixen.Gameplay.DefinitionRegistry, T:Vixen.Gameplay.DefinitionNotFoundException, T:Vixen.Gameplay.DefinitionSerialization, T:Vixen.Gameplay.Content.DefinitionContent, T:Vixen.Gameplay.Content.DefinitionLoad, T:Vixen.Editor.Assets.Gameplay.DefinitionImporter, T:Vixen.Editor.Assets.Gameplay.DefinitionImportSettings]
tags: [gameplay, content, definitions, addressables, vxdef]
since: 0.1
status: preview
related: [gameplay/tags, gameplay/modules, gameplay/effects, gameplay/randomness]
---

## What it is

A **definition** is authored, immutable, addressable content: an item, a quest, an ability, a recipe,
a loot table, a currency, or a kind a game invented. It is written as YAML with a type tag, imported
by one importer, and resolved at run time through a `DefId` — the FNV-1a hash of its address.

A **catalog** is every definition a build knows plus the tag table baked out of them. A **registry**
is the swappable holder a system reads through, so a content update is a reference assignment rather
than a restart.

## What it is for

The claim doc 28 exists to earn: adding an item, a quest, a recipe, a vendor or a loot table is a
content edit and a `vixen content build`, with no code and no server restart.

The `DefId` is what makes it cheap. It is a pure function of the address, so no peer has to be told
it, no registry has to be maintained by hand, and both ends of a wire compute the same number from
content they have already agreed on. The alternative — the numbered prefab list every engine without
a content pipeline grows — desynchronises the first time two people add an item on two branches.

## Using it

Derive a record from `Definition`, give it `[DataContract]`, and override `CollectTags` with every
tag it mentions so the content build can bake them. Declare it on a module
([modules](gameplay/modules)) so the composition report knows the `!Tag` exists.

Author it as `Assets/…/name.vxdef` — or `.vxitem`, `.vxquest`, `.vxeffect`, `.vxloot`, `.vxrecipe`,
which are the same importer and differ only in what an editor associates with them. The **type tag**
is the discriminator:

```yaml
# Assets/Effects/burning.vxeffect
!EffectDefinition
displayName: Burning
duration: 6
period: 2
stacking: StackTo
maximumStacks: 3
tags: [ Effect.Damage.Burning ]
grantedTags: [ State.Burning ]
```

⚠ **Take `IDefinitionRegistry.Catalog` once and use it for the whole of a piece of work.** A live
reload replaces it between reads, and code that resolves five ids through the property rather than
through one local can see two catalogs inside one ability.

⚠ **Not every content change can be applied live**, and `DefinitionRegistry.TryReload` says which:

- **A new tag renumbers the tag table**, so every tag index already in a component or in flight would
  mean something else. That is a *build* update — it rolls out rather than reloads.
- **A removed address is never additive.** A stack in a bank naming a definition the catalog no
  longer has is unresolvable. Deprecate, drain, then delete.

Everything else — a new address, a changed value, a retuned loot table — applies live.

## Examples

Declaring a definition type:

```csharp compile
using System.Collections.Generic;
using Vixen.Core;
using Vixen.Gameplay;

[DataContract("ItemDefinition")]
public sealed record ItemDefinition : Definition {
    public string DisplayName { get; set; } = string.Empty;

    public int ItemLevel { get; set; }

    public List<string> Tags { get; set; } = [];

    // Declared rather than discovered: walking fields by reflection for tag-shaped strings is a trim
    // hazard and silently wrong for a tag nested inside a list of records.
    public override void CollectTags(ICollection<string> tags) {
        foreach (var tag in Tags) {
            tags.Add(tag);
        }
    }
}
```

Baking a catalog and resolving through it:

```csharp compile
using Vixen.Gameplay;

static class Catalogue {
    public static DefinitionCatalog Build(Definition sword) =>
        new DefinitionCatalogBuilder()
            .Add("items/flamebrand", sword)
            .AddTag("State.InCombat")
            .Build();

    public static float DurationOf(IDefinitionRegistry definitions) {
        // A hash, computed here, with no lookup and no registry to have been told about it.
        var id = DefId.From("effects/burning");

        return definitions.Get<EffectDefinition>(id).Duration;
    }
}
```

Applying a content update, and finding out when it cannot be:

```csharp compile
using Vixen.Gameplay;

static class LiveUpdate {
    public static string Apply(DefinitionRegistry registry, DefinitionCatalog next) =>
        registry.TryReload(next, out var reason) ? "applied" : reason;
}
```

### Loading a build's definitions

`Vixen.Gameplay.Content` is the step between the two: it takes the addresses a content build labelled
and hands back one catalog with its tag table baked.

```csharp compile
using Vixen.Assets;
using Vixen.Gameplay;
using Vixen.Gameplay.Content;

static class Booting {
    public static async Task<DefinitionRegistry> LoadAsync(AssetManager assets, CancellationToken cancellation) {
        var load = await DefinitionContent.LoadAsync(assets, cancellation);
        var registry = new DefinitionRegistry();

        foreach (var problem in load.Problems) {
            // A .vxgroup broad enough to sweep up a texture. The rest still loaded.
            Console.Error.WriteLine(problem);
        }

        registry.Reload(load.Catalog);

        return registry;
    }
}
```

⚠ **A definition is copied out of its bundle rather than held by a ref-counted handle.** Ref-counting
them individually would put a load call on the damage path and admit a state where a sword in a bag
names a definition that has been unloaded — and a `DefId` that *sometimes* resolves is worse than one
that never does. What is ref-counted is what a definition points at: its mesh, its icon, its sound.

⚠ **A missing label contributes nothing; a missing address throws.** The first is content being broad
and the second is the caller being wrong, and conflating them turns a typo into a rule that silently
never fires.

⚠ **Load every label in one call.** Two catalogs number their tags separately, so `Slot.Weapon` would
be a different integer in each.

## See also

- [Gameplay tags](gameplay/tags) — what the catalog bakes alongside the definitions.
- [Modules](gameplay/modules) — where a definition type is declared.
- [Effects](gameplay/effects) — the one definition type the kernel itself ships.
- [Gameplay randomness](gameplay/randomness) — how a roll over authored weights stays reproducible.
