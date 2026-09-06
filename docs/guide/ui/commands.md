---
title: Commands and the focus route
slug: ui/commands
kind: guide
area: Core
summary: A menu declares what, and the focus decides who — a command id resolved by walking outwards from the focused element and on past the root to the document and the application, so two views can answer the same verb without knowing each other exists and an item nothing handles greys itself out.
api: [T:Vixen.Ui.CommandRoute, T:Vixen.Ui.CommandHandler, T:Vixen.Ui.IResponder, T:Vixen.Ui.CommandResponder, T:Vixen.Ui.ShortcutFormat, T:Vixen.Ui.Controls.EditorCommand, T:Vixen.Ui.Controls.CommandRegistry]
tags: [ui, commands, focus, input, menus]
since: 0.2
status: preview
related: [editor/index, ui/markup-panels, ui/dialogs, ui/strings, ui/accessibility, ui/background-tasks]
---

## What it is

A command is a string id — `edit.copy`, `file.save` — and `CommandRoute` is the answer to "who
handles it right now". The answer is not stored anywhere. It is worked out on demand by walking
from `UiDocument.Focused` outwards through `UiElement.Parent` until an element says it handles that
id, and the first one that does is the one that runs. Past the root the walk carries on through two
objects that are not elements at all: the document's responder, then the application's.

```
focused element → its ancestors → the root → UiDocument.CommandResponder → UiDocument.ApplicationCommandResponder
```

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
none. The root rather than nothing, so a handler declared on the root still answers while the focus
is nowhere — which is why a command with a single registration-time implementation is simply a
handler on the root, and behaves exactly as it always did.

Focus *acceptance* and tab participation are already separate, and no command API is involved:
`UiElement.Focusable` says an element can hold the focus, and `TabIndex = -1` takes it out of the
tab order while leaving it focusable. That pair is AppKit's `acceptsFirstResponder` plus exclusion
from the key view loop, and `Select`, `TabStrip`, `TextField` and `RadioGroup` already use it for
their inner parts.

### Past the root: responders that are not elements

An element is the wrong receiver for some verbs. The object that owns what a window is showing — a
view-model, an open file, an application's shell — is exactly the kind of object whose job is *not*
to be a view, and before this it had to own a piece of the element tree in order to say it handled
`edit.save`.

`IResponder` is the seam: one method, from an id to a handler. `CommandResponder` is the
implementation almost everything wants — a table with the same five arguments as
`AddCommandHandler`, and the same rule that declaring one id twice throws:

```csharp compile
using Vixen.Ui;

public sealed class Album {
    readonly CommandResponder commands = new();

    bool dirty;

    public Album(UiDocument document) {
        commands.Add("file.save", Save, () => dirty);

        // Consulted after the last element and before the application's.
        document.CommandResponder = commands;
    }

    void Save() => dirty = false;
}
```

Two slots, asked in this order:

| Slot | AppKit's equivalent | What belongs in it |
|---|---|---|
| `UiDocument.CommandResponder` | `NSDocument`, the window's delegate | The verbs of the thing this window is showing |
| `UiDocument.ApplicationCommandResponder` | `NSApp` and its delegate | The verbs that are true everywhere — Preferences, About, Quit |

⚠ **Nearer wins, all the way out.** A leaf beats its panel, a panel beats the root, the root beats
the document, the document beats the application. Nothing in the tail is a special case: the first
responder that *answers* wins, only that one is asked `CanExecute`, and a responder further along is
not consulted — not even to break a tie when the nearer one refuses. A document responder that
answers and says no leaves the item greyed; the chain does not carry on looking for somebody more
willing.

⚠ **Answering is not the same as being able to run.** An implementation whose verb is temporarily
impossible returns `true` with a predicate that says no, rather than `false`. Returning `false` lets
the id fall out of the chain entirely, and there is nothing after the application to catch it.

Implement `IResponder` directly only when the lookup already exists somewhere else and a
`CommandResponder` beside it would be a second copy to keep in step. `CommandRegistry` is that case,
and it is what the editor's shell installs as its document's application responder — which is why a
plain `Vixen.Ui` control bound to an application's command id resolves, greys and runs it with
nothing application-shaped in the control.

⚠ **Lifetime: the document holds the responders and never the other way about.** A responder is a
table of closures and a closure reaches everything it captured, so the reference has to point from
the short-lived thing at the long-lived one. `IResponder` deliberately has no event and no
back-reference: a responder never learns which documents it was installed on, so a long-lived one
cannot keep a closed window's element tree alive. `UiDocument.Dispose` drops both slots and the
`CommandsInvalidated` subscribers regardless.

Changing a responder's *table* does not invalidate anything, because a responder does not know a
document — installing one does. After adding or removing handlers on a responder that is already
installed, call `UiDocument.InvalidateCommands()`.

### The table itself: `CommandRegistry` and `EditorCommand`

⚠ **A route answers *who*; it does not hold *what*.** `CommandRoute` resolves an id against whatever
is on the chain right now, which is the right answer for a control that owns its own verb — but a
menu, a toolbar and a command palette all need to enumerate the actions an application has before
anybody has focused anything. That list is `CommandRegistry`, and each entry is an `EditorCommand`:
an id, a title, what it does, and a predicate that says whether it can run.

