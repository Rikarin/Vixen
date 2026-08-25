---
title: The accessibility tree
slug: ui/accessibility
kind: guide
area: Core
summary: A role, a name, a value, a state and a set of relations on every element, computed from what a control already holds rather than mirrored into it — plus one coalesced per-frame event, so a screen-reader bridge can cache a tree and diff it instead of asking a node at a time.
api: [T:Vixen.Ui.AccessibleRole, T:Vixen.Ui.AccessibleStates, T:Vixen.Ui.AccessibleRelation, T:Vixen.Ui.AccessibleRelationship, T:Vixen.Ui.Testing.AccessibilitySnapshot]
tags: [ui, accessibility, aria, screen-reader, testing]
since: 0.2
status: preview
related: [ui/commands, ui/markup-panels]
---

## What it is

Six things on `UiElement`, and one event on `UiDocument`.

| What | Where it comes from |
|---|---|
| `Role` | The type, through `NativeRole`. A `Button` is `AccessibleRole.Button` because it is a `Button`. Assigning `Role` overrides it, as the web's `role` attribute overrides an implicit one |
| `AccessibleName` | An explicit assignment, or the element this one is `LabelledBy`, or `NativeAccessibleName` — which for a button is its label and for a plain element is its own `Text` |
| `AccessibleDescription` | An explicit assignment, or the element this one is `DescribedBy` |
| `AccessibleValue` | `NativeAccessibleValue`: a field's text, the label of the option a `Select` is showing. `null` for anything that is an action rather than a value |
| `AccessibleState` | `NativeAccessibleState` from the control, `DeclaredAccessibleState` from the application, and `Disabled`, `Focused` and `Focusable` added by the framework from what it already knows |
| Relations | `AddAccessibleRelation(relation, target)` — the pairings the tree cannot show |
| `UiDocument.AccessibilityInvalidated` | Raised at most once a frame, from `Tick`, when anything above may have changed |

