# Vixen.Editor.Inspector

The editors an object's own attributes ask for, generated at build time, edited across a whole
selection at once, and recorded on the undo stack without any drawer knowing that is happening.

Spec: [docs/plan/11](../../docs/plan/11-editor.md) § "`Vixen.Editor.Inspector`".

```csharp
public sealed class WaterMaterial {
    [Inspector, Range(0, 1)]                     public float Roughness = 0.2f;
    [Inspector, ColorUsage(hdr: true)]           public Color4 Tint = Color4.White;
    [Inspector, AssetPicker(typeof(Texture))]    public AssetId NormalMap;
    [Inspector, Header("Waves"), Curve]          public AnimationCurve Amplitude = AnimationCurve.Linear();
    [Inspector, ShowIf(nameof(UseFoam))]         public float FoamWidth = 0.1f;
    [Inspector]                                  public bool UseFoam = true;
}

var view = panel.Add<InspectorView>();
view.EditedDocument = document;
view.Inspect(first, second, third);   // one type, several objects
```

## Generated, not reflected — and the accessor is why

`Vixen.Core.Reflection` already generates a descriptor per `[DataContract]` type, and the property
grid in `Vixen.Ui.Controls.Advanced` is built on it. This is a second descriptor layer, and it earns
its place on one point: **its accessors reach a field by reference.**

`MemberDescriptor`'s accessors pass values as `object`. That boxes, which is fine for tooling, and it
is why `PropertyGrid` documents that *a struct member of a struct member cannot be written back* — the
edit lands on a box nothing holds. Doc 11 asks for "get/set accessors as delegates over `ref` access
… it works for `struct` members without boxing", and that is what the generator here emits:

```csharp
new InspectorMember<WaterMaterial, Color4>("Tint", static (WaterMaterial o) => ref o.Tint)
```

A *property* has no reference to take, so it gets a getter and a setter and the drawer reads, modifies
and writes the whole value. The generator emits the strongest accessor each member admits rather than
the weakest one both admit.

The rest follows the same bet the reflection layer makes: registration is a module initializer, so
referencing an assembly is enough for its types to be inspectable — no scan, no
`AppDomain.GetAssemblies`, and a member the generator could not describe is a build error rather than
a row that quietly never appears.

**Attributes come from both places.** `[Range]`, `[Tooltip]` and `[Category]` are `Vixen.Core`'s and
are read by simple name, exactly as `Vixen.Core.Reflection`'s generator reads them, so this needed no
new vocabulary for the things both layers care about. `[Inspector]`, `[Header]`, `[ShowIf]`,
`[ColorUsage]`, `[AssetPicker]`, `[Curve]` and `[Multiline]` are editor concerns and live here — a
serialisation-facing descriptor has no business knowing what an asset picker is.

## Multi-object editing is the whole design, not a mode

Selecting twenty objects and setting one field on all of them is *the* operation an inspector exists
for. Showing the first one's values and silently editing only that is the bug, and it is the bug you
get by building the single-object case first.

So there is no single-object case. `InspectorField` binds one member to *n* targets, `Read` answers
either a value or `IsMixed`, and `Write` goes to every one of them. A drawer is handed a field and
nothing else, which is why a third-party drawer gets mixed-value handling, undo, conditions and
reset-to-default without knowing any of them exist.

**Disagreement is a state, not an average.** An indeterminate checkbox, an empty field with a dash for
a placeholder, a per-component dash in a vector row. Twenty objects with three different values must
never show one of them as though it were the answer.

⚠ **Every inspector has one bug here, and this is where it is fixed.** Putting a value into a control
raises the control's changed event, which calls back into `Write`. Ordinarily that is harmless — the
value is the one already held. A *mixed* field has no such value: it parks the control at a neutral
position, and that neutral position would then be written to every selected object the instant the row
was drawn. `InspectorField.Refreshing()` is a reference-counted guard around the fill, held by the view
rather than by each drawer, so a drawer nobody here wrote gets it too.

## Every edit is a command, by construction

