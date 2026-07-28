# Golden images

Rendering fixtures, compared against committed reference images.

This is the level of testing every other kind is a proxy for. A command-stream assertion proves the
backend emitted the calls somebody intended; only a picture proves those calls draw what they were
meant to.

Four kinds of fixture, forty in all — the suite `docs/plan/05` § Testing asks for.

| Class | What it is about |
|---|---|
| `GoldenImageTests` | The **backend**, at its simplest: a clear, a triangle, an indexed quad, blending, reversed depth |
| `PipelineStateImageTests` | One **state bit** each — cull mode, topology, instancing, vertex formats, index offsets, depth comparisons and bias, stencil, blend factors and operations, write masks, viewport, scissor, multiple targets, load actions, sampler filters and address modes, and the two transfer paths |
| `CompositorImageTests` | The **renderer** — the layer engine code actually uses |
| `UiImageTests` | The user interface's GPU half |

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

The failure names how many pixels differed, by how much, and where the worst one was. It also writes
three files into `artifacts/golden-diff/`:

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

Then **look at what it wrote** before committing. A suite that rewrites its own expectations when
they fail is a suite that always passes.

## Comparison

Perceptual with an explicit threshold, not bitwise ([`docs/plan/05`](../../docs/plan/05-graphics-rhi.md)
§ Testing). Bitwise across drivers is a maintenance sinkhole: MoltenVK and lavapipe round the same
sRGB conversion differently and both are conformant, so a bitwise suite is red on one machine from the
day it is written and gets disabled within a month.

The metric counts pixels exceeding a per-channel threshold rather than taking a mean-squared error.
MSE is the obvious choice and the wrong one — a value low enough to pass a whole image hides a bright
artefact in a corner, which is exactly the failure this suite exists to catch.

These references were generated on MoltenVK and are verified against lavapipe on every push. The
tolerances are what that cross-driver agreement actually needs, not what one machine happens to
produce.

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

The oracle is `ClusterGrid.Bounds` plus the sphere test written out again from the shader's own
description, compared over all 3456 clusters. Two guards keep it honest: a shader that wrote nothing
would agree with an oracle that expected nothing, so the fixture also asserts that *some* cluster
holds a light and that *not every* cluster does.

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
