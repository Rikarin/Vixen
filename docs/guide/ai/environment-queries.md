---
title: Environment queries
slug: ai/environment-queries
kind: guide
area: AI
summary: Generators, tests with three purposes, and the utility scorer with points substituted for actions.
api: [T:Vixen.Ai.IFactorSource, T:Vixen.Ai.IScoredCandidateSet`1, T:Vixen.Ai.CandidateScoring, T:Vixen.Ai.QueryPoint, T:Vixen.Ai.ScoredQueryPoint, T:Vixen.Ai.QueryResults, T:Vixen.Ai.QueryOrigin, T:Vixen.Ai.IQueryGenerator, T:Vixen.Ai.QueryGenerators, T:Vixen.Ai.QueryTestPurpose, T:Vixen.Ai.IQueryTest, T:Vixen.Ai.QueryReading, T:Vixen.Ai.QueryTest, T:Vixen.Ai.QueryTests, T:Vixen.Ai.QueryDistanceFrom, T:Vixen.Ai.EnvironmentQuery, T:Vixen.Ai.EnvironmentQueryLibrary, T:Vixen.Ai.QueryGeneratorKind, T:Vixen.Ai.QueryTestKind, T:Vixen.Ai.QueryGeneratorContent, T:Vixen.Ai.QueryTestContent, T:Vixen.Ai.QueryContent, T:Vixen.Ai.QueryContentCompiler, T:Vixen.Ai.Nodes.TraceEnds, T:Vixen.Ai.Nodes.WorldQueryTests, T:Vixen.Ai.Nodes.QueryBinding, T:Vixen.Ai.Nodes.RunQueryTask, T:Vixen.Ai.Nodes.RunQueryService, T:Vixen.Ai.Nodes.QueryNodes, T:Vixen.Ai.Diagnostics.QueryPreviewStyle, T:Vixen.Ai.Diagnostics.QueryPreview, T:Vixen.Editor.AssetEditors.Ai.QueryDocument, T:Vixen.Editor.AssetEditors.Ai.QueryView, T:Vixen.Editor.AssetEditors.Ai.QueryEditorFactory, T:Vixen.Editor.Assets.Ai.QueryImporter, T:Vixen.Editor.Assets.Ai.QueryImportSettings]
tags: [ai, queries, eqs, scoring, cover]
since: 0.1
status: stable
related: [ai/utility, ai/behaviour-trees, ai/goap, ai/debugger, ai/world-nodes]
---

## What it is

An **environment query** answers "where should I stand" the way a [utility set](utility.md) answers
"what should I do": generate candidates, run scored tests over them, take the best.

Those are the same machine, and here that is a fact about the code rather than an observation.
`UtilitySet` and `EnvironmentQuery` both implement `IScoredCandidateSet<T>`, both combine their
factors through `CandidateScoring`, and one `IResponseCurve` object can score an action and a point at
the same time.

## What it is for

The spatial questions a behaviour tree is bad at asking. *Where is the best cover with line of sight
to the target; which of these ledges can I actually reach; which of six guards is the one to shoot;
where do I throw this so it lands near them and not near me.*

You do *not* want it for "walk to the door". A query is a search over candidates, and reaching for one
when the answer is a blackboard key buys a generator, a test list and a per-point cost for nothing.

## Using it

Two lists: generators, then tests in order.

```csharp compile
using Vixen.Ai;
using Vixen.Core;

public static class Cover {
    public static EnvironmentQuery Build() =>
        new(
            Symbol.Intern("cover"),
            [QueryGenerators.Grid(8f, 2f)],
            // Must be within reach at all.
            new QueryTest(QueryTests.Distance()) {
                Purpose = QueryTestPurpose.Filter,
                Ceiling = 8f
            },
            // And prefer somewhere near the target.
            new QueryTest(
                QueryTests.Distance(QueryDistanceFrom.Context),
                new ResponseCurve { Slope = -1f, Shift = 1f }
            ) { Maximum = 30f }
        );
}
```

An agent runs one through `RunQuery` (once, now) or `KeepQueryResult` (on the branch's schedule),
both in `Vixen.Ai.Nodes`.

### The generators

| Generator | Makes |
|---|---|
| `Grid` | a square of points on the ground |
| `Circle` | a ring at a fixed radius |
| `Donut` | rings between two radii — "near, but not on top of" |
| `Cone` | a fan in front of the agent, aimed at the context |
| `CurrentLocation` | the one point the agent is standing on |
| `Composite` | several of the above, in order |
| `WorldQueryTests.Entities<T>` | a point at every entity carrying a component |

⚠ **`CurrentLocation` is not a degenerate case — it is how "should I move at all" is asked.** A query
whose candidates are the grid *and* where the agent already is lets the tests decide, and without it
an agent re-picks a spot a centimetre away every interval and shuffles for ever.

⚠ **There is a hard ceiling of 4096 points, and it is a ceiling rather than a warning.** A grid is
`(2·extent/spacing + 1)²`, so a designer who types `0.1` into a spacing field asks for four hundred
thousand points — each of which may be traced. The bound turns a hung frame into a coarse answer.

### The querier and the context are two things

"Points around **me**, scored by distance to **the enemy**" needs both, and a generator that only knew
the agent could not express the commonest cover query there is. `QueryOrigin` carries them, and a
query authored around a target and run with none generates around the agent rather than around the
origin of the world.

### A test has a purpose

| Purpose | Does |
|---|---|
| `Filter` | rejects a point that fails, and contributes nothing to the score |
| `Score` | contributes to the score, and rejects nothing |
| `Both` | rejects outside the bounds, then scores what survives |

⚠ **The distinction earns its keep the first time somebody writes a query.** "Must have line of sight"
and "prefer more cover" are the same reading used two ways, and a pipeline with only scoring makes
the first into a zero that any other test can outvote — which is how an agent ends up standing in the
open because the spot was otherwise excellent.

⚠ **A test that cannot answer filters the point rather than scoring it zero.** "There is no path to
here" and "the path to here is long" are different facts; a reading of `NaN` says the first.

### Test order is the author's, and the runtime does not reorder it

A filtering test rejects a point and everything below it is skipped. A four-hundred-point grid with a
trace at the top of the list is four hundred raycasts; the same list with a distance filter first is a
few dozen. The editor shows the running order on every row and lets you drag them; the runtime honours
what the file says.

⚠ **A runtime that reordered would make a query's cost unpredictable and its behaviour depend on a
heuristic nobody can see.**

### The world-facing tests

These live in `Vixen.Ai.Nodes`, because a trace needs a `PhysicsWorld` and a path needs a
`NavMeshQuery` — neither of which `Vixen.Ai` may reference.

| Test | Reads |
|---|---|
| `Trace` | whether a physics ray between two points is clear |
| `Overlap` | how many bodies are within a radius — "is there something solid beside me" |
| `PathLength` | how far the agent would actually walk, over the navmesh |
| `OnNavMesh` | how far off the mesh the point is |

⚠ **`OnNavMesh` is the cheapest of the four and belongs at the top of most lists.** A grid around an
agent puts most of its points inside walls and off ledges; rejecting those before anything traces is
the difference between a query that is affordable and one that is not.

⚠ **`Trace`'s eye height is not a detail.** A ray between two points on the floor hits the floor, so a
line-of-sight test without one rejects every point in the level and reads as the query being broken.

### The scoring is the utility scorer

```
score(point) = ( Π factor ) ^ (1/n)
```

The same weighted geometric mean, with the same zero rule, that [utility](utility.md) uses — because
it is the same function. A test's normalisation, its curve and its clamp are a consideration's, and
the editor draws them with the same curve control.

⚠ **A test's `Weight` pulls its factor toward one; it does not multiply the score.** Multiplying would
break the mean's whole property — that the count of factors is irrelevant — because a factor of 2 is
not in `[0,1]` and a factor of 0.5 would be a permanent half-veto on an otherwise perfect point.

## Examples

A query as a file. Two lists, and the order of the second one is the cost:

```yaml
version: 1
name: Cover
generators:
  - { kind: Grid, extent: 8, inner: 2, aroundQuerier: true }
tests:
  - { kind: Registered, source: on-navmesh, purpose: Filter, ceiling: 0.5 }
  - { kind: Distance, purpose: Filter, ceiling: 8 }
  - { kind: Registered, source: sight, purpose: Filter, floor: 0.5 }
  - { kind: Distance, fromContext: true, maximum: 30, curve: Linear, slope: -1, shift: 1 }
```

Registering the world-facing halves, which are the game's objects rather than strings in a file:

```csharp no-compile="a fragment; the physics world and the navmesh are the game's"
resolver.AddTest("on-navmesh", WorldQueryTests.OnNavMesh(navigation));
resolver.AddTest("sight", WorldQueryTests.Trace(physics));
resolver.AddGenerator("cover-spots", WorldQueryTests.Entities<CoverSpot>(20f));

QueryContentCompiler.TryCompile(content, resolver, out var diagnostics, out var query);
```

Running one from a tree, and drawing what it found:

```csharp no-compile="a fragment; the library and the keys are the game's"
QueryNodes.Register(resolver, queries);

// …and in a debug build, the same points the editor's preview draws:
QueryPreview.Draw(draw, task.Results);
```

⚠ **An unresolved test filters everything**, so an unfinished query answers "nowhere" rather than
confidently sending an agent to a spot nothing checked. The importer fails the build on a query with
no generators, and on one whose every test only filters — because that query returns whichever point
the generator happened to make first, which looks like it working.

## See also

- [Utility](utility.md) — the same scorer, with actions instead of points.
- [Behaviour trees](behaviour-trees.md) — where `RunQuery` and `KeepQueryResult` sit.
- [The AI debugger](debugger.md) — the preview, drawn from a running agent's last query.
- [Nodes over the world](world-nodes.md) — the other half of `Vixen.Ai.Nodes`.