`InspectorField.Write` builds a `SetMembersCommand<TOwner, TValue>` and executes it on the document's
stack. A drawer never touches an undo stack, which is what makes "every edit produces a command" true
by construction rather than by every drawer remembering.

⚠ **`Write`, `Read`, `WriteEach`, `Refreshing` and `Seal` are `EditProperty`'s** — the editing
pipeline in `Vixen.Editor.Core`, which every surface in the editor now writes through. An
`InspectorField` *is* one, plus the four things that are an inspector's business and nobody else's: a
type's defaults to reset to, a prefab to revert to, the condition that decides who an edit reaches,
and the generated `InspectorMember` a drawer reads its range and header off. `InspectorMember`
satisfies `IEditMember`, and `InspectorEditProvider` is how an `EditTarget` finds one — a lookup over
`InspectorRegistry`, not a second place a member can be declared.

The point is that a scene-view tool, a graph editor and a plugin's own panel get the same five verbs
without going through the inspector, so their entries land on the same stack in the same order. See
[docs/guide/editor/editing-pipeline](../../docs/guide/editor/editing-pipeline.md).

One command for the whole selection, not one per object — undoing an edit made to twenty things is one
keystroke, and a composite of twenty entries is a history nobody can read. **The old values are per
object**, because the point of a mixed-value edit is that the objects disagreed and undo has to put
each one back to what *it* held.

Merging is what makes a slider drag one entry: two commands merge when they set the same member on the
same objects, and the merged one keeps the earlier's old values, so one undo goes back to before the
drag. `Seal()` on mouse-up ends the run — a time window would make how many undo steps an edit produced
depend on how fast somebody moved a mouse.

## The panel is a fixed strip over one scroll region

`InspectorView` is three things down a column: the header, `Scroll`, and the empty state. The rows
live in `Scroll.Content` rather than directly in the view, and the header deliberately does not — a
search box that scrolled away with the rows is unreachable exactly when the panel is long enough to
need one.

⚠ **`Scroll.Content` is public, and that is what a host adds its own sections to.** The editor's
component foldouts are the case: they are not the members of a described type and so are not this
view's to draw, but a panel with two independent scroll regions in it is one where half the answer is
off screen whichever region you move. Adding them beside the view instead is what made an entity with
three components end below the bottom of the window with no way to reach the last one.

⚠ **`min-height: 0` on the scroll view is load-bearing**, for the reason `inspector-editor`'s
`min-width: 0` is: a flex item's automatic minimum is its content, so a region full of rows refuses
to be shorter than all of them, the bar never appears and the panel grows instead of scrolling.

## The small affordances

| | |
|---|---|
| **Reset to default** | From a fresh instance of the type, made once and kept. A type that cannot be constructed offers none rather than resetting to `null`, and a constructor that throws is treated as "no defaults" once rather than per row. |
| **Copy / paste property** | Typed, and paste refuses a conversion. Copying a position into a scale works; copying a `float` into an `int` is refused rather than truncated. Deliberately not the system clipboard — a property value is not text. |
| **Revert to prefab** | Per object and inside a transaction, because two instances of *different* prefabs revert to different values and reverting them both to the primary's source quietly rewrites one of them. ⚠⚠ It writes the template's value **and** calls `IPrefabSource.Release`, outside the "did the value move" test: an override *to the template's own value* writes nothing by definition, so a revert that was only the write would leave the row marked and the instance still claiming the member. |
| **Overridden** | A class on the row, drawn by un-muting the label. ⚠ It is what `IPrefabSource.IsOverridden` says and that is a **claim the instance records**, never a value comparison — a comparison cannot see an override to `0` or to a value equal to the template's, which is the whole of [doc 47](../../docs/plan/47-prefab-overrides-and-nested-prefabs.md) § 4. `InspectorField.Apply` is where an edit becomes one: it calls `Claim` after a write lands, and opens a transaction only when there is a claim to make, because a `CompositeCommand` cannot merge with the `SetMembersCommand` that follows it and a slider drag must stay one undo entry. |
| **Search** | Rows are hidden, not removed, so clearing the box costs a restyle and a field somebody was halfway through typing into survives a stray keystroke. |
| **Conditions** | A row is shown when *any* selected object would show it, and the edit reaches only the ones whose flag is on. Hiding a row because one of twenty has it off is how an edit misses nineteen objects. |

