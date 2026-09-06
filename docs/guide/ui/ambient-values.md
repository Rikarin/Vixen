---
title: Ambient values
slug: ui/ambient-values
kind: guide
area: Core
summary: A value provided on an element and found by type from anything inside it — SwiftUI's Environment with the walk written down — so a theme, a selection or a view-model stops being threaded through props by hand.
api: [T:Vixen.Ui.UiElement, T:Vixen.Ui.UiDocument, T:Vixen.Ui.Markup.Syntax.InjectDirectiveSyntax]
tags: [ui, markup, components, composition]
since: 0.2
status: preview
related: [ui/markup-panels, ui/commands, ui/documents]
---

## What it is

`element.Provide<T>(value)` puts a value on an element. `element.Inject<T>()` finds the nearest one
on the way up — this element, then its ancestors, then the document.

```csharp no-compile="a fragment; `panel` and `leaf` are UiElements in a document"
document.Provide<ITheme>(new DarkTheme());   // the application's
panel.Provide<ITheme>(new PreviewTheme());   // this subtree's

leaf.Inject<ITheme>();                       // the preview one
panel.Unprovide<ITheme>();                   // reveals the application's again
```

Every cross-cutting value in this framework was threaded through props by hand before this —
`Samples/02-HelloUi/Shell.vxml` repeats `Model="@Model"` on three panels in a row, and each panel
that gains a child gains another copy of the line.

⚠ **The key is the type argument, not the value's runtime type.** `Provide<ITheme>(new DarkTheme())`
is found by `Inject<ITheme>` and not by `Inject<DarkTheme>`. That is what makes an interface the
useful key and what stops a subclass silently shadowing the base everything else asks for.

⚠ **This is not `[UiProperty(Inherits = true)]` and not the cascade.** That attribute's generated
walk tests `ancestor is TOwner`, so it inherits down one *kind* of element and is CSS inheritance
wearing a C# name — its only producers in the whole tree are three test fixtures. Nothing that ships
inherits a property at all.

⚠ **Walked on every ask rather than cached**, which is `FindUndoManager`'s rule and
`FindEditedDocument`'s: an element is reparented and a cached answer is the one that was nearest when
the control was built. All three are deliberately the same walk — nearest declaration wins, and the
document is the last word.

⚠ **A value provided above a docking host survives both the arrangement and a tear-out**, and the
second half is not obvious. A `DockPanel` is parked and then placed, so it never stays where it was
written — but `Detached` and every group body are parts of the host, so a docked panel is always
under it. Torn into its own window it moves to a second `UiSurface`, and the value still reaches it:
a secondary surface's root is parented under the element that asked for the window, so the chain runs
`row < dock-panel < dock-body < dock-group < ui-surface < docking-host < shell-frame`. The element
tree spans surfaces even though the windows do not, so a shell can provide on its own frame rather
than on the document and a torn-off panel keeps the answer.

## What it is for

Cross-cutting values that every level of a tree may need and no level should have to thread through
the ones between — a theme, a selection, a service. ⚠ **The nearest one wins**, which is the whole
point: a preview pane provides its own theme and everything inside it sees that, while the rest of
the application sees the document's, and neither has to know about the other.

## Using it

### From a component

```csharp no-compile="a fragment; the code-behind half of a .vxml component"
partial class Workspace {
    public Project Project { get; set; } = Project.Empty;

    protected override void OnProvide() => Provide<ISelection>(new Selection(Project));
}

partial class Inspector {
    ISelection Selection => Inject<ISelection>() ?? Selection.None;
}
```

```xml no-compile="a fragment"
<inspector-name>@Selection.Name</inspector-name>
```

A provider overrides `OnProvide` in its code-behind; a consumer exposes a property that injects, and
the markup reads it as it reads any other expression.

### `@inject`, for the consumer's half

The property a consumer writes by hand is one line, and one line is what a header replaces:

```xml no-compile="a fragment"
@inject ISelection Selection

<inspector-name>@(Selection?.Name ?? "nothing")</inspector-name>
```

That generates `private ISelection? Selection => Inject<ISelection>();` and nothing else. Write it
more than once for more than one value — `@inject` and `@using` are the two headers a file may
repeat.

⚠ **The generated property is nullable and the fallback stays in the file.** `Inject` answering null
is an ordinary case, so a directive that pretended otherwise would move a decision into generated
code: what a missing value should mean is the consumer's to say, which is why `?? Selection.None`
is still written where it is. Reach for the code-behind property when the fallback is the interesting
part, and for the header when it is not.

