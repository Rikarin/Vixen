# Vixen.StyleGen

The utility stylesheet's build step. A project gets its sheet compiled at build time, with no code
of its own — no scanner call, no manifest walk, no reflection — and a `vixen.ui.vcss` carrying an
`@theme` block is optional, because the engine ships v4's palette and scales as the default.

Nobody runs it by hand. `Core/Vixen.Ui.Styling.Utilities/build/Vixen.Ui.Styling.Utilities.targets`
does, before `CoreCompile`, and that file is imported automatically by a `PackageReference` to
`Vixen.Ui.Styling.Utilities`.

## What it replaced

A startup bootstrap, written once per project. `Samples/14-Mmo/Mmo.Ui/Theme/MmoStyles.cs` is the copy
that still exists: a hundred and thirty lines that embed the markup as resources, walk the manifest,
run the scanner, run the generator and cache the answer — and whose own remarks say it is standing in
for a build step that had not been written. It has been written. The sample is deliberately left
alone as the reference for what the step replaced.

Three things were wrong with the bootstrap beyond its being repeated.

- **It could only see embedded markup.** Most of the editor's chrome is built in C# with
  `AddClass("flex")`, and a scanner pointed only at `.vxml` emits nothing for it — so the utility was
  silently missing on exactly the panels that most needed it. The build step scans `@(Compile)` too,
  which `CandidateScanner` is built for: it does not parse anything, it takes every run of characters
  that could be a class name and lets the generator throw the rest away.
- **It cost start-up time in every process that opened a document**, including every test.
- **It produced a string and not a file**, so nothing else in the tool chain could see the sheet.

## Why a process and not a source generator

The obvious shape for "read some files at build time and emit code" is an `IIncrementalGenerator`,
and it cannot be one here.

`ThemeTokens` reads YAML through `Vixen.Core.Yaml`, which is YamlDotNet. A Roslyn analyzer's
dependencies do not travel with it: `OutputItemType="Analyzer"` contributes exactly one DLL, so every
consuming project would have to place YamlDotNet on the analyzer path itself — the route
`Core/Vixen.Ui.Markup.Generators/Vixen.Ui.Markup.Generators.csproj` considers at length and rejects,
in the comment that is the best statement of this problem in the repository. That generator escapes
it by *linking* its front end's source into the analyzer assembly, which works because its front end
is Vixen's own code. It cannot work here: YamlDotNet is a package and there is no source to link.

An MSBuild `Task` assembly has the same problem twice over — it would have to be `netstandard2.0` to
load in Visual Studio's msbuild.exe, and `Vixen.Ui.Styling.Utilities` is net10.0.

So the step runs where the implementation already runs: out of process, at net10.0, against the
shipped assembly rather than a second copy of it. The cost is one process launch per build that
changed a scanned file, which MSBuild's `Inputs`/`Outputs` keeps to the builds that changed one. The
shape is not new — `Tools/Vixen.Sdk` invokes `Tools/Vixen.Cli` exactly this way for the content build.

## What it writes

Into `obj/…/Vixen/`:

| | |
|---|---|
| `<Class>.g.cs` | The sheet as `const string` — added to `@(Compile)`, so the binary carries the text and a shipped game has no build artefact to deploy. |
| `<Assembly>.g.vcss` | The same sheet as a file. Nothing compiles it; it is what a hot-reload watcher watches and what an asset-pipeline step will take as an input. |
| `<Assembly>.unrecognised.txt` | Every candidate that was not a utility. |
| `stylegen.rsp` | The command line, because MSBuild writes it long. |

⚠ **`unrecognised.txt` is not a warning list and must not become one.** The scanner is over-inclusive
on purpose, so with the C# scanned it is sixty kilobytes of ordinary English out of comments — a build
that warned about each would be one nobody reads the output of. But a *misspelt* utility lands there
too, and a misspelt utility is a style that silently does nothing, which no compiler and no binder can
see. The narrow question a project's own test suite can ask is *is every name written in a `class`
attribute a real utility*, and `Editor/Vixen.Editor.Ui.Tests/StylesheetTests.cs` is what that looks
like.

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
| `--scan <file>` | Searched for class names. Repeatable. C# and markup alike. |
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

⚠ **The editor still passes no `--base`.** `EditorTheme` is a `.vcss` now rather than a C# constant,
but it is loaded into the document directly by `EditorTheme.Install` rather than folded into the
generated sheet — the two arrive as separate `Load` calls at the same origin, base first, which is
what the layering needs. Folding it in would mean `EditorStyles.Css` carrying the whole stack and
`Install` loading one sheet instead of two; it is a real simplification and it is not done.

Licensed under Apache-2.0.
