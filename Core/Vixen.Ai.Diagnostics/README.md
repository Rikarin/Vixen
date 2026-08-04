<!--
SPDX-FileCopyrightText: Copyright (c) Rikarin
SPDX-License-Identifier: Apache-2.0
-->

# Vixen.Ai.Diagnostics

The AI gameplay debugger: **one keyed overlay over all three planners and the perception model**,
drawn through `DebugDraw`.

[docs/plan/37](../../docs/plan/37-ai-behaviour-trees-utility-and-goap.md) § P7. The guide page is
[the AI debugger](../../docs/guide/ai/debugger.md).

## Why this is its own assembly

For the reason `Vixen.Ai.Perception` is one: **`DebugDraw` lives in `Vixen.Engine`, and `Vixen.Ai`
depends on no engine and no renderer.** A game that wants behaviour trees and nothing else links
`Vixen.Ai` and stops.

It references perception as well, because § D20 asks for *one* debug surface — the sight cones are
drawn beside the active path rather than in a second overlay — and the assembly that draws has to see
both halves to do that.

The namespace is `Vixen.Ai.Diagnostics`, which `Vixen.Ai` has already opened. One surface, one
namespace; which assembly a type lands in is decided by whether it needs `Vixen.Engine`, and that is
a packaging fact rather than something a caller should have to think about.

## What is here

| Type | Does |
|---|---|
| `AiDebugCategory` | Unreal's numbered categories, as flags: agent, doing, why, data, senses, shapes, findings |
| `AiDebugCategories` | Turning them on and off by their number, which is what a key press does |
| `AiOverlayStyle` | What is drawn, how far, how many, and in what colours |
| `AiGameplayDebugger` | The overlay itself: `Draw(DebugDraw, AiSystem, World)` |
| `AiOverlaySystem` | Runs it once a frame, in `PreRender` |
| `QueryPreviewStyle` · `QueryPreview` | doc 37 § P8's query preview: the generated points green through red, the rejected ones crossed out, the winner ringed |

Everything it draws comes out of `AiAgentSnapshot` and `AgentDebugRecorder`, both of which live in
`Vixen.Ai`. The overlay adds a position, a colour and a layout, and nothing else — which is what makes
the editor's panel a second *view* rather than a second implementation.

## The two things it must never do

⚠ **It must not need a window.** Everything lands in a `DebugDraw`'s three lists, so
`AiOverlayExitCriteriaTests` reads the geometry directly: no device, no font atlas, no render pass.
That is doc 37 § P7's second exit criterion, and `ConstraintGizmos` established the arrangement.

⚠ **It must not change what an agent does.** A capture re-scores a utility set through
`UtilitySet.Score`, which takes its state by `ref readonly`, rather than through `Choose`, which would
advance the decision clock and start cooldowns; a GOAP plan is read rather than re-resolved. A
debugger that perturbed the thing it watched would be worse than none, because the bug would move.

## Using it

```csharp no-compile="a fragment; the systems come from the host"
var debugger = new AiGameplayDebugger { Perception = perception };

systems.Add(new AiOverlaySystem(debugger, agents, draw));

// One key opens it, digits turn parts of it on and off.
debugger.Style = debugger.Style with {
    Categories = AiDebugCategories.Toggle(debugger.Style.Categories, AiDebugCategories.Of(3))
};
```

⚠ **The default style is not `All`.** Every category at once over a dozen agents is a screen of
overlapping text, which reads as the tool being broken. `Range` and `MaximumAgents` bite before
anything is formatted, so an overlay in a crowd costs sixteen captures rather than four hundred.
