# Golden images

Rendering fixtures, compared against committed reference images.

This is the level of testing every other kind is a proxy for. A command-stream assertion proves the
backend emitted the calls somebody intended; only a picture proves those calls draw what they were
meant to.

Six kinds of fixture — the suite `docs/plan/05` § Testing asks for.

| Class | What it is about |
|---|---|
| `GoldenImageTests` | The **backend**, at its simplest: a clear, a triangle, an indexed quad, blending, reversed depth |
| `PipelineStateImageTests` | One **state bit** each — cull mode, topology, instancing, vertex formats, index offsets, depth comparisons and bias, stencil, blend factors and operations, write masks, viewport, scissor, multiple targets, load actions, sampler filters and address modes, and the two transfer paths |
| `CompositorImageTests` | The **renderer** — the layer engine code actually uses |
| `UiImageTests` | The user interface's GPU half |
| `DebugDrawImageTests` | The debug geometry's — the screen projection, and whether the stroke font is letters |
| `StandardFrameTierImageTests` | A **whole frame per quality tier** — the expansion, not the nodes |

`PipelineStateImageTests` is the largest and the most repetitive, deliberately. Every bit it covers is
one a backend can silently ignore: recording `BindPipeline` proves the call was made and proves
nothing about whether the driver was told to cull the right face or compare depth the right way
round. `docs/plan/05` § Cross-backend equivalence names that exact class of bug — "a backend silently
ignores a state bit" — and it is the reason this suite exists at all.

`CompositorImageTests` matters for a different reason: the renderer had been asserted entirely
against a recording backend, which will happily record a descriptor set bound to the wrong index and
a uniform written at the wrong offset and report that the calls were made.

```bash
./build.sh GoldenImages --configuration Release
```

Everything renders through `Vixen.Graphics.RenderGraph`, so the barriers and store actions it derives
are under test too. The fixtures also run under the ordinary `Test` target, so a wrong picture fails a
normal build; the separate target exists to write diffs somewhere a human will find them and to carry
`--update-golden`.

## When one fails

The failure names **which bound was crossed** — a count of badly-wrong pixels or the average channel
— by how much, and where the worst pixel was. It also writes three files into `artifacts/golden-diff/`:

| File | What it is |
|---|---|
| `<name>.rendered.png` | what this machine drew |
| `<name>.expected.png` | what is committed |
| `<name>.diff.png` | the differing pixels in red, over a dimmed reference |

CI uploads that directory on failure.

## Updating a reference

```bash
./build.sh GoldenImages --configuration Release --update-golden
```

Or, for one fixture without a whole gate run — the same environment variable the target sets:

```bash
VIXEN_UPDATE_GOLDEN=1 VIXEN_REQUIRE_VULKAN=1 dotnet test \
  Platform/Vixen.Graphics.Golden.Tests -c Release \
  --filter "FullyQualifiedName~StandardFrameTierImageTests"
```

It writes into the **source** `References/`, not the copy beside the binary — rewriting that one
would "pass" and change nothing anybody commits. `VIXEN_REQUIRE_VULKAN=1` is worth setting either
way: without it a machine with no device skips silently, and a skipped update writes nothing while
reporting success.

⚠ Run it again **without** `VIXEN_UPDATE_GOLDEN` afterwards, and note that `--no-build` will not copy
the new references beside the binary — a verification run that skipped the copy compares against the
old ones.

