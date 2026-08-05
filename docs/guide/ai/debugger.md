---
title: The AI debugger
slug: ai/debugger
kind: guide
area: AI
summary: One keyed overlay over all three planners, breakpoints on nodes, a diagnosis read out of the recorded log alone, and the one channel that crosses a wire.
api: [T:Vixen.Ai.Diagnostics.AiDebugSection, T:Vixen.Ai.Diagnostics.AiDebugRow, T:Vixen.Ai.Diagnostics.AiAgentSnapshot, T:Vixen.Ai.Diagnostics.AiSnapshots, T:Vixen.Ai.Diagnostics.AiBreakpoint, T:Vixen.Ai.Diagnostics.AiBreakpointHit, T:Vixen.Ai.Diagnostics.AiBreakpoints, T:Vixen.Ai.Diagnostics.AiSymptom, T:Vixen.Ai.Diagnostics.AiFinding, T:Vixen.Ai.Diagnostics.AiDiagnosisSettings, T:Vixen.Ai.Diagnostics.AiDiagnosis, T:Vixen.Ai.Diagnostics.AiDebugMessage, T:Vixen.Ai.Diagnostics.AiDebugChannel, T:Vixen.Ai.Diagnostics.AiDebugCategory, T:Vixen.Ai.Diagnostics.AiDebugCategories, T:Vixen.Ai.Diagnostics.AiOverlayStyle, T:Vixen.Ai.Diagnostics.AiGameplayDebugger, T:Vixen.Ai.Diagnostics.AiOverlaySystem, T:Vixen.Ai.Perception.Diagnostics.PerceptionSnapshots, T:Vixen.Editor.Ai.AgentDebugOrigin, T:Vixen.Editor.Ai.AgentDebugModel, T:Vixen.Editor.AssetEditors.Ai.AgentDebuggerView]
tags: [ai, debugging, overlay, breakpoints, diagnostics]
since: 0.1
status: stable
related: [ai/behaviour-trees, ai/utility, ai/goap, ai/perception]
---

## What it is

**One debug surface for all three planners.** A key opens an overlay over the agents near you;
numbered categories turn parts of it on and off; a breakpoint stops one agent at one node with its
state intact; and `AiDiagnosis` reads a recorded log and says what is visibly wrong with the agents
in it.