⚠ **These two lived in `Editor/Vixen.Editor.Ui/` until 0.2 and are the reason
`MenuItem.ShowShortcut` could draw a shortcut that nothing dispatched** — the drawing was in the
controls library and every part of the machinery behind it was in the editor, so an application that
was not the editor could render "⌘S" beside a menu item and pressing it did nothing. They are named
`EditorCommand` and `CommandRegistry` because that is what the editor called them; the names are
kept while the remaining files of that move are still in flight.

```csharp compile
using Vixen.Ui;
using Vixen.Ui.Controls;

public sealed class Actions {
    public static CommandRegistry Build(UiDocument document) {
        var commands = new CommandRegistry();
        var dirty = true;

        commands.Add(new EditorCommand("file.save", new StringId("app.file.save", "Save"), () => dirty = false) {
            Enablement = () => dirty
        });

        // The last link of the chain: a control bound to `file.save` now resolves against the table.
        document.ApplicationCommandResponder = commands;
        return commands;
    }
}
```

`Enablement` is asked rather than pushed, for the reason the rest of this page gives: a flag set on
every state change is right only if every path that changes state remembered to set it.

### A responder in the middle of the walk

The two slots above are the chain's two **ends**. AppKit puts a view controller, a window controller
and a document *between* the views and `NSApp`, and those need a position in the middle.
`UiElement.AddResponder` gives them one: a responder appended to an element is consulted right after
that element's own handlers and before the walk moves to its parent.

```
focused element → its responders → its parent → that parent's responders → … → root → its responders
    → UiDocument.CommandResponder → UiDocument.ApplicationCommandResponder
```

⚠ **There is no settable `nextResponder`, and that is a decision rather than an omission.** A mutable
next-link is where AppKit's worst chain bugs come from: anything holding the pointer can splice
itself in ahead of the window, forget to put it back, and orphan the root — after which the
application stops answering verbs it has always answered and there is nothing to look at. Appending
at a position cannot rewrite the walk, so the "nearer wins, all the way out" rule above holds
whatever anybody appends. Lifetime is the same bargain as the document's slots: the element holds
the responder and never the reverse.

`IResponder` carries two more members, both defaulted so an existing implementation compiles
unchanged:

| Member | Default | What it is |
|---|---|---|
| `OnKey(KeyEvent)` | `false` | A chance at a keystroke, at the position the responder sits at |
| `UndoManager` | `null` | The stack `UiElement.FindUndoManager` hands a control that is recording an edit |

⚠ **`OnKey` is the only way a non-element responder sees a key.** `EventRouter` is `UiElement`-typed
end to end — its route is a list of elements — so this could not have been fixed inside the router.
`UiDocument.Dispatch(KeyEvent)` offers the key to the responder walk **after** the bubble leg, so a
focused control still wins, and **before** the access-key and Tab fallbacks, because those are
defaults and a responder is not. Return `true` to say the key was taken.

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

### Writing a chord down: `ShortcutFormat`

A menu shows the chord beside the verb, and `ShortcutFormat` is how the chord becomes text:
`Describe(key, modifiers)` writes the neutral `Ctrl+Shift+S` form, `Name(key)` writes the key's own
legend (`Number1` is `1`, `Grave` is a backtick), and `Formatter` is the process-wide hook an
application replaces once so that every menu, tooltip and palette in it agrees.

```csharp no-compile="a fragment; the shell calls this once at start-up"
ShortcutFormat.Formatter = (key, modifiers) => MacGlyphs(modifiers) + ShortcutFormat.Name(key);
```

⚠ **Where it lives is the point.** Both members used to be statics on `KeyboardShortcut`, a
`Control` — so anything that wanted to say what a chord is called had to reference the controls
library to ask a view class a question with no element in it, and a keymap in `Vixen.Ui` therefore
could not exist. `KeyboardShortcut.Formatter` and `KeyboardShortcut.Describe` remain as forwarders
onto this one setting.

⚠ **`Describe` is deliberately neither localised nor platform-adapted.** A Mac writes `⌘⇧S` with no
separators and a different modifier order, and knowing that means knowing what the program is running
on — which this assembly, below `Vixen.Platform`, does not. `Formatter` is where an application says
otherwise; `Describe` is the default, not the answer.

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
    var where = handler.Element is { } element ? $"<{element.Tag}>" : handler.Responder?.ToString();
    Console.WriteLine($"{handler.Id} → {where}, enabled: {handler.CanExecute}");
}
```

Exactly one of `Element` and `Responder` is set, and which one says which leg of the chain answered:
an element while the walk was still in the tree, a responder once it was past the root.

`CommandHandler` is a `readonly struct` and `Resolve` returns it by value, so a toolbar re-asking
twenty items on the tick allocates nothing.

## See also

* [The editor shell](/docs/guide/editor/index) — `CommandRegistry` and `KeyMap`, which are the
  editor's layer above this: a title, a category, an icon and a keybinding for an id.
* [Panels in markup](/docs/guide/ui/markup-panels) — where the elements that declare handlers
  usually come from.
* [Dialogs that answer](/docs/guide/ui/dialogs) — what a command does when it has to ask something
  first, and why the answer arrives on the tick rather than on the click.
* [The accessibility tree](/docs/guide/ui/accessibility) — the same coalescing, one field over, for
  the other surface that has to be told once a frame that its answers may have changed.
