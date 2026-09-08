# The compound library

Doc 48 § 4.9's catalogue, as content. Every file here is a `.vxtexgraph` an author could have made in
the tool: no C#, no kernel, no build step. `TextureCompoundLibrary` embeds them and publishes them as
node types beside the atomic ones, and a project's own folder is published into the same menu.

⚠ **This folder is a measurement, and the measurement is the deliverable.** #575's first line:

> The kernels are the C#; the library is content. Anything here that has to become a kernel to be
> fast is a bug in the atomic set — that is the standing test of whether M2/M3 got it right, and this
> phase is where it is run.

So the interesting half of this file is not the list of what shipped. It is **what could not be
authored, and what it cost to author the things that could**.

## What ships

| Folder | Compound | Built out of |
|---|---|---|
| `Utility` | Histogram Scan | `Levels` with its two handles on the interface |
| | Histogram Range | `Levels`, output range from `position ± range/2` |
| | Histogram Select | two `Levels` and a `Darken`, then a contrast curve |
| | Contrast Luminosity | one `Levels`, all four handles from two knobs |
| | Highpass | `Blur` → `Invert` → `Blend Copy` at half opacity |
| | Equalize | `Highpass` at a coarse radius, then `Contrast Luminosity` |
| | Safe Transform | `Transform 2D` under `Wrap`, quarter turns and whole tile counts |
| | Make It Tile | offset-wrap, a mirrored `Gradation` cross for the seam mask, a masked composite |
| `Patterns` | Brick | `Shape Square` → `Tile` with a half-tile row offset |
| | Panels | `Shape Square` → `Tile` → `Blur` for the bevel |
| | Tile Random | `Tile Sampler`, jitter on size, turn and position |
| | Rivets | `Shape Paraboloid` → `Tile Sampler` |
| `Generators` | Dirt · Curvature Edge Wear · Grunge Rough Dirty | mesh maps, by usage |
| | Mask Editor | curvature, occlusion and noise under four sliders |

`TextureCompoundLibraryTests` proves each of these parses, publishes, names only ports that exist and
produces a plan; `TextureCompoundBakeDeviceTests` bakes every one of them on a device and refuses a
flat fill. `Make It Tile` has an oracle of its own, because it is the only one whose whole purpose is
a property rather than a look.

## What an author has, and what a compound is made of

- **Interface ports** — `Image`, `Float`, `Int`, `Bool`. A containing graph wires them or types a
  number into them, and they survive inlining because they are edges. **Reach for one first**: it
  costs no Raven compilation and it can be *driven* by another node.
- **Exposed parameters** — `Scalar`, `Integer`, `Boolean`, each with a range and a group. An
  expression inside the graph spells the name and the real Raven compiler folds it. This is what a
  port cannot do: **arithmetic**. `Contrast Luminosity` turns one `contrast` into an input range's
  two ends, and nothing in the atomic set computes that from a wire.
- **Nesting.** `Curvature Edge Wear` contains `Histogram Scan`; `Equalize` contains `Highpass` and
  `Contrast Luminosity`. Two levels of inlining, from files, with no code between them.

