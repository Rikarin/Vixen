---
title: Panels in markup
slug: ui/markup-panels
kind: guide
area: Core
summary: Writing a control in .vxml — @inherits for a class callers can hold and add, ref and refs for the parts they read, change: for the values they edit, and the key rule — for @for and for @if alike — that decides whether a row updates at all.
api: [T:Vixen.Ui.Markup.Syntax.InheritsDirectiveSyntax, T:Vixen.Ui.Styling.InlineDeclaration, T:Vixen.Editor.Ui.FactRow, T:Vixen.Ui.Composition.ElementRefs`1, T:Vixen.Ui.Composition.EventSubscription, T:Vixen.Ui.Controls.SubmitEvent]
tags: [ui, markup, vxml, controls, components, reactivity]
since: 0.2
status: preview
related: [editor/inspectors-in-markup, ui/key-value-list, ui/reactive-collections, ui/markup-project-setup]
---

## What it is

A `.vxml` file compiles to a C# partial class. By default that class is a
[`Component`](/docs/api/vixen.ui.composition/component): it *builds* elements and is not one.

`@inherits` changes what the class is. With it the generated class derives from whatever the header
named — a `Control`, or any `UiElement` — and the markup describes that element's own tree. `ref`
hands a named element back to a member of the class, so the parts a caller reads are the parts the
markup already declares.

```vxml
@component ModelImportView
@inherits Vixen.Ui.Controls.Control
@tag model-editor

<TreeView ref="@Parts" />
<EmptyState ref="@Empty" Title="Not imported yet" />

@code {
    public TreeView Parts { get; private set; } = null!;
    public EmptyState Empty { get; private set; } = null!;
}
```

## What it is for

**`@inherits` is for a panel whose public surface is its parts.** A `Component` is not in the element
tree, so `parent.Add<T>()` cannot make one — its constraint is `where T : UiElement, new()` — and
walking the tree cannot find one. That is right for a view nobody reads the insides of and wrong for
an editor panel whose callers write `view.Tree`, `button.Disabled` or
`Assert.Equal(2, view.Parts.Root.Children.Count)`.

Whichever the file is, the reactive machinery is identical. An `@inherits` class gets the same
`BuildContext` a component's `Build` gets, so `@if`, keyed `@for`, effects and region-scoped disposal
are the same code rather than a second implementation.

**`ref` is for the parts that are populated imperatively.** A tree of sub-assets, a list a caller
fills, a control whose method you have to call — none of those is markup, and all of them need a
typed handle to the element the markup made.

⚠ **Nothing checks the member here.** `ref="@Parts"` emits `Parts = n0;` under the `.vxml`'s own
`#line`, so a member that does not exist, one that is readonly and one of the wrong type are all
reported by the C# compiler *on the name between the quotes*. That is the same bargain the tag name
and every parameter are emitted under.

## Using it

### The header

`@inherits` takes a type name, dotted, and it may rely on a `@using`. Absent, the class is a
`Component`. There is no generic form: `@inherits Row<T>` does not lex.

The generated partial has no accessibility modifier, which makes it `internal`. A panel another
assembly constructs needs a one-line companion file:

```csharp no-compile="the companion file; the other half is the generated partial — Editor/Vixen.Editor.AssetEditors/Importing/ModelImportView.cs"
public sealed partial class ModelImportView;
```

### Two hooks, both partial methods

`OnComposed` runs once the whole body has been built, which is where wiring belongs — every `ref` in
the file is assigned, including those under a live `@if` arm. It runs again after a hot reload.
`OnUnmounted` runs when the panel leaves the tree, before its effects are disposed — for an
`@inherits` element when the element is removed, and for a `Component` when the element it drew
itself into is removed or the branch that built it closes. Whichever comes first; it runs once.

Neither is an override, so neither costs anything when nobody implements it. `@code` may not override
`OnCreated` or `OnRemoved`: the generated scaffold uses both, and these two run in the same places.

### A component is built with its parameters

`<Panel Model="@Model" />` assigns `Model` **before** the panel's `Build` runs — the emitter writes
`BuildContext.Create`, the assignments, then `BuildContext.Compose`, and only a tag that carries a
parameter pays for the split.

⚠ **It used not to.** `Child<T>` constructs a component, mounts it — which runs `Build` — and
returns, so the assignment landed after every effect inside the panel had already read the property
once at its default. A plain C# property assigned then notifies nobody, so the panel drew the default
for ever and nothing said so.

⚠ **Signal-backing is still what makes a prop keep up.** The order fixes the value the child is built
with; it does not make a plain property something an effect inside the child can subscribe to. A prop
that has to follow its source after the build is signal-backed, exactly as before:

```csharp no-compile="the shape every panel prop takes; Samples/02-HelloUi/Shell.vxml has the real one"
readonly Signal<ShellModel> model = new(new());

public ShellModel Model {
    get => model.Value;
    set => model.Value = value;
}
```

### The three universal attributes

`class`, `style` and `binding-path` mean the same on a component tag as on an element, and are never
assigned as properties. On a capitalised tag they reach the element the control drew.

(The other two attributes that are never parameters are [`tag`](#tag-for-a-capitalised-tag-under-another-name)
and [`use`](#use-for-a-control-fed-by-a-method), which are below because neither reaches the style
tree: one *is* the element's name and the other never touches the document at all.)

`style` is an *inline style*: a cascade origin that beats every rule, not an attribute a selector can
match. Use it for the lengths no stylesheet was given.

```vxml no-compile="a bar whose left edge is a measured width times a timestamp"
<gpu-lanes style="height: @Length(Chart.Height)">
    @for (var bar in Chart.Bars) {
        <gpu-bar key="@bar" class="@bar.Hue" style="@bar.Geometry">@bar.Caption</gpu-bar>
    }
</gpu-lanes>
```

The value may be a bound expression, which is the point of it. It goes through the same parser a rule
body does, so `style="padding: 4px 8px"` becomes the four longhands the layout reads; a brace in the
value is refused with a diagnostic; and re-evaluating a binding to the text it already had costs no
parse.

It takes back only the properties it wrote. A control positions its own parts with
`UiElement.SetStyle` — a `DataGrid` row's `top`, a `DockingHost` pane's `flex-grow` — and a `style`
attribute never reaches those.

⚠ **It is the escape hatch, not the first answer.** Anything a rule can say belongs in a rule: a
`display` toggle is a class, and `OffsetX` is cheaper than either when moving a box is enough. And
because the parse is real, something writing one number every frame should call `SetStyle` directly.

There is deliberately **no `id`**. Styling one element is a class; getting one in C# is `ref`, which
hands back the object rather than a name and is checked by the compiler.

### Controls with two places for content

A child written inside a capitalised tag hangs from that control's `ContentHost`. `Tabs` has two
places a child could go — the strip and the panels — and both are reachable by nesting:

```vxml no-compile="Editor/Vixen.Editor.AssetEditors/Prefabs/PrefabView.vxml"
<Tabs class="document-tabs">
    <TabItem ref="@HierarchyTab" Label="Hierarchy" />
    <TabItem Label="Compiled">
        <CompiledSceneView ref="@Compiled" />
    </TabItem>
</Tabs>
```

`Tabs.ContentHost` is the strip, so `<TabItem>`s land there; `TabItem.ContentHost` is its panel, so a
tab's own content lands where it shows. A tab pairs itself with a panel in `OnCreated` and unregisters
in `OnRemoved`, which is what lets an `@if` or `@for` add and remove tabs — markup removes elements
without calling `RemoveTab`.

### `slot`, for a tag with more than one place for content

A tag's children go to its content host unless they say otherwise. A *component* can declare as many
holes as it likes and let the consumer say which one each child goes in:

```vxml no-compile="Core/Vixen.Ui.Controls.Tests/Markup/ToolbarShell.vxml"
<shell-root>
    <shell-toolbar><slot name="toolbar" /></shell-toolbar>
    <shell-body><slot /></shell-body>
    <shell-status><slot name="status" /></shell-status>
</shell-root>
```

```vxml no-compile="Core/Vixen.Ui.Controls.Tests/Markup/ShellConsumer.vxml"
<ToolbarShell>
    <shell-note slot="status">Ready</shell-note>
    <Button slot="toolbar" Label="Reload" />
    <body-first>One</body-first>
</ToolbarShell>
```

The shell decides the order: the status line is written first and drawn last. Children with no
`slot` go to the default `<slot />`, and they keep their source order relative to each other.

| | |
|---|---|
| Declaring a *named* slot in markup | Needs a plain component. `@inherits` makes the class an element, which has one `ContentHost` and therefore one `<slot>` — a second name is `VXML2012`. |
| Filling one | Any tag may, from anywhere. The restriction is on the declaring side only. |
| Where `slot="…"` may go | On a **direct child of a capitalised tag** and nowhere else — `VXML2016`. A grandchild's name is addressed to a tag that is not listening, and an `@if`/`@for`/`@switch` body is not a direct child either. |
| What the name may be | A literal — `VXML2018`. It is read once, when the element is made, exactly as `tag` is. A bare `slot` with no value still means the default one. |
| A name that matches no slot | Throws at compose, naming the slots the component does declare. Not dropped: the two sides are compiled together, so it is a typo you can fix. |
| Fallback content | Not supported — `VXML2017`. `<slot>Nothing yet</slot>` refuses rather than silently drawing nothing. Put the default in the consumer. |

⚠ `slot="…"` is consumed at placement, like `key` and `tag`. It never reaches the document, so a
stylesheet rule written against `[slot]` matches nothing.

#### A control's named slots

A control's parts are named by C#, not by `<slot>`, and `Expander` publishes one:

```vxml no-compile="Core/Vixen.Ui.Controls.Tests/Markup/FoldoutSheet.vxml"
<Expander Label="Transform" IsExpanded="true">
    <Icon slot="header" class="section-icon" />
    <IconButton slot="header" class="section-remove" Label="Remove" />
    <section-body>0, 0, 0</section-body>
</Expander>
```

Without a `slot` a child goes to `ContentHost`, which for an expander is the body that collapsing
hides. `slot="header"` puts it in the strip that opens it, beside the chevron and the label — where
a panel puts a component's icon, a remove button and the handle a drag reads.

⚠ **A slot appends, so anything that belongs in front of the label needs `order`.** The header's own
parts are built before any markup child exists, so a slotted glyph lands after the name. Two CSS
rules move it: `expander-header label { order: 1 }` and `.section-remove { order: 2 }`.

⚠ **A control publishes the names it has and no more.** A name it does not publish throws at compose
rather than falling back on the content host — a misspelt slot that quietly put the header's icon in
the body draws a panel that is wrong in a way nothing reports. To publish one on your own control,
override `UiElement.NamedHost`.

### Where a `ref` may go

| | |
|---|---|
| On an element or a component | Yes. On a capitalised tag it hands back the *component*, not the element it drew — `BuildContext.Host` is how you get that. |
| Inside `@if` | Yes. Null until the arm is live; stale, not cleared, when the arm leaves — ask `UiElement.IsRemoved`. |
| Inside `@for` | **No** — `VXML2010`. The body runs once per item and there is one member to assign. Write `refs` instead, or put the `ref` on the element the loop is inside. |

### `refs`, for a row of a `@for`

```xml
@code { public ElementRefs<Slider> Faders { get; } = new(); }

@for (var bus in Buses.Value) {
    <Slider key="@bus" refs="@Faders" change:Value="@(v => Write(bus, v))" />
}
```

`Faders[bus]` is that row's control, keyed on the loop's own key — so a reorder cannot hand back a
neighbour, and a row that leaves takes its entry with it. Look it up with the expression you wrote in
`key=`; when the loop declares none, that is the item itself.

⚠ **A handle is filled by an effect, so it is empty until the next flush.** That is the difference
from `ref`, which is assigned in the straight-line body — a test that changes the sequence and reads
the handle on the next line needs a frame between. The indexer throws and says so; `TryGet` is for
code to which an absent key is an answer.

`refs` outside a `@for` is `VXML2013`.

### `on:`, for the keyboard as well as the pointer

```xml
<row on:click="@Open" on:dblclick.stop="@Rename" />
<picker on:keydown.capture="@((KeyEvent e) => Keyed(e))" on:textinput="@((TextInputEvent e) => Typed(e))" />
```

The names are `tap`, `click`, `dblclick`, `longpress`, `pointerdown`, `pointerup`, `pointermove`,
`dragstart`, `drag`, `dragend`, `keydown`, `keyup`, `textinput`, `focus`, `blur` and — where
`Vixen.Ui.Controls` is referenced — `submit`.

#### Most of those names are a filtered view over one routed event

| The event | The names over it | What each one is |
|---|---|---|
| `PointerEvent` | `pointerdown`, `pointerup`, `pointermove` | its `Action` |
| `DragEvent` | `dragstart`, `drag`, `dragend` | its `Stage`: `Started`, `Moved`, and `Completed` **or** `Cancelled` |
| `KeyEvent` | `keydown`, `keyup` | its `Action` |
| `FocusEvent` | `focus`, `blur` | whether focus was gained |
| `TapEvent` | `tap`, `click`, `dblclick` | `dblclick` is a tap whose `Count` reached two |

The filter is in the table rather than in your handler, which is the point: a handler that tested
`args.Action` itself would be a handler that fires twice per keystroke until somebody notices.

⚠ **So a handler that wants a whole gesture subscribes to every name it is split across, and
`on:drag` on its own is the *middle* of a drag.** `AddHandler<DragEvent>` in C# delivers all four
stages; one `on:drag` translated from one of those compiles, binds, fires on every pointer move —
and never sees the grab or the drop. Nothing is grabbed, nothing is dropped, and there is no
exception and no diagnostic to say so; what it looks like is a drag that reorders nothing. Three
attributes on one handler is the shape that works:

```xml
<Expander on:dragstart="@((DragEvent args) => Rearrange(args))"
          on:drag="@((DragEvent args) => Rearrange(args))"
          on:dragend="@((DragEvent args) => Rearrange(args))" />
```

`ComponentsView` is written that way, and `Drag_is_three_names_over_one_event_and_on_drag_is_the_middle_one`
in `Vixen.Ui.Tests` is what keeps the split honest. ⚠ Note that `dragend` covers `Cancelled` as well
as `Completed` — a cancelled drag is the one a handler must not miss, because it is the one that has
to put back whatever the grab took.

⚠ **`keydown` is a key's *position* and `textinput` is what was typed.** `KeyEvent.Key` is the
US-QWERTY legend of the physical key, so a handler that reads a letter out of it types `q` where an
AZERTY keyboard says `a`. Escape, Tab and the arrows are `keydown`; letters are `textinput`.

The modifiers are `.stop`, `.capture`, `.once`, `.self`, `.handled` and `.slot-<name>`. **`.capture` is what a panel
over a text field needs**: it listens on the way *down* the tree, so Down and Enter reach the list
before the search box inside it treats them as caret movement and submit. **`.handled` is for a
listener that wants to know an event happened rather than to act on it** — a focus manager, a
diagnostic overlay, a panel that closes on any press — because an event something downstream has
marked handled does not otherwise reach a handler at all.

#### `.slot-<name>`, for a handler on a control's part

```xml
<Expander Label="@row.Name"
          on:dragstart.slot-header="@((DragEvent args) => Rearrange(row, args))"
          on:drag.slot-header="@((DragEvent args) => Rearrange(row, args))"
          on:dragend.slot-header="@((DragEvent args) => Rearrange(row, args))" />
```

`slot="header"` writes *children* into a control's part. This writes a *handler* onto one, and it is
the only modifier that is a name rather than a word — because it is the only one that moves the
subscription instead of qualifying it. The generated call is
`ctx.On(BuildContext.Into(n1, "header"), "dragstart", …)`, which is exactly what a hand-written panel
says with `fold.Header.AddHandler<DragEvent>(…)`.

⚠ **The difference is not cosmetic on a foldout.** A drag handler on the whole `Expander` fires for a
pointer that came down on a slider *inside* the component being dragged; one on the header fires only
for the strip that is meant to be a grab handle. Standing in for it means walking up from
`args.Source` looking for a header — eleven lines in `ComponentsView`, and a set of events that is
equal to what the part would have seen only if the walk is exactly right.

The name is the control's, from `UiElement.NamedHost` — `slot-header` on a control that publishes no
`header` throws from `Into`, naming the control and the slot, at the moment the panel is built. It
reaches a **control's part**; a component's own `<slot>` is where its children go, and `on:` on a
component tag addresses that component's host element.

### `<self />`, for a handler on the component's own element

```xml
@component NodeSearchPopup

<self on:keydown.capture="@((KeyEvent e) => Keyed(e))" />
<SearchBox />
<result-list />
```

A `.vxml`'s markup roots are *children* of the element the component is building, so an attribute
written on the first of them is a different element with different route coverage: a key arriving
while the focus is on the result list would never reach it. `<self />` is the tag for the thing there
was no tag for. It creates nothing — its attributes apply to the host — so `class`, `style`, `on:`
and `bind:` all mean on it what they mean on a `<div>`, and it works the same in a plain component
and in an `@inherits` file.

⚠ **Top level only, and the `@for` case is why it is an error rather than a warning.** It emits
against the host, so a copy inside a loop subscribes the same element once per row — five items,
five handlers, one click counted five times, and the count follows the data. Nested inside an
ordinary tag it is merely a lie about where it is. `VXML2015` refuses both.

⚠ **A handler that wants the event has to name its parameter's type, and a method group will not
do.** Which event type a name delivers is the runtime's business, so the type parameter is inferred
from the handler — and `@Keyed` gives C# nothing to infer it from, however singular `Keyed` is. Write
`@((KeyEvent e) => Keyed(e))`. A handler that wants no argument — `@Open`, `@(() => Move(1))` — is
unaffected.

⚠ **Writing a test for one: press a key somewhere the handler is *not*, or the test proves
nothing.** A panel like this focuses its search box on open, so the capture route is
host → box — and a handler mistakenly written on `<SearchBox>` instead of on `<self />` is on that
route as its target and hears every key such a test presses. Three editor pickers nearly shipped a
suite that passed with the attribute in the wrong place. What separates them is the case
`<self />` exists for: raise at the *list*, where a handler on the box is not on the route at all.

### `change:`, for a control's value

```xml
<Slider change:Value="@(v => model.Gain = v)" />
<CheckBox change:IsChecked="@(on => model.Muted = on)" />
<NumericInput change:Number="@(n => model.Count = (int) n)" />
<Select change:Value="@(name => model.Choose(name))" />
```

`on:change` does not exist and could not: `on:` binds routed events, whose handlers take a `UiEvent`,
and a value is not one. `change:` names a `[UiProperty]` — whatever `bind:` can bind, `change:` can
watch — and the handler is given that property's own type, with no cast.

Use `bind:X` when the change is an assignment to somewhere, and `change:X` when it has to *run*
something: a method call, an undo entry, a write that touches two fields.

⚠ **A value arriving from the model does not fire it.** A change made while effects are draining came
from a binding, so reporting it would be an undo entry for something the user never did. A change
made by input, or by the panel's own code, does fire it.

⚠ **A selection is a value too, and a collection the control mutates in place is not.**
`change:Selection` on a `TreeView` throws — `Selection` is a read-only view over a `HashSet` that is
the same instance before and after every change, so no property system could report it. What a
control publishes for this is a *snapshot*: `<TreeView change:SelectedNodes="@(nodes => Chose(nodes))" />`
is the whole of the subscription a panel used to write by hand, and it is quieter than the
`SelectionChanged` event, which fires again for a click on the row that was already selected.

### `bind:X.submit`, for the event that commits the write

```vxml
<TextBox bind:Value="@model.Name.Value" />
<TextBox bind:Value.submit.blur="@model.Title.Value" />
<Slider bind:Value.dragend="@model.Gain.Value" />
```

`bind:X` on its own writes the model on every change — every keystroke, every frame of a drag. The
dots say otherwise: each one names an **event**, out of the same table `on:` subscribes to, and the
write-back happens when one of them arrives rather than when the value moves. Several names are
several moments, so `bind:Value.submit.blur` is what a form field usually wants.

⚠ **They are event names, not the filter words `on:` takes.** `on:click.stop` qualifies a
subscription; `bind:Value.stop` asks the runtime to commit on an event called `stop`, which does not
exist, and says so at compose. Nothing checks the names at build time, for the same reason nothing
checks `on:`'s: the table belongs to the runtime and a control library adds to it.

⚠ **Every-change stays the default, which is the opposite of Blazor's.** Almost every binding's other
end is a `Signal<T>`, where writing per keystroke is idempotent and deferring it only makes the panel
lag its own field. And a commit-by-default would silently never write on a control that publishes no
commit moment — most of them. The consumer that wants the other behaviour is the one that treats a
write as a *decision*: an undo entry, a query, a save. Ask for it there.

The moments a control publishes are ordinary events. `blur` and `focus` are `Vixen.Ui`'s, raised on
any element the focus reaches. `submit` is `Vixen.Ui.Controls`' [`SubmitEvent`](/docs/api/vixen.ui.controls/submitevent),
raised by a `TextField` when Enter finishes it — which is *not* Enter in a `TextArea`, where Enter is
a line break and Ctrl-Enter submits. That rule lives in the control, which is why the commit is an
event it raises and not an `on:keydown` a binding would have to reconstruct.

⚠ **The value is read at the event, not remembered from the change.** So `NumericInput`, which only
rereads its text in `OnSubmit`, hands the model the `7` it settled on rather than the `007` that was
typed. `on:submit` is the same moment without a binding, and is the only way a `.vxml` has ever been
able to hear `TextField.Submitted`.

### `tag`, for a capitalised tag under another name

A control's element name comes from its type, which is what makes `button { … }` reach every
`<Button />` without anyone passing a string. `tag` is how a caller says otherwise:

```xml
<ScrollView tag="add-component-list" ref="@List" />
<WaterZoneFacts tag="water-facts" />
```

It is allowed **only on a capitalised tag**, because a lowercase one already writes its own name —
`<div tag="fact-row">` is `<fact-row>` with the answer somewhere a reader has to go and look for it,
and two ways to name one element is how a stylesheet comes to be checked against the wrong one.
Writing it on a plain element is `VXML2014`.

Two shapes wanted this and neither had a spelling. A control whose tag a sheet already names —
`Part<ScrollView>("add-component-list")` — needed a subclass, and most of the control library is
`sealed`. And `@tag` is a *header*, so "the same part under another name" meant a second, nearly
identical `.vxml`; `Vixen.Editor.Water` had two for exactly this and now has one.

⚠ **The tag is read once, when the element is created, and is never a binding.** An element's name is
interned into its style node at creation and there is no setter for it — a rule that matched
`scroll-view` on one frame and `add-component-list` on the next is a cascade nobody could reason
about. So a computed tag is legal and useful, and inside an `@for` it obeys the key rule exactly as
everything else does:

```xml
@for (var row in Report.Rows) {
    <QueryRow key="@(row, row.Selected)" tag="@(row.Selected ? "query-row-selected" : "query-row")" />
}
```

A surviving key keeps its element, and an element keeps the tag it was born with — so the flag the
tag depends on has to be in the key. That is not a limitation of `tag`; it is the same sentence the
[`@for` key rule](#the-for-key-rule) already says about every binding in a row.

### `use`, for a control fed by a method

A component-tag parameter is a property assignment, so a control fed by *properties* is entirely
sayable and one fed by a **method** was not sayable at all:

```xml
<InspectorView use="@(view => view.Inspect(Chosen.Descriptor, Chosen.Provider, Chosen.Targets))" />
<CategoryList use="@(list => list.SetItems(Visible))" />
```

`use` takes a lambda whose parameter is whatever the tag made — the control, the element, or the
`Component` — and runs it as an **effect**. So it is not an initialiser: every signal the expression
reads is a dependency, and the control is re-fed whenever one of them changes. That is the whole
value of it, and it is why the property this replaces was usually shadowed by a hand-written
`Restate` that somebody had to remember to call.

It leaves with the region that declared it, like every other binding: an `@if` arm or a `@for` row
that goes takes its `use` with it, which a subscription made in `OnComposed` does not.

⚠ **It must be idempotent, because it runs more than once.** Say what the control should *be*, not
what to do to it: `SetItems`, not `Add`. A `use` that appends will append again the next time one of
its dependencies changes.

⚠ **It is also the escape when a subclass is impossible.** The four-line wrapper below is the better
answer whenever it is available — it is checked at the tag, it reads as a property, and it costs
nothing at run time. `use` is what is left when the control is `sealed`, or when what is needed is a
call with several arguments rather than one value.

### Writing an element's own text

An interpolation is a `text` **child**, not the parent's `Text`:

```xml
<fact-name>@Label</fact-name>          <!-- <fact-name><text>…</text></fact-name> -->
<fact-name Text="@Label" />            <!-- a selector attribute; sets nothing you can see -->
```

An attribute on a lowercase tag is not a property assignment — it goes to the style tree, where a
selector can match it and nothing reads it back. So `row.Add("fact-name").Text = label` has no direct
markup spelling, and the difference is a box: a `text` child is a layout node and the parent's own
text is not.

⚠ **`VXML2020` warns when the name is capitalised**, because that is the shape of an author who
expected an assignment: `<div AccessibleName="Save" Focusable="true">` compiled, matched an
`[AccessibleName]` selector and did nothing at all. The rule reads the *case* of the name rather than
looking the property up — the binder is syntax only, and never touches the compilation — so a
selector attribute spelled the way CSS spells one (`data-state`, `role`, `aria-label`) is left alone.
A warning rather than an error: a capitalised attribute really is matchable, so the reading is legal
and merely almost never meant.

⚠ **A capitalised tag is a real property assignment, and that is the whole escape.** It does not have
to be a `Component` or a `.vxml` — the emitter writes `ctx.Child<T>(…)` for any PascalCase tag and
lets C# resolve it, and `Text` is a `[UiProperty]` on every `UiElement`:

```csharp no-compile="a fragment; the real one is Editor/Vixen.Editor.AssetEditors/Audio/AudioMixerView.cs"
internal sealed class FactName : UiElement {
    protected override string TagName => "fact-name";
}
```

```xml
<FactName Text="@Label" />
```

Same tag, same position, same own text, four lines. Reach for a `.vxml` part instead when the thing
has a *shape* — several elements, or content of its own; a caption has none. `AudioMixerView` uses
nine of these and `Parts/FactRow.vxml` is the other kind.

⚠ **And [`use`](#use-for-a-control-fed-by-a-method) says it with no type at all**, which is what to
write when there is no subclass to be had:

```xml
<fact-name use="@(cell => cell.Text = Label)" />
```

The four-line subclass is still the better of the two where it is possible: `<FactName Text="@Label" />`
is checked at the tag and reads as what it is. `use` is the general answer — it reaches a `sealed`
control, and it reaches a method rather than a property.

### The `@for` key rule

**Key on the item's value when the item is immutable data. Key on the object only when that object
holds signals.**

`BuildContext.For` matches a key, reuses that item's region and **does not re-run the body** — which
is what makes focus, scroll offset and animation state survive a reorder. The consequence is that
every per-item binding stays closed over the item as it was when its key first appeared.

So a row of immutable data keyed on a stable field never updates again. `VXML2011` warns when a key
is a member access off the loop variable, which is the shape that mistake always takes; whether the
item holds signals is a question about its type, and the markup binder deliberately resolves none.

### ⚠ And the same rule governs `@if`

`@if` and `@for` are one mechanism, and an arm is rebuilt **only when the arm index changes** — so an
arm is a surviving region on exactly the terms a row is. The rule reads the same both times: *a
binding may close over a region's identity and never over its content.* For a row that identity is
the key; for an arm it is the predicate, which usually identifies far less.

```vxml
<!-- Wrong. Choosing a different cell does not change which arm is live, so the arm is not rebuilt
     and `shown` stays whatever was selected the first time anything was. -->
@if (Chosen is { } shown) {
    <FactValue Text="@shown.Label" />
}

<!-- Right. The condition may be a shape; every readout goes back through the signal. -->
@if (Chosen is null) {
    @("Select a cell.")
} else {
    <FactValue Text="@ChosenLabel" />
}
```

⚠ **Nothing diagnoses this one.** The loop shape is watched from three sides — `VXML2010`,
`VXML2013`, `VXML2011` — but a pattern variable in an `@if` arm is ordinary, legal C# that is correct
for the first value it ever sees. **If your panel has a detail pane over a selection, the test that
catches it is the one that selects a second thing.**

## Examples

A panel with three sections, which is where the nesting rules earn their keep. A child written inside
a capitalised tag hangs from that control's `ContentHost` — the viewport of a `ScrollView`, the
collapsible part of an `Expander` — so the nesting the markup draws is the nesting that exists.

```vxml
@component ImportSettingsView
@inherits Vixen.Ui.Controls.Control
@tag import-settings

<Alert ref="@Unknown" Title="This sidecar has settings this editor does not know" />

<ScrollView ref="@Scroll">
    <Expander Label="Import Settings" IsExpanded="true">
        <InspectorView ref="@Settings" />
    </Expander>

    <Expander Label="Addressable" IsExpanded="true">
        <InspectorView ref="@Addressable" />
    </Expander>
</ScrollView>

@code {
    public Alert Unknown { get; private set; } = null!;
    public ScrollView Scroll { get; private set; } = null!;
    public InspectorView Settings { get; private set; } = null!;
    public InspectorView Addressable { get; private set; } = null!;

    partial void OnComposed() => Unknown.AddClass("hidden");
}
```

And the key rule, both ways round:

```vxml
<!-- `StatisticRow` is a readonly record struct. Its value is its identity: change the count and
     the key changes, the old region goes, and a new row is built with the new number in it. -->
@for (var row in Rows) {
    <statistic-row key="@row">@row.Count</statistic-row>
}

<!-- `BackgroundTask`'s properties are signal-backed, so the object is stable and the bindings
     inside the row update themselves. This is what a stable key is for. -->
@for (var task in Tasks) {
    <task-row key="@task">@task.Progress.Value</task-row>
}
```

`key="@row.Label"` in the first loop would compile, draw the right number of rows in the right order,
and show the first reading for ever.

## A part is how markup writes an element's own text

Markup has exactly one spelling for text, and it is a child. `<fact-name>@Name</fact-name>` calls
`BuildContext.Text`, which creates a `text` element inside `fact-name`; it does not set
`fact-name.Text`. Nor does an attribute — on a lowercase tag `BuildContext.Attribute` special-cases
`class` and `style` and sends every other name to `StyleTree.SetAttribute`, which is an attribute a
`[name=…]` selector can match and nothing reads back. So `<fact-name Text="@Name" />` compiles,
runs, and does nothing.

That matters because a `text` child is a box of its own. Whether it lands where the parent's own
text would is a question about the parent's padding, its `align-items` and how many lines the text
wraps to — so replacing one with the other is a layout change, and a port that makes it silently is
a port that moved pixels.

⚠ **A capitalised tag is the way out, and this is the second reason to reach for `@inherits`.** A
component tag's attributes *are* property assignments — the emitter writes
`ctx.Bind(() => n1.Name = …)` — and a component's `ref`s belong to the component, so they still work
when a caller drops it into a `@for`, where a bare `ref` is `VXML2010`. Wrapping the row in a part
buys both at once:

```vxml
@component FactRow
@inherits Vixen.Ui.UiElement
@tag fact-row

<fact-name ref="@NameCell" />
<fact-value><text ref="@ValueText" /></fact-value>

@code {
    public UiElement NameCell { get; private set; } = null!;
    public UiElement ValueText { get; private set; } = null!;

    public string Name { set => NameCell.Text = value; }
    public string Value { set => ValueText.Text = value; }
}
```

`Vixen.Editor.Ui`'s [`FactRow`](/docs/api/vixen.editor.ui/factrow) is that file. A caller writes
`facts.Add<FactRow>()` from C# or `<FactRow Name="@f.Name" Value="@f.Value" />` from a `@for`, and
both produce the tree the four hand-written copies produced — which is asserted by dumping every
element's rectangle for the old shape and the new one and comparing the two.

⚠ **The setters are write-through rather than signal-backed on purpose.** The build runs from
`OnCreated`, so the parts exist before any parameter is assigned, and `BuildContext.Bind` runs its
assignment immediately as well as on every later change. A signal in between would buy nothing and
cost a frame.

## See also

* [Inspectors in markup](../editor/inspectors-in-markup.md) — a `.vxml` bound to an editing target by name
* [Key/value lists](key-value-list.md) — the control the row loops above used to be written by hand
* [Reactive collections](reactive-collections.md) — what a `@for` binds to when the rows are mutated rather than reprojected
* [`BuildContext`](/docs/api/vixen.ui.composition/buildcontext) — what both flavours build with
* [`Component`](/docs/api/vixen.ui.composition/component) — the default base, and what it cannot do
