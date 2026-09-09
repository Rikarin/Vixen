---
title: Declaring agent actions
slug: ai/declaring-agent-actions
kind: guide
area: AI
summary: How a project says what its agents can do so that a host which never ran the game — the editor's play mode, a headless harness, a plugin — can still build the registry an AiSystem takes.
api: [T:Vixen.Ai.AgentDeclarations, T:Vixen.Ai.AgentActionDeclaration, T:Vixen.Ai.BlackboardKeyDeclaration]
tags: [ai, agents, actions, blackboard, editor, plugins]
since: 0.1
status: preview
related: [ai/agents, ai/blackboard]
---

## What it is

A process-wide table a project's assembly fills from its own `[ModuleInitializer]`, and two calls a
host makes to turn that table into the pair `AiSystem`'s constructor wants:

| Call | Who makes it |
|---|---|
| `AgentDeclarations.DeclareKey(name, type)` | The project, once per blackboard key. |
| `AgentDeclarations.DeclareAction(name, factory, stateSize)` | The project, once per action. |
| `AgentDeclarations.BuildLayout()` | The host, after every assembly is loaded. |
| `AgentDeclarations.BuildRegistry(layout)` | The host, with the layout from the line above. |
| `AgentDeclarations.Evict(assembly)` | Whoever unloads a collectible context. The editor already does. |

`AgentActionDeclaration` and `BlackboardKeyDeclaration` are what `Actions` and `Keys` hand back — the
name, the factory or the value type, the state size, and the assembly the declaration came from.

## What it is for

**An `IAgentAction` is a project's own type**, so an `AgentActionRegistry` could only ever be built by
code the project ran. That is a bigger constraint than it looks: any host that is *not* the game — the
editor's play mode, a headless determinism harness, a plugin compiled at run time — could not
construct an `AiSystem` at all, because it could not construct the registry the constructor takes.

⚠ **Handing such a host an empty registry is worse than handing it nothing.** The agent debugger would
then show agents whose every action is missing, which teaches an author something false about their
own project.

Every other project-declared kind already had a way out of this, and agent actions were the one that
did not:

| Kind | Route |
|---|---|
| `[GameSystem]` | a generator emits one `[ModuleInitializer]` per declaring assembly into `GameSystemRegistry` |
| Scene components | `SceneComponentRegistry.Declare<T>()` from a `[ModuleInitializer]` |
| Serializers | `SerializerRegistry.Register(...)`, same shape |
| Agent actions | this page |

⚠ **The route is the module initialiser and not a reflection scan**, which is the property that makes
it work: `ProjectAssemblies.Load` runs the loaded assembly's module constructor, so the declarations
are present the moment the project's code is loaded — before, and independently of, anything calling
`Game.OnInitialise`. It also survives trimming, which a scan does not.

## Using it

### The project's side

```csharp no-compile="A fragment: HarvestTask and GuardTask are MyGame's own action types."
static class MyGameAgents {
    [ModuleInitializer]
    internal static void Declare() {
        AgentDeclarations.DeclareKey("target", BlackboardValueType.Entity);
        AgentDeclarations.DeclareKey("depot", BlackboardValueType.Vector3);

        AgentDeclarations.DeclareAction(
            "harvest",
            layout => new HarvestTask(layout.Key("target")),
            HarvestTask.StateSize);

        AgentDeclarations.DeclareAction("guard", _ => new GuardTask(), GuardTask.StateSize);
    }
}
```

⚠ **The factory takes the layout, and that is the whole reason it is a factory.** Most actions are
constructed with a `BlackboardKey`, and a key is an index into a layout that does not exist until
*every* assembly has declared its keys. A parameterless factory cannot express `MoveToTask`, which is
the commonest action there is.

### The host's side

```csharp no-compile="A fragment: `services` is whatever the host builds its systems from."
var layout = AgentDeclarations.BuildLayout();
var actions = AgentDeclarations.BuildRegistry(layout);
var agents = new AiSystem(actions, layout);
```

Two phases, in that order, and the order is not negotiable: every key first, then the actions that
resolve them. A registry of its own comes back each time rather than a cached one — an action instance
is shared by every agent running it, so two worlds wanting two sets of action objects is a legitimate
thing to ask for.

### Ordering, and why it is by name

⚠ **Both lists are sorted by name, never by declaration order.** Which assembly's module initialiser
runs first is a property of the run, so a registry built in arrival order would hand out different
indices in two runs of the same program. `SceneComponentRegistry` learned this the expensive way —
its panel's foldouts moved between two runs of one build, and five dump tests passed on a developer
machine and failed on all three CI runners at once.

Because indices are assigned in name order, a compiled asset that names an action by string still
resolves to the same index every run — which is what keeps a behaviour-tree node sixteen bytes.

### Two declarations of one name

The first wins, silently, exactly as `GameSystemRegistry.Declare` does: that is what an assembly
loaded twice into two contexts needs, and the alternative doubles every action in the registry. Both
`Declare` calls return whether *this* declaration is the one in force, which is how a test or a tool
sees that a second one lost. A module initialiser normally ignores it.

### Unloading

⚠ **A declaration holds a delegate over the project's code.** One left behind after a Play keeps the
collectible context alive and makes the next Play build agents out of the previous build's assembly —
the same failure `GameSystemRegistry.Evict` exists for, and the strongest of the holds the editor has
to release. `ProjectAssemblies.Unload` calls `AgentDeclarations.Evict` alongside the other five
registries; anything else that loads a project assembly into a collectible context must do the same.

## Examples

**A host that never ran the game, building the pair from nothing but a loaded assembly.** This is the
test that proves the route, in the shape a play-mode contribution would use it:

```csharp no-compile="A fragment: `origin` is the assembly the declarations belong to."
AgentDeclarations.DeclareKey("build.target", BlackboardValueType.Entity, origin);
AgentDeclarations.DeclareAction("build.chase", layout => new KeyedAction(layout.Key("build.target")), 8, origin);

var layout = AgentDeclarations.BuildLayout();
var registry = AgentDeclarations.BuildRegistry(layout);

registry.TryGetIndex(Symbol.Intern("build.chase"), out var chase);

// The action was constructed against the host's layout, not one the project built for itself. A
// factory that ignored its argument would still produce an action, still register, and resolve to
// the wrong slot — which is why this is the assertion and not the registry's Count.
Assert.Equal(layout.Key("build.target"), ((KeyedAction)registry[chase]).Key);
```

**Listing what a project declared**, which is what a panel showing "these are your agents' actions"
is built from:

```csharp no-compile="A fragment: `Console` stands in for wherever the host reports."
foreach (var action in AgentDeclarations.Actions) {
    Console.WriteLine($"{action.Name} — {action.StateSize} B, from {action.Origin.GetName().Name}");
}
```

## See also

- [Agents and actions](agents.md) — what an `IAgentAction` is, what its state span is for, and the
  registry this fills.
- [The blackboard](blackboard.md) — what a key is, and why a layout has to exist before an action
  that reads one can be constructed.
- `Core/Vixen.Ai/README.md` — the three planners and what they share.