Then **look at what it wrote** before committing, against the questions in
[Judging a golden that moved](#judging-a-golden-that-moved). A suite that rewrites its own
expectations when they fail is a suite that always passes.

## Comparison

Perceptual with an explicit threshold, not bitwise ([`docs/plan/05`](../../docs/plan/05-graphics-rhi.md)
§ Testing). Bitwise across drivers is a maintenance sinkhole: MoltenVK and lavapipe round the same
sRGB conversion differently and both are conformant, so a bitwise suite is red on one machine from the
day it is written and gets disabled within a month.

**Two bounds, and a fixture may set either or both.**

| Bound | What it catches | What it is blind to |
|---|---|---|
| `Channel` + `Fraction` — how many pixels exceed a per-channel threshold | something small and badly wrong: an artefact in a corner, a shifted edge, a pass that stopped running | a frame that is *slightly* wrong everywhere |
| `Mean` — how far the average channel moved | a shading change across the whole frame: an albedo, an exposure, a light's intensity | a bright artefact in a corner, which barely moves an average |

They are complements, and the second exists because the first alone was measurably not enough. A
material's base colour moved four per cent shifts 62–91% of a frame by one or two levels and only
4–67 pixels by more than three, so **every per-pixel threshold at or above two passes it** — which
the tier goldens duly did, until `Tolerance.Shaded` grew a mean. A mean alone would be the
mean-squared-error mistake: low enough to pass a whole image while a corner is blown out. Whichever
bound is crossed first fails, and the message says which, because "everything moved a little" has no
coordinate to point a reader at.

`Mean` defaults to no bound at all, so a fixture written before it existed keeps exactly the claim it
was written with.

These references were generated on MoltenVK and are verified against lavapipe on every push. The
tolerances are what that cross-driver agreement actually needs, not what one machine happens to
produce — with one stated exception: `Tolerance.Shaded`'s mean of 0.35 was measured against one
driver's zero and against injected regressions of 0.44 to 1.26, and its cross-driver half does not
exist yet. See its remarks before raising it.

## The tier goldens

`StandardFrameTierImageTests` is the only fixture here that renders a **whole frame the way a game
does**: a `!StandardFrame` node expanded by `PostEffectFactory`, built by `CompositorBuilder`, drawn
by `WorldRenderer`, with the quality tier as the only difference between its four pictures. Every
other fixture assembles its own nodes, which tests the nodes and leaves the expansion — the thing
that decides what a tier *does* — asserted only against its own structure.

Four things it pins, because a golden that drifts by one frame's history fails randomly:

- **FXAA, not TAA.** A temporal resolve converges over frames against a jitter sequence.
- **The meter's `DeltaTime` is set to ten seconds**, which makes its per-frame adaptation fraction
  one: the exposure arrives at its target on the first frame instead of being a picture of the frame
  count. The adaptation *rate* is untouched, because the rate is what a regression would move.
- **Two frames, and the second is kept** — so a history plane, a depth pyramid or a reprojected fog
  volume is read as well as written. Nothing moves between them.
- **The scene is photometric**: 12 000 lux of sun, a sky in cd/m², a lamp in lumens. The meter and
  the tone curve are calibrated in real units, so a scene in 0–1 colours is a dozen stops under
  everything downstream and comes back flat white. That was this fixture's first picture.

⚠ **`tier-low` has no sun shadow, and that is a defect rather than a tier.** A resolved
`cascadeCount` of anything but four draws no directional shadow at all, because nothing wires the
number into the shader's `CascadeCount` permutation — the shader reads four cascade slots while the
host fills two. Low ships two. If that reference gains a shadow, the fix has landed and the new
picture is the right one.

⚠ **`tier-high` and `tier-epic` were re-recorded when the local exposure stopped washing them.**
They are the only two tiers whose `post.localExposure` is on, and the effect pivoted around the wrong
number in both directions at once: `LocalExposureRenderer` anchored the pivot to `Photometry.MiddleGrey`,
which is ISO 2720's calibration constant of 1.2 and not the 0.18 reflectance a frame is graded onto —
2.74 stops — and in a metered frame it could not have known the exposure anyway, because the meter
writes it into a buffer nothing reads back. The pivot therefore sat above nearly every texel in a
photometric scene, all of which the shader classified as shadow and lifted by `shadowContrast`.

What moved, and it is the mean that moved rather than a count: **27.2/255 of average channel, with a
worst single channel of 58**, in the direction of *less* wash. Both pictures were pale and milky —
lifted sky, a caster shadow washed towards the floor's own value, the emissive block near white — and
both now carry the contrast the reference for the tier beside them always had. That is the check worth
repeating, because it needs no judgement about what looks nice: **`tier-medium` does not run this node
at all**, so it was never washed, and High and Epic now sit at its tonality with their local contrast
intact instead of a stop above it. A local exposure that changes the overall brightness of a frame is a
global exposure wearing a local one's clothes, which is what these two references used to be pictures
of.

⚠ **`tier-high` and `tier-epic` differ by ten pixels.** Everything Epic adds over High in this frame
is either invisible at 128² or belongs to the GI and reflection stacks the fixture cannot host, so
the pair is held only to "differ at all" — zero would mean the tier stopped resolving. It is not
evidence that Epic is worth its cost.

**What the tier goldens cannot see.** A fidelity *number* — 1024 cascade texels against 512, 64
froxel slices against 32, a five-level bloom pyramid against four — changes almost nothing a picture
can measure at this size; the largest of those three moved the average channel by 0.010. That is not
a fixture fault, it is what a cost trade is. Those live in
[`QualityTableSnapshotTests`](../../Core/Vixen.Rendering.PostFx.Tests/QualityTableSnapshotTests.cs),
which writes down all 240 numbers and fails by name when one moves.

## Judging a golden that moved

This is the whole risk of the technique and the reason teams abandon it: the cheapest response to a
red golden is `--update-golden`, and a suite whose references are rewritten whenever they fail is a
suite that asserts nothing. The question is never "does the new picture look fine" — it always looks
fine, or somebody would have noticed before committing.

Ask these, in order:

1. **Did you change anything that should have moved it?** If the diff is in a fixture you did not
   touch and the commit is a rename, stop: that is the failure this suite exists for.
2. **Does the change explain the *shape* of the diff?** Open `<name>.diff.png` — the differing pixels
   are painted red over a dimmed reference, so a shading change is a wash over whole surfaces and a
   geometric one is an outline. A bias change that comes out as a wash, or an albedo change that
   comes out as an outline, is not the change you think you made.
3. **Which bound was crossed?** The message says. A crossed *mean* with nothing over the per-pixel
   threshold is a whole-frame shading change; a crossed *count* with a small mean is something local.
   If a refactor that was supposed to be behaviour-preserving crossed either, it was not.
4. **Did only the pictures you expected move?** The four tier goldens share one scene, so a change to
   shading moves all four and a change to a tier-gated pass moves one or two. One tier moving alone,
   for a change that was not tier-specific, is a knob leaking.
5. **Is the new picture a picture of a bug being fixed?** Say so in the commit message, with the
   before and after described in words. The next person to read this file learns what the reference
   means from that sentence and from nowhere else.

Only then regenerate, and commit the images in the same commit as the change that moved them —
never in a commit of their own, which is a diff no reviewer can attach to anything.

⚠ **Build between regenerating and re-checking, or the suite will look nondeterministic.**
`--update-golden` writes to the `References` directory in the *source tree*, deliberately — rewriting
the copy beside the binary would "pass" and change nothing anybody commits. But `Verify` reads that
copy, and only a build refreshes it. So `dotnet test --no-build` immediately after a regeneration
compares the new rendering against the *old* reference and fails with the identical numbers it failed
with before, which reads exactly like a fixture that does not reproduce. It reproduces; run the
`GoldenImages` target, or build first.

## Writing one

A fixture is worth having when the mistakes it is looking for are **visible** — an upside-down
picture, a black one, a blown-out one — and worth checking by breaking it on purpose before it is
committed. `tonemapped-triangle` dims a gradient by an exposure the host wrote into a uniform block
and rolls it off against a white point the host never set; setting that white point to zero moves
17.6% of its pixels by up to 112/255. `depth-prepass` blends additively so that a fragment shaded
twice is a different colour from one shaded once; relaxing its depth comparison to `Always` turns the
overlap yellow and moves 17.2% by up to 204/255. A fixture that cannot be made to fail is asserting
nothing.

`shadow-cascade` is the one that took three attempts to become load-bearing. Its first two versions
passed a deliberate sabotage — sampling the wrong atlas tile — because the caster was bounded loosely
enough to survive every cascade's cull and therefore landed in every tile, so both tiles held the same
thing and the mapping was untested. It fails that sabotage now, with "nothing is shadowed anywhere".
**Sabotage the claim the fixture is supposed to make, not merely some claim**: a fixture that fails
when you break something unrelated tells you very little.

`shadow-cascade` gained a second job later: it now reads what `ShadowMapRenderer` **publishes** —
`cascades[i].viewProjection` and `cascades[i].split` — and picks its own cascade per fragment, running
the same search `ForwardPlus.CascadeOf` does. Before that it was handed one cascade's matrix and tile
by the test, which exercised `ShadowCascades.AtlasProjection` and nothing downstream of it. Note what
the picture can and cannot show: cascades overlap by design, so a fragment that picks a *farther*
cascade is still shadowed, just more coarsely. The failure that shows is the other direction — a far
fragment sent to a near cascade projects outside its tile and comes back unshadowed, which is what
reversing the comparison does here.

## Not every fixture is a picture

`ClusterCullingDeviceTests` dispatches `ClusterCulling.rvn` — compiled here through the same
`RavenEffectCompiler` the content build uses — and reads the cluster buffer back. There is no image,
and it belongs in this project anyway: it needs a device, and a device is what this project has.

It exists because of the shape of the bug it would have caught. `Transform.ViewRay` pointed down +Z
against a right-handed view space, so every cluster's box was mirrored away from the lights tested
against it and **every list came back empty** — a scene lit by the sun alone, which is a plausible
frame rather than a crash. Reverting that one character today fails this fixture with
`expected [0], got []`, which is the bug verbatim.

It runs the shader **through the compositor** — a `ComputeRenderer` node and the render graph — rather
than through a hand-written dispatch, which is the second thing it is for: the barrier between the
dispatch and the read is the graph's to place, and the uniform block is filled from the node's own
parameters. Recording it by hand tests the shader and leaves the path a frame actually takes
unexercised, which is how a compute node with no way to fill its uniforms went unnoticed.

The oracle is `ClusterGrid.Bounds` plus the sphere test written out again from the shader's own
description, compared over all 3456 clusters. Two guards keep it honest: a shader that wrote nothing
would agree with an oracle that expected nothing, so the fixture also asserts that *some* cluster
holds a light and that *not every* cluster does.

Its other half *is* a picture. `ClusteredShadingDeviceTests` renders one composed Forward+ frame —
the culler as a `ComputeRenderer` node, the shading pass reading what it wrote — and asserts a quad
comes back red from the light two units in front of it while the corner it does not cover stays the
clear colour. The clear is a deliberate blue for exactly that reason: `RenderPassRenderer.ClearColour`
defaults to transparent black, so a fixture that leaves it alone cannot tell a pass that ran and drew
nothing from a pass that never ran.

Two engine bugs stood between writing it and its passing, and the point of recording them here is
that **neither was visible to anything but a picture**. A composed material parameter's qualified
name depended on the order the lowerer merged types, so a single-pass compilation named it one thing
and the engine predicted another and every material value uploaded as zero. And one Raven struct used
in both a uniform block and a storage buffer became two SPIR-V types with the same debug name, which
a translator with one namespace for struct definitions collapses — on Metal the padded `float3` won
and the fragment stage read a light four bytes late, while the compute stage that filled the same
buffer read it correctly. Both produce valid SPIR-V, no validation message, and a black frame.

Where the arithmetic is beyond hand-checking — `bloom` is nine passes of bilinear taps — the fixture
asserts the **properties** a correct result has before it trusts the picture: the glow is centred on
its source, symmetric about that centre, and reaches well past it. Otherwise committing the first
reference is committing whatever came out first. Those assertions earn their place: setting the
chain's intensity to zero fails on "the glow does not reach past the source" rather than on a pixel
count.

Two more traps, both caught while writing the state fixtures and both worth knowing before adding
another:

- **Look at the picture before committing it.** `copy-region` copies a 4×4 block into an 8×8 texture
  and samples the whole thing — so its first version recorded the driver's uninitialised memory as
  the expected value of the other three quarters, which came out white and would have been a
  different colour on the next machine. It fills the texture first now.
- **Check that the state you set does something.** `depth-bias` asked for a bias of `0.002` and
  produced a picture identical to no bias at all, passing forever. Every API multiplies
  `RasterizerState.DepthBias` by the depth format's smallest resolvable difference, which for a
  32-bit float buffer is around `6 × 10⁻⁸`, so the number that reads as "tiny" is nothing whatsoever.
  Both failures look exactly like a fixture that works.
