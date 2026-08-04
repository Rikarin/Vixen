---
title: Behaviour trees
slug: ai/behaviour-trees
kind: guide
area: AI
summary: An event-driven tree that is not walked every frame — composites, tasks, decorators and services over one flat compiled template.
api: [T:Vixen.Ai.BehaviorTreeTemplate, T:Vixen.Ai.BehaviorTreeInstance, T:Vixen.Ai.BehaviorTreeCompiler, T:Vixen.Ai.BehaviorTreeAsset, T:Vixen.Ai.BehaviorNodeDefinition, T:Vixen.Ai.BehaviorTree, T:Vixen.Ai.BehaviorNode, T:Vixen.Ai.BehaviorNodeKind, T:Vixen.Ai.BehaviorCompositeKind, T:Vixen.Ai.ObserverAborts, T:Vixen.Ai.ParallelFinishMode, T:Vixen.Ai.BehaviorDecorator, T:Vixen.Ai.BehaviorService, T:Vixen.Ai.BehaviorContext, T:Vixen.Ai.BehaviorDecoratorSlot, T:Vixen.Ai.BehaviorServiceSlot, T:Vixen.Ai.BehaviorServiceDefinition, T:Vixen.Ai.BehaviorTreeDiagnostic, T:Vixen.Ai.BehaviorTreeLibrary, T:Vixen.Ai.BlackboardDecorator, T:Vixen.Ai.BlackboardTest, T:Vixen.Ai.CompareEntriesDecorator, T:Vixen.Ai.CompositeDecorator, T:Vixen.Ai.DecoratorLogic, T:Vixen.Ai.ConeDecorator, T:Vixen.Ai.IsAtLocationDecorator, T:Vixen.Ai.InverterDecorator, T:Vixen.Ai.ForceSuccessDecorator, T:Vixen.Ai.ForceFailureDecorator, T:Vixen.Ai.RandomChanceDecorator, T:Vixen.Ai.CooldownDecorator, T:Vixen.Ai.TagCooldownDecorator, T:Vixen.Ai.SetTagCooldownDecorator, T:Vixen.Ai.ITagCooldown, T:Vixen.Ai.TimeLimitDecorator, T:Vixen.Ai.LoopDecorator, T:Vixen.Ai.ConditionalLoopDecorator, T:Vixen.Ai.UpdateBlackboardService, T:Vixen.Ai.IWorldSensor, T:Vixen.Ai.INestedTreeTask, T:Vixen.Ai.WaitTask, T:Vixen.Ai.WaitBlackboardTimeTask, T:Vixen.Ai.FinishWithTask, T:Vixen.Ai.LogTask, T:Vixen.Ai.SetBlackboardValueTask, T:Vixen.Ai.ClearBlackboardValueTask, T:Vixen.Ai.RunSubtreeDynamicTask]
tags: [ai, behaviour-trees, agents, aborts]
since: 0.1
status: stable
related: [ai/agents, ai/blackboard]
---

## What it is

A **behaviour tree** is an authored priority list, read left to right and top to bottom. A composite
walks its children by a rule; a task is a leaf that does something; a decorator is a condition
attached to a node; a service runs on an interval for as long as a branch is active.

It compiles to a `BehaviorTreeTemplate` — a flat array of nodes in depth-first pre-order, immutable
and shared by every agent running it — and each agent gets a `BehaviorTreeInstance`, which is a byte
block and an active node.

⚠ **It is not walked every frame.** The tree keeps the active node and does nothing at all when
nothing has changed: the active task ticks, active services tick if their interval has elapsed, and
pending aborts are serviced. An agent walking across a courtyard costs one `MoveTo.Tick` and a
service every 0.4 s.

## What it is for

Anything a designer must be able to read and predict: boss phases, scripted encounters, a guard's
patrol-notice-chase-give-up loop. A tree is the planner you choose when *"why did it do that"* has to
be answerable by pointing at a branch.

It is bad at the cross-product. Twenty conditions is a tree nobody can hold in their head, and a
reactive condition costs a decorator on every branch it can interrupt. When the action set is
open-ended and tuning by curve beats tuning by structure, a utility set is the better shape; when the
interesting thing is a sequence nobody authored, GOAP is.

## Using it

Register the actions, build a layout, author the tree, compile it, and give an agent its index.

```csharp no-compile="a fragment; the world, the layout and the tasks are the game's"
var actions = new AgentActionRegistry();

actions.Register("wait", new WaitTask(2f), WaitTask.StateSize);
actions.Register("patrol", new PatrolTask(), PatrolTask.StateSize);

var tree = BehaviorTree.Asset(
    "guard",
    BehaviorTree.Selector(
        "root",
        BehaviorTree.Task("respond", "chase")
            .With(BlackboardDecorator.Set(target, true, ObserverAborts.Both)),
        BehaviorTree.Sequence(
            "idle",
            BehaviorTree.Task("patrol"),
            BehaviorTree.Task("pause", "wait")
        )
    )
);

var ai = new AiSystem(actions, layout);

ai.Trees.Add(BehaviorTreeCompiler.Compile(tree, actions, layout));
world.Create(AiAgent.Thinking(0));
```

### The four kinds

