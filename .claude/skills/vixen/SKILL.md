---
name: vixen
description: >-
  Builds with Vixen, the .NET 10 game engine and application framework — writing components,
  systems, scenes, shaders, UI and editor tooling against the `Vixen.*` packages, and working
  inside the engine repository itself. Provides the engine's vocabulary, its opinionated
  declarations (`[Component]`, `ISystem`, `[Replicated]`, `[Node]`, `[Importer]`, Raven shaders)
  and the API index. Applies to any project referencing `Vixen.Core`, `Vixen.Ecs`, `Vixen.Ui` or
  `Vixen.Engine`, and to work in the engine's own tree. Also triggers for "add a component",
  "write a system", "a Raven shader", "vixenengine.org" or "Vixen.…".
user-invocable: false
---

# Vixen

Vixen is a .NET 10 game engine and application framework: an archetype ECS, a Vulkan/Metal/WebGPU
RHI behind one abstraction, a retained-mode UI framework, an asset pipeline, an editor, and Raven —
its own shading language. Everything is C# except the shaders.

**3 679 public types.** That number is the single most important fact for an agent working here:
guessing a type name is the failure mode, not a shortcut. The engine ships its own API index for
exactly that reason.

## Discovery: never guess a type name

`vixen-mcp` exposes the documentation graph — the same artefact the site renders — extracted from
the engine's source by `nuke Docs`. Its answers match the checkout, not a scraped web page.

1. `vixen_meta` — what this index is: the commit, the counts per kind, the guide pages, the releases.
2. `vixen_search` — by name, by summary, or **by kind**, which is the query that matters here:
   `kind="scene-component"` is "what can a scene put on an entity", `kind="system"` is "what runs in
   the frame", `kind="importer"` is "what file types are understood".
3. `vixen_symbol_get` — signature, doc comment, members, and the kind-specific facts: a component's
   **size in bytes**, a system's **phase and declared access**, a shader's **descriptor sets**.
4. `vixen_guide_get` and `vixen_examples` — the written half, and code the build compiles.
5. `vixen_diff` — what changed in a release, including the breaking changes whose signatures are
   identical.

Without the server, the same information is in `artifacts/docs/graph.json` after `./build.sh Docs`,
and on [vixenengine.org](https://vixenengine.org). See [mcp.md](mcp.md).

## The taxonomy: a type is what it declares, not what it is called

The engine's own vocabularies are what the index is organised by, and they are decided by attributes
and base types the compiler already relies on:

| Kind | What makes it one |
|---|---|
| `component` | `[Component]` on a struct — data in a column, addressed by entity |
| `scene-component` | `[Component]` **and** `[DataContract]` — the ones a scene file can carry |
| `replicated-component` | `[Replicated]` — plus a channel, a send rate and per-field quantisation |
| `system` | `ISystem` or `SystemBase` — plus a phase, and `IDeclaredAccess` for what it reads and writes |
| `behavior` | The per-entity script model, for the things an ECS system is the wrong shape for |
| `ui-control` | Lives in `Vixen.Ui.Controls` |
| `graph-node` | `[Node("Category/Title")]` — a node in the shader or VFX graph |
| `importer` | `[Importer(".fbx")]` — what turns a file into an asset |
| `annotation` | An `Attribute` the engine reads at compile time |
| `generator` | An `IIncrementalGenerator` or `DiagnosticAnalyzer` |
| `shader` | A Raven `.rvn` with compiled reflection beside it |
| `diagnostic`, `log-event` | A `VX####` code, or a stable log id |

## Principles

1. **Search before writing.** Three and a half thousand types; the one you want probably exists.
2. **Declare, do not configure.** A component is a struct with an attribute; a system's ordering is
   `[RunsBefore]`, not a registration call. If you are writing setup code to express a relationship,
   check whether an attribute already says it.
3. **A component is a layout.** Its size is a fact the index reports, and changing it breaks every
   saved scene — which is why the release table has a row for it.
4. **Declare access.** A system that reads and writes without saying so cannot be scheduled in
   parallel safely. `SystemAccess.Declare().Read<Position>().Write<Velocity>()`.
5. **Shaders are Raven, not HLSL.** `.rvn` compiles to SPIR-V with reflection; the reflection is what
   the documentation and the pipeline both read.
6. **The examples in the guide compile.** A fence marked `compile` is built against the engine on
   every CI run — copy from those rather than from memory.

## Key patterns

```csharp
using Vixen.Core;
using Vixen.Ecs;

// A component: data, in a column, with a known size.
[Component]
public struct Velocity {
    public float X;
    public float Y;
}

// Iterating: describe the set, then walk chunks — a column at a time, not an entity at a time.
public static class Movement {
    public static void Step(World world, float delta) {
        var moving = new QueryDescription().WithAll<Position, Velocity>();

        foreach (var chunk in world.Chunks(moving)) {
            var positions = chunk.Values<Position>();          // Span<T>, writable
            var velocities = chunk.ReadValues<Velocity>();     // ReadOnlySpan<T>

            for (var index = 0; index < chunk.Count; index++) {
                positions[index].X += velocities[index].X * delta;
                positions[index].Y += velocities[index].Y * delta;
            }
        }
    }
}
```

That block is quoted from [`docs/guide/ecs/queries.md`](../../../docs/guide/ecs/queries.md), which the
build compiles — which is the point of preferring `vixen_examples` to memory.

A system is a `Vixen.Ecs.Systems.SystemBase` (note the namespace — not `Vixen.Ecs`), whose
`Update` returns a `JobHandle` and takes a `SystemContext`, and which declares its access through
`IDeclaredAccess`:

```csharp
public sealed class MovementSystem : SystemBase, IDeclaredAccess {
    public static SystemAccess Access { get; } =
        SystemAccess.Declare().Read<Velocity>().Write<Position>().Build();

    public override JobHandle Update(in SystemContext context, JobHandle dependency) { … }
}
```

⚠ **Check both against the index before copying them into a project.** `vixen_symbol_get` on
`T:Vixen.Ecs.Systems.SystemBase`, `T:Vixen.Ecs.Systems.SystemAccess` and `T:Vixen.Ecs.QueryDescription`
gives the signatures *this* checkout has — the second block above is shaped from them and is not
itself compiled by the build.

## Working in the engine's own tree

- **Every gate is a Nuke target, and they are the same ones CI runs**: `./build.sh Test`,
  `CheckFormat`, `CheckArchitecture`, `CheckApi`, `CheckDocs`, `Docs`.
- **A new public type needs a page.** `CheckDocs` fails on a public type with neither a guide page
  nor a line in `docs/DocsExempt.txt` — the file only ever shrinks. Write
  `docs/guide/<area>/<page>.md` with the five headings: *What it is*, *What it is for*, *Using it*,
  *Examples*, *See also*.
- **A new public API needs a baseline line.** `PublicAPI.Unshipped.txt`, reviewed;
  `./build.sh CheckApi --update-api` writes it.
- **Design decisions live in `docs/plan/`**, the state of the tree in `docs/overview.md`, and the
  detail in each project's README. Read the plan document before changing a subsystem's shape.
- **Releases are generated**: `./build.sh Release --release-version 0.2.0` folds the API baselines,
  archives the graph and writes the release table. Never hand-write `CHANGELOG.md`.

## Reference

- [mcp.md](mcp.md) — configuring `vixen-mcp` and what each tool answers.
- [docs/plan/25](../../../docs/plan/25-documentation-generator-and-site.md) — how the index is built and
  what it guarantees.
- [vixenengine.org](https://vixenengine.org) — the same graph, rendered.
