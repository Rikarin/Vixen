<!--
SPDX-FileCopyrightText: Copyright (c) Rikarin
SPDX-License-Identifier: Apache-2.0
-->

# Vixen.Core.Diagnostics.Analyzers

The half of [doc 13 § Discipline](../../docs/plan/13-diagnostics.md)'s *"every `catch` either handles
or logs with the exception object"* that a compiler can decide.

⚠ **That bullet claimed an analyzer enforced it for as long as it existed and none did** (#344). A
rule believed to be a gate is worse than a rule nobody wrote: the belief is what stops anybody
checking.

## Diagnostics

| Code | Meaning |
|---|---|
| `VXLG0001` | A `catch` takes every exception and never names it. Warning by default, which `TreatWarningsAsErrors` makes an error. |

## What it reports

```csharp
try {
    Reload(asset);
} catch (Exception) {          // VXLG0001 — every failure becomes this one, and nothing says which
    UseThePlaceholder();
}
```

One shape: the clause takes `Exception` (or is bare, which reaches as wide), has no `when` filter,
never rethrows, and never names what it caught. A clause like that cannot have handled the failure it
was handed, because it never looked at it — every failure the `try` can produce, including the ones
nobody predicted, takes the same silent path. That is this repository's recurring defect seen from
the inside: fifteen renderers degraded silently until they were made to log, and `PageResidency`'s own
log events had never fired.

## ⚠ CA1031 is not this rule, and is not running

The BCL's "do not catch general exception types" reads the *clause* and says nothing about the body,
so it reports the very form doc 13 asks for — `catch (Exception exception)` followed by a log carrying
`exception` — and stays silent on nothing `VXLG0001` reports.

It is also **off**. `AnalysisLevel` is `latest-recommended`, which does not enable it: sixteen
unfiltered broad catches compile clean in `Core/` today, and the seven
`#pragma warning disable CA1031` comments in the tree suppress a rule that was never switched on.

## What it deliberately does not report

| Shape | Why it is not reported |
|---|---|
| The body names the exception | It reached a log, a message, a condition, a list of failures. ⚠ Naming it inside an interpolated string counts, and so does naming it inside a lambda the body hands to something else — the check is the symbol an identifier binds to, never text, because a text scan gets exactly those two wrong. A hand-written scan drawing up this rule's first site list got three of eight findings wrong that way. |
| The body throws | `throw;`, or a wrapped rethrow. That is the other half of "handles or logs". |
| A `when` filter | A filter is a written predicate about which failures the clause is for, which is the thing an untyped catch is missing. |
| A narrower type | ⚠ `catch (OperationCanceledException) { }` is **not** reported, and that is a scope decision rather than an oversight. Naming a type is a decision about a named failure — cancellation, a socket closed under an accept, a codec's own error — and the twelve such clauses in `Core/` each carry the reason above them. A rule that reported those is one people learn to switch off, which is how the widest form gets through with it. |

## Where it runs, and what is off by name

Referenced from `Directory.Build.props` by every project under `Core/` that is not a test, a generator
or an analyzer — the same set as `VXIO0001` and `VXHP0001`.

⚠ **A project of its own rather than a rule inside `Vixen.Core.Analyzers`, and the reason is the
package boundary.** `Vixen.Core.csproj` packs that assembly into `analyzers/dotnet/cs` of its NuGet
package, deliberately: `VXHP0001` reports nothing in a compilation that marks nothing `[HotPath]`, so
a consumer gets the attribute *and* the rule that gives it meaning. `VXLG0001` reports on ordinary
code, so shipping it in the same assembly would hand somebody's game a warning — an error under their
own `TreatWarningsAsErrors` — for a `catch` in code this rule was never written about. That is exactly
the test `docs/GeneratorPackagingExempt.txt` asks of a compiler plugin, and `Vixen.Core.IO.Analyzers`
is the precedent for the answer.

Five clauses fire on `Core/` as it stands, and every one is somewhere the failure cannot be reported
from. They are off by name in `.editorconfig` with a written reason each:

| File | Why |
|---|---|
| `Core/Vixen.Core.Threading/JobScheduler.cs` (twice) | Thread placement. The scheduler holds no logger at all — by design, since the logging sinks' own work runs on it — and a worker that was not placed is already visible as a short `WorkersPlaced`. |
| `Core/Vixen.Core.Serialization/Storage/ChunkFormat.cs` | "This is not a frame this format produces" is the answer rather than a failure, and every byte range a reader is handed can fail it. |
| `Core/Vixen.Fuzz/FuzzSession.cs`, `Core/Vixen.Fuzz/SyntaxDomain.cs` | Catching everything is a fuzzer's assertion, and the throw has already been recorded as a named finding. |

⚠ `Gameplay/`, `Platform/`, `Editor/`, `Tools/` and `Raven/` do **not** get it, which is the same
scope `VXIO0001` has and for the same reason: it is the set `Directory.Build.props` applies engine
rules to, not a judgement that a silent catch is better there.

Licensed under Apache-2.0.
