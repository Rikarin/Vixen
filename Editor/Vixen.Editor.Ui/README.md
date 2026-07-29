# Vixen.Editor.Ui

The editor shell from [docs/plan/11](../../docs/plan/11-editor.md) § "`Vixen.Editor.Ui` — the
shell": docking over the existing `DockingHost`, a command registry, menus/toolbars/palette as views
over it, theming, notifications, background tasks, and localisation.

```csharp
using var shell = new EditorShell(1600f, 1000f);

shell.RegisterPanel("scene", new StringId("editor.panel.scene", "Scene"), panel => panel.Add<Viewport>());
shell.RegisterLayout("Default", new StringId("editor.layout.default", "Default"),
    () => LayoutPresets.Standard(["hierarchy"], ["scene"], ["inspector"], ["console"]));

shell.Commands.Add(new EditorCommand("file.save", EditorStrings.CommandSave, project.Save) {
    Enablement = () => project.IsDirty
});
shell.Keys.SetDefault("file.save", new KeyChord(InputKey.S, ModifierKeys.Control));

shell.Workspace.Reset();
```

`file.save` is now in the File menu, in the command palette, on Ctrl+S, and greyed out when the
project is clean. Nothing was told about it twice.

## Everything is a command

Doc 11's claim is that "menus, toolbars, context menus and the command palette are all *views over
the command registry*, so a new action appears everywhere at once". `MenuPresenter`,
`ToolbarPresenter` and `CommandPaletteSource` are those views, and each holds command *ids* rather
than labels — so a command renamed, rebound, disabled or translated is right in all four places
without any of them being told.

**Enablement is a predicate, not a flag.** A flag has to be pushed at every view whenever the world
changes, which means a menu that is right only if somebody remembered to invalidate it. A menu asks
as it opens; a toolbar asks on the tick. The cost is that the predicate must be cheap —
`stack.CanUndo`, not a directory scan.

**A command carries no keybinding.** That is `KeyMap`'s, because a binding is the user's and a
command is the application's.

⚠ **A presenter that rebuilds puts itself back where it was.** Registering a command rebuilds the
menu bar, and rebuilding replaces the bar rather than editing it — for the good reason that its
menus hang off the document root and editing in place would leak one per rebuild. But adding a child
*appends* it, and the shell registers the application's commands long after the workspace and the
status bar are in the chrome. So both presenters remember the position they were constructed for and
move the new strip back into it; without that the menu bar and the toolbar arrive along the bottom
edge of the window, on whichever frame the application happened to register its last command.

## Keybindings

Two layers: the defaults the application ships and the overrides the user made, and only the second
is saved. A keymap file holding every binding freezes the defaults at the version the user first ran
— every editor that shipped one has a support burden to prove it.

Conflicts are **detected, not resolved**: a chord belongs to one command, and binding an occupied
chord fails and says who has it. Bindings **survive the commands they name**, so unloading a plugin
does not throw away the shortcut the user gave it.

⚠ **A chord with no Control or Meta is not taken from a text field.** A single-key binding — `F` for
frame-selection, which every 3D editor has — would otherwise fire while somebody was naming an
object, and the object would end up called `Cubeaaa` with the camera somewhere else.

## Docking

`DockingHost` (in `Vixen.Ui.Controls.Advanced`) knows about panels by id, splits, tabs, dragging and
a serialisable arrangement. It knows nothing about where a panel's contents come from or what "the
Shading layout" is, which is right for a control in `Vixen.Ui`. `DockingWorkspace` is the other half:
a registry of what can be shown, lazy construction, named presets and one place that saves.

⚠ **A preset is a factory, not a layout.** One handed out as an object is the object the first
splitter drag edits — and "reset to Default" then puts the window back to whatever the user last
dragged it to.

⚠ **Restoring an arrangement writes the saved tab selection back after the panels are built.**
Opening a panel brings it to the front, which is what it means everywhere else, so a two-tab group
would otherwise always come back showing whichever panel happened to be built last.

## The palette

`Ctrl/Cmd+P`, fuzzy, over an ordered list of `IPaletteSource`. Commands are one source; assets,
scene objects and settings are others and are not this assembly's business.

The matching is subsequence-with-bonuses — a run beats scattered letters, a word start beats the
middle, a shorter candidate beats a longer one that matched equally well. A source does its **own**
matching, because an asset index with a hundred thousand entries wants something better than a
linear scan and a palette that dictated the search would prevent it.

⚠ **The field keeps the focus and the rows never take it.** A palette where Down moved the focus
into the list is one where the next letter typed goes nowhere.

## Theming

Nine custom properties on the root and one class name. `ThemeService.Mode` writes `dark`; a user
theme file is a YAML mapping of token to value compiled into an *author* stylesheet, which beats the
three user-agent sheets by origin — so overriding `--accent` needs no `!important` and no knowledge
of the control set's selectors.

The sheet order is `ControlTheme` → `AdvancedTheme` → `EditorTheme`, and it matters: each is written
against tokens the one before declares, and a custom property nothing declared substitutes to
nothing.

