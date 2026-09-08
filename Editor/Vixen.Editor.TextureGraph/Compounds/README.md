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
| | Metal Edge Wear | curvature, broken away by a noise through `Colour/Mix`, gated by occlusion |
| | Dust | a world normal's up axis and an occlusion under `Lighten`, then broken away |
| | Position Gradient | a position map through `Grayscale` weights, then `Levels` |
| `Grunges` | Concrete · Clouds · Damage · Fibres · Leaks · Rust · Scratches · Smears | `Noise` and `Slope Blur` in eight arrangements, which is § 4.9's own description |
| `Surface` | Height Blend | one `Levels` on a height, then one `Colour/Mix` |
| | Bevel | `Distance` inside → `Levels` for the profile → `Height to Normal` |
| | Curvature Smooth | `Blur HQ` → `Height to Normal` → `Curvature` |
| | Height to AO | `Ambient Occlusion` → `Blur` → `Levels` |

`TextureCompoundLibraryTests` proves each of these parses, publishes, names only ports that exist and
produces a plan; `TextureCompoundBakeDeviceTests` bakes every one of them on a device and refuses a
flat fill. `Make It Tile` has an oracle of its own, because it is the only one whose whole purpose is
a property rather than a look.

⚠ **Both of those roll calls had a floor that had stopped being able to fail, and that is worth
reading before trusting either.** The library's read `>= 4` and the bake's read `>= 12` while sixteen
compounds shipped — a floor written by the batch that authored the first four and never re-derived —
so twelve compounds could have stopped being embedded with every assertion in both files green. The
library's floor is now **per folder and quoted from § 4.9's own ● marks** rather than counted off the
tree, which is a claim the document makes and can therefore be checked by reading it rather than by
remembering to bump a number. Removing four grunges turns it red where the old whole-library floor
passed.

⚠ **And the bake roll call runs at 64×64 while every file here declares `baseWidth: 1024`** —
[#1085](https://github.com/Rikarin/Vixen/issues/1085). § D8 makes a filter's numbers *texels at the
base resolution*, so the roll call measures the library at one sixteenth of the scale it is authored
for: `Grunge Rust`'s 7.75-texel erosion bites into 73-texel cells at 1024 and wipes 4.5-texel ones at
64, where it baked four distinct values — one above the flat-fill bar. The compound was softened so it
reads at both extents, which is the fix for the file and not for the instrument.

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

Five findings, each measured rather than reasoned, and each filed.

⚠ **Written over two rounds and the second one is where the arithmetic gets checked.** The first round
authored twelve compounds and predicted three gaps; the second authored fifteen more — the grunge
family of eight, the Surface row, the last three mask generators — against an atomic set that had
gained `Colour/Mix` in the meantime. **Two of the first round's predicted gaps turned out not to be
gaps**, which is recorded below beside what did, because a round that only writes down its hits will
predict the same misses again.

### 1 · An expression on a sub-graph node's port is dropped, silently — [#1058](https://github.com/Rikarin/Vixen/issues/1058)

`=Radius` set to `"32f"` on a `Utility/Highpass` node produces an inlined `Blur` with radius **8** —
the port's declared default — and **no diagnostic at all**. `SubGraphs.Flatten` reads `node.Values`
for an unfed interface input and never the expression key.

This was #742's shape one level over and worse in one respect: #742's fix reports an unparseable
override against the node the author can select, and this reported nothing.

**Answered, and the rule this folder followed is retired.** ⚠ **An expression on a scalar sub-graph
port now folds**, against the scope it was written in — so a compound may carry `edge * 0.5f` on a
nested generator's port and get the number it says. `TG0003` was the intermediate state (a refusal,
because folding looked like a seam rather than a line) and the id is free again; the diagnostic that
survives is **`TG0016`**, for an expression naming a port the published graph has not got, or one on
a port that cannot hold a number.

⚠ **Two things it deliberately does not do.** An expression on a *wired* port is silent, which is the
universal wire-beats-value rule. And only `Float`, `Int` and `Bool` ports are claimed — an expression
is one number, and answering a `Float4` with one would splat it across four lanes.

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

**Answered.** `Colour/Mix` over `Shaders/Mix.rvn` is `Blend` with the opacity read per texel out of a
third image, and it is a second file rather than a fourth port on `Blend` — so § M7's refusal stands
exactly as written. The four-node form above is one node and one intermediate. ⚠ `Make It Tile` is
**not** rewritten to use it in the same change: the compounds are the *measurement* of the
atomic set, and editing the worked example out of a finding would leave nothing in the tree showing
what the gap cost.

