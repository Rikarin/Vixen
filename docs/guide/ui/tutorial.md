---
title: Building an interface, end to end
slug: ui/tutorial
kind: tutorial
area: Core
summary: One panel built in the order the pieces actually go together — the markup file and what it compiles to, a list that is a keyed @for over a reactive collection, a form that is two-way bound and submitted, a verb declared as a command so a menu can run it, and the stylesheet that says what none of it looks like.
api: [T:Vixen.Ui.Reactive.CollectionSignal`1, T:Vixen.Ui.Controls.SubmitEvent, T:Vixen.Ui.CommandRoute]
tags: [ui, tutorial, vxml, vcss, reactive, signals, forms, commands]
since: 0.2
status: preview
related: [ui/markup-panels, ui/reactive-collections, ui/commands, ui/markup-project-setup, ui/desktop-application, engine/getting-started]
---

## What it is

The other forty-four `ui/` pages each say what one thing is. This one builds something: a task panel
with a list in it, a field and a button that add to the list, and a Clear verb a menu can run —
assembled in the order the pieces depend on each other, which is not the order a reference is
organised in.

It is the argument for `Vixen.Ui` as an application framework rather than a game's overlay, and the
argument is only worth anything as a finished panel.

## What it is for

A reader who can already find `Button`, `CollectionSignal<T>` and `AddCommandHandler` in the guide
and still does not know **which of them to reach for first**, or what belongs in markup and what
belongs in the `@code` block beneath it.

The panel below is small on purpose. Every construct in it is one you will use in a real panel, and
there is nothing in it that exists only to make the tutorial work.

## Using it

Start from the `vixen-app` template — see [getting started](../engine/getting-started.md). It gives
you `AppShell.vxml`, a `Theme/vixen.ui.vcss` and a `Program.cs` that opens a window. Everything below
replaces the contents of `AppShell.vxml`.

### 1. The file, and what it compiles to

A `.vxml` compiles to a C# partial class. With no `@inherits` that class is a `Component`: it
*builds* elements and is not one. The header names it, says which namespace it lands in, and — for a
component — what the element it draws itself into is called.

```vxml
@component AppShell
@namespace Kestrel
@tag app-shell
@using Vixen.Ui.Controls
@using Vixen.Ui.Reactive

<app-panel class="flex flex-col gap-3 p-6">
    <app-title class="text-xl font-semibold">Tasks</app-title>
</app-panel>
```

A lowercase tag is an element with that tag name — `app-panel` is not a control, it is a box a
stylesheet can select. A capitalised tag is a control: `<Button>` is the control library's `Button`.
`class` on either one is the utility vocabulary, compiled into the project's stylesheet at build time
from the class names actually used in its `.vxml` and `.cs` files.

⚠ **A project whose build step did not run compiles perfectly and produces an *empty* stylesheet**,
and every class name in the markup then quietly does nothing. A panel that lays out as one unstyled
column is that, and not a mistake in the markup.

### 2. The model, before the list

The list has to live somewhere that a binding can subscribe to. That is the decision this step is,
and it is the one that most often gets made wrong:

* A `List<T>` in a field — a binding cannot subscribe to it, so the panel draws its first answer for
  ever.
* A `Signal<List<T>>` — **silently dead**: the signal compares the value it is handed with the value
  it holds, an append leaves the same instance in both, and nothing propagates.
* A `Signal<ImmutableArray<T>>` — correct, and the right answer for rows *projected* from something
  else once per change.
* A `CollectionSignal<T>` — correct, and the right answer when the panel owns the rows and mutates
  them in place, because it records each change for the keyed `@for` reconciler instead of forcing a
  rebuild of the whole list.

The rows here are owned by the panel and mutated one at a time, so:

```csharp no-compile="the @code block of AppShell.vxml; Task is the record below it"
readonly CollectionSignal<Task> tasks = [];
readonly Signal<string?> draft = new(null);

/// <summary>One row. A record, so two rows with the same text are the same key.</summary>
public sealed record Task(string Text);
```

### 3. The list

`@for` over the collection, one element per row, with a `key`:

```vxml
@for (var task in tasks) {
    <task-row key="@task" class="flex gap-2 items-center px-2 py-1 rounded">
        <task-text class="grow">@task.Text</task-text>
        <Button Label="Delete" on:click="@(() => tasks.Remove(task))" />
    </task-row>
} @empty {
    <task-hint class="text-sm opacity-60">Nothing to do.</task-hint>
}
```

Three things are happening, and only the first is obvious.

**Reading the collection inside the loop is what subscribes to it.** Adding a row re-runs the
reconciliation and nothing else in the panel.

⚠ **The key rule decides whether a row ever updates again: key on the item's value when the item is
immutable data, and on the object only when that object holds signals.** A surviving key keeps its
region and **does not re-run the body**, so every per-row binding stays closed over the row as it was
when its key first appeared. `Task` is an immutable record, so keying on the row itself is right and
`@task.Text` is a value that cannot go stale. If a row grew an editable field, that field would have
to be a signal inside `Task` — not a plain property the loop would have to re-read.

**`@empty` is the arm for a list with nothing in it**, written where `else` is written. The thing an
author reaches for instead — an `@if` beside the loop asking whether the sequence is empty —
evaluates the sequence twice and puts the two halves of one decision in two places that can
disagree.

### 4. The form

A field, a button, and a submit that is the same code path as the button:

```vxml
<task-form class="flex gap-2">
    <TextBox class="grow" Placeholder="Add a task" bind:Value="@draft.Value" on:submit="@Add" />
    <Button Variant="Primary" Label="Add" on:click="@Add" />
</task-form>
```

```csharp no-compile="the same @code block; Add is what both the button and the Enter key run"
void Add() {
    var text = draft.Value?.Trim();

    if (string.IsNullOrEmpty(text)) {
        return;
    }

    tasks.Add(new Task(text));
    draft.Value = null;
}
```

`bind:Value` is two-way: the field writes `draft.Value` on every change, and the field is redrawn
when anything else assigns it — which is what makes `draft.Value = null` above clear the box without
the panel touching the control.

⚠ **`bind:` needs an lvalue of the property's exact type.** `bind:Number` on an `int` is refused by
name rather than coerced, because a coercion hidden inside a binding is a cast nobody can see. The
two-line form — the value in as an ordinary attribute and the write-back out through `change:` — is
what to write instead, and the cast is then something a reader can point at.

⚠ **Enter is `on:submit` and not a `keydown` handler.** `submit` is `Vixen.Ui.Controls`'
`SubmitEvent`, raised by a `TextField` when Enter finishes it — which is *not* Enter in a
`TextArea`, where Enter is a line break and Ctrl-Enter submits. That rule lives in the control,
which is why the commit is an event it raises rather than a key a panel has to reconstruct. It is
also the moment `bind:Value.submit` writes on, for a field whose write is a decision rather than a
keystroke.

### 5. The verb

A button is not the only thing that will ever want to clear the list: a menu item, a keyboard
shortcut and a context menu all want the same verb, and none of them should have to know which panel
is on screen. That is what a command is — a string id, answered by whichever element up the chain
from the focus says it handles it.

```csharp no-compile="the @code block again; OnComposed runs once the whole body has been built"
partial void OnComposed() =>
    Root.AddCommandHandler("tasks.clear", () => tasks.Clear(), () => tasks.Count > 0);
```

`Root` is the element the component drew itself into — a `Component` is not an element, and this is
the element a `class` on its tag styles. `OnComposed` is where wiring belongs because every `ref` in
the file is assigned by then, including those under a live `@if` arm.

The `canExecute` predicate is asked every time something *shows* the command, so a menu item for
`tasks.clear` greys itself out on an empty list with nothing having to notify it. Two different
panels may declare the same id — that is the point of it, and the first responder from the focus
outwards is the one that runs. Nobody responding means the command is simply not executable, which
is why a menu can be declared by an application that has no idea what handles any of it.

⚠ **The same element declaring one id twice throws.** A silent replace would let one control take
over another's verb, and a silent ignore would leave the second registration dead.

### 6. The stylesheet

Everything above says what things *are*; `Theme/vixen.ui.vcss` says what they look like. A rule for
a tag is a fact about that tag, and belongs here rather than in a class list repeated on every row:

```vcss
task-row {
    background: var(--surface);
}

task-row:hover {
    background: var(--surface-hover);
}
```

Reach for the utility classes for the things a class name says well — layout, spacing, the type
scale — and for a rule when the fact belongs to the tag. Reach for a `style` attribute only for a
length no stylesheet could have been given, such as a measured width; it is a cascade origin that
beats every rule, which is exactly why it is the escape hatch and not the first answer.

## Examples

**The finished panel**, with the pieces in the order they were built:

```vxml
@component AppShell
@namespace Kestrel
@tag app-shell
@using Vixen.Ui.Controls
@using Vixen.Ui.Reactive

<app-panel class="flex flex-col gap-3 p-6">
    <app-title class="text-xl font-semibold">Tasks</app-title>

    <task-form class="flex gap-2">
        <TextBox class="grow" Placeholder="Add a task" bind:Value="@draft.Value" on:submit="@Add" />
        <Button Variant="Primary" Label="Add" on:click="@Add" />
    </task-form>

    @for (var task in tasks) {
        <task-row key="@task" class="flex gap-2 items-center px-2 py-1 rounded">
            <task-text class="grow">@task.Text</task-text>
            <Button Label="Delete" on:click="@(() => tasks.Remove(task))" />
        </task-row>
    } @empty {
        <task-hint class="text-sm opacity-60">Nothing to do.</task-hint>
    }
</app-panel>
```

**A filter over the list is those two things and nothing else** — a bound field, and a loop whose
sequence reads the signal:

```vxml
<SearchBox Placeholder="Filter" bind:Value="@filter.Value" />

@for (var task in tasks.Where(t => t.Text.Contains(filter.Value ?? "", StringComparison.OrdinalIgnoreCase))) {
    <task-row key="@task">@task.Text</task-row>
} @empty {
    <task-hint>No matches.</task-hint>
}
```

⚠ **What makes it live is that the sequence expression reads the signal.** A predicate computed in
the code-behind and stored in a plain field narrows once and then stops — the same half-live failure
a `bind:` over a non-reactive model has, one construct along.

**A real one to read next.** `Samples/02-HelloUi` is this shape at the size of an editor shell: a
menu bar over a docking host, a virtualised tree in one panel, a property grid in another, a form of
standard controls in a third, and toasts over the lot. Its `Shell.vxml` is the file to open first,
and the panels beside it are three variations on what this page built once.

## See also

- [Panels in markup](markup-panels.md) — the whole markup surface: `@inherits`, `ref`, `on:`,
  `bind:` and `change:`, slots, and the `@for` key rule in full.
- [Reactive collections](reactive-collections.md) — `CollectionSignal<T>` and
  `SignalDictionary<TKey, TValue>`, and when a `Signal<ImmutableArray<T>>` is the better answer.
- [Commands and the responder chain](commands.md) — how the walk from the focus decides who answers
  a verb, and what a command scope is for.
- [Making a project compile markup](markup-project-setup.md) — the build wiring behind step 1, and
  the diagnostics for when it is missing.
- [Getting started](../engine/getting-started.md) — the templates, the feed and the first run.