## The sheet is not optional

**This project declares two sheets and both are files**: `InspectorTheme.vcss` (200 lines) and
`BrowserTheme.vcss` (212 lines), beside the single `InspectorTheme.cs` that declares both classes.
They are embedded by the `**/*.vcss` glob in `Vixen.Ui.targets` and read back by their own `Css`
accessors; each was a `const string` until it was moved out byte for byte. Two files rather than one
because they are two sheets with two `Install` methods, loaded by different panels — a single file
would make either one unloadable on its own.

`InspectorTheme` is a fourth user-agent stylesheet after `ControlTheme`, `AdvancedTheme` and
`EditorTheme`, and a host has to load it:

```csharp
ControlTheme.Install(document);
AdvancedTheme.Install(document);
InspectorTheme.Install(document);
```

Without it the inspector is not merely unstyled, it is *wrong*: CSS's initial `flex-direction` is
`row` and `LayoutStyleBuilder` starts from CSS's initial values, so an element nothing styles lays
its children out side by side — the search box beside the fields, every member beside the one before
it, and the three boxes of a vector sized by whatever number happens to be in them. It is also where
a field stops being invisible. The control set gives a text box `--surface`, which is right on a page
and wrong in a tool window, because `dock-group` is `--surface` too.

The one thing it overrides rather than adds is `expander-content`'s indent. A `[Header]` does not
start a nested thing — it names a group of members that are siblings of the ungrouped ones above it —
so twenty pixels of prose indent puts "Name" and "Position" in two different columns of one panel.

## Drawers

`DrawerRegistry` resolves a member to a drawer by attribute first, then by type, then by fallback. The
attribute wins because it is the more specific statement: a `float` is a numeric field and a `float`
under `[Range]` is a slider; an `AssetId` is a text box and one under `[AssetPicker<Texture>]` is a
picker. A member with several attributes is matched in declaration order, so the answer does not depend
on a dictionary's iteration order.

Built in: bool, string, multiline string, every numeric type, enums (and `[Flags]` enums as a
multi-select), `Vector2/3/4`, `Quaternion` (as three angles), `Color4`, `AnimationCurve`, `AssetId`,
`AssetReference`, and a read-only fallback. A member nothing can edit is drawn read-only rather than
omitted — a member the inspector cannot edit is still one somebody needs to see the value of.

⚠ **`AssetReference` was the omission that mattered, and it did not look like one.** A bare `AssetId`
can only ever name an asset's *main* object, so what a scene actually stores is a reference — which
means `MeshRenderable.Mesh` and every material member on every component were `AssetReference`, had
no drawer, and fell through to the read-only last resort. The two most-used asset fields in the
editor were grey text. `AssetDrawer` now answers for both and boxes whichever the member holds:
`InspectorField.Write` hands its value to a generated setter that casts, so an id written into a
reference member is an `InvalidCastException` thrown from inside a click handler — which in a UI
framework kills the frame rather than refusing the edit. `AssetDrawer.Assign` is the one place that
knows this, and the picker and the drop both go through it.

## What an asset field will accept

`[AssetPicker(typeof(Texture))]` is this assembly's, and a runtime component must not carry it: a
component in `Vixen.Rendering` annotated for the editor would be a runtime assembly referencing an
editor one, which is the whole reason `ReflectedDescriptor` exists. So an asset member on a
*component* had no way to say what it holds, and every one of them offered the entire project — for
a clip member, a list with every texture and every scene in it.

`Vixen.Core`'s `[AssetType(typeof(AudioClip))]` is the other half. It rides through
`MemberPresentation` with the rest of what the serialization generator already records, and
`ReflectedMember` reads it into the same `InspectorMember.AssetType` the editor-side attribute fills
in — so the drawer, the picker and the drop path cannot tell which kind of type they are looking at,
and a game's own component gets the filter by adding one attribute from an assembly it already
references.