⚠ **And "answered" is now measured rather than asserted, which it was not when it was written.**
"`Colour/Mix` composites correctly" and "`Colour/Mix` composites what the four nodes above computed"
are different claims, and only the second one licenses the rewrite this finding is about.
`The_masked_composite_computes_what_Make_It_Tiles_four_nodes_compute` builds both forms over the same
three noise fields **in one graph** — `TexturePlan.SeedFor` mixes the op's identity, so two graphs
would be two different pictures — and they agree to **1/255 at worst over 4096 texels** on an M1 Max,
with 3941 of those texels moved off the backdrop. Both guards are sabotage-proved against the kernel:
a `Coverage` returning 0 fires the "the mask did nothing" guard, returning 1 fires the equality at
95/255.

**What it made cheap in the second round, which is the other half of the measurement.** `Colour/Mix`
in `Copy` mode with a `Source/Uniform` **black** foreground is `background · (1 − mask)` — one node
where the atomic set wanted an `Invert` and a `Blend Multiply`, and it is the commonest thing a mask
generator does. `Generators/Metal Edge Wear` and `Generators/Dust` both break an edge mask away where
a grunge says, that way; `Surface/Height Blend` is one `Levels` and one `Mix` and would otherwise have
been one `Levels` and four. ⚠ **Nothing else in the fifteen wanted a node that does not exist.** The
masked composite really was the missing one.

### 3 · A compound's knobs are numbers only — [#1060](https://github.com/Rikarin/Vixen/issues/1060)

No name, no choice, no colour. `TextureGraphParameterKind` is `Scalar`, `Integer`, `Boolean`, and a
node's *setting* — `Placement/Tile Sampler`'s `Accumulation`, `Space/Transform 2D`'s `Tiling`, a
noise's `Basis` — is `Texts` on a node inside the graph that the interface cannot reach.

Consequences in § 4.9's own list: **Metal Reflectance** ("a named-metal lookup") cannot be authored at
all, and the `.vxsmartmat` family whose whole difference is a tint has nowhere to put the tint. Every
compound here hard-wires its settings, which is why none of them offers a mode.

⚠ **Narrowed by the second round, and the line is not where "no name, no choice, no colour"
puts it.** A *choice among channels* is authorable, because some nodes spell that choice as **numbers**
rather than as a setting: `Colour/Grayscale`'s `Weight R` / `Weight G` / `Weight B` are `Scalar`
**ports**, so `Generators/Position Gradient` exposes "which axis" as three knobs — and it is a better
selector than an enum would have been, because `-1` is the reversed ramp and a fraction is an oblique
one. The rule an author actually needs:

> A compound can expose whatever the atomic set spells as a `Scalar`, `Int` or `Bool` **port**, and
> nothing it spells as a `[Setting]`. The line is drawn by the node's declaration and not by the kind
> of thing being chosen.

