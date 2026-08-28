# Vixen.StyleGen

The utility stylesheet's build step. A project gets its sheet compiled at build time, with no code
of its own — no scanner call, no manifest walk, no reflection — and a `vixen.ui.vcss` carrying an
`@theme` block is optional, because the engine ships v4's palette and scales as the default.

Nobody runs it by hand. `Core/Vixen.Ui.Styling.Utilities/build/Vixen.Ui.Styling.Utilities.targets`
does, before `CoreCompile`, and that file is imported automatically by a `PackageReference` to
`Vixen.Ui.Styling.Utilities` or to anything that depends on it — it is packed into
`buildTransitive/`, which is what makes a reference to `Vixen.Ui.Controls` enough.

⚠ **This tool travels inside that package's `tools/`, and it travels with its whole dependency
closure.** It used to be one assembly and a `runtimeconfig.json`, which is an entry point with
nothing behind it: the first line of `Main` touches `Vixen.Ui.Styling.Utilities`, and every package
produced before this shipped a `tools/` that threw `FileNotFoundException` out of an `Exec` on the
first build of the first project that used it. A framework-dependent build is flat, so what is packed
is the output directory's `*.dll` plus the `.deps.json` and the `.runtimeconfig.json` beside them.

## What it replaced

A startup bootstrap, written once per project — a hundred and thirty-five lines that embedded the
markup as resources, walked the manifest, ran the scanner, ran the generator and cached the answer.
`Samples/14-Mmo/Mmo.Ui/Theme/MmoStyles.cs` was the last copy of it; that file is deleted and the
sample's whole share of the step is now `<VixenUi>true</VixenUi>` and two item declarations.
`git log` is the reference for what it looked like.

Three things were wrong with the bootstrap beyond its being repeated.

- **It could only see embedded markup.** Most of the editor's chrome is built in C# with
  `AddClass("flex")`, and a scanner pointed only at `.vxml` emits nothing for it — so the utility was
  silently missing on exactly the panels that most needed it. The build step scans `@(Compile)` too,
  which `CandidateScanner` is built for: it does not parse anything, it takes every run of characters
  that could be a class name and lets the generator throw the rest away.

  ⚠ **A scanned `.vcss` is the one exception, and this is where the choice is made.** The scanner
  parses nothing, so `position: absolute` in a hand-written sheet looked exactly like
  `class="absolute"` — which put `.absolute`, `.block`, `.grid`, `.hidden`, `.inline`, `.relative` and
  `.static` into the editor's generated sheet, none of them written by anybody. A class name cannot be
  *used* from the right of a colon, so `CandidateScanner.ScanStyleSheet` skips a declaration's value;
  the extension is what selects it, because this is the only place that knows what kind of file it just
  read. `@apply p-4 flex;` is not a declaration and is scanned whole.
- **It cost start-up time in every process that opened a document**, including every test.
- **It produced a string and not a file**, so nothing else in the tool chain could see the sheet.

## Why a process and not a source generator

The obvious shape for "read some files at build time and emit code" is an `IIncrementalGenerator`.
This is not one, and the reason **is no longer the one this section used to give.**

⚠ **The YamlDotNet argument is dead. Do not cite it, and do not re-open this on the strength of its
having expired — that is already accounted for below.** `ThemeTokens` read YAML through
`Vixen.Core.Yaml` until `@theme` replaced it, and `Vixen.Ui.Styling.Utilities` now has no
`PackageReference` at all, so "an analyzer's dependencies do not travel with it" decides nothing
here any more. The question was re-asked and re-measured in 2026-08 against the editor's own build.
The answer came back the same and the reasons are different.

### The measurement

On an M1 Max, `Vixen.Editor.Ui` (43 scanned files, 528 KiB of text), twelve runs per figure:

