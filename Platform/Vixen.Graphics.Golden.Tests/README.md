# Golden images

Rendering fixtures, compared against committed reference images.

This is the level of testing every other kind is a proxy for. A command-stream assertion proves the
backend emitted the calls somebody intended; only a picture proves those calls draw what they were
meant to.

Two kinds of fixture. `GoldenImageTests` renders from a command list and is about the **backend**: a
clear, a triangle, an indexed quad, blending, reversed depth. `CompositorImageTests` renders from a
`GraphicsCompositor` and is about the **renderer** — the layer engine code actually uses, and one
that had been asserted entirely against a recording backend, which will happily record a descriptor
set bound to the wrong index and a uniform written at the wrong offset and report that the calls were
made.

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
17.6% of its pixels by up to 112/255. A fixture that cannot be made to fail is asserting nothing.
