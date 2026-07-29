# Vixen.Rendering.VirtualGeometry

One authored mesh, every level of detail at once, and no cracks between them.

This is **phases 1 and 2's offline half** of
[docs/virtualized-geometry.md](../../docs/virtualized-geometry.md) — the part of a Nanite-class
pipeline that runs at import time, and the part that decides whether the result has cracks.
What comes out is a cluster DAG: about a hundred and twenty-eight triangles per cluster, an error per
cluster that says how far it has moved from the mesh it stands in for, and the structure that lets a
device pick a different level for every cluster of one object in one frame without the seams between
them opening.

It references no graphics device, and it is called from `ModelCompiler` at import time. Everything it
promises is checked against spheres and grids, where the answer is known.

## The loop

```
cluster  →  group  →  simplify the group as a unit  →  split  →  repeat
```

**Cluster.** The triangle adjacency graph is cut into parts of about `MaxTriangles` by an edge-cut
partition, so a cluster is a patch of surface rather than a run of the index buffer. Adjacency is
decided by *position*, not by index: an exporter splits a vertex wherever an attribute is
discontinuous, and an index-based adjacency reads every UV seam and every hard edge as a hole.

**Group.** The same partitioner one level up, over a graph whose nodes are clusters and whose edge
weights are how many mesh edges two clusters share. About `GroupSize` clusters per group.

**Simplify the group as a unit, with its outer boundary locked.** This is the whole trick and the
only part that has to be exactly right. Every edge *interior* to the group may collapse — including
every edge between two of its clusters — and every edge on its outside may not move by so much as a
float. That is what guarantees a cut through the finished DAG meets along edges that were never
touched.

**Split.** The simplified triangles are cut into clusters again, and those clusters are the group's
parents: each of them replaces *all* of the group's children, never some of them.

## What makes the cut crack-free

Three properties, and the validator refuses a DAG that lacks any of them.

**A cluster is drawn when `Error ≤ t < ParentError`** — a decision it takes alone, with no knowledge
of what its neighbours decided. Along any path through the DAG those intervals tile the number line
exactly once, so for every threshold each part of the surface is drawn exactly once.

**Error increases strictly along every edge.** A parent whose error merely equalled its child's would
leave a band of thresholds where neither is drawn: a hole that opens at one distance. The build takes
the maximum against every child and then steps past it by one representable float if it has to.

**A group's boundary is bit-identical between its children and its parents.** Not nearly — identical.
That is why the simplifier collapses a vertex onto *another vertex* rather than onto a quadric's
optimal position: a boundary that survived to within a rounding error would still be a slit, and a
lock that is only a heavy weight in the cost function is a lock that fails on the mesh that needed it.

Two rules keep it: an endpoint of a locked edge is never removed, and no collapse may destroy a
triangle carrying one. The second is not optional — a locked edge whose only triangle is deleted
disappears even though neither of its endpoints moved.

## The error is measured, not inferred

The quadric chooses which collapse to make. It is the wrong number to *report*: a quadric is the
distance to the planes of the triangles that met at the vertex, which on a smooth surface is about a
third of the distance to the surface itself. A cut chosen for a one-pixel budget against a quadric
pops by three.

So after each collapse, the point that was removed is measured against the triangles that replaced
it, and that distance is added to what it already carried. `MeshletCutTests` asserts the consequence
directly: over twenty distances, the true one-sided Hausdorff distance from the original sphere to
whatever the cut drew stays under the pixel budget the cut was chosen with.

## Seams do not simplify

A welded vertex whose copies carry different normals, tangents, texture coordinates or skinning
weights is a seam, and a seam neither moves nor is moved onto. Both halves are needed: welding made
the copies one vertex, so moving it moves both charts at once, and collapsing anything *into* it would
have to pick one of the copies for triangles arriving from both sides.

The cost is a floor on how coarse a heavily-charted mesh can get, and it is a property of how the mesh
was unwrapped rather than of this build. The alternative — interpolating attributes at a collapse — is
what makes a texture smear across a chart boundary, and it would also mean the DAG inventing vertices
the source mesh does not have.

## What is not here

**The partitioner is recursive bisection, not multilevel k-way.** METIS coarsens, partitions the small
graph and projects back; this grows two fronts from a pseudo-peripheral pair and refines the boundary
in balance-preserving swaps. On a square lattice it cuts about a third fewer edges than splitting the
index buffer into equal runs, where an optimal partition would cut two thirds fewer — the gap is
diagonal cuts compounding down the recursion. It costs cluster quality, not correctness, and
`GraphPartitionerTests` states the measured number so that changing it is a decision.