⚠ **The value is read at the moment the property is read, not when the component was built.** Nothing
is cached, so a component that outlives a change of provider sees the new one. It is still not
*reactive* — see below — and that distinction is the whole of what an ambient value promises.

### `<provide>`, for a value the markup already has

A component that is providing something it can name in an expression writes it as a tag instead:

```xml no-compile="a fragment"
<workspace>
    <provide type="ISelection" value="@Selection" />
    <Inspector />
    <Timeline />
</workspace>
```

That becomes `workspace.Provide<ISelection>(Selection)` — on the element the tag is *written in*, not
on the component's own root, so a sibling of `<workspace>` does not see it.

⚠ **The key is written out because it cannot be inferred, and that is a fact about `Provide<T>` and
not about the tag.** The runtime keys on the type argument so that an interface is the useful key and
a subclass cannot shadow its base; an inferred key would be the concrete class every time, and
`Inject<ISelection>` would find nothing. The binder could not infer it in any case — it never touches
the compilation, which is what keeps a C# edit from re-running the markup generator.

⚠ **Document order is the rule.** The emitter writes nodes in the order they appear, so a component
written *above* the `<provide>` is built before the value exists and injects null. That is the same
reading an author already has of every other tag, and it is why the conventional place for a
`<provide>` is the first line inside its element.

`OnProvide` is still the hook to use when the value has to be *computed* — it runs after this
component's parameters are assigned and before any child of it exists, which no tag position can
express.

⚠ **`OnProvide` is the one hook that runs after this component's parameters are assigned and before
any child of it exists**, and both halves matter. Earlier — at attach time — and the value would be
computed from a parameter still at its default, which looks correct from C# (where a caller sets
properties before mounting) and is wrong from every `.vxml`, where the emitter's `Create` …
assignments … `Compose` is what sets them. Later — after `Build` — and the children that were meant
to inject it have already been built.

⚠ **It is a hook rather than the first line of `Build` because a markup component has no first line
to write in.** The generator owns the whole method, so a runtime reachable only from inside `Build`
would be reachable from hand-written components and from no markup at all.

**`Component.Inject` is answerable from inside `Build`** because `Root` is already parented by then:
`BuildContext.Create` makes the host element and attaches it before `Compose` runs the build.

## Examples

**A preview that overrides one value for its subtree.** `Unprovide` reveals what was underneath
rather than clearing it, so the application's theme is never lost:

```csharp no-compile="a fragment; `document` and `preview` are the caller's own"
document.Provide<ITheme>(new DarkTheme());
preview.Provide<ITheme>(new PreviewTheme());

preview.Inject<ITheme>();     // the preview one
preview.Unprovide<ITheme>();
preview.Inject<ITheme>();     // the document's again
```

**A leaf that must work with nothing provided.** `Inject` answering null is an ordinary case, not an
error — a control written to fall back is a control that can be dropped into any tree:

```csharp no-compile="a fragment; the code-behind of a component"
ISelection Selection => Inject<ISelection>() ?? Selection.None;
```

## What is deliberately not here yet

**A generic key.** `@inject Row<T> Rows` does not lex, for `@inherits Row<T>`'s reason: a `<` does
not appear inside a name, and teaching one directive to read angle brackets would make the lexer's
one unambiguous character ambiguous. A generic key is still a code-behind property, and
`Provide<T>`/`Inject<T>` take one happily.

**Three diagnostics and no fourth.** `VXML2021`, `VXML2022` and `VXML2023` are about the `<provide>`
tag's own shape, and `@inject` adds none: a half-written header is the parser's missing token and a
key that is not a type is Roslyn's, both reported on the `.vxml`'s own characters through the
`#line`. Nothing checks that anything ever provides what a consumer injects, and nothing can — the
same component is correct with a provider above it and correct without one, in a tree that is
assembled at run time. That is why the generated property is nullable rather than a shortcoming of
the directive.

**No reactivity of its own.** An ambient value is read where it is read; a value that has to *change*
is a signal put into the ambient slot, and the consumer's `@expr` subscribes to it as it would to
any other. Providing a new object does not invalidate anything that already read the old one.

## See also

- [Markup panels](../ui/markup-panels.md) — `OnProvide` is the component seam these are written from.
- [Commands and the responder chain](../ui/commands.md) — the other walk up the tree, and why the two
  answer different questions about the same ancestors.
- [Documents](../ui/documents.md) — the document is the last provider on the way up, which is what
  makes it the right place for an application-wide value.