Which *importer* produces a given runtime type is not decided here. That join is the shell's, next to
the project it needs in order to answer.

Registration is per instance with a shared `Default`, so a plugin adds a drawer to the default and a
test proving one in isolation builds an empty registry. A single static would make two tests that
register drawers for one type unable to run in the same process.

⚠ **A plugin's drawer has to be removable, and `Remove` is why it exists.** `Default` is a static, so
a drawer left in it after its plugin was unloaded is a live reference into an assembly the editor is
trying to collect — and the symptom is not a stale drawer, it is a load context that stays in memory
for the rest of the session with nothing reporting it. `Remove` takes a drawer out of everything it
was registered for at once, because one drawer is commonly registered for a type *and* an attribute
and its owner should not have to remember which.

## Rotations are the one place a view is not the value

Nothing in the engine stores Euler angles — three orders give three rotations from the same numbers,
which is why the runtime representation is a quaternion. An inspector still has to show three boxes,
because "rotate it fifteen degrees about Y" is a thing people say.

`EulerAngles` is that conversion and only that: the order is `Quaternion.FromYawPitchRoll`'s, the
matrix is built from the library's own `Transform` rather than written out as a formula so the two
cannot disagree, and gimbal lock is *resolved* rather than avoided — at ninety degrees of pitch the
whole turn goes into yaw and roll is reported as zero. That is a real loss of the numbers the user
typed, and it is why the stored value stays a quaternion: only the display round-trips imperfectly.

### A curve over several objects, and what "mixed" means for one

Two curves agree when their keys agree — the same number of them, each at the same time and value
with the same tangents and mode. Disagreement is **not per-key**: two curves with different key
counts have no third key to call mixed, so there is nothing between "the same curve" and "not". The
row is mixed or it is not.

⚠ **Compared key by key rather than by `EditProperty.Read`, and that is a fix rather than a
preference.** `Read` compares with `Equals(object, object)`, which for a type with no equality is
reference identity — and `AnimationCurve` has none. A member written `= AnimationCurve.Linear()`
gives every instance its own object, so *every* multi-selection read as mixed whatever it held, and
`IsModified` was permanently true beside it. The comparison lives in the drawer rather than on the
type on purpose: an `AnimationCurve` is edited in place, raises `Changed`, and its keys sit in a
`HashSet` inside `CurveEditor`'s selection — value equality on a mutable model obliges a hash code,
and a hash that moves when a key is dragged takes the dragged key out of the set tracking it.

**A mixed curve shows an empty graph and stays editable.** Empty rather than one of them: showing
the first object's curve has the user editing "the" curve while looking at one arbitrary object's,
which is what `EditValue`'s remarks say must never happen. Editable because the only thing an empty
graph can produce is a curve authored in front of them, which every selected object then gets.

⚠ **And every write is a separate copy per object, through `WriteEach`.** One `Write` puts the *same
instance* on all of them, and twenty objects sharing one curve is not "they all have the same curve"
— it is "editing any of them edits all of them", silently, for the rest of the session.

⚠ **`Show` does not re-assign a curve the editor is already showing.** It runs on every change a
gizmo drag makes, and `CurveEditor.Curve` no-ops only on *reference* equality — so a fresh copy per
call swaps the object out from under the control forty times a second, clearing its selection and
re-subscribing.

## Not in

**A drawer for a nested object.** A member whose type has its own descriptor is drawn read-only rather
than expanding into a sub-inspector. The descriptor and the field binding both already support it; what
is missing is the row grouping and the decision about how a nested mixed value reads.

**The asset picker's picker.** `AssetDrawer` raises `PickRequested` and shows the name the host
resolves; the browser it opens belongs to the shell, as does the drop — this assembly has no
project, so it cannot say what a field will take, only where a field is and how to write one.

Licensed under Apache-2.0.
