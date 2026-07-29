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

One command for the whole selection, not one per object — undoing an edit made to twenty things is one
keystroke, and a composite of twenty entries is a history nobody can read. **The old values are per
object**, because the point of a mixed-value edit is that the objects disagreed and undo has to put
each one back to what *it* held.

Merging is what makes a slider drag one entry: two commands merge when they set the same member on the
same objects, and the merged one keeps the earlier's old values, so one undo goes back to before the
drag. `Seal()` on mouse-up ends the run — a time window would make how many undo steps an edit produced
depend on how fast somebody moved a mouse.

## The small affordances

| | |
|---|---|
| **Reset to default** | From a fresh instance of the type, made once and kept. A type that cannot be constructed offers none rather than resetting to `null`, and a constructor that throws is treated as "no defaults" once rather than per row. |
| **Copy / paste property** | Typed, and paste refuses a conversion. Copying a position into a scale works; copying a `float` into an `int` is refused rather than truncated. Deliberately not the system clipboard — a property value is not text. |
| **Revert to prefab** | Per object and inside a transaction, because two instances of *different* prefabs revert to different values and reverting them both to the primary's source quietly rewrites one of them. |
| **Search** | Rows are hidden, not removed, so clearing the box costs a restyle and a field somebody was halfway through typing into survives a stray keystroke. |
| **Conditions** | A row is shown when *any* selected object would show it, and the edit reaches only the ones whose flag is on. Hiding a row because one of twenty has it off is how an edit misses nineteen objects. |

## The sheet is not optional

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
and a read-only fallback. A member nothing can edit is drawn read-only rather than omitted — a member
the inspector cannot edit is still one somebody needs to see the value of.

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

## Not in

**A drawer for a nested object.** A member whose type has its own descriptor is drawn read-only rather
than expanding into a sub-inspector. The descriptor and the field binding both already support it; what
is missing is the row grouping and the decision about how a nested mixed value reads.

**Multi-edit of a curve.** A curve is edited one object at a time, and a mixed one says so. Merging
twenty curves has no answer that is not a guess, and "apply this one to all" is a button rather than a
state of the editor.

**The asset picker's picker.** `AssetDrawer` raises `PickRequested` and shows the name the host
resolves; the browser it opens belongs to the shell.

Licensed under Apache-2.0.