| | |
|---|---|
| the whole `VixenGenerateUtilityStyles` target | 117 ms (MSBuild's own performance summary; `Exec` is 115 ms of it) |
| the same target on a build where nothing changed | **0 ms — skipped.** The `.stamp` is what buys this |
| `dotnet` host start + assembly load | ~52 ms (a run with no scan files at all) |
| scanning all 43 files | 11 ms — and 1 ms for one file |
| `UtilityGenerator.Generate` over the candidate set | 20 ms |

**~52 ms is the entire prize.** That is what disappears when the process does, and it is paid once
per build that changed a scanned file — never on a build that changed nothing.

**~20 ms is the price, and it would be paid per keystroke.** Scanning incrementalizes cleanly: one
file per `IncrementalValuesProvider` entry, 1 ms for the edited one. Generation does not, because it
runs after `.Collect()` over the whole candidate set — and that set is *designed* to be unstable.
The scanner parses nothing, so `Vixen.Editor.Ui` produces 7 262 candidates of which 7 173 match no
family at all: it is reading English out of comments. Almost any edit changes the set, so the
collected stage re-runs almost every time and the cache that would have saved it never hits. Making
the set stable means making the scanner parse, which is the one thing it must not do — a panel built
in C# with `AddClass("flex")` is why.

Six projects in this tree use the step. Trading 52 ms once per changed build for 20 ms per keystroke
in each of them is the wrong way round.

### Two blockers the YamlDotNet one was hiding, both still live

- **The target framework.** `Vixen.Ui.Styling.Utilities` is net10.0 and an analyzer must be
  netstandard, so it cannot be referenced from one — the same wall
  `Core/Vixen.Ui.Markup.Generators/Vixen.Ui.Markup.Generators.csproj` describes at length, in the
  comment that is still the best statement of this problem in the repository. The escape it uses,
  *linking* the source, **is** available here (5 995 lines across 8 files, and they touch nothing
  outside the BCL — unlike YamlDotNet, which had no source to link). But it means the code compiles
  twice under two language surfaces, with a `Compat` file to keep the second one holding. That
  generator calls the same cost real, and it is buying rather more with it.
- **RS1035.** `System.IO` is banned inside an analyzer, and every one of the thirteen generators in
  this tree sets `EnforceExtendedAnalyzerRules` — there is no precedent here for switching it off.
  Nor would switching it off help: a generator runs in the IDE's generator host, off any build's
  working directory, on every edit, so writing the sheet from one means writing it on every
  keystroke. So "make it a generator and keep the file too" is not the free option it sounds like —
  keeping the file means keeping an MSBuild step, which is the process, which is the thing being
  removed.

An MSBuild `Task` assembly has the framework problem twice over — it would have to be
`netstandard2.0` to load in Visual Studio's msbuild.exe.

So the step runs where the implementation already runs: out of process, at net10.0, against the
shipped assembly rather than a second copy of it. The shape is not new — `Tools/Vixen.Sdk` invokes
`Tools/Vixen.Cli` exactly this way for the content build.

### What would reverse this

Not "the dependency went away" — that already happened, and this section is the answer to it. The
decision turns on the 20 ms, so what reverses it is:

- **The candidate set becoming stable across keystrokes** — a declarative source for class names, or
  anything that stops the scanner emitting seven thousand English words. Then `.Collect()` caches,
  the generation stage stops re-running, and the per-keystroke cost goes to roughly nothing.
- **`UtilityGenerator.Generate` getting fast enough** — call it under ~2 ms for a project this size —
  that paying it per keystroke stops mattering.
- **Roslyn gaining a supported way for a generator to write a build artefact**, which removes the
  RS1035 half.

⚠ Note which way one familiar argument points. "The `.vcss` file is wanted downstream" is *not* a
reason to move — it is a reason to stay, because a generator cannot write it. Today nothing reads it
at all; see the table below.

## What it writes

Into `obj/…/Vixen/`:

| | |
|---|---|
| `<Class>.g.cs` | The sheet as `const string` — added to `@(Compile)`, so the binary carries the text and a shipped game has no build artefact to deploy. |
| `<Assembly>.g.vcss` | The same sheet as a file. **Nothing reads it** — see the warning below. |
| `<Assembly>.unrecognised.txt` | Every candidate that emitted no rule, in two sections: the ones that named a registered family, then the ones that named nothing. Read by people, not by code. |
| `stylegen.rsp` | The command line, because MSBuild writes it long. |

⚠ **`<Assembly>.g.vcss` has no consumer, and this line used to claim two it never had.** It said the
file "is what a hot-reload watcher watches and what an asset-pipeline step will take as an input".
The asset-pipeline step is future tense and always was. The watcher is the opposite of true:
`Platform/Vixen.Ui.Desktop.HotReload/DesktopHotReload.cs` excludes `obj` and `bin` *on purpose*, and
its remarks explain why binding to this file is a trap rather than an improvement — it is a build
artefact, so every rebuild would fire a reload of a file nobody edited, and the `obj/Release` copy
would bind a sheet the running process is not using. Everything that wants the sheet takes the
`const string` out of `<Class>.g.cs`: `Editor/Vixen.Editor.Ui.Tests/StylesheetTests.cs`,
`Editor/Vixen.Editor.App.Tests/SharedThemeTests.cs` and `Samples/14-Mmo/Mmo.Ui.Tests` all read the
constant, and `Core/Vixen.Ui.Styling.Utilities.Tests/ArbitraryPropertyTests.cs` carries a comment
forbidding the switch to the file.

Kept anyway, and the grounds are honest ones rather than a claimed reader: it costs one write of a
few kilobytes on a build that was going to write the accessor regardless, it is the only form of the
sheet a person can open while debugging what the step emitted, and it is the input an asset-pipeline
step would need on the day one exists. If that day does not come, deleting it is a one-line change to
the `.targets` and one to `StyleGenRunner.Write` — but note that its existence is an argument for
keeping the CLI, since a Roslyn generator could not write it at all.

⚠ **`unrecognised.txt` is not a warning list and must not become one.** The scanner is over-inclusive
on purpose, so with the C# scanned it is sixty kilobytes of ordinary English out of comments — a build
that warned about each would be one nobody reads the output of. But a *misspelt* utility lands there
too, and a misspelt utility is a style that silently does nothing, which no compiler and no binder can
see. The narrow question a project's own test suite can ask is *is every name written in a `class`
attribute a real utility*, and `Editor/Vixen.Editor.Ui.Tests/StylesheetTests.cs` is what that looks
like.

⚠ **Two sections, because the two refusals are not the same news and used to be one list.**
`UtilityFamilies.TryResolve` says `false` both for "no such family" and for "that family has no such
value", and `bg-clip-text` — a real Tailwind class against a root this engine registers — used to
arrive among seven thousand English words with nothing to mark it out. The first section is the
`UtilityGenerator.Unresolved` channel: each line names the family that was consulted and the value or
variant it had nothing for. For `Vixen.Editor.Ui` it is 43 lines against 7 060. The build line
carries both counts, so a number that moves is visible without opening `obj/`.

⚠ **43 is not clean either, and a build message per refusal was tried and measured before being
dropped.** Thirty-four of those 43 are a bare English word colliding with a registered family name —
`left`, `me`, `to`, `size` — and most of the rest are CSS property names scanned out of a `.vcss`. No
channel downstream of the scanner can undo the scanner's over-inclusiveness. See
`docs/plan/43-web-styling-parity.md` § F8.

⚠ **Only when the bytes differ.** An output rewritten with identical content still gets a new
timestamp, and a timestamp is what every incremental step downstream reads — so an unconditional write
would make the compiler rerun on every build and a watcher fire on every build, and the watcher would
get the blame.

⚠ **Nothing is written when anything failed.** A half-written stylesheet left in `obj/` is one the
next incremental build considers up to date, so the error appears once and then the build succeeds
against broken output for ever.

## The options

Written by the `.targets`, never by a person. `@file` reads one argument per line, which is the real
interface: a project with four hundred sources gives four hundred `scan` paths, past the command-line
limit on Windows and near it everywhere else, and one-per-line also means a path with a space in it
needs no quoting.

| | |
|---|---|
| `--theme <file.vcss>` | A sheet whose `@theme` blocks layer over the shipped default. Repeatable and ordered. Absent means the default alone. |
| `--scan <file>` | Searched for class names. Repeatable. C# and markup alike; a `.vcss` or `.css` is read as a stylesheet, which is the one input where a declaration's value can be skipped. |
| `--base <file.vcss>` | A hand-written sheet, emitted **before** the utilities, with `@apply` expanded. Repeatable and ordered. |
| `--safelist <name>` | Emitted whether or not anything was seen to use it. Repeatable. |
| `--output`, `--accessor`, `--report` | Where the three outputs go. |
| `--namespace`, `--class`, `--public` | What the accessor is called and whether it is public. |

⚠ **`--base` reads `@theme` too, and then takes it out.** A hand-written sheet may carry its tokens
at the top the way a v4 project does; the block is read into the token set before anything is
generated and stripped before the text is emitted, because `@theme` is a build-time construct and the
cascade has never heard of it. What the finished sheet references comes back as a `root` rule at the
very top, holding the theme variables it actually uses and the ones those use — never all 347, and
never one whose value names itself, which would shadow the declaration it is an alias for.

⚠ **The editor still passes no `--base`.** Every hand-authored sheet in the tree is a `.vcss` now
rather than a C# constant — ten of them — so `--base` has plenty it *could* be given; each is still
loaded into the document directly by its own `Install` rather than folded into the generated sheet — the two arrive as separate `Load` calls at the same origin, base first, which is
what the layering needs. Folding it in would mean `EditorStyles.Css` carrying the whole stack and
`Install` loading one sheet instead of two; it is a real simplification and it is not done.

Licensed under Apache-2.0.
