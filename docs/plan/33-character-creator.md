<!--
SPDX-FileCopyrightText: Copyright (c) Rikarin
SPDX-License-Identifier: Apache-2.0
-->

# 33 — Character creator

A digital-human toolset shaped like MetaHuman: a character is a **parameter vector over a fitted
statistical model**, not a mesh; a face rig is a **compiled program** evaluated per frame rather than
a DCC graph replayed; wardrobe items **conform to the body** they are worn on instead of being
authored per body; and the whole thing **bakes down** to ordinary skeletal meshes, textures and
material instances so a shipped game pays nothing for the creator that made them.

⚠️ **Extends [06](06-rendering-pipeline.md), [08](08-asset-pipeline-and-addressables.md),
[16](16-networking.md), [20](20-editor-parity.md) and [28](28-gameplay-framework.md).** It is a
separate file for the reason [22](22-virtualized-geometry.md), [24](24-blockout-tools.md) and
[31](31-terrain-grass-and-trees.md) are: it is much larger than a row in a status table, five
subsystems own a piece of it, and the first half is an argument rather than a schedule.

**The claim this document has to earn.** A character's *identity* — face, body, skin, hair, clothes —
is a few hundred bytes of parameters that a solver turns into geometry, and every consumer downstream
of that (the renderer, the animation system, the network, the save file, the crowd) sees ordinary
engine objects and knows nothing about the creator. If that claim fails anywhere, the honest answer
is a character importer and a slider panel, not a creator.

