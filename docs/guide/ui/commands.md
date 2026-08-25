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
related: [editor/index, ui/markup-panels, ui/dialogs]
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

`CommandRoute.Origin` is where the walk starts: `UiDocument.CommandFocus`, or the root when there is
none. The root rather than nothing, so a handler declared on the document still answers while the
focus is nowhere — which is why a command with a single registration-time implementation is simply a
handler on the root, and behaves exactly as it always did.

### Binding a control to an id

`ButtonBase.Command` is the whole of the wiring, and everything that derives from it — `Button`,
`IconButton`, `MenuItem`, `ToggleButton`, `Link` — gets it:

```vxml no-compile="one line of a menu; the whole file is Core/Vixen.Ui.Controls.Tests/Markup/CommandMenu.vxml"
<MenuItem Label="Copy" Command="edit.copy" />
```

That item runs whatever the focus handles, greys itself out when nothing does, and shows a tick when
the handler says it is on. There is no `Disabled` anywhere in the markup and no handler in the
component behind it. A toolbar is the same tag with `Button` instead of `MenuItem`; there is no
separate `Toolbar` control, because a strip of command-bound buttons in a `Panel` is one.

Three things follow the route, and each has a rule about the case where the handler says nothing:

| What | When the handler supplies it | When it does not |
|---|---|---|
| `Disabled` | `!CanExecute` | `true` — nothing responds means not executable |
| `Label` | replaced by `Title` | left as the markup wrote it |
| check state | `ElementState.Checked`, and a `MenuItem`'s tick gutter | no gutter at all, so an ordinary menu is not indented by a column of empty ticks |

⚠ **While an id is bound, `Disabled` belongs to the route.** A caller that also writes it is writing
a value the next refresh overwrites.

### A surface that shows commands is not a place

⚠ **`UiElement.IsCommandTransparent` is the flag that makes the whole binding work**, and it is the
one thing about this design that is not obvious.

A menu has to take the focus to be usable — `Menu.OnOpened` focuses the first item so the arrow keys
work, and a `MenuBarItem` takes the focus the moment it is pressed. So an item asking "who handles
`edit.copy`" from `UiDocument.Focused` gets the answer *the menu item*, and the view the menu was
opened over is not on the walk at all.

`Menu`, `MenuBar` and any control with a bound `Command` therefore declare themselves transparent:
focusing them leaves `UiDocument.CommandFocus` pointing where it was. It does not make anything
unfocusable — Tab still reaches it, the ring still shows, the arrow keys still move between items.
It is AppKit's rule that a menu is not in the responder chain, stated as data rather than as an
event loop.

An application writing its own command surface — a palette, a floating tool strip whose buttons take
the focus — sets it once on the surface's root, and everything inside is covered.

### When it is re-asked

A menu asks the route as it opens, which is the right moment for a surface that is not on screen the
rest of the time — nothing polls, and a menu of forty items costs forty walks once per opening.

A toolbar has no such moment, so a bound control also follows `UiDocument.CommandsInvalidated`: one
event, raised **at most once per frame**, from `UiDocument.Tick`. Three things ask for it —

* the command focus moving,
* a handler being added or removed,
* `UiDocument.InvalidateCommands()`, which an application calls when something *its* predicates read
  has changed.

The third is the one you write. A `canExecute` closure may read anything at all — a selection count,
a dirty flag, a network state — and nothing in the framework can know what it looked at, so the view
that changed the selection says so in one line and every surface showing any of its commands
follows:

```csharp no-compile="a fragment; `document` is the application's UiDocument"
selection.Add(entity);
document.InvalidateCommands();
```

⚠ **Call it as often as you like.** It sets a flag. Fifty calls in one frame raise the event once,
which is the whole point: a menu bar that re-asked forty items on every mutation is a menu bar that
stutters. On frames where nothing asked, nothing is raised and no predicate is invoked at all.

It is raised from `Tick` rather than from `Update` because `Update` is allowed not to happen — a
frame in which nothing dirtied the document returns early, and a command becoming executable does not
dirty one. `Tick` is the call a host must make every frame regardless.

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
* [Dialogs that answer](/docs/guide/ui/dialogs) — what a command does when it has to ask something
  first, and why the answer arrives on the tick rather than on the click.