It is one surface rather than three because [D2](https://github.com/Rikarin/Vixen/blob/master/docs/plan/37-ai-behaviour-trees-utility-and-goap.md)
made the agent one shape. A behaviour tree's active path, a utility set's scored candidates and a
GOAP plan's steps are all *a name, a reading and whether it is the live one* — so they become rows in
one `AiAgentSnapshot`, and everything downstream reads rows.

## What it is for

Answering "why did my AI do that" without attaching a source debugger, and answering it in a build
that has no editor attached.

Three questions, and the tool is arranged around them:

| Question | Where the answer is |
|---|---|
| What is it doing, right now, in front of me? | the overlay — categories 1 and 2 |
| Why did it decide *that*? | category 3, and a breakpoint on the node |
| Why did the nightly build's agent get stuck at 03:14? | `AiDiagnosis` over the recorded log |

You do *not* want it on in a shipping build. `AgentDebugRecorder` and `AiDebugChannel` are off by
default and the host turns them on from `BuildVariants.Current.HasDiagnostics()`, which is false for
`Release`.

## Using it

The overlay is a system. A host that never adds it pays nothing at all:

```csharp no-compile="a fragment; the systems and the DebugDraw come from the host"
var debugger = new AiGameplayDebugger { Perception = perception };

systems.Add(new AiOverlaySystem(debugger, agents, draw));

debugger.Viewpoint = camera.Position;
debugger.Selected = picked;
```

### The categories are numbered, and the numbering is the feature

Unreal's gameplay debugger is one key and then digits, and it is the most-used AI feature in that
engine because a menu of checkboxes somewhere else is a thing you stop doing while chasing a bug.

| # | Category | Shows |
|---|---|---|
| 1 | `Agent` | where each agent is, what it is running, and how that is getting on |
| 2 | `Doing` | the active path, the scored candidates, the plan |
| 3 | `Why` | a decorator's last answer, a consideration's factor, an unmet condition |
| 4 | `Data` | the blackboard, live, and a GOAP agent's world keys |
| 5 | `Senses` | the perceived list, and a line to each thing being sensed |
| 6 | `Shapes` | the sight cone and both sight radii, in the world |
| 7 | `Findings` | what `AiDiagnosis` makes of the recorded log |

⚠ **The default is not everything.** Every category at once over a dozen agents is a screen of
overlapping text, which reads as the tool being broken. `AiOverlayStyle.Default` is categories 1 and 6
out to forty metres, capped at sixteen agents — and both the range and the cap bite *before* anything
is captured or formatted.

⚠ **Both sight radii are drawn.** The gap between the acquire radius and the lose-sight radius is the
whole of why a guard keeps following somebody it should have lost, and it is invisible in every
debugger that draws one circle.

### It is testable with no window

Everything lands in a `DebugDraw`'s three lists — world lines, world labels, screen lines — so a test
reads the geometry directly — no device, no font atlas, no render pass. `ConstraintGizmos` established the
arrangement and [D20](https://github.com/Rikarin/Vixen/blob/master/docs/plan/37-ai-behaviour-trees-utility-and-goap.md) adopts it, because an
overlay that could only be checked by looking at it is one that quietly stops being checked.

### A debugger must not move the bug

⚠ **Taking a picture cannot change what the agent does.** A capture re-scores a utility set through
`UtilitySet.Score`, which takes the state by `ref readonly`, rather than through `Choose`, which would
advance the decision clock and start cooldowns; a GOAP plan is read rather than re-resolved. Ten
captures in a row leave the decision count exactly where it was, and there is a test that says so.

### Breakpoints stop the agent, not the game

⚠ **A breakpoint on a composite catches anything inside it**, which is the same containment test
[D6](https://github.com/Rikarin/Vixen/blob/master/docs/plan/37-ai-behaviour-trees-utility-and-goap.md)'s aborts use and the same one the editor's
abort-scope overlay shades. One rule an author can *see* is worth more than two they have to remember
apart.

A stopped agent does nothing at all: not one further tick reaches the task, so the blackboard, the
active path and every decorator's last answer are exactly as they were when the decision was made.
The rest of the level carries on, which is the point — there is no world to freeze from here, and
freezing one would be the wrong tool.

Resuming does not clear the breakpoint. Stepping a loop is resume, resume, resume.

### The diagnosis reads the log and nothing else

Four symptoms, and every one of them is a *shape in the record stream* rather than a fact about a
tree — which is what makes one reader work for all three planners:

| Symptom | What was counted |
|---|---|
| `Flapping` | the action changed too many times in the window |
| `StuckFailing` | one action failed over and over **and nothing else was tried** |
| `Idle` | the planner chose nothing, every step |
| `Thrashing` | a tree averaged too many node transitions a step |

⚠ **It reports symptoms and never causes.** "This agent changed action nine times in forty ticks" is a
fact; "its danger consideration is mis-tuned" is a guess, and a debugger that guesses is one people
learn to disbelieve. Every finding carries the count it is built from and the ticks it spans.

⚠ **"And nothing else was tried" is the whole of the stuck test.** An action that fails sometimes is a
tree working — a selector's first child failing is *how* a selector chooses.

⚠ **There was a fifth, `NeverFinishes`, and P9's sample deleted it.** It reported an agent that had
run one action for the whole window — which is a patrol between two waypoints, a `MoveTo` across a
courtyard, and every other long action in a working game. The log has no notion of *progress*, so it
cannot tell a guard walking its beat from one stuck against a wall, and a symptom that fires on
healthy agents is worse than no symptom.

## Examples

The exit criterion, as a test: an agent misbehaving in a headless run, diagnosed from the log alone.
The world and the system are gone before anything is asked.

```csharp no-compile="a fragment; Arrange builds the agent and steps it"
var recorder = Arrange();          // returns nothing but system.Debug
var findings = new List<AiFinding>();

AiDiagnosis.Analyse(recorder, findings);

Assert.Contains(findings, finding => finding.Symptom == AiSymptom.Flapping);
```

Setting a breakpoint from the editor's panel, and letting the agent go again:

```csharp no-compile="a fragment; the system and the world are the running game's"
var model = new AgentDebugModel();

model.Refresh(system, world);                       // installs its breakpoints on the system
model.ToggleBreakpoint(Symbol.Intern("guard"), 4);

// …later, once it has stopped…
model.Resume(system, world);
```

Debugging a dedicated server. ⚠ **The switch is tested before the request is even parsed**, so a build
that does not carry the feature is not distinguishable from one that does by *how* it fails:

```csharp no-compile="a fragment; the transport is doc 13's"
channel.Enabled = BuildVariants.Current.HasDiagnostics();

// On the build:
channel.TryHandle(request, agents, world, reply);

// In the editor:
if (AiDebugChannel.TryReadAgent(reply, snapshot)) {
    model.Show(snapshot);
}
```

The panel cannot tell a remote picture from a local one, which is what makes debugging a server the
same tool rather than a second one written later and worse.

## See also

- [Behaviour trees](behaviour-trees.md) — the active path, and what a breakpoint stops.
- [Utility](utility.md) — the candidates and the factors category 3 shows.
- [GOAP](goap.md) — the plan, and the conditions still unmet.
- [Perception](perception.md) — the cones and the perceived list drawn beside them.