The role tokens are [WAI-ARIA 1.2](https://www.w3.org/TR/wai-aria-1.2/#role_definitions)'s, PascalCased
and nothing else — `tablist` is `TabList`, `menuitemcheckbox` is `MenuItemCheckBox`, and `img` is
`Img` rather than `Image` so that lowercasing a member name is the token for every member with no
table of exceptions to fall out of step. The states are ARIA's state attributes with four AT-SPI2
names for the three ARIA expresses by omission. A role this enum does not have yet is added *by its
ARIA name* rather than approximated with a neighbour.

## What it is for

A screen reader, a braille display, a magnifier and an automation harness all want the same four
answers about the same element, and none of them can get any of them from a draw list. The tree is
what a platform bridge — AT-SPI2 on Linux, UI Automation on Windows, `NSAccessibility` on macOS —
reads and republishes. Vixen ships the tree; the bridges are the platform's.

Two decisions are worth knowing before you use it, because both change what you have to write.

**A control's accessible view is computed, never stored.** `NativeRole`, `NativeAccessibleName`,
`NativeAccessibleValue` and `NativeAccessibleState` are virtual members, so a `CheckBox` reads its own
`IsChecked` when asked and there is no second copy to keep in step, no callback to remember, and no
state in which a box is ticked on screen and unticked to a screen reader. The cost of the whole
feature on an element that declares nothing is eight bytes — one nullable reference, allocated only
when an application sets a name, a role, a value or a relation on that particular element.

**The framework fills in what only it knows.** `AccessibleState` always carries `Disabled` when
`ElementState.Disabled` is set, `Focused` when the element has the focus, and `Focusable` when it is
a tab stop. No control declares any of the three, so no control can forget one — and the symptom of
a forgotten one is a screen reader saying a greyed button is available, which nobody writing the
control would ever see.

## Using it

Most controls need nothing. A `Button`, a `CheckBox`, a `Switch`, a `Link`, a `TextBox` and a
`Select` already carry a role and, where they have words of their own, a name.

What an application writes is the part the framework cannot know: **a field's name**. A `TextField`
deliberately answers `null` to `NativeAccessibleName` — its placeholder is a hint rather than a name
and it disappears the moment there is a value, so a form named from placeholders is a form whose
fields lose their names as they are filled in. The words beside a field are somebody else's element,
so say so:

```csharp no-compile="a fragment; `panel` is a UiElement in a document"
var caption = panel.Add<TextBlock>();
caption.Text = "Project name";

var field = panel.Add<TextBox>();
field.AddAccessibleRelation(AccessibleRelation.LabelledBy, caption);
```

`field.AccessibleName` is now `"Project name"`, and stays right if the caption is translated or
changed.

**Relations are for the pairings parent-and-child is the wrong shape for**, and there are three that
matter in practice:

* `LabelledBy` / `DescribedBy` — the element whose text names or describes this one. `LabelledBy` is
  the only relation that feeds another property.
* `Controls` — operating this element changes that one. A `TabItem` points at its panel: the two are
  in different parts of the tree, so no walk over `Parent` recovers the pairing.
* `Owns` — the target is this element's child in the accessibility tree but not in the element tree.
  A `Select`'s option list is a child of the document *root*, because a popover inside the field that
  opens it would be clipped by every scrolling ancestor between the two.
* `ActiveDescendant` — what the focus is "on" while this element holds it. A `Select` keeps the
  keyboard focus on itself while its list is open, so this is the only way to say which option is
  current.

A layout element is not a node: `Role` defaults to `AccessibleRole.None`, which is ARIA's `none`, and
a bridge walks straight through it and reads its children in its place. That is what stops a
four-field form being announced as thirty nested groups. `IsInAccessibilityTree` is the question.

**One event, once a frame.** Subscribe in `OnCreated`, unsubscribe in `OnRemoved`:

```csharp no-compile="a fragment; `document` is the application's UiDocument"
document.AccessibilityInvalidated += invalidated => bridge.Republish(invalidated.Root);
```

It says *that* something changed and never *what*. Accumulating the changed set per mutation is the
allocation the coalescing exists to avoid, and a bridge holds a cached tree it has to diff anyway.
Call `UiDocument.InvalidateAccessibility()` for a change the framework cannot see; it sets a flag and
is free to call a hundred times a frame.

## Examples

Reading an element's whole accessible view:

```csharp no-compile="a fragment; `element` is a UiElement in a document"
if (element.IsInAccessibilityTree) {
    Console.WriteLine($"{element.Role} \"{element.AccessibleName}\" = {element.AccessibleValue}");

    if ((element.AccessibleState & AccessibleStates.Disabled) != 0) {
        Console.WriteLine("  and it is greyed out");
    }
}
```

Giving an element a role the framework has no way to infer — a landmark, say:

```csharp no-compile="a fragment; `sidebar` is a UiElement in a document"
sidebar.Role = AccessibleRole.Navigation;
sidebar.AccessibleName = "Project";
```

`ClearRole()` hands the role back to the type; assigning `AccessibleRole.None` is a different
statement — "this is decoration, read through it" — and on a control the two do not land in the same
place.

**The test doc 09 promised itself.** `Vixen.Ui.Testing.AccessibilitySnapshot` renders the tree as
text a test can compare, and audits it for the failure a snapshot cannot catch:

```csharp no-compile="a fragment; `form` is a UiElement in a document, inside an xunit test"
// The assertion that cannot pass vacuously: every widget has a role *and* a non-empty name.
Assert.Empty(AccessibilitySnapshot.Unnamed(form));

Assert.Equal(
    """
    textbox "Project name" = "Vixen" [editable]
    checkbox "Overwrite existing"
    button "Save"
    """,
    AccessibilitySnapshot.Render(form)
);
```

`Render` walks *through* elements that are not nodes, so the indentation is accessibility-tree depth
rather than element depth and a control that grows a wrapper does not move in the snapshot. Owned
elements are emitted under their owner rather than where the tree has them, which is the picture the
`Owns` relation exists to produce.

Assert `Unnamed` first. `Render` of a document with no accessibility at all is the empty string, and
an expectation of the empty string matches it.

## See also

* [Commands and the focus route](/docs/guide/ui/commands) — the coalesced-invalidation pattern this
  follows, and the answer to "what has the focus", which is a question both features ask.
* [Panels in markup](/docs/guide/ui/markup-panels) — where the elements that carry roles usually come
  from.
