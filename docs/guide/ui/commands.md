---
title: Commands and the focus route
slug: ui/commands
kind: guide
area: Core
summary: A menu declares what, and the focus decides who — a command id resolved by walking outwards from the focused element, so two views can answer the same verb without knowing each other exists and an item nothing handles greys itself out.
api: [T:Vixen.Ui.CommandRoute, T:Vixen.Ui.CommandHandler]
tags: [ui, commands, focus, input, menus]
since: 0.2
status: preview
related: [editor/index, ui/markup-panels]
---

## What it is

A command is a string id — `edit.copy`, `file.save` — and `CommandRoute` is the answer to "who
handles it right now". The answer is not stored anywhere. It is worked out on demand by walking
from `UiDocument.Focused` outwards through `UiElement.Parent` until an element says it handles that
id, and the first one that does is the one that runs.

`UiElement.AddCommandHandler` is how an element says so. `UiElement.CommandScope` is a name a panel
declares once at its own root, which everything inside it then reports as its
`EffectiveCommandScope` — the same upward walk, asked for a different thing.

Three rules make the whole of it:

* **The first responder wins.** A second element further up the chain that would also have handled
  the id is not consulted.
* **`CanExecute` is asked of that handler only** — not even to break a tie when it says no. If it
  refuses, the command is disabled; the chain is not searched for somebody more willing.
* **Nobody responds ⇒ not executable.** There is no rule to write for that case, and no closure that
  has to reason about global state to produce it.

## What it is for

The property that makes Edit ▸ Copy work everywhere without the menu knowing anything. A text view
and a scene outliner each declare a handler for `edit.copy`; whichever has the focus is the one the
menu item runs, and neither view mentions the other, the menu, or a registry.

The failure it exists to prevent is a *pushed* scope: a mutable "which panel is active" string that
every focus handler has to remember to assign. That value is right only if every path that moves the
focus remembered to push it, and every new panel is a fresh chance to forget. Deriving it from the
tree the focus already lives in makes the mistake unavailable rather than discouraged.

The second thing it buys is enablement that falls out of absence. A menu of ids can be declared by
an application that has no idea which of them anything handles, and the items grey themselves out
wherever the chain is silent.

## Using it

`AddCommandHandler(id, execute, canExecute?)` on any element. The predicate is optional and is asked
every time something shows the command, so it may read whatever it likes — a selection count, a
document's dirty flag — without anything having to notify a cache.

Two *different* elements declaring the same id is the point and is not a collision. The same element
declaring one id twice throws: a silent replace would let one control quietly take over another's
verb, and a silent ignore would leave the second registration dead.

A scope is declared, not assigned:

```csharp no-compile="a fragment; `panel` is a panel's own root element"
panel.CommandScope = "outliner";
```

Everything below `panel` is in the `outliner` scope from then on, including controls added later and
controls added by a plugin. An element inside it may declare a narrower one, and the nearest
declaration at or above the focus is what `CommandRoute.ScopeOf` returns.

`CommandRoute.Origin` is where the walk starts: the focused element, or the root when nothing has
the focus. The root rather than nothing, so a handler declared on the document still answers while
the focus is nowhere — which is why a command with a single registration-time implementation is
simply a handler on the root, and behaves exactly as it always did.

## Examples

A view that copies its selection, and greys the menu item out while there is nothing to copy:

```csharp compile
using Vixen.Ui;

public sealed class Outliner {
    readonly List<string> selection = [];

    public Outliner(UiElement root) {
        root.CommandScope = "outliner";
        root.AddCommandHandler("edit.copy", Copy, () => selection.Count > 0);
    }

    void Copy() {
        // Whatever copying means here. Nothing outside this class knows it happens.
    }
}
```

Asking the route, which is what a menu item does as it opens:

```csharp no-compile="a fragment; `document` is the application's UiDocument"
if (CommandRoute.CanExecute(document, "edit.copy")) {
    CommandRoute.Execute(document, "edit.copy");
}
```

`Resolve` hands back the handler itself when a caller needs to know *which* element answered — a
diagnostic overlay, or a test that has to distinguish "the nearer view ran" from "something ran":

```csharp no-compile="a fragment; `document` is the application's UiDocument"
if (CommandRoute.Resolve(document, "edit.copy") is { } handler) {
    Console.WriteLine($"{handler.Id} → <{handler.Element.Tag}>, enabled: {handler.CanExecute}");
}
```

`CommandHandler` is a `readonly struct` and `Resolve` returns it by value, so a toolbar re-asking
twenty items on the tick allocates nothing.

## See also

* [The editor shell](/docs/guide/editor/index) — `CommandRegistry` and `KeyMap`, which are the
  editor's layer above this: a title, a category, an icon and a keybinding for an id.
* [Panels in markup](/docs/guide/ui/markup-panels) — where the elements that declare handlers
  usually come from.
