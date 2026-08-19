<!--
SPDX-FileCopyrightText: Copyright (c) Rikarin
SPDX-License-Identifier: Apache-2.0
-->

# Virtual Geometry

One authored mesh through the virtualized-geometry path, on screen: a cluster DAG built at start-up,
pages streamed on demand, hierarchical culling and level-of-detail decided per cluster per frame on
the device, and one indirect draw whose instance count the host never learns.

```bash
dotnet run --project Samples/12-VirtualGeometry
```

`--vixen-frames N` runs a fixed number of frames and exits, which is what lets CI prove the whole
stack — window, device, document, traversal, indirect draw, present — starts, runs and stops without
a validation error or a hang. At shutdown the sample logs how many clusters the last frame's
traversal accepted; zero after a real run is a frame that drew nothing, whatever else went right.

⚠ **This sample needs a display, and `--vixen-capture` cannot stand in for one.** It owns its device,
its swapchain and its present — `config.Graphics.Enabled = false` — so there is no `AppGraphics` for
the capture path to read a frame back out of, and no `frame.png` is written however the run is asked.
Under `--vixen-headless` the window cannot present, and the sample says *there is no window to present
to* and draws nothing rather than pretending otherwise. That is not a bug in the flag: samples 03 and
13 both capture headlessly on the real device, because their frame is the host's. Until this sample
grows an offscreen path of its own, its CI leg is `--vixen-frames N` on a machine with a display.

Until 2026-08-19 the flag was not honoured here at all: `Program.cs` built a `DesktopPlatform` and
handed it to `WithPlatform`, which `AppBuilder.Build` takes ahead of the factory — so a
`--vixen-headless` run logged `on Desktop (macOS)` and opened a window. It now logs `on Headless` and
opens none.

## What is on screen

The visibility buffer, phase 4's output, as a debug view: every pixel names the visible cluster and
the triangle that won the depth test, and the present pass hashes cluster identities to colours. The
coloured patches *are* the traversal's cut — watch them change size as the camera moves in and out
and you are watching per-cluster level of detail decide, per frame. A patch keeps its colour for as
long as its cluster is in the cut: the pixel actually stores a per-frame *visible-list slot*, which
an atomic append reshuffles every frame, so the present pass decodes it through the visible list to
the cluster it names — the first version hashed the slot and the whole sphere flickered. Shading
those identities for real is the material resolve's job (phase 5 of
`docs/plan/22-virtualized-geometry.md`) and needs the whole clustered-lighting frame around it,
which is a different sample.

**The software raster is off, and the shutdown log says so.** Phase 6 routes the clusters whose screen
diameter is under `VirtualGeometryRenderFeature.SoftwareThreshold` to a compute scanline raster instead
of the hardware one; the threshold defaults to zero, because where that trade pays is a property of the
hardware and this sample is not a benchmark. Set it and the log's *software* count moves. ⚠ On Apple
silicon it will not: MoltenVK reports no 64-bit buffer atomics, so the routing is forced off whatever
the threshold says and the frame is the one phase 4 draws — which is the capability gate behaving.

**Drag with the left mouse button to orbit.** Until the first drag the camera turns by itself, so
the sample shows its point unattended; the first drag takes over, seamlessly, at wherever the orbit
was. The distance keeps breathing in and out either way — between two and a half and nine radii —
because the level of detail responding to it is the thing being demonstrated.

## What the sample demonstrates

**The join `docs/plan/22-virtualized-geometry.md` phase 5 records as owed**: a `Vixen.App.Game` whose
frame is `SceneRenderHost.Draw`. Every piece existed and was tested — features extract, a document
builds a compositor, a graph orders the passes — and until this sample nothing outside a test project
put them together; the other samples open a device and issue draws directly, on purpose, because they
are about the layers underneath.

The division of labour is the thing to read for:

- **The document decides where the passes go.** The YAML in `VirtualGeometryGame.Document` — a depth
  clear, the cluster traversal, the visibility-buffer draw — is the same document
  `VirtualGeometryDeviceTests` runs against a scratch device.
- **The host decides what exists.** A device, an effect system, the `VirtualGeometrySystem` that owns
  every buffer of the virtualized path, a `RenderView` bound to the name the document uses, and the
  visibility-buffer texture lent to the frame by name — imported so the present pass may read it
  after the graph is done, with the exit state making that legal.
- **Neither knows the other's half**, which is what lets the same document run against a swapchain
  here and a golden test's scratch texture there.

## A log line to ignore

On macOS the first frame logs `Blending is enabled for attachment with format VK_FORMAT_R32_UINT,
which does not support it`. It is a MoltenVK false positive: its pipeline-creation guard warns for
every colour format Metal cannot blend without checking whether blending is actually enabled — and
here it is not. `docs/rhi-backend-mapping.md` § 5 records the verification; if that message ever
names a format you *meant* to blend, take it seriously.

## What it is honest about

Effects compile from `Raven/Library` at start-up, which no shipped game would do — a shipped game
loads the bundle its content build baked. The samples run from the repository and the repository has
the sources; the moment a content build exists, `LibraryEffects` is what it replaces. The mesh's
cluster DAG and pages are likewise built at start-up rather than imported, because a sample should
not need a content build to run — in a game they are two artefacts the model importer wrote.

## What finding this sample's numbers wrong bought

The first run logged the traversal accepting **1 376 clusters of a 442-cluster mesh** — more than
exist. That number led straight to a traversal defect no test had seen: every parent of a group
carries the group's whole child set, the shared error centre makes all of them refine at the same
moment, and each pushed the same children, compounding per level. The cut was still a cut — every
path through the DAG held exactly one visible cluster, which is why the property tests passed — and
the golden image compares coverage, which duplicates cover perfectly. `CullCluster.GroupLead` is the
fix, and `ClusterTraversalGroupTests` now holds it. A sample that prints its numbers is a test with a
human in the loop.
