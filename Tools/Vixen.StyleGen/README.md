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
| `<Assembly>.unrecognised.txt` | Every candidate that emitted no rule, in two sections: the ones that named a registered family, then the ones that named nothing. |
| `stylegen.rsp` | The command line, because MSBuild writes it long. |

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