**Refinement swaps in pairs and never singly**, which is not tidiness: a partition into forty parts is
five bisections deep, and five single moves put a part five triangles over its budget — which is a
full cluster and a cluster holding five triangles. With paired swaps every part comes out exactly the
size it was asked for, and level-zero clusters run about 122 of 128 triangles full.

**No page format and no residency.** Phase 2. `MeshletMesh` is deliberately relocatable — a cluster's
triangles index its own vertex list with a byte, and that list indexes the mesh — so a page pool that
evicts can move one without rewriting it.

**No quantisation.** Positions are the source mesh's own, shared by every level. Phase 2 quantises
them to the cluster bound; doing it here would mean the boundary equality this phase exists to
guarantee held only to the quantisation grid.

## What it costs

A unit sphere, Release, one machine, whole DAG and validation:

| Triangles | Build | Validate | Levels |
|---|---|---|---|
| 5,120 | 0.27 s | 0.07 s | 7 |
| 20,480 | 0.54 s | 0.04 s | 9 |
| 81,920 | 5.2 s | 0.19 s | 21 |

Import-time and artefact-cached, so it is paid once per mesh per change. The number worth recording
is what it was before: **the same 82k mesh took 54 seconds** until the triangle adjacency stopped
allocating a `List<int>` per edge. It was not the allocation — it was every subsequent collection
walking a quarter of a million live objects, and it read as an algorithmic problem right up until it
was measured.

## Determinism

Two builds of one mesh on two machines produce the same bytes, which is what
[doc 12](../../docs/plan/12-build-ci-and-testing.md) gates the content build on. Nothing here depends
on the order a dictionary enumerates or a heap pops equal keys in: welded ids are the lowest index of
their group, every partition tie breaks by node index, every priority carries the pair it came from, and
groups simplified in parallel report their errors back to be folded in sequentially rather than
writing through to a shared array.

`MeshletBuilderTests` asserts it twice — the same build twice, and the parallel build against the
sequential one.

Licensed under Apache-2.0.

## Pages

`MeshletPageBuilder` is phase 2's offline half: the same DAG with its geometry cut into fixed-size
pages that can be loaded without loading all of it. Two things happen there and they are independent
— the geometry is *quantized*, which is about bytes per vertex, and it is *paged*, which is about
what can arrive separately.

**Only the geometry is paged.** `Meshlet` — bounds, cone, error, parent error, the group links — is
the hierarchy, and a traversal has to walk it to find out which clusters it wants, so it cannot
itself be streamed. It is also sixty-odd bytes against a cluster's two kilobytes, which makes the
split obviously right rather than a compromise.

**One quantization grid for the mesh, not one per cluster.** This is the one decision in the format
that cracks the mesh if it is made the obvious way. Quantizing each cluster against its own bound is
how you spend sixteen bits well, and it is wrong: a vertex on a locked boundary is referenced by a
cluster on each side, the two have different bounds, and the same position rounds to two different
numbers. Everything above about collapsing onto existing vertices — so a locked boundary is
bit-identical rather than nearly so — is thrown away in the last step before the device sees it.

So the grid is sixteen bits across the *mesh's* longest extent, and a cluster stores offsets from its
own grid-aligned origin in whole numbers. That is exact: two clusters sharing a vertex decode it to
the same bits however far apart their origins are. It also makes every cluster's local coordinates
fit in sixteen bits by construction, because the coarsest cluster there can be spans the whole grid.

**Page zero holds the roots**, because clusters are packed coarsest level first and a root is by
definition at the coarsest level there is. Pinning that one page is what makes an object whose pages
have not arrived draw at its coarsest level rather than not at all.

## Streaming degrades by threshold

`MeshletCut.SelectByError`'s residency-aware overload is the CPU reference for what a frame draws
when only some of the pages are there. What it does *not* do is drop the clusters that are missing:
that is a hole, and a hole at a group boundary is a crack.

What it also does not do is patch the cut locally — swap the missing cluster and its siblings for
their group's parents and repeat. That one is wrong in a way that looks right: a group's other
children may not be in the cut at all, because the cut took *their* children instead, so swapping in
the parents leaves those finer clusters underneath and the surface is covered twice in one place and
once in another.

It raises the threshold instead, to the next group error at which the answer changes, until the whole
cut is resident. Every threshold's cut is an antichain by construction, so the result is always a
valid cut — just a coarser one, closing as the pages land.