**Read [the rows this touches](#the-rows-this-touches) before the phases.** One of them is a
deliberate out-of-scope decision in [20 § Part G](20-editor-parity.md#part-g--out-of-scope) and it is
*not* being overturned — it is being drawn more precisely, once, and the precision is the whole
argument for why this is buildable at all.

**And read [the content problem](#the-content-problem-which-is-not-an-engineering-problem) before
believing the schedule.** The engineering here is large but ordinary. What decides whether any of it is
*usable* is a fitted model of how humans vary — which is not code and cannot be written by an engineer,
and is the reason MetaHuman was an acquisition rather than a feature. ⚠ **That section has been
rewritten once already**: it used to conclude that acquiring a scan library was the gate, and that was
wrong. Two permissively-licensed fitted models exist, and what survives of the problem is narrower and
differently shaped.

**[34](34-move-sets-and-pose-constraints.md) landed after this document was written**, and the two
turn out to be closer than either's scheduling section suggests. Neither blocks the other; but 34
supplies an arbitration mechanism this document needed and did not have, this document supplies a
guarantee 34 lists as a risk, and **three evaluation-order rules fall out that neither document stated
and both need** — [§ 34](#34--move-sets-and-pose-constraints) is the reconciliation, and it has since
been reconciled from 34's side too, which is noted where it happened.

---

## The rows this touches

Six, and only one of them is a reversal.

### 20 § Part G — "Modelling tools"

[Part G](20-editor-parity.md#part-g--out-of-scope) lists, under things deliberately not being built:

> **Modelling tools** — sculpting, retopology, UV unwrapping, map baking, remesh — Authoring for its
> own sake, and not what an engine is for. Import from a DCC.

**That stays, in full, and this document does not reopen it.** What needs saying is why a face
creator is not on the far side of it, because at a glance "drag a point on the cheek" is sculpting.

It is not, and the difference is not a matter of degree:

| Sculpting a mesh | Editing a character |
|---|---|
| The output is arbitrary geometry | The output is *always* a mesh in one fixed topology, with the same vertex count, the same UVs, the same skin weights and the same rig |
| A vertex moves where it is pushed | A control's displacement is **projected onto a model of how human faces vary** — pushing the cheek out moves the zygomatic region the way a wider zygomatic actually looks, including the parts nobody dragged |
| A result can be non-manifold, self-intersecting, or unriggable | A result cannot be any of those, because it is a point in a space every one of whose points is a valid human |
| Needs UV unwrap, retopo, weight paint, rig, LOD chain | Needs none of them — they are the template's, and the template is authored once |

⚠ **This is the same redrawing [24](24-blockout-tools.md) did to the same row, and it earns its place
the same way**: the test is not "is a vertex moving", it is "what does the tool guarantee about its
output". Blockout guarantees a closed solid a designer can walk through five seconds later; a
character creator guarantees a rigged, textured, LOD'd human. Neither guarantee is available from a
general mesh editor, which is exactly why neither is a general mesh editor.

The row gains a pointer. It does not change.

### 06 — "Blend shapes ⬜"

[06 § Geometry](06-rendering-pipeline.md) carries `Blend shapes ⬜` with no note, and
[overview](../overview.md) repeats it. It is promoted from an unscheduled row to **the hard
prerequisite of everything below**, and it is [P0](#p0--the-two-missing-primitives-15-em). A face rig
without morph targets is a face rig that can only rotate joints, which is a puppet.

### 06 — "SSS blur", filed under Phase 10

The same list carries `Volumetric fog, contact shadows, light shafts, SSS blur, upscaler + FSR1 ⬜`.
`Raven/Library/Shading/Subsurface.rvn` already says, in its own header, that real subsurface
scattering needs "either a screen-space blur pass (see `PostFx`) or a precomputed profile" and that
what it implements is the per-light part. **The face is the consumer that makes the missing half
matter**, and it moves onto this document's critical path — [P3](#p3--skin-15-em).

### 20 § B5 — Authoring

The authoring-panel inventory gains one row: **Character creator · MetaHuman Creator / — · ⛔**. Unity
ships no first-party equivalent at all, so this is a row where the comparison column is honestly
empty on one side.

### 14 — Roadmap

A **post-1.0 track**, like [21](21-realtime-collaboration.md) and the back half of
[19](19-lighting-and-global-illumination.md). Two items of it are not: [P0](#p0--the-two-missing-primitives-15-em)
is a rendering primitive several other things want, and [P3](#p3--skin-15-em)'s skin path is Phase 10
work that a face merely makes urgent.

### 34 — Move sets and pose constraints

[34](34-move-sets-and-pose-constraints.md) already names this document in its own rows-this-touches,
correctly, and concludes *"Neither blocks the other"*. That is true of the schedule and it understates
the coupling, because the two documents are the same problem seen from opposite ends of one skeleton:
**this one makes bodies vary continuously, and that is precisely the condition under which an authored
clip stops being correct.**

Six consequences. Four are gains; two are defects that would otherwise be found in content.

⚠ **1. Pose-driven correctives must evaluate *after* the constraint stage.** This document's
[D2](#d2--the-model-is-an-asset-the-solver-is-the-engine) gives the archetype *pose correctives* —
the JCM mechanism — which fire from joint angles. [34 § D10](34-move-sets-and-pose-constraints.md)
places `ConstraintStack` in `IPoseProcessor`, which **changes joint angles**: an elbow bent to reach a
goal is a different elbow from the one the clip animated. Correctives computed before the stack are
computed for a pose the character is not in. [D14](#d14--the-characters-work-has-a-place-in-the-pose-pipeline-and-it-is-last)
fixes the order, and it is worth being explicit about the symptom because it does not look like an
ordering bug: a shoulder that deforms correctly while idle and collapses whenever the hand reaches for
something reads as bad skin weights, and somebody will spend a day repainting them.

⚠ **2. The neck now has two claimants, and 34 is the mechanism that settles it.** Part 3 below warns
about *"two things fighting over the neck joints"* and names no arbiter, because at the time there was
none. 34's `Aim` goal turning a head is exactly `FACIAL_C_Neck1Root` and `FACIAL_C_Neck2Root` — the
joints [the DNA survey](#metahuman-dna--four-layers) records as never-modify. `IConstraintArbiter`
plus label suppression is the answer, and [D14](#d14--the-characters-work-has-a-place-in-the-pose-pipeline-and-it-is-last)
takes it: the face rig owns the facial joints outright, the neck is a labelled goal like any other, and
the two negotiate through one arbiter rather than by write order.

**3. Proxy shapes must be *derived*, and that closes 34's R4.**
[34 § D13](34-move-sets-and-pose-constraints.md) has a `ProxyShapeSet` authored by hand against a
skeleton, and [34 § R4](34-move-sets-and-pose-constraints.md) is the risk that inconsistent naming
across characters breaks clip portability silently. Under this document hand-authoring is not merely
tedious, it is **impossible** — there is no finite set of characters to author against. The archetype
already regresses joints from a shape; regressing the proxy set is the same solve with a different
output, so every character on one archetype shares one shape vocabulary **by construction** and the
naming risk cannot occur. [D15](#d15--proxy-shapes-are-derived-from-the-archetype-like-joints) is that
decision.

✅ **34 has since taken this**, and split the risk in two: R4 now records that D15 closes it outright
for characters that come from an archetype, and answers the hand-authored remainder with a declared
`.vxshapevocab` — turning what its earlier draft called a project convention the tool could only report
on into an import error. **Neither document could have reached that split alone**: derivation does
nothing for a hand-authored character, and a vocabulary is redundant for a derived one.

**4. [34 § D14](34-move-sets-and-pose-constraints.md) is this document's claim, stated from the other
side.** *"A hand on the belly of a slim character resolves to the belly of a heavy one; the same clip
works on both."* Normalised surface coordinates are the reason a contact survives a parametric body at
all, and they are the piece this document would otherwise have had to invent. Taken wholesale.

**5. 34 P4's exit criterion becomes testable.** It asks for *"one authored clip, three bodies of
visibly different proportions, hand contact correct on all three"*. Before [P4](#p4--the-model-and-the-solver-275-em)
that is three hand-sculpted bodies and a weak test — three points chosen by whoever wanted it to pass.
After it, it is three points sampled from a continuous space, and the interesting sampling is at the
model's edges.

**6. `IGaitModel` should be able to read the measurement map** — raised here as a note back to 34, and
✅ **34 has since taken it**. Stride length is a function of leg length, and this document's measurement
map ([D2](#d2--the-model-is-an-asset-the-solver-is-the-engine)) is the only thing that knows it; a
1.5 m and a 2.0 m character asked for 4 m/s want different gait targets, and given the same ones the
short one skates. [34 § D8](34-move-sets-and-pose-constraints.md) now reads leg length from the
measurement map where a character carries one **and falls back to the skeleton's bind pose where it
does not** — which is the half this document did not think of, and is what keeps `IGaitModel` usable
on a rig that never met an archetype.

And one dependency that was missing: [D13](#d13--a-named-control-set-is-the-interoperability-surface)
says a clip stores facial curves named by the control set, and [P2](#p2--import-15-em)'s exit is a
MetaHuman face moving *driven by a clip*. **There is no runtime path for `.vxanim` until
[34's P0](34-move-sets-and-pose-constraints.md) builds one**, which this document did not say and now
does.

### Where the line goes

| In | Out |
|---|---|
| A parametric body: measurements and semantic axes over a fitted statistical model | A body sculpting tool; per-vertex body edits |
| A parametric face: blending archetypes, then constrained region edits | Free-form face sculpting; wrinkle-map painting; pore-level detail authoring |
| A compiled facial rig — controls, correctives, joint deltas, morph weights, per-LOD joint subsets | Authoring a facial rig from nothing; a rigging tool |
| Fitting the template to a supplied scan or mesh | Being a scan-processing pipeline — cleanup, hole fill, photogrammetry |
| Layered skin: tone, secondary tones, freckles, blemishes, makeup, over an authored texture set | Painting textures; a material graph per character |
| Wardrobe slots, garments that conform to the body, hair slots | Being a garment modeller or a groom modeller — author in Marvelous, Maya, Blender or Houdini |
| Cloth simulation on a garment, driven by the body's motion | A cloth solver of our own — this is `Vixen.Physics`'s, and it does not have one |
| Hair as cards at every LOD, strands at the top one | Strand simulation, strand grooming tools |
| A bake that emits ordinary skeletal meshes, textures and materials | A runtime that keeps the creator's data resident in a shipped game by default |
| The *same solver* in-game, so a title can ship a character creator | A UI kit for that creator — a game builds its own from `Vixen.Ui` |
| Import of MetaHuman DNA and of assembled MetaHuman meshes | Reimplementing MetaHuman Creator's model data, which is not ours and not reproducible |
| One archetype pack, assembled from permissively-licensed fitted models and honest about its range | Pretending we have Epic's scan library, or running a capture programme to get one |

---

## What the references actually ship

Surveyed rather than remembered. "Copy MetaHuman" is not a specification, and the parts of MetaHuman
worth copying are not the parts that are visible.

### MetaHuman, in the order it runs

Since **Unreal Engine 5.6** the creator is
[a tool inside the editor](https://dev.epicgames.com/documentation/metahuman/metahuman-creator-in-unreal-engine)
rather than a cloud application: a MetaHuman Character asset is created in the Content Browser and
double-clicked open. That move is the single most important fact in this survey, and
[D8](#d8--one-solver-editor-and-runtime) is what Vixen takes from it.

#### The asset graph

From [Assets Overview](https://dev.epicgames.com/documentation/en-us/metahuman/assets-overview):

| Asset | What it is |
|---|---|
| **MetaHuman Character** | The source of truth — "face DNA, body shape, skin, eyes, and makeup settings" |
| **DNA (`UDNA`)** | The rig and geometry description; see below |
| **Skeletal meshes** | Face and body, generated under the character's folder |
| **Grooms** | Hair, eyebrows, eyelashes, fuzz, moustache, beard — strands at high LODs, cards or meshes lower down |
| **Textures / material instances** | Baked per character |
| **`BP_<Name>`** | The assembled actor: mesh components, grooms, a **LODSync** component, and a MetaHuman component |
| **Collection / Instance / Wardrobe Item / Pipeline** (5.8) | A *non-destructive* layer: a Collection is slots with items assigned; an Instance is one selection from a Collection; a Pipeline defines the slots and the assembly logic in C++ |

The 5.8 Collection/Instance split is the interesting one, and it is the same shape this document
reaches for from the other direction: **the assembled character stops being the artefact and becomes
a projection of a recipe**, which is what makes runtime customisation possible at all.

#### MetaHuman DNA — four layers

[The format](https://dev.epicgames.com/documentation/metahuman/metahuman-dna-rig-definition-and-rig-operation)
"encodes all information necessary to assemble and configure a full MetaHuman head, body and its
rigs", and its [open specification](https://github.com/EpicGames/MetaHuman-DNA-Calibration) describes
four layers that load independently:

| Layer | Holds |
|---|---|
| **Descriptor** | Name, archetype, metadata, compatibility parameters |
| **Definition** | The *static* rig: control names, joint names, blend-shape names, animated-map names, mesh names, LOD mapping, joint hierarchy, bind pose |
| **Behavior** | The *dynamic* rig: GUI→raw control mapping, corrective expressions, joint deltas, blend-shape weights, animated-map weights |
| **Geometry** | Vertex positions, skin weights, blend-shape target deltas |

Two properties of that split matter more than the field list:

1. **The geometry layer is separable**, so "the same DNA file" can drive characters of different
   shapes and sizes — the behaviour of a face and the shape of a face are different data.
2. **The definition is shared.** A Rig Definition fixes "the names and numbers of shapes" and is
   static across every character built on one archetype. Only the numbers vary per person.

The joint taxonomy is worth copying verbatim: **surface joints** placed automatically on the mesh
surface (the majority), **volume joints** that move large regions and sometimes need manual
placement, **body joints** supplied by the body rig and not to be altered, and pupil joints that
uniquely use dynamic scaling. Three joints — `FACIAL_C_Neck1Root`, `FACIAL_C_Neck2Root`,
`FACIAL_C_FacialRoot` — are named as never-modify, which is the kind of fact a format only learns by
being wrong once.

**Eight LODs, 0 through 7.** Each LOD has its own geometry and its own skinning weights, there is no
mesh sharing between levels, and **only LOD 0 carries per-vertex shape displacement**. One joint set
serves all LODs; lower levels *exclude* joints rather than defining their own.

#### RigLogic — the evaluator

[The whitepaper](https://cdn2.unrealengine.com/rig-logic-whitepaper-v2-5c9f23f7e210.pdf) is the best
technical document Epic has published about MetaHuman, and it is where the shape of
[D3](#d3--a-face-rig-is-a-compiled-program) comes from.

The problem it solves: a state-of-the-art facial rig is **~800 joints, 200+ expression controls, and
1000+ corrective expressions**, and evaluating it the way a DCC does is not a real-time operation.
RigLogic is a solver that takes the semantically meaningful controls and produces deformation, in a
fixed pipeline:

```
GUI controls  →  raw controls  →  correctives (PSDs)  →  ┬─▶ joint deltas
(what an animator moves)        (products of raw        ├─▶ blend-shape weights
                                 controls)              └─▶ animated-map weights (to shaders)
```

The details that are engineering rather than description:

- **Every mapping is linear** — `y = k·x` per output per input — so the whole rig is a sparse matrix
  and evaluation is a sparse matrix–vector product. Everything non-linear lives in the *correctives*,
  which are products of raw controls fed back in as further inputs.
- **Correctives are selective.** "Due to memory and computation costs, it is not feasible to enable a
  corrective solution for every possible combination" — so the rig stores the combinations that
  matter and no others. Which combinations those are is authored knowledge, not derivable.
- **The matrix is repacked, not just stored sparsely.** Compressed-row storage was replaced with
  dense sub-matrix blocks after pruning outliers, "in a cache- and SIMD-friendly manner, minimally
  padded such that no scalar remainder loop is necessary" — a claimed **~6× improvement** in joint
  evaluation with *more* joints evaluated, at under 2% padding overhead. Overall data reduction is
  claimed at 10–15×.
- **LOD reduction is free.** Every LOD's joint set is a strict subset of LOD 0's, so raising a LOD
  adds joints to data already loaded and lowering one ignores rows. No rebuild, no second rig.
- **Animated maps are outputs too** — scalar multipliers fed straight to shaders for wrinkles and
  blood-flow effects. The rig drives *shading*, not only geometry.

**And Epic open-sourced the evaluator.** [OpenRigLogic](https://github.com/EpicGames/openriglogic) is
MIT-licensed C++ with Python bindings: a DNA read/write library and the RigLogic evaluator, explicitly
intended to let third-party tools "animate the character with the same runtime evaluation Unreal
Engine uses", on consoles and mobile as well as the three desktops. What is *not* open is the model
that produces a face from a set of sliders — [the content problem](#the-content-problem-which-is-not-an-engineering-problem).

#### The tools

[Head and Body](https://dev.epicgames.com/documentation/metahuman/metahuman-creator-head-and-body-tools-in-unreal-engine)
is five tools, and their division of labour is the one to copy:

| Tool | What it does |
|---|---|
| **Blend** | Combine **up to three presets** and reshape head and body toward them, globally or per region |
| **Body Params** | Proportions from semantic and measurement inputs — or a fixed "compatibility" body |
| **Head Transform** | Broad repositioning of facial features |
| **Head Sculpt** | Precise, localised feature adjustment |
| **Teeth and Eyelashes** | Exactly that |

All five require a character **without a rig** — there is a *Remove Rig* button, and re-rigging is an
explicit step. That is not an implementation wart; it is the honest consequence of the rig being
*fitted to* a shape rather than living independently of it, and [D3](#d3--a-face-rig-is-a-compiled-program)
adopts the same edge.

[Body Params](https://dev.epicgames.com/documentation/metahuman/metahuman-creator-body-params-tool-in-unreal-engine)
is the most instructive tool in the set:

- **Global** parameters: masculine/feminine, muscularity, fat, height.
- **Regions**: upper torso, lower torso, neck/arms, legs, hands.
- **Diagnostics** (read-only): shoulder height, rise.
- The sliders move through "a statistical space defined by real-life data", so **parameters
  correlate** — raising muscularity moves fat, because it does in the population.
- **Pinning** is how you fight the correlation: editing a parameter auto-pins it, and a pin holds a
  measurement while the rest of the body re-solves around it. Pinned measurements draw themselves in
  the viewport.
- A fixed compatibility body converts to parametric with *Perform Parametric Fit*, and the docs say
  plainly that the conversion is approximate.

That last group is the design. A parametric body is not sliders wired to blend shapes; it is a
**constrained solve** — "find the point in the model space that satisfies my pinned measurements and
is otherwise as close as possible to what the un-pinned parameters ask for". [D2](#d2--the-model-is-an-asset-the-solver-is-the-engine)
is that sentence, formalised.

#### From a custom mesh

[The From Custom Mesh tool](https://dev.epicgames.com/documentation/metahuman/metahuman-creator-from-custom-mesh-tool-in-unreal-engine)
takes a scan, an AI-generated model or a sculpt "with non-standard topology" and conforms it to
MetaHuman topology while preserving the original proportions: import, choose full-character or
head+body, **Auto Solve**, refine with keypoints, manual solve for problem areas, then export DNA or
assemble. The stated failure modes are the useful part: the solver wants human-like anatomy in an
A-pose, "cartoon-like features, oversized heads, tiny bodies, sharp stylized geometry" solve with
varying quality, heavily-clothed meshes solve badly because clothing is read as the body surface, and
holes or self-intersections are not repaired.

#### Wardrobe

[Hair and Clothing](https://dev.epicgames.com/documentation/metahuman/hair-and-clothing-controls) is
a slot system: six hair slots — **head hair, eyebrows, eyelashes, moustache, beard, peach fuzz** —
one item each; clothing slots take several. Items must be **prepared** before first use, which caches
per-character data on the character asset and can be **unprepared** to release the memory. Outfits
"automatically resize to match the character's body shape and continue adapting if body modifications
occur". Custom items enter by drag-and-drop into a slot or by pointing project settings at a watched
folder. A **Costume** panel exposes each worn item's attributes, overridden per character without
touching the source asset. Compatibility is validated, and failures produce warnings rather than
broken characters.

Garments come in two kinds
([Tailoring](https://dev.epicgames.com/documentation/en-us/metahuman/tailoring-your-own-wardrobe-items)):
**parametric outfits** — Chaos Outfit assets that resize with body measurements, with a size override
for a deliberately tighter or looser fit — and **skeletal clothing**, fixed-size meshes for exact
silhouettes. Grooms come from the Maya plugin's XGen descriptions, from Houdini's MetaHuman Groom
Tools, or from Fab. Everything travels as an `.mhpkg`.

#### Assembly

[Assembly](https://dev.epicgames.com/documentation/metahuman/assembly) turns a character into
project-ready assets, through one of four pipelines:

| Pipeline | For |
|---|---|
| **UE Cine** | Full fidelity, offline rendering or performance-secondary work |
| **UE Optimized** | Comparable fidelity, real-time budgets, three tiers (High / Medium / Low) |
| **UEFN Export** | UEFN-ready, three tiers |
| **DCC Export** | Moved to the Export tool in 5.8 |

And the numbers, which are the reason this section exists:

| | Cinematic | Optimized |
|---|---|---|
| Memory per character | **1–2 GB** | **under 100 MB** |
| Textures | Full resolution | Compressed |
| Hair | Strand-based | Optimised groom settings |
| LODs | Quality-first | Aggressive |

A **10–20× memory difference for "comparable fidelity"** is the strongest argument in the survey for
[D10](#d10--a-character-bakes-down-and-the-bake-is-the-shipped-artefact): the creator's
representation and the shipped representation are different things, and an engine that conflates them
ships the first one.

The Cine and Optimized pipelines "bake down" materials and textures through a Texture Graph into
real-time-friendly versions, with per-texture resolution configurable in the Assembly tool.

#### Crowds

[MetaHuman Crowds](https://dev.epicgames.com/documentation/en-us/metahuman/metahuman-crowds) (5.8,
experimental) assembles optimised *instances* compatible with Mass, "scaling from tens to thousands",
and transitions between a high-fidelity actor and a low-fidelity instanced skeletal mesh by camera
distance. Filed here as evidence for a decision rather than as something to build: it exists because
the assembled character is too heavy to instantiate a thousand times, which is the same fact the
memory table states.

#### The facial animation standard

[The MetaHuman Facial Description Standard](https://dev.epicgames.com/documentation/en-us/metahuman/mh-standards-docs)
is a named set of **control curves** that is "the basic representation of a MetaHuman facial
animation" — what a baked AnimSequence contains and what MetaHuman Animator exports. The rig was
built on FACS foundations, and the surrounding tooling speaks FACS, ARKit, Preston Blair and 3ds Max
phoneme sets.

⚠ **The standard is the interoperability layer, not the rig.** A capture solver, a lip-sync system, a
hand-animated clip and a retarget all agree on a list of named scalars; what each scalar *does* to a
face is that face's rig. This is the piece most third-party "MetaHuman-compatible" work actually
plugs into, and [D13](#d13--a-named-control-set-is-the-interoperability-surface) adopts it.

### Reallusion Character Creator 4

The commercial competitor, and it solves the problem differently: a base figure plus a very large
library of **morph sliders**, with custom sliders authorable in-app via Edit Mesh. **SkinGen** is a
layered skin material system — tones, makeup, effects — that flattens to textures on export.
**AccuRIG** auto-rigs arbitrary humanoid meshes. Export targets game engines directly, with an
Auto Setup plug-in that carries materials and parameters across, and skeleton presets that land on
Unreal's own rigs.

What Vixen takes: **layered skin that bakes** ([D11](#d11--skin-is-layers-that-bake-not-a-texture-set-per-character))
and the fact that a slider library and a statistical model are not the same product. CC4 is a slider
library — powerful, art-directable, and with no notion of "this combination is not a real human".

### Daz Genesis

The longest-running morph-based figure system. Genesis 9 returns to **one mesh for all genders**,
around 30k quads, with gender and everything else expressed as morphs on a single shared topology;
**JCMs** (joint-corrective morphs) fire from joint angles to fix deformation as the figure bends;
proportions and asymmetry are parameterised. The entire third-party content economy — clothing,
hair, morphs — exists because the topology never changes.

What Vixen takes: **JCMs are the same mechanism as RigLogic's correctives, arrived at independently**
by a completely different product two decades earlier. A pose-driven corrective is not a MetaHuman
idea; it is what happens to anyone who skins a shoulder. And: **shared topology is the entire
economy**. It is why [D1](#d1--identity-is-a-parameter-vector-geometry-is-derived) fixes the
template first and everything else second.

### The academic parametric models

The public state of the art in "a human body as a low-dimensional parameter vector":

| Model | What it is |
|---|---|
| **SMPL** | A skinned vertex-based body: shape as PCA coefficients over a mean mesh, pose-dependent corrective blend shapes, joints **regressed from the shape** |
| **SMPL-X** | SMPL plus articulated hands and an expressive face |
| **FLAME** | The same construction applied to the head — identity, expression and pose spaces |

⚠ **All three are licensed for non-commercial research use**; commercial use requires a separate
licence from Max Planck / Meshcapade
([SMPL](https://smpl.is.tue.mpg.de/modellicense.html), [SMPL-X](https://smpl-x.is.tue.mpg.de/modellicense.html)).
**Vixen cannot ship any of them**, and a plan that quietly assumed otherwise would be worthless.

What Vixen takes: **the construction, which is not encumbered.** "Mean mesh + shape basis +
shape-dependent joint regressor + pose-dependent correctives" is a published architecture, and it is
almost exactly what MetaHuman does. What is encumbered is the *fitted data* — the basis learned from
several thousand scanned bodies. That distinction is the whole of
[the content problem](#the-content-problem-which-is-not-an-engineering-problem).

### In-game creators, and Unity

Shipping games — the Sims, Black Desert, Cyberpunk 2077, Baldur's Gate 3, every MMO — all build the
same thing: morph sliders over a fixed topology, swappable meshes in slots, texture layers for skin
and makeup, and a compact serialised representation so a character can be saved, shared and
replicated. None of them share tooling with the DCC that authored the base mesh, and all of them run
the creator **inside the game**.

**Unity ships no first-party equivalent.** Its Digital Human package is a set of shaders and a demo
character, not a creator. This is one of the very few places in [20](20-editor-parity.md) where the
comparison table has an empty column, and it is worth noticing which way the empty column cuts: it is
evidence that this is genuinely hard and genuinely optional, not evidence of an easy win.

### What the survey settles

1. **A character is a recipe, not a mesh.** Every serious system has converged on this, and the two
   that started elsewhere (MetaHuman pre-5.8, CC4) have been moving toward it.
2. **Topology is fixed and shared, forever.** It is the precondition for conforming clothing,
   transferable grooms, a reusable rig, a third-party economy, and morph-based identity.
3. **The rig is compiled, and correctives are the expensive, authored part.** Both RigLogic and JCMs
   are the same answer, and neither is derivable from the base mesh.
4. **Sliders correlate, and the interesting UI problem is pinning**, not slider count.
5. **The creator representation and the shipped representation must be different**, by an order of
   magnitude in memory.
6. **Interoperability happens through a named control set**, not through a rig format.
7. **The model data is the product.** Every one of these systems is a modest amount of code around a
   large amount of measured human data.

---

## Where Vixen already is

Honest, and the three hard numbers at the bottom are the ones that matter.

| Piece | State |
|---|---|
| Skeleton, poses, clips, blend trees, layers, masks, state machine | ✅ `Vixen.Animation` — local-space poses, model space derived once at the end |
| Retargeting | ✅ `SkeletonRetarget`, baked rather than per-frame |
| IK, foot placement | ✅ analytic two-bone |
| GPU skinning | ✅ `SkinningRenderFeature` — palette is `inverseBindPose · jointModelSpace`, in model space, in a **storage buffer**, one range per object |
| Animation graph asset + editor | 🟡 `Vixen.Editor.AnimationGraph` |
| Bindless materials, GPU culling, virtualized geometry, page residency | ✅ — the machinery a thousand characters would need |
| Hair *shading* | ✅ `Raven/Library/Shading/Hair.rvn` — Kajiya-Kay diffuse and a two-lobe Marschner approximation |
| Subsurface *shading* | 🟡 `Subsurface.rvn` — wrapped diffuse and back-lit transmission only; its own header names the missing diffusion pass |
| Model import, `ModelCompiler`, sub-asset addressing | ✅ `Vixen.Editor.Assets` |
| Asset editors, inspector, `IEditorMode`, plugin surface | ✅ |
| Cloth simulation | ⛔ nothing, anywhere. `Vixen.Physics` is Jolt, and Jolt's soft bodies are not a garment solver |
| Strand hair geometry | ⛔ nothing |
| Blend shapes / morph targets | ⛔ `⬜` in [06](06-rendering-pipeline.md) and in [overview](../overview.md) |
| Texture baking / render-to-texture compositing for skin layers | ⛔ nothing, though the compositor and compute path make it small |

And the three numbers:

| Number | Where | Why it matters |
|---|---|---|
| **Four influences per vertex** | `MeshData.BoneIndices` — "Four joint indices per vertex"; `BonePalette` in `Skinning.rvn` is four `mat4`s; the vertex stream takes `bones0: float4, weights0: float4` | A MetaHuman face uses more than four on the regions that matter, and the fifth influence is *not* below threshold on a face the way `Skinning.rvn`'s comment correctly says it is on a body |
| **`MaxBones = 256`** | `Skinning.rvn` — sized to Vulkan's guaranteed 16 KiB uniform range | ⚠ **Less serious than it looks.** The palette is already a `Buffer<BoneMatrix>` and `ShadowCaster.rvn` says why; 256 is what a *host* sizes by. An ~800-joint face needs the host constant raised and the indices to stay exact in a `float4` — which they are, well past 2²⁴ |
| **No morph target path at all** | — | Everything below is blocked on it |

⚠ **The good news is the shape of the gap.** Nothing about the existing animation stack is wrong for
this: local-space poses, a model-space pass at the end, palettes in a storage buffer, retargeting that
transfers animation rather than pose, and events collected rather than dispatched are all exactly what
a facial rig wants. What is missing is *primitives*, not architecture — which is the difference
between a phase and a rewrite.

---

## Decisions

### D1 — Identity is a parameter vector; geometry is derived

A `.vxcharacter` holds no vertices. It holds:

```
archetype        → the .vxarchetype it is a point in
bodyParameters   → the measurement / semantic vector, plus which are pinned
faceParameters   → archetype blend weights, then region deltas
skin             → the layer stack: base tone, secondary tones, freckles, blemishes, makeup
eyes, teeth      → parameters, not meshes
wardrobe         → slot → item reference + per-character attribute overrides
```

That is a few hundred bytes to a few kilobytes. Everything a renderer eventually sees — a face mesh,
a body mesh, a bone palette, a texture set — is **derived** from it by the solver, and derived data is
cached under `Library/` keyed by the parameter hash, exactly as every other derived artefact in
[08](08-asset-pipeline-and-addressables.md) already is.

⚠ **The consequence that pays for the whole design**: a character is *diffable*, *mergeable*,
*replicable* and *versionable*. Two artists editing one character do not conflict on a mesh. A save
game stores the character. A server tells a hundred clients what everyone looks like for the price of
a few packets ([D12](#d12--an-appearance-replicates-because-it-is-a-parameter-vector)).

### D2 — The model is an asset; the solver is the engine

A `.vxarchetype` is the statistical model. It carries:

| Part | What it is |
|---|---|
| **Template** | The fixed topology: positions, UVs, per-LOD meshes, and the LOD chain. Never varies |
| **Shape basis** | *k* orthogonal shape directions over the template's vertices, with their statistics (mean, variance, and the correlations that make muscularity move fat) |
| **Measurement map** | How each named measurement — height, chest, waist, inseam — is computed from a shaped mesh. A *function of the mesh*, not a slider |
| **Joint regressor** | Where the skeleton's joints go for a given shape. Joints are **derived**, never authored per character |
| **Skin weight template** | Weights on the template, transferred to every shape |
| **Pose correctives** | Shape- and pose-dependent deltas — the JCM / RigLogic-corrective mechanism |
| **UV layout and texture set** | The albedo/normal/roughness/cavity sets the skin layers composite over |

⚠ **The engine ships the format, the solver, the editor and the bake. It does not ship the data.**
That sentence is load-bearing and it is repeated in
[the content problem](#the-content-problem-which-is-not-an-engineering-problem) and in
[Risks](#risks), because it is the difference between "Vixen has a character creator" and "Vixen has
a character creator with one usable human in it".

**The solve is constrained, not evaluated.** Given a set of pinned measurements and a set of desired
parameters, find the shape coefficients that satisfy the pins exactly and minimise distance to what
the un-pinned parameters ask for, subject to staying inside the model's plausible range. That is a
small least-squares problem with equality constraints over *k* ≈ 50–200 coefficients — milliseconds
on a CPU, no iteration worth worrying about, and `Vixen.Core.Mathematics` already carries the linear
algebra. Pinning is not a UI feature bolted onto sliders; it is the *equality constraints*, and
building it any other way produces the thing every naïve creator produces, where setting height
silently destroys the waist you spent five minutes on.

### D3 — A face rig is a compiled program

A `.vxfacerig` is RigLogic's shape, deliberately:

```
control values (named, 0…1)
   │
   ├── raw controls          — a remap, per control
   ├── correctives           — products of raw controls, fed back as inputs
   │
   ├──▶ joint deltas         — sparse linear map → translation/rotation/scale per joint
   ├──▶ morph weights        — sparse linear map → blend-shape channel weights
   └──▶ shading scalars      — sparse linear map → material parameters (wrinkle masks, blood flow)
```

Every mapping is **linear and sparse**, so the compiled rig is three sparse matrices plus a corrective
term list, and evaluation is three sparse matrix–vector products. Non-linearity lives entirely in the
correctives, which are authored knowledge — the combinations somebody decided were worth fixing.

Five decisions inside that:

1. **Compiled, not interpreted.** The `.vxfacerig` is built by the importer into a layout chosen for
   the machine: dense blocks after pruning, padded so no scalar remainder loop is needed, evaluated
   through `System.Numerics.Vector<float>`. This is RigLogic's own reported ~6× and it is the
   difference between a face costing 0.1 ms and 0.6 ms — times however many characters are on screen.
2. **Managed, not a native binding.** [OpenRigLogic](https://github.com/EpicGames/openriglogic) is MIT
   and it would work. It is still the wrong dependency here: it would be a second native library to
   build for six platforms, it would not run on `browser-wasm`, and NativeAOT on iOS makes every
   native dependency a static-link problem — [10](10-platforms.md)'s standing tax. The algorithm is a
   sparse matvec. ⚠ **What we do take from OpenRigLogic is the DNA reader**, reimplemented against its
   open format so [D5](#d5--the-first-usable-characters-come-from-outside) works.
3. **The rig is fitted to a shape, and re-fitting is explicit.** MetaHuman's *Remove Rig* button is
   not a wart. Joint positions come from the joint regressor and corrective deltas are expressed in
   the fitted frame, so editing the face invalidates the rig — and the honest interface says so rather
   than silently producing a rig that is subtly wrong about where the jaw hinge is.
4. **Per-LOD joint subsets, as strict subsets.** Every LOD's joint list is a subset of LOD 0's, so
   changing LOD adds or ignores rows and rebuilds nothing.
5. **Morph targets on LOD 0 only.** MetaHuman's rule, and it is right: below LOD 0 the vertex count is
   too low for a displacement to survive and the joints already carry the expression.

### D4 — Morph targets are a compute pre-pass, not a vertex-shader loop

Blend shapes are [P0](#p0--the-two-missing-primitives-15-em) and their design is decided here because
getting it wrong is expensive later.

- **Deltas are sparse and stored sparsely.** A brow-raise touches a few hundred vertices of a
  40k-vertex face. Storage is `(index, Δposition, Δnormal)`, quantised — 16-bit snorm positions
  against a per-target range, octahedral normals — sorted by index for coalesced writes.
- **A compute pass scatters the active targets into a per-instance vertex buffer**, then skinning
  reads that buffer instead of the mesh's. One dispatch per skinned instance with morphs, over the
  union of the active targets' index lists. The alternative — a vertex shader that loops over targets
  — reads every delta for every vertex including the ones it does not touch, and does it again in the
  shadow pass, the motion-vector pass and the depth pre-pass.
- **The morphed buffer is per instance, and it is the buffer every later pass reads.** That is
  what keeps the shadow, velocity and visibility passes agreeing with the shading pass about where a
  vertex is — the class of bug that shows up as a face whose shadow does not match it.
- ⚠ **This is a general feature and it is not the character system's.** It belongs in
  `Vixen.Rendering` beside `SkinningRenderFeature`, it closes [06](06-rendering-pipeline.md)'s open
  row, and a game that never touches a character gets facial animation on a hand-authored head out of
  it.

Alongside it, the second missing primitive: **eight influences behind a Raven permutation.** Four
stays the default and the fast path, because it is right for a body and it is what glTF stores;
`Skinning.Influences = 8` adds a second index/weight pair to the stream and a second four-term
accumulation. The permutation is the mechanism [07](07-raven-shader-pipeline.md) exists to provide,
and a project with no characters compiles neither variant.

### D5 — The first usable characters come from outside

⚠ **The single most important scheduling decision in this document.**

Since 2025 Epic's terms state plainly that
[MetaHumans "can be used with any engine or creative software"](https://www.metahuman.com/license),
with no revenue share when used outside Unreal. The DNA format is
[openly specified](https://github.com/EpicGames/MetaHuman-DNA-Calibration) and the evaluator is
[MIT-licensed](https://github.com/EpicGames/openriglogic).

So the first release **imports** rather than **creates**:

| Import | Produces |
|---|---|
| A MetaHuman DNA file | A `.vxfacerig` — controls, correctives, joint deltas, morph weights, per-LOD joint subsets |
| The assembled meshes and textures | Ordinary Vixen skeletal meshes, morph targets and material instances |
| The facial control set | Curves named by [the standard](#d13--a-named-control-set-is-the-interoperability-surface), which the animation system already knows how to drive |

This is worth doing on its own merits and it happens to be the correct engineering order. It builds
and *proves* the runtime half — morphs, eight influences, an 800-joint rig, the skin path, the LOD
sync — against real data of known quality, before a single line of the solver is written. If the
runtime half is wrong, we find out against a character Epic already validated, not against our own
half-fitted model where every artefact has two possible causes.

#### What import buys, and what it does not

The distinction is structural rather than a matter of effort or terms, and it is worth stating once
because it is the thing people get wrong about this path. **A DNA file describes one person
completely.** The machinery that turns sliders into a face is the statistical model inside MetaHuman
Creator; it is not in the export and it never travels. So import delivers a **cast**, not a creator.

| | Import-only | |
|---|---|---|
| [D3](#d3--a-face-rig-is-a-compiled-program) compiled face rig | ✅ | It *is* the DNA behavior layer |
| [D4](#d4--morph-targets-are-a-compute-pre-pass-not-a-vertex-shader-loop) morph targets | ✅ | The geometry layer's LOD0 deltas |
| [D9](#d9--lod-belongs-to-the-rig-as-much-as-to-the-mesh) LOD, with per-LOD joint subsets | ✅ | Eight levels, strict subsets, intact |
| [D13](#d13--a-named-control-set-is-the-interoperability-surface) named control set | ✅ | The Facial Description Standard is the interop layer |
| [D14](#d14--the-characters-work-has-a-place-in-the-pose-pipeline-and-it-is-last) pipeline order | ✅ | Applies to any character |
| [D10](#d10--a-character-bakes-down-and-the-bake-is-the-shipped-artefact) bake | ✅ n/a | Assembly already did it; what arrives is the baked result |
| [D11](#d11--skin-is-layers-that-bake-not-a-texture-set-per-character) layered skin | ⚠️ **flattened** | Baked textures arrive; the editable layer stack is MHC's and does not export |
| [D7](#d7--wardrobe-items-conform-they-are-not-authored-per-body) wardrobe | ⚠️ **partial** | Garments arrive as skinned meshes. Conforming is meaningless against fixed bodies, and there is no cloth solver either way |
| Hair | ⚠️ **partial** | Card LODs are usable; strands need geometry this document defers |
| [D12](#d12--an-appearance-replicates-because-it-is-a-parameter-vector) appearance replication | ⚠️ **degraded** | *Which* character replicates, not a parameter vector. Right for a cast, useless for player-made characters |
| [D15](#d15--proxy-shapes-are-derived-from-the-archetype-like-joints) derived proxy shapes | ❌ | Nothing to derive from — back to authoring per character against [34](34-move-sets-and-pose-constraints.md)'s `.vxshapevocab`, which is tolerable for twelve characters |
| [D1](#d1--identity-is-a-parameter-vector-geometry-is-derived) identity as a parameter vector | ❌ | What arrives is a mesh and a rig, not a point in a space |
| [D2](#d2--the-model-is-an-asset-the-solver-is-the-engine) archetype, solve, pinning, measurements | ❌ | The model is not in the file |
| [D6](#d6--sculpting-is-projection-never-displacement) sculpting as projection | ❌ | No space to project onto |
| [D8](#d8--one-solver-editor-and-runtime) one solver | ❌ | There is no solver |
| **An in-game character creator** | ❌ | The headline casualty, and the reason this document does not stop here |

Two operational consequences fall out of that table and neither is obvious from the ✅ column:
**the authoring loop lives in Unreal** — artists build there, export, and Vixen consumes, so every
cheekbone is a round trip through another engine — and **no title on this path can let a player make a
character.** A fixed cast is served completely; anything else is not served at all.

⚠ **The obligation this carries.** Terms change and this document is not legal advice: the importer
reads a published format, and whether a given character may ship in a given product is the licence's
question and the user's, not the engine's. What Vixen ships is a reader.

⚠ **And one shortcut that is not taken.** MetaHuman topology is fixed, so any set of exported
MetaHumans is already in dense correspondence — which is the expensive half of building a shape basis,
apparently free. It is not taken, for two reasons that are independently sufficient: deriving a
statistical model from a corpus of somebody's generator output is a different act from using a
character and needs a licence read rather than an engineering decision, and the result would inherit
whatever bias that generator has while looking like measured data. [The content
problem](#the-content-problem-which-is-not-an-engineering-problem) has a route that needs neither
caveat.

### D6 — Sculpting is projection, never displacement

The Head Sculpt equivalent moves a control point and the mesh follows — but the mesh does not follow
*where the point went*. The gesture produces a target displacement for one region; the solver finds
the point in the model space that best achieves it and applies **that**, which moves neighbouring
regions the way a real face varies.

Three consequences, all of them features:

- The result is always a valid human, so it is always riggable, always fits clothes and always has
  correct weights.
- The user is told when they have hit the model's edge, instead of the mesh quietly becoming a
  gargoyle. The right feedback is resistance, not a clamp.
- **Stylisation is a property of the archetype, not an escape hatch.** A cartoon character comes from
  a cartoon archetype whose model space contains cartoon faces. Adding an "ignore the model" mode
  would immediately break rigging, clothing and LODs, and would be the first step down a road that
  ends at [20 § Part G](20-editor-parity.md#part-g--out-of-scope)'s modelling tools.

### D7 — Wardrobe items conform; they are not authored per body

A garment is authored **once, on the template's mean body**, with:

- its own mesh and materials,
- a **body mask**: which template vertices it hides (so a shirt does not push torso geometry through
  itself),
- a **binding** to the body's surface: for each garment vertex, the nearest template triangle, a
  barycentric coordinate, and a signed offset along the interpolated normal.

Conforming to a shaped body is then evaluating that binding against the *shaped* template — a
displacement per garment vertex, computed once when the body changes and cached. Skin weights come
across the same binding, so a garment inherits the body's weights and needs none of its own.

⚠ **Displacement along a smoothed normal, not the raw one**, and the fitting pass runs a small
collision push-out afterwards. The naïve version produces poke-through at every crease, because the
offset that was correct on the mean body is wrong wherever curvature changed.

**Cloth simulation is deferred and named as deferred.** Conformed, skinned garments are the whole of
the first delivery. Simulation needs a solver `Vixen.Physics` does not have and Jolt does not provide
in a usable form, and pretending otherwise would put an unbounded subsystem inside a phase estimate.
When it arrives it attaches at the same seam — a garment already has a body-relative binding, which
is exactly what a cloth solver needs for collision and for its skinned rest state.

### D8 — One solver, editor and runtime

`Vixen.Characters` is a **runtime** assembly. The editor's creator panel is a UI over it, not a
separate implementation.

This is where MetaHuman was until 5.6 pulled the creator into the editor and 5.8 added
Collections and Instances, and it is worth building the right way round from the start: a game that
wants a character creator gets the solver, the archetype loading, the wardrobe fitting and the
material layering as ordinary runtime API, and builds its own UI from `Vixen.Ui`. We ship no
in-game creator UI — that is a game's design, not an engine's.

⚠ **The cost, stated up front.** The solver must be allocation-free per solve, must run under
NativeAOT and on `browser-wasm`, and cannot reference anything in `Vixen.Editor.*` — the same
constraints `Vixen.Animation` already lives under, which is why they are affordable.

### D9 — LOD belongs to the rig as much as to the mesh

A character LOD changes four things at once, and they must change together:

| | LOD 0 | Lower |
|---|---|---|
| Mesh | Full | Decimated, per-LOD weights |
| Rig joints | All | A strict subset, rows ignored |
| Morph targets | Active | None |
| Hair | Strands (eventually) or high-density cards | Fewer cards, then a mesh |

MetaHuman's LODSync component exists because a face at LOD 0 on a body at LOD 3 with hair at LOD 1
looks broken in a way that is hard to attribute. Vixen's equivalent is a **`CharacterLod` component
that owns one level for the whole character** and drives every part from it. Not a component per
mesh with independent thresholds — that is the bug, shipped as a feature.

⚠ **And it drives [34](34-move-sets-and-pose-constraints.md)'s knobs too.**
[34 § D22](34-move-sets-and-pose-constraints.md) gives the constraint stage three independent LOD
knobs — rate, detail, scope — and its table drives all three from distance. Two of them are this table's fifth and sixth
rows in disguise: **detail** is which proxy shape set resolves a surface frame, and **scope** is which
chains are solved at all. A character whose meshes drop to LOD 3 while its constraints still resolve
against the fine shape set is the same failure as the mismatched-LOD face, one layer down. So detail
and scope come from `CharacterLod`; **rate** stays 34's, because it is a frame-budget decision rather
than a fidelity one and it is answerable to a governor this document knows nothing about.

### D10 — A character bakes down, and the bake is the shipped artefact

`Assemble` turns a `.vxcharacter` into ordinary engine assets: skeletal meshes with their LOD chain,
a skeleton, morph targets, baked textures, material instances and a prefab. Downstream, nothing knows
a creator was involved.

- **The default for a shipped build is baked.** The 1–2 GB vs under-100 MB table is not a MetaHuman
  quirk; it is what a creator's representation costs.
- **A game that ships the runtime creator opts in**, and pays for the archetype, the solver and the
  unbaked texture layers — knowingly, because it wants players to make characters.
- **The bake is content-pipeline work**, so it is an `ImportPipeline` task with a `BuildPlanner` entry
  and an artifact key, which is the machinery [08](08-asset-pipeline-and-addressables.md) already has.
- ⚠ **A baked character must be byte-identical for identical parameters**, or the incremental build
  rebakes every character on every build. Same determinism bar the rest of the content pipeline holds.

### D11 — Skin is layers that bake, not a texture set per character

Skin is a stack of operations over the archetype's texture set: base tone, secondary tones, then
freckles, blemishes, moles, makeup layers with per-layer blend, colour and mask. Two evaluations of
the same stack:

- **Baked** — composited to a texture set at assembly time by a compute pass over the archetype's
  masks. Shipping cost is one texture set per character, same as any other character.
- **Live** — the layers evaluated in the material, for the runtime creator, where a player dragging a
  freckle slider must see it move.

Both paths are the same stack; the second is the first without the bake. CC4's SkinGen makes the
identical split and it is the right one.

⚠ **The skin *shading* is a separate problem and it is the one that decides whether a face reads as
human.** Wrapped diffuse is not enough at close range — `Subsurface.rvn` says so itself. A separable
screen-space diffusion pass, driven by a per-pixel scattering profile, is [P3](#p3--skin-15-em) and it
closes an already-open row in [06](06-rendering-pipeline.md).

### D12 — An appearance replicates because it is a parameter vector

[16](16-networking.md) has no way to replicate an appearance today, and [28](28-gameplay-framework.md)
has nothing to say about what a character looks like. Under [D1](#d1--identity-is-a-parameter-vector-geometry-is-derived)
both come out nearly free:

- A `CharacterAppearance` component is `[Replicated]` and carries the parameter vector plus wardrobe
  slot references. A few hundred bytes; quantised, it is far less.
- Clients solve locally. The mesh is never on the wire.
- Wardrobe changes are slot assignments — one small delta, not a mesh swap.
- Cosmetic state is **unreliable and low-priority** by definition. A late hat is not a rollback.

⚠ **The one real hazard is that clients must solve *identically enough*.** Two clients disagreeing by
a millimetre on a cheekbone is invisible and nobody cares; the same disagreement in the *joint
regressor* moves a hand attachment point, and a held weapon ends up in the wrong place. So the joint
regressor's output is deterministic across platforms to the same bar
[16](16-networking.md)'s bit-exactness tests already hold, and the shape solve is not.
[D15](#d15--proxy-shapes-are-derived-from-the-archetype-like-joints) puts the proxy-shape regressor on
the same side of that line, for the same reason.

⚠ **[34 § R3](34-move-sets-and-pose-constraints.md) reaches the identical stance from the other
direction** — *"pose is not authoritative: parameters and the selected `MoveKey` replicate, the pose is
reproduced locally"*. Two documents arriving independently at "replicate the inputs, derive the result"
is worth noticing rather than restating: what travels is what a human chose, and everything downstream
of it is a function.

### D13 — A named control set is the interoperability surface

Vixen adopts a published, versioned list of facial control names — the same role MetaHuman's Facial
Description Standard plays — and everything speaks it:

- An ARKit blend-shape stream maps onto it.
- An animation clip stores curves named by it, so it retargets to any character on any archetype.
- A lip-sync or capture solver produces it.
- A rig consumes it.

⚠ **The names are the contract, and the rig is not.** A clip authored for one character plays on
another whose rig is entirely different, because both agree that `browRaiseInnerL` is a thing a face
does. This is the mechanism that makes third-party facial animation possible at all, and it costs a
YAML file and a generated constant class.

### D14 — The character's work has a place in the pose pipeline, and it is last

`IPoseProcessor` used to have one occupant. With [34](34-move-sets-and-pose-constraints.md) it has
several, and an order that was previously arbitrary becomes load-bearing:

```
pre-evaluation stage                   — before any animator evaluates       (34 § D19)
  └─ 0  CharacterSolveSystem           — shape → joints, meshes, proxy shapes
layer mix                              — the animated pose
  │
  ├─ 1  FaceRigSystem                  — facial joints and morph weights
  ├─ 2  ConstraintStack                — root placement, then goals          (34 § D10)
  └─ 3  CharacterCorrectiveSystem      — pose-driven correctives             ← last, always
        │
        └─ SkinningSystem
```

**Four rules, and each has a failure it prevents:**

0. **The body is solved before anything evaluates against it.** A re-solve moves *joints*, and a
   layer mix evaluated against last frame's skeleton is a pose built on a body that no longer exists —
   briefly, on the frame a slider moves, which is exactly the frame a player is looking at. Part 3
   below used to place `CharacterSolveSystem` vaguely in `SystemPhase.Animation` *"exactly as
   `AnimationSystem` does"*, which named a phase and not an order. ✅
   [34 § D19](34-move-sets-and-pose-constraints.md) has since given the frame a stage **before any
   animator evaluates**, built for grouped multi-character solves, and it is the right home for this:
   the character solve is the other thing that must happen before the first blend tree runs.
   ⚠ **34 justifies that stage on the grounds that it is worth building even with the default
   scheduler.** This is a second consumer for it, arrived at independently, which is the strongest
   form that argument can take.

1. **Correctives are last, unconditionally.** They are a function of the *final* joint angles. Any
   processor that runs after them invalidates them, and the invalidation is invisible — see
   [§ 34](#34--move-sets-and-pose-constraints), consequence 1. This is a rule about the phase, not
   about a list order somebody can reorder in an inspector: `CharacterCorrectiveSystem` refuses to
   register anywhere but the end and says so.
2. **The face rig owns the facial joints outright, and negotiates for the neck.** Everything under
   `FACIAL_C_FacialRoot` is the rig's and nothing else may write it — a constraint naming one is a
   compile-time error in the clip's markup, not a runtime fight. `FACIAL_C_Neck1Root` and
   `FACIAL_C_Neck2Root` are the exception, because a look-at goal has a legitimate claim on them: the
   rig's neck contribution is emitted as a **labelled goal** into the same `IConstraintArbiter` as
   everyone else's, so head-turn-versus-expression is one weighted decision with a residual anybody can
   read, rather than whichever system wrote second.
3. **The root suggestion belongs to the controller, not to the character.**
   [34 § D20](34-move-sets-and-pose-constraints.md) already says the root solve is offered to the
   character controller as a suggestion. Nothing here changes that, and this document must not add a
   second opinion about where a character stands.

⚠ **Rule 1 is the one that would have been discovered late.** A corrective evaluated a frame early is
correct in every idle test, correct in every locomotion test, and wrong only when something reaches —
which is the case nobody has a golden image of.

### D15 — Proxy shapes are derived from the archetype, like joints

A `ProxyShapeSet` ([34 § D13](34-move-sets-and-pose-constraints.md)) is authored **once, on the
template**, and becomes a seventh row of the archetype in
[D2](#d2--the-model-is-an-asset-the-solver-is-the-engine):

| Part | What it is |
|---|---|
| **Proxy shape template** | Named, tagged primitives on the template body, and a regressor that places and sizes each one for a solved shape |

Solving a body therefore produces its proxy shapes in the same pass that produces its joints, from the
same machinery, with no per-character authoring anywhere.

Three consequences, in descending order of how much they matter:

- **Naming cannot drift.** Every character on one archetype has exactly the shapes the template
  declares. [34 § R4](34-move-sets-and-pose-constraints.md) — a clip that works on one character and
  not another because a shape is called something else — is not mitigated here, it is *unreachable*.
- **Sizes track the body, which is the whole point.** A `SurfaceFrame`'s normalised coordinate is only
  portable if the primitive it names actually got wider when the character did. A hand-authored set
  fitted to the mean body would resolve a belly contact to a point inside a heavy character.
- **The coarse set comes free.** 34's generator — smallest enclosing primitive per tag group — runs on
  the template once rather than per character.

⚠ **The regressor is under the same determinism gate as the joint regressor**, and for the same reason
[D12](#d12--an-appearance-replicates-because-it-is-a-parameter-vector) gives: two clients disagreeing
about where a grip surface is puts a held object in two different places.

⚠ **What is not derived is the *tagging*.** Which primitive affords `grip-surface` or `seat` is
authored knowledge about the template, exactly as which expression combinations need correctives is
authored knowledge about the face. It is written once and inherited by every character, which is the
best available outcome and not a free one.

**And the vocabulary has a name now.** [34 § D13](34-move-sets-and-pose-constraints.md) has since made
the shape names and tags a declared asset — a **`.vxshapevocab`** — rather than a project convention,
and [34's P4](34-move-sets-and-pose-constraints.md) builds it as *"names, tags and the class
declaration 33 § D15 generates against"*. So the two documents meet exactly here: **an archetype's
proxy shape template generates the vocabulary, and every character it solves satisfies that
declaration by construction.** A hand-authored character validates against the same asset and gets an
import error instead of a silent contact failure, which is 34's answer for everything that does not
come from an archetype.

---

## The content problem, which is not an engineering problem

Everything above is buildable and estimated below. None of it produces a good-looking human without a
`.vxarchetype`, and an archetype is not a mesh — a distinction worth spelling out, because it is why
the obvious shortcuts are not shortcuts.

### What a model is, and why a mesh is not one

Four parts, and only the first is geometry:

| Part | What it is |
|---|---|
| **Template** | One mesh, fixed forever: topology, UVs, LOD chain |
| **Correspondence** | The guarantee that **vertex 4 812 is the same anatomical point on every human the model can produce** |
| **Basis** | Fifty to two hundred *directions* over that vertex set, with the statistics of how strongly they co-occur |
| **Regressors** | Functions from the coefficients to joints, skin weights, measurements, proxy shapes |

⚠ **Correspondence is the whole thing, and it is what a pile of meshes does not have.** Average two
ordinary head meshes vertex by vertex and the result is noise, because vertex 500 is an earlobe on one
and a nostril on the other. Every operation this document needs — blending two faces, fitting one
garment to any body, reusing one rig, saying what "a wider jaw" *is* — is defined per template vertex
and is meaningless without it. Establishing it over a corpus is
[non-rigid registration](https://link.springer.com/article/10.1007/s11263-017-1009-7): rigid alignment,
then iteratively deforming the template onto each target under landmark constraints. It is the
expensive step and it is the one every plan underestimates.

So a model is *a mesh plus the guarantee that every other mesh in the family is that same mesh, moved*.

### Why generated geometry does not shorten this

The obvious 2026 move is to generate the corpus instead of scanning it —
[TRELLIS](https://github.com/microsoft/TRELLIS) is MIT, excellent, and emits PBR-textured meshes from
images. It does not help with the basis, for three reasons in increasing order of severity:

1. **The output has arbitrary topology** — a marching-cubes surface, different every time. Five hundred
   generated heads leave you exactly where you started, five hundred times: the registration bill is
   unchanged, and registration was the expensive part.
2. **Fitting to generated data models the generator.** PCA over its output gives the covariance of its
   training distribution seen through its biases. The result is self-consistent and wrong in a way
   nothing downstream can detect.
3. **There is no metric scale.** Single-image reconstruction is
   [ill-posed up to scale](https://arxiv.org/abs/2409.17671) — a small person near the camera and a
   large person far from it make the same picture — and generators are
   [weakest exactly on anthropometric deviation](https://arxiv.org/html/2601.06035v2). A body model
   whose "waist: 82 cm" is not 82 cm is decoration, which is what
   [the measurement round trip](#testing) exists to catch.

⚠ **Where generation genuinely belongs is one layer up: as an input to the fitting tool, not as a
substitute for the model.** [P6](#p6--the-creator-25-em) builds scan fitting anyway. Register a
generated head *into* an existing basis and it becomes a **preset** — a point in the space rather than
a claim about its distribution, which is what contains the bias problem structurally. Presets,
textures, hair and wardrobe: yes, today. The basis: no.

### What already exists, permissively licensed

⚠ **This section previously said there was no model data and no plan that produced any by writing
code. That was wrong**, and it was the load-bearing claim of the document's risk section. Three
assets are already fitted, already registered, and licensed for commercial use:

| | Licence | What it is | Closes |
|---|---|---|---|
| **[ICT-FaceKit](https://github.com/USC-ICT/ICT-FaceKit)** — USC ICT | **MIT** | 26 719 vertices, **100 identity PCA modes** over light-stage scans registered to one topology, 53 ARKit expression shapes, 68-point landmarks, and eye, lacrimal and occlusion meshes styled after UE's Digital Human | The **face shape space** |
| **[Anny](https://europe.naverlabs.com/blog/anny-a-free-to-use-3d-human-parametric-model-for-all-ages/)** — NAVER Labs Europe | **Apache 2.0** | ~13 000 vertices, **163 bones**, 564 artist-authored blendshapes on semantic axes — age, gender, height, weight, muscle — calibrated against WHO population data, infant to elderly, with skinning | The **body**, skeleton included |
| **[MakeHuman / MPFB2](https://static.makehumancommunity.org/about/license.html)** core assets | **CC0** | Base mesh, targets, skins. No attribution, closed-source commercial use fine | The raw material Anny is built from |

**Anny is not scan-derived, and that cuts in our favour.** Its axes are artist-authored phenotypes
calibrated to published anthropometry rather than principal components of a scan set, so its
correlation structure is *asserted* rather than *measured*. That is a real weakness for a research
model and an advantage for [Body Params](#part-2--the-authoring-surface): the axes are already
semantic, which is precisely what a raw PCA basis is notoriously bad at and what MetaHuman's own
tool spends its interface hiding.

Two gaps survive, and they are the honest residue:

- **Textures.** ICT-FaceKit's light model ships no albedo. The full model does, under a USC-specific
  licence rather than MIT.
- **The face rig.** ICT gives 53 ARKit blendshapes. MetaHuman is 200+ controls, ~800 joints and 1000+
  correctives. These assets close the *shape* problem; the *rig* is authored knowledge either way and
  [D3](#d3--a-face-rig-is-a-compiled-program) is where it goes.

### Five options

| Option | What it costs | What it gets |
|---|---|---|
| **A. Import only** — MetaHuman DNA in, Vixen out | The importer, and the runtime half | A real **cast**, immediately, with the model problem entirely outside the engine. [D5](#d5--the-first-usable-characters-come-from-outside), including [the table of what it does not buy](#what-import-buys-and-what-it-does-not). **Still the recommendation for the first release**, because it de-risks every runtime decision |
| **E. Assemble the open models** — ICT-FaceKit head, Anny body, one unified template | Graft the two templates into one mesh, one UV layout, one skeleton, one LOD chain; re-express both bases on it; build the measurement map; source textures. **Skilled character-art and integration work — months, not years, and no research** | ⚠ **A genuine archetype of our own, under MIT and Apache 2.0.** This is the option the previous revision of this document did not know about, and it changes the conclusion |
| **B. A small authored archetype** — 15–30 hand-sculpted heads | Weeks of skilled character-art work | A working creator with a visibly limited range. Now better understood as *presets over E's basis* than as a basis of its own |
| **C. Licence a fitted model** — Meshcapade or equivalent | Money, per title or per seat, and somebody else's terms | A scan-derived statistical body, quickly. Worth it only if E's fidelity proves insufficient |
| **D. Build the scan pipeline** | A capture rig, subjects, consent, months of processing, expertise we do not have | Independence. Not a 1.0 conversation and possibly not ever |

⚠ **A then E is the plan.** A proves the runtime against data of known quality; E is what makes Vixen
a peer in this rather than a consumer of Epic's ecosystem, and it is reachable without a scan
programme, a licence negotiation or a research hire. B becomes a content task *inside* E — the preset
library — and generated heads registered into E's basis are the cheap way to fill it.

**What remains genuinely unsolved is texture diversity and the expression rig**, and neither is
mitigated by anything above. They are smaller than "acquire a scan library", which is what this
section used to say, and they are still the two things that decide whether the output looks human.

---

## Part 2 — the authoring surface

One asset editor, in `Editor/Vixen.Editor.Characters`, registered like every other in
[20 § B5](20-editor-parity.md#b5--authoring). A viewport with the character, a tool rail, and an
inspector whose contents are the active tool's.

| Panel / tool | Mirrors | Notes |
|---|---|---|
| **Presets** | MHC Presets | The archetype's shipped points, and the user's saved ones. Drag onto the blend triangle |
| **Blend** | MHC Blend | Up to three presets, weighted, globally or per region. Three because two is a slider and four cannot be shown on a plane |
| **Body** | Body Params | Global axes, then regions. Every row is a measurement with real units, a pin toggle, and a live readout. Pinned measurements draw in the viewport |
| **Face** | Head Transform + Head Sculpt | Coarse feature placement, then region controls. Gestures are [D6](#d6--sculpting-is-projection-never-displacement) projections |
| **Eyes / Teeth** | Teeth and Eyelashes | Parameters over the archetype's parts |
| **Skin** | Materials | The layer stack — add, reorder, mask, blend. Live evaluation ([D11](#d11--skin-is-layers-that-bake-not-a-texture-set-per-character)) |
| **Wardrobe** | Hair & Clothing | Slots down one side, a library the project's watched folders fill, per-item attribute overrides |
| **Rig** | Rig / Remove Rig | Fit the rig, drop the rig, and a control board that drives it — the honest test that it works |
| **Assemble** | Assembly | Pipeline choice, per-texture resolution, LOD budget, and a **derived cost readout** — triangles, textures, memory, per LOD. The same "(derived)" pattern [20 § B6](20-editor-parity.md#b6--world-building) uses for lighting budgets |
| **Import** | From Custom Mesh | A scan or sculpt, fitted to the template, with keypoint refinement |

⚠ **The cost readout is not a nicety.** The single most common failure in this kind of tool is a user
building a beautiful character that cannot ship, discovering it at integration. A number that updates
as they work is the cheapest possible fix, and the Assembly memory table is the argument for it.

---

## Part 3 — the runtime surface

```csharp
var character = assets.Load<CharacterAsset>("characters/hero");
var solved    = CharacterSolver.Solve(character, archetype);   // meshes, palette, textures

var entity = world.Create(
    new CharacterAppearance { Asset = character },             // [Replicated]
    new CharacterLod { Level = 0 },                            // one level for the whole character
    new AnimatorComponent { Value = animator },
    LocalTransform.Identity
);
```

| Component / system | Does |
|---|---|
| `CharacterAppearance` | The recipe. `[Replicated]`, unreliable, low priority |
| `CharacterLod` | One level for face, body, rig, morphs and hair ([D9](#d9--lod-belongs-to-the-rig-as-much-as-to-the-mesh)) |
| `FaceRigComponent` | Control values in, joint deltas and morph weights out |
| `CharacterSolveSystem` | Re-solves when parameters change, in [34 § D19](34-move-sets-and-pose-constraints.md)'s **pre-evaluation stage** — before any animator evaluates, because a re-solve moves joints ([D14](#d14--the-characters-work-has-a-place-in-the-pose-pipeline-and-it-is-last) rule 0). Off the frame thread through the `JobScheduler`, as `AnimationSystem` already is |
| `FaceRigSystem` | Evaluates the compiled rig into the pose and the morph weight set, before `SkinningSystem` fills palettes |
| `CharacterCorrectiveSystem` | Pose-driven correctives — JCMs — evaluated **last** ([D14](#d14--the-characters-work-has-a-place-in-the-pose-pipeline-and-it-is-last)) |
| `MorphRenderFeature` | The compute pre-pass and the per-instance vertex buffers ([D4](#d4--morph-targets-are-a-compute-pre-pass-not-a-vertex-shader-loop)) |

⚠ **The face rig writes into the same `SkeletonPose` the animation system produces**, as a pose
processor — the seam `IPoseProcessor` already defines for IK. It is not a parallel animation system,
and building it as one is the mistake that ends with two things fighting over the neck joints. **What
settles that fight is [34](34-move-sets-and-pose-constraints.md)'s arbiter, not write order** —
[D14](#d14--the-characters-work-has-a-place-in-the-pose-pipeline-and-it-is-last) is the ordering, and
it is the one part of this section that is a rule rather than a suggestion.

---

## Part 4 — phases

Effort in engineer-months, on [14](14-roadmap.md)'s scale. **Post-1.0 except P0**, which is a
rendering primitive with several consumers.

### P0 — the two missing primitives (1.5 EM)

Morph targets and eight influences. `MorphRenderFeature`, the sparse quantised delta format, the
compute scatter, the per-instance buffer every pass reads, `ModelCompiler` reading morph targets out
of glTF and FBX, and the `Skinning.Influences` permutation.

**Nothing here mentions characters**, it closes an open row in [06](06-rendering-pipeline.md), and it
is worth building whether or not the rest of this document is ever scheduled. Exit: a hand-authored
head with twenty morph targets animates from an `AnimationClip`, and its shadow and motion vectors
agree with it.

### P1 — the rig (2.5 EM)

`Vixen.Characters`: the `.vxfacerig` format, the compiler that produces the blocked layout, the
vectorised evaluator, per-LOD joint subsets, the pose-processor integration, and the control-set
constants of [D13](#d13--a-named-control-set-is-the-interoperability-surface).

Exit: an ~800-joint, 200-control rig evaluates in **under 0.1 ms** and the arithmetic matches a
straightforward reference implementation to a stated tolerance.

### P2 — import (1.5 EM)

The DNA reader, the assembled-mesh importer, control-name mapping onto the standard.

**Exit: a MetaHuman moves its face in Vixen**, driven by a clip, at every LOD. ⚠ This is the
milestone that makes everything above real, and it arrives before any of our own model data exists.

⚠ **Depends on [34's P0](34-move-sets-and-pose-constraints.md)** — "driven by a clip" needs a runtime
path for `.vxanim`, which is an already-owed row 34 pays. This phase can be built against a clip
constructed in code, but it cannot *exit* until that row is closed.

### P3 — skin (1.5 EM)

The separable screen-space diffusion pass and a per-pixel scattering profile, closing
[06](06-rendering-pipeline.md)'s `SSS blur` row; the layered skin stack, live and baked; eye shading
(refracted iris, limbal ring, correct caustic-free cornea).

Exit: a golden-image suite over a head under three lighting setups, and the honest before/after
against wrapped diffuse alone.

### P4 — the model and the solver (2.75 EM)

`.vxarchetype`: format, loader, shape basis, measurement map, joint regressor, weight transfer,
pose correctives. The constrained solve with pinning. Determinism cover for the joint regressor.

**Plus the proxy-shape regressor** ([D15](#d15--proxy-shapes-are-derived-from-the-archetype-like-joints)),
which is the same solve with a different output and is costed at **+0.25 EM** rather than folded in
silently — it needs its own template row, its own bake, and its own determinism cover.

Exit: measurements in, a body out, pins held exactly; two platforms agree on joint positions **and on
proxy shape placement** to the bit-exactness bar. And the criterion this shares with
[34's P4](34-move-sets-and-pose-constraints.md): **one authored clip, three bodies sampled from the
model's range — including two at its edges — hand contact correct on all three.**

### P5 — wardrobe (2.0 EM)

Garment binding and conforming, body masking, weight transfer through the binding, the slot model,
per-character attribute overrides, and hair as cards with a LOD chain.

Exit: one garment worn correctly across the model's full range of bodies, with no poke-through in the
adversarial suite.

### P6 — the creator (2.5 EM)

The asset editor of [Part 2](#part-2--the-authoring-surface), every tool, the cost readout, presets,
and the scan-fitting import.

### P7 — assembly (1.5 EM)

The bake: meshes, LOD chain, texture composite, material instances, prefab, artifact keys, incremental
rebuild, and determinism.

Exit: an assembled character loads in a build with no reference to `Vixen.Characters`, and rebuilding
an unchanged character produces identical bytes.

### P8 — runtime and network (1.0 EM)

The runtime solve path, allocation-free and NativeAOT-clean; `CharacterAppearance` replication;
`CharacterLod`; the sample that is an in-game creator.

| Phase | EM | Cumulative |
|---|---|---|
| P0 | 1.5 | 1.5 |
| P1 | 2.5 | 4.0 |
| P2 | 1.5 | 5.5 |
| P3 | 1.5 | 7.0 |
| P4 | 2.75 | 9.75 |
| P5 | 2.0 | 11.75 |
| P6 | 2.5 | 14.25 |
| P7 | 1.5 | 15.75 |
| P8 | 1.0 | 16.75 |

⚠ **16.75 EM makes this the largest amendment in `docs/plan`**, half again the size of
[24](24-blockout-tools.md) and more than [31](31-terrain-grass-and-trees.md). **And none of it buys a
model** — every phase below assumes an archetype exists to solve over, and
[the content problem](#the-content-problem-which-is-not-an-engineering-problem) is where that comes
from. ⚠ **The good news, since this table was first written, is that it is an integration job rather
than a capture programme**: [option E](#five-options) assembles one from MIT- and Apache-licensed
models, in skilled character-art time that is not counted here and does not compete with these phases
for the same person.

**The cut line, in [14](14-roadmap.md)'s style.** P0 alone is worth building now. **P0–P3 (7.0 EM) is
the whole of the value for a studio that already has MetaHuman characters**, and it is what
[option A](#five-options) needs and no more. P4 onward is only worth starting once
[option E](#five-options) has produced an archetype for it to solve over — which is a content
milestone the schedule cannot make happen, and the one thing on this page worth starting early.

---

## Testing

The same bargain the rest of the engine makes, and it applies unusually well here: **the solver, the
rig evaluator, the binding and the measurement map are pure functions over arrays**, so most of this
is a unit test with no world, no renderer and no device.

| Level | Mechanism |
|---|---|
| **Rig evaluation** | The compiled, blocked, vectorised evaluator against a naïve reference over the same sparse data, control by control, output by output. The reference is the oracle and it stays in the test project — [18](18-raven-parser-migration.md)'s differential pattern |
| **Solve invariants** | Property tests (CsCheck, as `Vixen.Core.Mathematics` does): a pinned measurement is *exactly* held; solving twice gives the same answer; the solve of an unmodified character is the identity; every solved shape is inside the model's stated range |
| **Measurement round trip** | Set height to 1.83 m, solve, measure the result: 1.83 m. The most valuable test in the suite, because a measurement map that is subtly wrong produces a creator whose numbers are decoration |
| **Joint regressor determinism** | The same shape on two platforms, bit-identical joints **and proxy shapes**, in the gate [16](16-networking.md)'s bit-exactness tests already run |
| **Pose pipeline order** | A character with an active reach goal and a pose-driven corrective on the same limb: the corrective's input is asserted to be the *post-constraint* angle. ⚠ The test has to reach — an idle character passes whatever the order is, which is why the bug survives review |
| **Shape vocabulary completeness** | Every name and tag the template declares resolves on a character solved anywhere in the model's range. This is [34 § R4](34-move-sets-and-pose-constraints.md) as an assertion rather than a convention, and it is cheap because there is one template |
| **Morph correctness** | The compute scatter against a CPU reference vertex by vertex; and the pass-agreement test — depth, shadow, velocity and shading passes all reading one morphed buffer |
| **Garment conforming, adversarially** | Every garment against a large sample of the model's shape range, asserting no vertex of the body mask is visible and no garment vertex is inside the body. The suite that would otherwise be found by a screenshot in review |
| **LOD coherence** | Every LOD's joint set is a strict subset of LOD 0's, asserted at compile time; a character at every level has consistent face, body, rig and hair |
| **Bake determinism** | The same character baked twice produces identical bytes; an unchanged character does not rebuild |
| **Allocation** | Zero bytes per frame for the solve, the rig evaluation and the morph upload, under `Measured` — the bar the player path already holds |
| **Golden images** | Skin under three lighting setups, an eye close-up, and one full character per LOD. Only for what is *drawn* |

⚠ **The measurement round trip is the highest-value item and it must be written before the solver.**
A creator whose "waist: 82 cm" produces a waist of 79 cm is worse than one with no numbers at all,
and the error is invisible until somebody measures — which nobody does, because the number is right
there on the screen.

---

## Risks

| Risk | Mitigation |
|---|---|
| ⚠ **The model data is not something engineering produces**, and it is what decides whether the output looks human | ⚠ **Downgraded, and the reason is recorded rather than quietly edited.** This row used to read *"there is no model data, and there is no plan that produces it by writing code"*, and treated acquiring a scan library as the gate. That was wrong: [ICT-FaceKit](https://github.com/USC-ICT/ICT-FaceKit) (MIT, 100 identity modes over light-stage scans) and [Anny](https://europe.naverlabs.com/blog/anny-a-free-to-use-3d-human-parametric-model-for-all-ages/) (Apache 2.0, body and skeleton) are fitted, registered and commercially licensed today, and [option E](#five-options) assembles them in months of integration rather than years of capture. What survives is narrower and still real — **texture diversity and the expression rig** — and the schedule still orders P0–P3 to deliver value before any of it |
| **16.75 EM is more than [24](24-blockout-tools.md) and [31](31-terrain-grass-and-trees.md) together** | Post-1.0, cut line stated per phase, and P0 is independently justified. If only P0 is ever built, [06](06-rendering-pipeline.md) closes a row and nothing is wasted |
| **The archetype format is a compatibility promise that outlives every decision in it** | It is versioned from the first commit and the importer is the only thing that reads the on-disk form. MetaHuman DNA is explicitly versioned for the same reason, and the Expression Editor requiring 5.6-or-later files is what that costs when you get it slightly wrong |
| **Cloth simulation gets pulled in** | Explicitly out ([D7](#d7--wardrobe-items-conform-they-are-not-authored-per-body)), with the seam named. A garment is conformed and skinned; the day there is a solver, it attaches to a binding that already exists |
| **Strand hair gets pulled in** | Cards at every LOD is the delivery. The shading model is already built, which is the half that would otherwise tempt someone into thinking the geometry is close |
| **The creator becomes a modelling tool by a thousand small requests** | [D6](#d6--sculpting-is-projection-never-displacement) is the test, and it is a hard one: a feature that requires leaving the model space is out, because it breaks rigging, clothing and LODs simultaneously. This is the same discipline [24](24-blockout-tools.md) applies to its own scope row |
| **Two clients solve the same character differently** | The shape solve may drift; the joint regressor may not ([D12](#d12--an-appearance-replicates-because-it-is-a-parameter-vector)). Only the regressor is under the determinism gate, so the expensive guarantee is bought only where it is needed |
| **A face costs more per frame than the rest of the character** | The rig's exit criterion is a number (under 0.1 ms), the LOD scheme drops joints and morphs together, and the assembly cost readout makes it visible while authoring rather than at integration |
| **MetaHuman's terms change and the import path becomes unusable** | The importer reads a published format and the runtime it feeds is ours. If the terms move, P0–P3 still stand and the model options in [the content table](#the-content-problem-which-is-not-an-engineering-problem) are unaffected. ⚠ Not legal advice: a shipping product's licence is its own question |
| **The pose pipeline grows a fourth occupant and the order rots** | [D14](#d14--the-characters-work-has-a-place-in-the-pose-pipeline-and-it-is-last) makes "correctives last" a registration-time refusal rather than a documented convention, and the reach test asserts it. ⚠ The residual risk is real: a project's own `IPoseProcessor` can still be registered after the correctives, and nothing outside its own code can stop that |
| **Uncanny valley — it looks worse than a stylised character would** | ⚠ **Real, and not an engineering risk.** A near-photoreal human that misses is worse than an obviously stylised one that lands, which is why [D6](#d6--sculpting-is-projection-never-displacement) makes stylisation a property of the archetype rather than a mode. The engine should be able to ship a stylised archetype on day one of P4 |

---

## Documents this changes

| Document | Change |
|---|---|
| [06 § Geometry](06-rendering-pipeline.md) | `Blend shapes ⬜` gains an owner, a design ([D4](#d4--morph-targets-are-a-compute-pre-pass-not-a-vertex-shader-loop)) and a phase. `SSS blur` moves out of the Phase 10 grab-bag into [P3](#p3--skin-15-em) with a named consumer |
| [07](07-raven-shader-pipeline.md) | One new permutation, `Skinning.Influences`, and the four-influence comment in `Raven/Library/Geometry/Skinning.rvn` gains the exception it does not currently have — a face |
| [08](08-asset-pipeline-and-addressables.md) | Four asset kinds (`.vxcharacter`, `.vxarchetype`, `.vxfacerig`, wardrobe items), two importers (DNA, MetaHuman meshes) and one build task (assembly) |
| [02](02-repository-layout.md) | Two assemblies and their tests: `Core/Vixen.Characters`, `Editor/Vixen.Editor.Characters` |
| [16](16-networking.md) | `CharacterAppearance` as a replicated component, and the statement that a joint regressor is under the bit-exactness gate while a shape solve is not |
| [20 § Part G](20-editor-parity.md#part-g--out-of-scope) | The **Modelling tools** row is *not* reopened. It gains a pointer here explaining why a character creator is on the other side of it, in the terms [the first table](#20--part-g--modelling-tools) sets out |
| [20 § B5](20-editor-parity.md#b5--authoring) | One row: **Character creator**, with an empty Unity column |
| [28](28-gameplay-framework.md) | Appearance becomes something the gameplay library can name — cosmetic slots, unlocks and wardrobe items are definitions over `.vxcharacter` wardrobe slots rather than a new concept |
| [34](34-move-sets-and-pose-constraints.md) | ✅ **Reconciled in both directions, and 34 moved further than this document asked.** It took the `IGaitModel` note (**D8** now reads leg length from [D2](#d2--the-model-is-an-asset-the-solver-is-the-engine)'s measurement map, with a bind-pose fallback this document did not think of); **R4** now names [D15](#d15--proxy-shapes-are-derived-from-the-archetype-like-joints) as closing it outright for archetype characters, and answers the hand-authored remainder with a declared `.vxshapevocab` its **P4** generates against D15. Unchanged from here: **D22**'s detail and scope knobs take their level from `CharacterLod` ([D9](#d9--lod-belongs-to-the-rig-as-much-as-to-the-mesh)), rate stays 34's; and its **P0** is a hard dependency of [P2](#p2--import-15-em). ⚠ Going the other way, **D19**'s pre-evaluation stage — added for grouped solves — is where `CharacterSolveSystem` belongs, which changed [D14](#d14--the-characters-work-has-a-place-in-the-pose-pipeline-and-it-is-last) here from three rules to four |
| [14](14-roadmap.md) | A post-1.0 track at 16.75 EM, with [P0](#p0--the-two-missing-primitives-15-em) pulled forward on its own merits and a cut line at [P3](#p3--skin-15-em). ⚠ It now shares a prerequisite with [34](34-move-sets-and-pose-constraints.md): that document's P0 (the `.vxanim` runtime row) gates this one's P2 |
| [15](15-risks-and-open-questions.md) | One new ranked risk, and it is not an engineering one: the model data is content, and [five ways to get some](#five-options). ⚠ Ranked **lower than this document first claimed** — MIT and Apache-2.0 fitted models exist, so the residual risk is texture diversity and the expression rig rather than a scan programme |

Licensed under Apache-2.0.
