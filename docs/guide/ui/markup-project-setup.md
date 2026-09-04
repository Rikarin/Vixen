---
title: Making a project compile markup
slug: ui/markup-project-setup
kind: guide
area: Core
summary: What turns a .vxml on disk into a class — one line outside this repository and one inside it — and the two build errors, VX4001 and VX4002, that say which half is missing rather than blaming the markup.
api: [D:VX4001, D:VX4002]
tags: [ui, markup, vxml, msbuild, build, diagnostics]
since: 0.2
status: preview
related: [ui/markup-panels, ui/desktop-application]
---

## What it is

A `.vxml` is compiled by `Vixen.Ui.Markup.Generators`, a Roslyn source generator. Two things have to
be true for it to run: the file has to be an `AdditionalFiles` item, and the generator has to be
loaded as an analyzer. Neither is automatic for a file that merely exists.

Outside this repository both arrive with the package:

```xml
<PackageReference Include="Vixen.Ui.Controls" />
```

`Vixen.Ui` ships its build assets in `buildTransitive/`, so the `.vxml` glob, the `.vcss` glob, the
utility-stylesheet step and the two generators come with the reference and nothing else is written.

Inside this repository there are no packages, and MSBuild assets and analyzers do not travel through
a `ProjectReference`. `Directory.Build.targets` stands in for the package, and the declaration is one
line in the `.csproj`:

```xml
<VixenUi>true</VixenUi>
```

## What it is for

The reason this page exists is that the failure had no message of its own. A `.vxml` names a `partial
class` that the generator completes; when the generator does not run, the hand-written half is all
there is, and the compiler reports every member the markup was supposed to add:

```
error CS1061: 'WaterZoneFacts' does not contain a definition for 'Show'
```

That names a file that is correct, a type that is correct, and a member the author did write — just
in the other half. Every mistake in the message is upstream of the markup, in the project file.

⚠ **And the quiet form is worse than the noisy one.** A `.vxml` with no hand-written partner produces
no error at all: nothing reads the file, no class is generated, nobody asks for one, and the build
succeeds with a panel missing from the binary.

`VX4001` and `VX4002` are the two diagnostics that replace both outcomes. They are `VX` codes rather
than `VXML` ones because a `VXML` code is a claim about a file's *contents*, made by a generator that
has read it — and the whole content of these two is that no generator ran. `docs/manual/diagnostic-codes.md`
is the register the `VX` ranges are allocated from.

## Using it

**`VX4001` — the file is not compiler input.** No glob has claimed it, so it is not an
`AdditionalFiles` item and nothing will ever read it. Add the declaration:

```xml
<PropertyGroup>
    <VixenUi>true</VixenUi>
</PropertyGroup>
```

**`VX4002` — the file is compiler input and the compiler is absent.** The globs ran but
`Vixen.Ui.Markup.Generators.dll` is not in `@(Analyzer)`. This is the shape that used to cost an
hour, because it looks identical to having no wiring at all: a project that imports
`Vixen.Ui.targets` by hand — for the `**/*.vcss` glob, say — gets the `.vxml` glob with it and no
generator, so the moment a first `.vxml` appears the file becomes an item nothing compiles. Half the
wiring is worse than none of it, because none of it at least leaves the file inert.

Both are **errors**. A warning would print ahead of a wall of C# errors naming the wrong file, and in
the quiet case there is no wall to print ahead of.

**To keep a `.vxml` deliberately uncompiled**, say so:

```xml
<VixenUiMarkupCheck>false</VixenUiMarkupCheck>
```

That is for markup that is data rather than source — a scaffold a template copies, or a malformed
fixture a test feeds to the parser on purpose. It is not a way to make `VX4002` go away; a project
that wants its markup compiled wants the generator.

## Examples

A project whose first interface arrives:

```xml
<Project Sdk="Microsoft.NET.Sdk">

    <PropertyGroup>
        <TargetFramework>net10.0</TargetFramework>
        <RootNamespace>Vixen.Editor.Water</RootNamespace>

        <!-- One .vxml or one .vcss in this project is the whole reason for this line. -->
        <VixenUi>true</VixenUi>
    </PropertyGroup>

</Project>
```

A template project that ships markup as content rather than as source:

```xml
<PropertyGroup>
    <!-- The .vxml under templates/ is copied into a new project, not compiled into this one. -->
    <VixenUiMarkupCheck>false</VixenUiMarkupCheck>
</PropertyGroup>
```

## See also

- [Panels in markup](markup-panels.md) — what to write once the file compiles.
- [Running a UI application](desktop-application.md) — the `Main` that hosts what the file compiles to, and where the generated utility sheet has to be named.
- `docs/manual/diagnostic-codes.md` — the register the `VX` ranges are allocated from, and why these
  two are not `VXML`.
