<!--
SPDX-FileCopyrightText: Copyright (c) Rikarin
SPDX-License-Identifier: Apache-2.0
-->

# Vixen.Core.Analyzers

The enforcement behind `[HotPath]`.

⚠ **The attribute promised this analyzer and the analyzer did not exist** (#1161). `HotPathAttribute`
was public, exported, exempted from the docs gate and described in two READMEs, and its own summary
called itself "a contract for the allocation analyzer" — which nobody had written. It was also applied
to no member anywhere in the tree. An attribute in that state is worse than none: from a call site it
reads like a checked contract, and it checks nothing.

Both halves are now real. This project is the rule, and `Vixen.Ecs` and `Vixen.Ui.Reactive` carry the
first marked members.

## Diagnostics

| Code | Meaning |
|---|---|
| `VXHP0001` | A `[HotPath]` member allocates on the managed heap. Warning by default, which `TreatWarningsAsErrors` makes an error. |

## What it reports

Six shapes, all of them written in the marked body:

```csharp
[HotPath]
void Step() {
    var log = new List<int>();       // a new reference type
    var scratch = new int[8];        // an array
    object boxed = frameIndex;       // boxing
    Enqueue(() => Use(scratch));     // a closure — it captures `scratch`
    Report($"frame {frameIndex}");   // an interpolated string
    Report("frame " + frameIndex);   // a concatenation
}
```

## What it deliberately does not report

⚠ **It reads one method body and cannot see through a call.** That is a property of a compile-time
rule and not a shortage of work: an interprocedural allocation analysis over the BCL is not something
an analyzer can do. This repository already owns the instrument for that half, and it is stronger —
`Vixen.Testing.Measured` counts `GC.GetAllocatedBytesForCurrentThread` across real work and asserts
exactly zero, which is what `TransformSystemTests`, `GraphTests`, `CharacterAllocationTests` and
twenty others do. The two are complements: the counter sees a callee on the paths a test drives, and
the rule sees the edit on every build, over the members no test measures.

Four more silences, each with a reason and a named negative test:

| Shape | Why it is not reported |
|---|---|
| `new SomeStruct(...)` | Bytes on the stack or in the enclosing object. A rule that reported `new Vector3` would be switched off within a week. |
| A capture-free lambda, and a method group over a `static` method | Allocated once and cached in a static field by the compiler (C# 11 for the method group). Reporting it names a cost nobody pays per call, and `static` — the fix a reader would reach for — changes nothing. |
| Anything under a `throw` | A frame that throws has already lost. Demanding a cached exception instance is how a rule teaches people to swallow errors instead of throwing them. |
| A member's own attribute list | ⚠ This one was a real defect first. A member's attributes are an operation block *owned by that member*, so the rule's first finding on every marked method was `new HotPathAttribute` — it reported the mark that turned it on. Thirteen of nineteen fixtures caught it, including every negative. |

`stackalloc` is a different operation kind and never reaches the rule, which is the point: it is the
answer the rule wants people to reach for.

## Where it runs

Referenced from `Directory.Build.props` by every project under `Core/` that is not a test, a generator
or an analyzer — the same set as `VXIO0001`. Unlike that rule this one reports nothing until a member
is marked, so the breadth costs a compilation that has marked nothing exactly nothing.

⚠ `Gameplay/`, `Platform/`, `Editor/`, `Tools/` and `Raven/` do **not** get it, so a `[HotPath]` there
is a comment again. `Vixen.Engine`'s frame code is the first thing that would want it and the reason
is scope rather than principle.

## What is marked, and what is owed

| Member | Why |
|---|---|
| `Query.ChunkEnumerator.MoveNext` | The frame's inner loop: every system walks chunks through it. |
| `Chunk.Values<T>` / `Chunk.ReadValues<T>` | The primitive every other iteration form is written in terms of. |
| `World.Get<T>` / `World.Read<T>` / `World.AdvanceVersion` | Per-entity component access and the per-phase version bump. |
| `EffectScheduler.Flush` | Doc 09's per-frame gate, which `GraphTests` already measures at zero bytes. |

**Owed: the sweep.** Seven members is a seed and not an inventory. The honest next list is the
production code that `Vixen.Testing.Measured`'s twenty-three callers already assert allocates
nothing — `TransformSystem`, the coroutine runner, the navigation query queue, the character
controller, the layout pass — because somebody has already decided those must not allocate and only a
test says so.

Licensed under Apache-2.0.