| | What it is |
|---|---|
| **Composite** | Ordered children and a rule: `Selector`, `Sequence`, `Parallel`, `RandomSelector`, `Priority` |
| **Task** | A leaf, and an `IAgentAction` — so the same object serves a tree, a utility set and a GOAP plan |
| **Decorator** | A condition attached to a node. Gates entry, may rewrite the result, may loop it |
| **Service** | Attached to a composite, runs on an interval while that branch is active |

Two kinds would have been enough to *express* every tree. Four is what makes one *readable*: "chase
the player, checking every half second whether he is still visible, and give up if he stops being" is
nine nodes in four levels with two kinds, and one task with a decorator and a service with four.

⚠ **Decorator order on a node is significant.** They evaluate top to bottom and the first failure
stops the rest, so putting the cheap test above the expensive trace is your decision and the tree
honours it. On the way *out* they unwind innermost-first, so an inverter under a force-success reads
the way it is drawn.

⚠ **`Selector` resumes and `Priority` does not.** *"Does a selector resume"* is the question every
implementation answers differently and silently, so they are two composites. A `Priority` re-walks
from child zero every step, which is how a higher-priority branch takes over with no observer
anywhere.

⚠ **`Parallel` runs one main task, not N branches.** Child 0 must be a task; child 1 is the branch
that runs alongside it. True N-way parallelism makes the abort scope undefinable — two branches whose
decorators want to abort each other — and the compiler refuses anything else.

### Aborts

A decorator declares `ObserverAborts`. When it is not `None`, a write to a key it reads re-tests it,
and if the answer *changed*:

- **`Self`** — it is failing and its own subtree is running, so tear that down.
- **`LowerPriority`** — it is passing and something *after* its subtree is running, so take over.
- **`Both`** — both.

The test is two integer comparisons against the decorated node's pre-order range, which is what the
flat layout exists for.

⚠ **An abort takes effect at the start of the next step, not immediately.** A task writes its own
results during its tick — that is the ordinary case — and tearing it down from inside that write
would destroy the state of the thing currently executing. The one-step latency is the price and it is
stated rather than hidden.

⚠ **A decorator reaches the siblings under its own parent composite, and no further.** Unreal's abort
reaches further up the tree, which is more powerful and is the subject of most of the confusion in
its forums. The narrower rule is what lets the editor *draw* what a decorator can interrupt. The cost
is real and worth knowing: **a condition that becomes true deep inside a branch the agent has already
walked past does not pull it back**, because that branch is not running and nothing there is
listening. Put the decorator on the branch you want interrupted, not inside it.

⚠ **A decorator that declares an abort mode but reads no key can never fire.** The compiler refuses
it rather than letting it ship, because the symptom is *"the AI sometimes gets stuck"*.

### Conditions nothing writes

A time limit expiring, a cooldown ending, a target walking out of a cone: no key changed, so no
observer can see it. Those decorators set `Continuous` and are re-tested every step while their
branch runs — and when one goes false it **fails its branch**, so the composite moves on to its next
child. That is different from an observer abort, which restarts the composite from child zero.

### Subtrees

`BehaviorTree.Subtree` is **spliced at compile time**: the child's nodes are written into the
parent's array in place of the calling node. Pre-order still equals priority across the boundary, so
a decorator in the parent can abort a branch inside the child and the range test still means
something. A cycle is refused by name.

`RunSubtreeDynamicTask` names its tree from a blackboard key, so it cannot be spliced and gets an
instance of its own. The parent can abort the whole of it and can see nothing inside it — the honest
cost of choosing a tree at run time.

## Examples

A condition of your own. Everything per-agent goes in the span, because one decorator object is
shared by every agent running the tree:

```csharp compile
using System.Runtime.InteropServices;
using Vixen.Ai;

public sealed class TimesEnteredDecorator(int limit) : BehaviorDecorator {
    public override int StateSize => sizeof(int);

    public override bool Evaluate(in BehaviorContext context, ReadOnlySpan<byte> state) =>
        MemoryMarshal.Read<int>(state) < limit;

    public override void Enter(in BehaviorContext context, Span<byte> state) =>
        MemoryMarshal.AsRef<int>(state)++;
}
```

Reading the world into a key on a schedule — a service is a local sensor with an interval, which is
why there is one implementation and two front ends onto it:

```csharp compile
using Vixen.Ai;

public sealed class DistanceToTargetSensor(BlackboardKey target) : IWorldSensor {
    public void Sense(in AgentContext context, Blackboard blackboard, BlackboardKey key) {
        if (!blackboard.IsSet(target)) {
            blackboard.Clear(key);

            return;
        }

        blackboard.SetFloat(key, 0f);
    }
}
```

Stepping a tree by hand, which is what a headless test does:

```csharp no-compile="a fragment; the agent context is the one AiSystem builds"
var tree = new BehaviorTreeInstance(template, pool);

for (var frame = 0; frame < 60; frame++) {
    tree.Step(in context, 1f / 60f);
}

var path = new int[16];
var depth = tree.ActivePath(path);
```

## See also

- [Agents and actions](agents.md) — what a task is, and the system that steps a tree.
- [The blackboard](blackboard.md) — what a decorator reads and what an abort observes.
