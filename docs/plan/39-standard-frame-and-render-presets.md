# 39 — The Standard Frame, and render presets

## The problem, stated from evidence

The compositor document is the most honest frame-authoring format in any engine we know of: every
resource, extent, load action, pass order and binding seat is written down, and nothing renders that
the file does not say. It is also, for exactly that reason, unusable as a *default*. Sample 13's
`Frame.vxcompositor` is ~1100 lines, and the 2026-08-04 rendering audit catalogued what those lines
cost even their own authors: an atlas declared at the wrong extent (silent — an empty scissor), a
`depthLoad` left to its `Clear` default under a comment claiming the depth was shared, a resolve
whose colour was overwritten by a later node, seat/publisher name pairs that fail as "set written
short", and per-resource arithmetic (`ShadowCascades.AtlasSize`) that a human must transcribe into
YAML by hand. Meanwhile the engine's built-in `GraphicsCompositorAsset.Default` is a single opaque
pass with no shadows and no post — a debug view, not a game.

An indie developer opening Vixen today is offered either a frame that looks like 2004 or a document
that requires understanding reverse-Z, descriptor set completeness and render-graph load semantics.
Unity and Unreal both solved this the same way from opposite directions: **one engine-owned uber
pipeline, configured through settings, with authoring reserved for those who opt in.**

## How the incumbents do it

**Unreal Engine 5** ships exactly one renderer. Nobody authors a frame: you configure it through
Project Settings (a settings page over `r.*` console variables), scale it through the Scalability
system (`BaseScalability.ini` → project `DefaultScalability.ini` → device profiles — a waterfall of
named quality tiers: Low/Medium/High/Epic/Cinematic per feature group), and art-direct it through
Post Process Volumes placed in the level. Extensibility is deliberately narrow: custom passes hook
in at fixed extension points (SceneViewExtension), not by restructuring the frame. The result: a
new project renders like a AAA title in minute one, and the cost is that restructuring the frame
itself is effectively off the table.

**Unity (URP/HDRP)** made the pipeline itself replaceable (Scriptable Render Pipelines) and then
had to claw usability back: the *Render Pipeline Asset* (quality, shadows, lighting toggles — an
inspector object, assignable per quality level) references a *Renderer asset* whose ordered
**Renderer Features** list is the sanctioned extension point; volumes drive post; and in Unity 6 the
internal frame is expressed on a Render Graph API that custom passes must adopt (with a
compatibility mode for the old way). The result is configurable-by-inspector defaults with a
scriptable escape hatch — and a decade of ecosystem pain from having *three* pipelines whose
shaders and features do not port.

Two lessons fall straight out. First: **both engines put an object with named, typed, bounded knobs
in front of the developer, and neither makes the default path touch pass wiring.** Second, from
Unity's scar tissue: **do not fork the pipeline to add a preset layer.** The preset must *generate*
the same underlying thing the advanced path authors, or the ecosystem splits.

## The proposal: `!StandardFrame`

Vixen already has the one architectural fact that makes this cheap: the document reader produces a
`GraphicsCompositorAsset` object model, and the builder consumes the object model — the YAML is
just one way to make one. So the uber pipeline is not a second renderer and not a second code path.
It is **one new node kind that expands, at build time, into the same node graph a hand-authored
document would contain.**

A new project's entire frame document:

```yaml
version: 2
game: !StandardFrame
  quality: High          # Low | Medium | High | Epic — the scalability tier
  shadows: Cascades      # Off | Cascades | Virtual (cascades + VSM A/B, as sample 13 runs)
  gi: Probes             # Off | Ambient | Probes (doc 19's stack, pre-wired)
  reflections: Screen    # Off | Probe | Screen
  antialiasing: Taa      # Off | Fxaa | Taa | TaaFxaa
  exposure: Automatic    # Fixed | Automatic (the meter, with sane clamps derived from the sky)
  output: SceneColour
```