`EditorTheme` also **overrides** the control set's tokens rather than only adding to them, which is
the mechanism `ControlTheme`'s own remarks nominate for exactly this: an application shipping a
button gets the neutral palette, and a tool window is a different room. Neither of the other two
sheets is edited, so a game that never installs this one is untouched — and the control screenshots
still show what a game gets.

### What it is trying to look like

The neutral density of a 3D tool and the material layering of an audio workstation. The unit is the
**pane**: a working grey with a seam around it, not a card on a desk.

- **Mid greys, not near-black.** A tool window that bottoms out at black has nowhere left to put a
  recess — every field, gutter and gap ends up the same colour and the ramp collapses into one flat
  sheet. The working surface sits in the middle of the range so there is room below it for the wells
  and above it for the things you press.
- ⚠ **The hairline is darker than the surface it edges.** A lighter border is a bevel and belongs on
  something raised; a darker one is a seam, and a tool window is made of seams. The gap between two
  panes is the same colour as the line around one, so a split and an edge are the same fact drawn at
  two widths and cannot disagree.
- **Small radii.** A pane is a region of the window with work in it, not a card that arrived from
  somewhere. `--radius-panel` is 5px and `--radius-control` is 4px — rounding a panel like a dialog
  throws away four pixels of every corner and makes a dense tool read as a settings screen.
- **Four surfaces.** `--workspace` (the seam) → `--surface-sunken` → `--surface` →
  `--surface-raised`. Depth is a luminance step. Raised where you press, sunken where you type.
- ⚠ **The well is a fill, not an inner shadow.** `DrawListBuilder` refuses `inset` box-shadows
  outright and says why, so a recessed field is two steps down the ramp and nothing else. That is
  the reason the ramp has four entries rather than three.
- **`--elevation` is a token**, so what floats and how far is one edit rather than forty. Everything
  that leaves the plane — menu, popover, dialog, palette, toast, floating panel — shares it. A docked
  pane has no shadow at all; it is not on top of anything.
- **`--accent-deep` for resting selection, `--accent` for what you just did.** A list is mostly
  selection, and the accent at full strength is the brightest thing in a dark editor, so the
  outliner sits a step below the palette's highlight.
- **Focus is answered twice.** `dock-group:focus-within` tints the focused panel's hairline, and its
  selected tab's label turns accent — one says *a* panel has the keyboard, the other says which.

`EditorChromeVisualTests` holds all of it to a picture, in both themes and with the palette open.
The tests beside it ask which panel is open and which command ran, which stays true through any
palette at all — the inspector bugs that started this work were invisible to every one of them and
obvious in the first screenshot.

## Notifications and background work

A toast **and** a history, because the thing a user does after an import fails is look away, look
back, and find the message gone. ⚠ **An error does not expire**; everything else does.

Background tasks are never a modal dialog. Work runs wherever the caller put it, what it reports is
queued, and `Pump()` applies the queue at one point in the frame — so a status bar never draws a
title that was replaced between two reads. The pump has a budget, which is a livelock guard rather
than a nicety: a task reporting per file over a hundred thousand files can enqueue faster than a
frame can drain.

## Localisation

Every user-visible string is a `StringId` — an id and the English text it says — so
`item.Label = EditorStrings.Save.Text` is no more work than the literal and there is never a reason
to write the literal. That is the retrofit `Stride.Core.Translation` exists to repair.

⚠ **The source text lives at the declaration, not in an `en` catalog.** An editor whose fallback is
a file shows `editor.command.file.save` to anybody whose install is missing it. Here the worst case
is English. `Strings.Missing` is the list a translator works from.

`Strings` is the one static thing in the assembly, and the price is that anything subscribed to
`Strings.Changed` must unsubscribe — `MenuPresenter` is `IDisposable` for exactly this, and
`EditorShell.Dispose` calls it.

## What is deliberately not here

- **`Vixen.Editor.Core`.** A command is an id and a delegate; a panel is an id and a factory. Nothing
  here knows what a project, a document or an undo stack is, which is what lets the whole assembly be
  tested against a bare `UiDocument` — the same bargain `Vixen.Ui` makes with `Vixen.Platform`.
  Joining the two is `Vixen.Editor.App`'s job.
- **A window.** Floating dock groups float *within the document*; promoting one to an OS window needs
  a second surface, swapchain and input queue, which belong to `Vixen.Platform` and the app head.

## Known gaps

- **A keybinding editor.** `KeyMap` has the model — conflict detection, per-command customisation,
  reset — and no UI. Interactive "press a key" capture is `Vixen.Input`'s rebinding path.
- **A notification panel.** The history is kept and bounded; only the toasts and the task centre have
  views.
- **`Strings.Resource` generation.** `EditorStrings` is hand-written in the shape the generator will
  emit, so nothing at a call site changes when it lands — but an id used nowhere and an id declared
  nowhere are not yet build errors.
- **Layout presets do not remember floating groups' promotion**, because nothing can promote one yet.