So what stays blocked is sharper: a selector `int` (`Slope Blur`'s `Mode`, a noise's `Basis`,
`Tile Sampler`'s `Accumulation`), a name, and a colour. `Grunges/` pays the first of those visibly —
it is eight arrangements of `Noise` and `Slope Blur` where one "erode / dilate / mean" knob would have
made three of them one file. **Metal Reflectance stays refused rather than half-built**: a named-metal
lookup with the metal hard-wired is one row of § 4.9 with the row's whole purpose removed.

### 4 · A call does not fold, and that is documented rather than broken

`floor(amount * 4f) * 0.25f` is `TG0014`: *"which Raven binds and cannot fold to a number at compile
time … a function call is not folded."* So `abs`, `min`, `max` and `smoothstep` over a knob are
unavailable, and a compound that wants one expresses it **structurally**: `Histogram Select` gets its
`min` from a `Blend Darken` over two `Levels`. `TextureGraphExpressions`' remarks state the limit and
say plainly it is the price of not writing a second evaluator. Quantisation is *not* affected — a
parameter declared `Integer` is already whole, which is how `Safe Transform` keeps its tile count safe.

### 5 · The roll call measures the library at a sixteenth of its own extent — [#1085](https://github.com/Rikarin/Vixen/issues/1085)

Every file here declares `baseWidth: 1024` and `TextureCompoundBakeDeviceTests` bakes at **64**. § D8
makes a filter's numbers *texels at the base resolution*, so a compound whose knobs are texel-valued
is measured at a sixteenth of the scale it was authored for — in both directions. `Grunge Rust` is the
worked case: a Worley field at scale 14 eroded 7.75 texels bites into 73-texel cells at 1024 and wipes
4.5-texel ones at 64, where it baked **four** distinct values against a flat-fill bar of three.

⚠ **The dangerous direction is the other one.** A compound that saturates at 1024 — a `Blur` wider
than the picture, a `Distance` whose `Max Distance` covers everything — is invisible at 64 and passes,
which is exactly the class § D8 exists to prevent. This is not covered by exit criterion 2's scale
invariance: that one walks the **atomic** nodes and never a compound.

## What did **not** turn out to be a gap

Worth recording, because four of these were predicted to be — and **two of the first round's three
predictions were wrong**, which is the number that should make the next round slower to predict.

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

The second round's four, which are the ones a third round should not re-predict:

- **A grunge library needs a grunge kernel.** It does not. § 4.9's own description — "`Noise` and
  `Slope Blur` in eight arrangements" — turned out to be exact rather than dismissive: eight files,
  three to five nodes each, and the only thing they share is that the *slope* field is a second noise
  chosen to disagree with the first. What distinguishes them is the slope mode (`Min` erodes into a
  pit, `Max` dilates into a streak, `Blend` smears), which is a `[Setting]` and therefore hard-wired —
  #1060 again, and the reason the family is eight files rather than one with a knob.
- **A bevel needs a distance transform with a profile.** `Analysis/Distance` in `Inside` mode is a
  height field already, and the profile is a `Colour/Levels` gamma. `Surface/Bevel` is three nodes.
- **A height blend needs a comparison the atomic set has not got.** It does not: the comparison is a
  `Colour/Levels` on the deciding height with the two handles folded off `position ± softness/2`, and
  the cut is one `Colour/Mix`. Two nodes, and the sharp transition a height blend is *for* comes from
  the handles being close together rather than from anything special.
- **Reading a world normal's up axis needs a swizzle node.** `Colour/Grayscale` is the swizzle — see
  finding 3 — and `Generators/Dust` takes the up axis out of a world normal with weights `(0, 1, 0)`.

⚠ **And one thing that is neither a gap nor free: a Worley basis writes three different things into
three channels** (F1, F2 and a cell index), so a cell field used as a `Slope Blur` slope is `TG0004`
and costs a `Colour/Grayscale`. `Grunge Concrete` and `Grunge Damage` both pay one node for it. The
diagnostic names the port and says what to do, so this is the atomic set working rather than failing —
but it is the one thing in the fifteen that a first draft got wrong twice.

## Who reads them, and the edge that is a string

Two consumers, and they fail differently.

- **A graph** contains a compound as a node. The path is a node *type*, so a compound that stopped
  shipping is a node type the registry has not got and `NodeGraphDocument.Load` says so when the
  document opens.
- **A layer stack's mask** names a compound as `MaskAsset.Generator` — "the published compound's
  node-type path", carried as **text**. ⚠ Nothing resolves it until `LayerStackGraph` compiles the
  stack, so a compound renamed or moved between folders leaves a mask that refuses **at bake time, on
  somebody else's machine**, with a sentence about a node type rather than about the material.

That second one is the only edge between this folder and `Vixen.Editor.Texturing`'s content, and it is
the one a rename breaks silently. Doc 48 § M10's five `.vxsmartmat` smart materials — `Painted Metal`,
`Rusted Iron`, `Worn Wood`, `Concrete`, `Plastic`, under `Vixen.Editor.Texturing/SmartMaterials/` —
are built almost entirely out of masks from this folder, so
`SmartMaterialContentTests.Every_generator_a_shipped_smart_material_names_is_a_compound_that_ships`
resolves every one of those strings against the registry the compounds are published into. It counts
them as well as checking them: five smart materials whose masks were all constants would satisfy the
loop and prove nothing, and a stack of constant-masked fills is exactly what a half-authored one is.

⚠ **A compound retired from this folder is therefore a breaking change to content in another
assembly**, which is a stronger claim than "a node type vanishes from a menu" and is why the roll
call's named list is a deliberate edit rather than a snapshot.

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