`StandardFrameAsset` is a `[DataContract]` node like any other. Its expansion — engine-owned,
versioned, tested — emits the resources (with extents computed from the nodes' own arithmetic, so
an atlas extent can never disagree with its fold again), the stages, the pass ordering, every load
action and every seat/publisher pair that sample 13 spells out by hand today. The knobs are
deliberately *few and semantic*: they say what the game wants, never how the frame is wired.

### The layers, mirroring what works in both incumbents

1. **`!StandardFrame` knobs** — the Unity "pipeline asset" layer, but living in the same document
   format, edited in the editor as a plain inspector over the node's `[DataContract]` members. Hot
   reload already works; the panel is a form, not new machinery.
2. **Quality tiers** — the Unreal scalability layer. `quality:` selects a named tier; a project may
   override per-tier values in a `RenderQuality.vxpreset` asset (cascade counts, AO scale, probe
   budgets, bloom levels — the numeric sub-knobs the top-level enum folds). Engine defaults →
   project preset → per-platform preset, the same waterfall Unreal uses, as assets rather than ini.
3. **Volumes** — already exist (doc 32) and already do the Unreal PPV job. Unchanged; the Standard
   Frame simply guarantees the nodes volumes drive are present.
4. **Extension points, not surgery** — the URP "Renderer Features" lesson, narrowed: the Standard
   Frame exposes named insertion points (`beforePost`, `afterOpaque`, `beforeUi`) taking ordinary
   node lists. A project that needs one custom full-screen pass adds three lines; it does not fork
   the frame.

```yaml
game: !StandardFrame
  quality: High
  extensions:
    beforePost:
      - !Outline { source: SceneResolved, ... }
```

5. **The escape hatch: explode, don't eject blindly** — `vixen frame explode` (CLI + editor button)
   writes the *fully expanded document* — comments included, generated from the same doc-strings
   the expansion carries — into the project, replacing the `!StandardFrame` node. One-way, clearly
   marked, exactly what "eject" means everywhere else. Sample 13's document becomes what it always
   should have been: the *reference output* of the explode, kept authored because it is the
   showcase and the test bed.

### Guardrails the audit already paid for

Independent of the preset layer, the expansion bakes in the checks this audit proved necessary, and
the document path inherits them as loud build-time refusals with `VX####` codes rather than silent
wrongness: resource extents checked against node arithmetic (the ShadowAtlas guard, generalized),
producer/consumer ordering checked against load actions (the Sky/visibility discard), seat lines
checked against declared compose slots (the "set written short" family). A hand-authored document
stays exactly as powerful — it just stops being able to be *silently* wrong in the ways sample 13
was.

### Testing story

Each (tier × knob combination in a small support matrix) has a golden-frame test: the expansion is
deterministic, so the expanded asset is snapshot-tested structurally (cheap, every CI run) and a
handful of tiers render golden images on device (the existing golden test infra). The audit's
hardest lesson — "every counter says the frame is healthy" — is answered by testing the *picture*
of the default path, because the default path is finally one thing.

## What `RenderQuality.vxpreset` contains

Unreal's model is the right skeleton — per-feature groups, named tiers, override waterfall — and
Unity's failure mode is the thing to exclude: quality values split across three homes (pipeline
asset, renderer asset, QualitySettings) with unclear ownership. Here everything quality-shaped
lives in this one asset, every entry maps to a named engine knob that already exists, and the
boundary with `.vxlook` is one rule: **look changes the intent, quality changes only the fidelity
and cost of the same intent.** Bloom threshold is look; bloom pyramid levels are quality. DoF
aperture is the camera's; DoF sample count is quality.

Per tier — Low, Medium, High, Epic, each group overridable independently:

| Group | Entries (existing knob each maps to) |
|---|---|
| Resolution | render scale (generalizing the AO passes' `Scale`); upscaling filter reserved |
| Shadows | cascade count/resolution, `shadowDistance`, `splitLambda`, constant/slope bias; punctual `tilesPerSide`/`resolution`; VSM `levels`/`firstExtent`/`pagesPerFrame` |
| Global illumination | irradiance `budget`/`dilationPasses`; screen-probe `tileSize`, `screenTraces`; DFAO samples/scale; SSAO `directions`/`steps`/scale; surface-cache atlas size |
| Reflections | `screenSteps`, `roughnessThreshold`, trace resolution |
| Post fidelity | TAA variance clipping; bloom `levels`/`filterRadius`; DoF `samples`/`maximumRadius`; motion-blur `samples`; local-exposure `taps`; FXAA preset |
| Lights | `MaxLights`/`MaxLightsPerObject`; clustered on/off; cookie resolution (when it lands) |
| Geometry & culling | GPU culling mode (`readBack`/`indirectDraws`/late phase); virtualized geometry on/off; LOD bias |
| Textures & effects | streaming pool size / mip bias; particle budget scale; foliage & terrain density (land with those projects) |

The waterfall is Unreal's, as assets rather than ini: engine tier defaults → the project's
`.vxpreset` overrides → the platform's pick of tier in `GraphicsOptions`. A runtime settings menu
is a tier switch plus, at most, per-group overrides — the same two levels every shipped game's
options screen actually exposes.

## The look profile: one master volume asset

The Standard Frame deliberately emits *neutral* artistic values, because the audit showed what
happens when art direction lives inside pipeline structure: sample 13's dusk works only because
`ev100`, the meter's clamps and the fog colour all agree with the Preetham sky, and those three
agreements are scattered across an 1100-line document. They belong together, in one named artifact.

**A project look profile — `.vxlook` — is the base of the volume stack**, Unity's Volume Profile
by way of Vixen's existing overlay model. It carries the artistic values as per-parameter
*opinions* (doc 32's "says nothing / has an opinion" distinction, which is also the standing answer
to the `ColorGradingRange` zero-value trap): exposure target and meter clamps, bloom
threshold/intensity, grading ranges, vignette, fog colour, DoF policy. `!StandardFrame` references
it (`look: Assets/Dusk.vxlook`); the editor edits it live like any asset; every scene shares it
unless a scene says otherwise.

The precedence is fixed, four layers, and never ambiguous — the confusion both incumbents earned
(Unity's project-default-vs-scene-global, Unreal's copy-the-volume-between-levels) is excluded by
construction:

1. **Engine neutral defaults** — what the Standard Frame expansion emits.
2. **Project look profile** — the master asset, below everything a scene says.
3. **Scene unbound volume** — doc 32's base layer, already implemented, per-level override.
4. **Local volumes and gameplay overlays** — already implemented, unchanged.

The editor surfaces the **resolved stack per camera** — the engine already computes "N of M volumes
reaching the camera, the fold has an opinion" for the frame summary; shown as a panel, it answers
"why does it look like this" in one glance, which is the support question volume systems drown in.

Hand-authored documents are untouched: authored node values remain the base under the volume stack
exactly as today. The look asset is how the Standard Frame keeps art out of structure, not a new
obligation on the advanced path.

## What this is not

- Not a second renderer: one builder, one node registry, one document format. The Unity
  three-pipelines mistake is structurally excluded because the preset *is* a node.
- Not a graph editor: node-graph UIs for frames (Unity's early SRP visual editors, various engines'
  frame-graph editors) consistently serve neither audience. The inspector-over-knobs +
  explode-to-text pair covers both ends better.
- Not a migration: existing documents keep working untouched. `DefaultFrame` (the bare pass)
  remains the no-content fallback; new project templates ship the seven-line Standard Frame.

## Sequencing

1. `StandardFrameAsset` + expansion for the sample-13 feature set (cascades, punctual, GI stack,
   post chain), snapshot tests, explode CLI.
2. Quality tiers + `RenderQuality.vxpreset` waterfall.
3. Editor inspector panel + explode button (rides doc 36's property-panel machinery).
4. Extension points; convert samples 01/03 templates; document the two-audience story in the guide.
