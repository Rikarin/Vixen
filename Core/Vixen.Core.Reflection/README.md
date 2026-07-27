# Vixen.Core.Reflection

The AOT-safe replacement for `Type`-driven discovery. Every annotated type in an assembly, its
members, its attribute values and its factory — decided at compile time and registered before any of
that assembly's code runs.

```csharp
foreach (var component in TypeRegistry.With(TypeTraits.Component)) {
    var instance = component.Create();

    foreach (var member in component.Members) {
        Console.WriteLine($"{member.Name} = {member.GetValue(instance)}");
    }
}
```

No scan, no `AppDomain.GetAssemblies`, no `PropertyInfo.GetValue`. Referencing an assembly is enough
for its types to be discoverable.

## Why this exists

Stride injects module initialisers with Cecil to build the equivalent registry. ADR-002 forbids IL
weaving and `[ModuleInitializer]` does the same job in the language — but the mechanism is not the
interesting difference. The interesting difference is that **what gets registered is what a generator
saw in the source**, so it survives trimming and NativeAOT, where an assembly scan reads metadata the
publisher has already deleted. A type that cannot be described is a build warning at compile time
rather than a subsystem that quietly finds nothing on iOS.

## What is here

| | |
|---|---|
| `TypeDescriptor` | One type: alias, traits, members, factory, category, serializer. |
| `MemberDescriptor` | One member: name, type, order, presentation, and two accessor delegates. |
| `TypeTraits` | `DataContract`, `Component`, `EditorVisible`, `Abstract` — flags, because a type is usually several. |
| `MemberPresentation` | Category, tooltip, range, display name — what an inspector needs. |
| `TypeRegistry` | Everything registered, queryable by type, by name, by trait, and by base type. |

## The decisions

**Accessors are generated lambdas, not reflection.** A member reads as
`static instance => (object)((Foo)instance).X`. That is what lets an inspector read and write
arbitrary members on a platform where `System.Reflection`'s member access has been trimmed away.

**They pass values as `object`, which boxes a struct.** Deliberate, and it is why this is not a
frame-loop API: it is for tooling, inspection and boot-time discovery, where one allocation per
property read is invisible. Frame code uses the generated serializer or touches the field directly.

**An `init` setter is reached through `[UnsafeAccessor]`.** `{ get; init; }` is the shape doc 08 uses
for every importer's settings record, and a deserializer reading a `.meta` file has no object
initializer to write it in — so the generator binds to the setter directly rather than declaring the
member unwritable. `IsInitOnly` records the distinction, because a *tool* may reasonably want to
behave differently: an inspector editing an immutable record probably wants to rebuild it with `with`
and raise a change event rather than write through the box behind everyone's back. Nothing here is
reflection; `[UnsafeAccessor]` is bound at compile time and survives trimming like the rest.

**A struct's setter reaches into the box.** Assigning through a cast would modify a temporary copy
and silently do nothing, so the generator emits `Unsafe.Unbox<T>(instance).X = …` for value types.
The caller sees the change on the object it handed over, which is what an inspector editing a boxed
component needs.

**Editor visibility and serialisation are different questions.** `[DataMemberIgnore]` keeps a member
out of the serialised form; `[EditorVisible(false)]` keeps it out of the inspector. Conflating them
is how a cache field ends up in the inspector, or a tuning knob ends up out of it — so a descriptor
records both and neither implies the other.

**The serializer is resolved on demand, not stored.** Two module initializers fill two registries in
an order nobody chose. Looking the serializer up at the moment of asking removes the question
entirely.

**Two types claiming one name is an error.** Not last-one-wins, because the alternative fails as data
loading as the wrong type in whichever assembly happened to initialise second.

## In-repo wiring

Analyzers do not flow transitively through a `ProjectReference`, so a project in this repository that
declares annotated types names the generators it needs — both of them, since the descriptors come
from this one and the serializers they point at come from `Vixen.Core.Serialization.Generator`.
Consumers of the NuGet packages get both automatically; each package carries its own under
`analyzers/dotnet/cs`.

## Still to come

**`[Behavior]`**, which [doc 03](../../docs/plan/03-core-foundation.md) lists alongside the other
three. The attribute does not exist yet — it arrives with the engine loop in Phase 2 — and the
generator gains one line when it does.

**Generic types** get a warning (`VXS0201`) and no descriptor. A descriptor names one closed type; a
generic definition would need one per instantiation and the generator cannot know which exist. A
closed generic used as a component would need the ECS to declare it, which is a Phase 2 conversation.

Licensed under Apache-2.0.