⚠ **The parameters half only started working recently, and this folder is the first content standing
on it.** [#742](https://github.com/Rikarin/Vixen/issues/742) — a sub-graph node's overrides were lost
in flattening, so a published graph's knob accepted a number and changed nothing — closed with
`NodeGraphInlining` carrying each expansion's settings. Its own closing note says none of the four
compounds that then shipped declared `parameters`, so the fix had no shipped reader.
`An_expression_in_a_shipped_compound_folds_and_an_override_moves_it` is now that reader.

## The dogfooding report — what could not be authored

Four findings, each measured rather than reasoned, and each filed.

### 1 · An expression on a sub-graph node's port is dropped, silently — [#1058](https://github.com/Rikarin/Vixen/issues/1058)

`=Radius` set to `"32f"` on a `Utility/Highpass` node produces an inlined `Blur` with radius **8** —
the port's declared default — and **no diagnostic at all**. `SubGraphs.Flatten` reads `node.Values`
for an unfed interface input and never the expression key.

This is #742's shape one level over and worse in one respect: #742's fix reports an unparseable
override against the node the author can select, and this reports nothing. Until it is answered, the
rule this folder follows is **arithmetic lives in the published graph's own parameters, and a
containing graph's ports carry plain numbers**.

### 2 · There is no masked composite — [#1059](https://github.com/Rikarin/Vixen/issues/1059)

`Colour/Blend` takes two images and a scalar opacity. A mask is an image, and no port takes one, so
`offset·(1 − m) + original·m` is four nodes and three intermediate images:

```
Colour/Invert            m → 1 − m
Colour/Blend  Multiply   offset × (1 − m)
Colour/Blend  Multiply   original × m
Colour/Blend  Add        the two
```

`Make It Tile` pays this. ⚠ `Blend.rvn` refuses a mask input by name — "a layer's mask is doc 48
§ M7's" — and that argument is about a **layer**, which is an object with a mask stack of its own. In
a graph an image is just an image. The two are different questions and the kernel answers only the
first.

### 3 · A compound's knobs are numbers only — [#1060](https://github.com/Rikarin/Vixen/issues/1060)

No name, no choice, no colour. `TextureGraphParameterKind` is `Scalar`, `Integer`, `Boolean`, and a
node's *setting* — `Placement/Tile Sampler`'s `Accumulation`, `Space/Transform 2D`'s `Tiling`, a
noise's `Basis` — is `Texts` on a node inside the graph that the interface cannot reach.

Consequences in § 4.9's own list: **Metal Reflectance** ("a named-metal lookup") cannot be authored at
all, and the `.vxsmartmat` family whose whole difference is a tint has nowhere to put the tint. Every
compound here hard-wires its settings, which is why none of them offers a mode.

### 4 · A call does not fold, and that is documented rather than broken

`floor(amount * 4f) * 0.25f` is `TG0014`: *"which Raven binds and cannot fold to a number at compile
time … a function call is not folded."* So `abs`, `min`, `max` and `smoothstep` over a knob are
unavailable, and a compound that wants one expresses it **structurally**: `Histogram Select` gets its
`min` from a `Blend Darken` over two `Levels`. `TextureGraphExpressions`' remarks state the limit and
say plainly it is the price of not writing a second evaluator. Quantisation is *not* affected — a
parameter declared `Integer` is already whole, which is how `Safe Transform` keeps its tile count safe.

## What did **not** turn out to be a gap

Worth recording, because two of these were predicted to be.

- **Per-instance variation.** `Tile Random` and `Rivets` were expected to expose a hole.
  `Placement/Tile Sampler` covers them completely: size, rotation, position and colour jitter are all
  ports, and the seed comes from the plan, so the same file is the same picture on every machine.
- **A brick bond.** `Space/Tile`'s per-row offset is exactly a running bond — `Tile.rvn` says so, and
  `Brick` is one `Shape` and one `Tile`.
- **Make It Tile**, which #575 flagged as the highest-value finding if it could not be authored. It
  can. An offset-wrap under `Transform 2D` is exactly periodic at the border by construction, and the
  seam mask is a `Shape Gradation` mirrored about the centre on each axis, combined with `Lighten`.
  Measured on an M1 Max: the wrap seam of a non-tiling gradient noise falls from 21.5 to 6.8 while an
  ordinary neighbouring step stays at 4.6.
- **The escape hatch, which exists and was deliberately not used.** `Filters/Pixel Processor` compiles
  a Raven expression into a real kernel, and since [#729](https://github.com/Rikarin/Vixen/issues/729)
  a plan carries that kernel's source, so it bakes. One expression would have replaced five of
  `Make It Tile`'s nodes. It is avoided here on purpose: a library that reaches for it stops being a
  measurement of the atomic set. ⚠ `PixelProcessorNode`'s own remarks still say the op does not
  evaluate — [#1061](https://github.com/Rikarin/Vixen/issues/1061).

## Adding one

Drop a `.vxtexgraph` under a folder here and it ships — `Compounds\**\*.vxtexgraph` is an
`EmbeddedResource` glob, and `TextureCompoundLibrary.Shipped` is derived from the manifest rather than
from a list. Both test files then cover it with no edit.

Three rules the tests hold you to:

- ⚠ **No dot in the file name.** A manifest resource name cannot tell a folder separator from one, so
  `Grunge v2.vxtexgraph` publishes under a path with a phantom folder in it.
- **A `Generators/` compound reads its maps by usage and names no mesh.** Every external it asks for
  is a `meshmap:` reference, which is what makes one generator work on every mesh.
- **One `Sub-graph/Output` and no `Output/Output`.** Inlining carries a second output into the
  containing graph, and two nodes writing `baseColor` is `TG0006`.
