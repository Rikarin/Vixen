---
title: Making a project compile markup
slug: ui/markup-project-setup
kind: guide
area: Core
summary: What turns a .vxml on disk into a class — one line outside this repository and one inside it — and the three build errors, VX4001, VX4002 and VX4003, that say which half is missing rather than blaming the markup.
api: [D:VX4001, D:VX4002, D:VX4003]
tags: [ui, markup, vxml, msbuild, build, diagnostics]
since: 0.2
status: preview
related: [ui/markup-panels, ui/desktop-application]
---

## What it is

A `.vxml` is compiled by `Vixen.Ui.Markup.Generators`, a Roslyn source generator. Two things have to
be true for it to run: the file has to be an `AdditionalFiles` item, and the generator has to be
loaded as an analyzer. Neither is automatic for a file that merely exists.

⚠ **And there are two generators, not one.** `Vixen.Ui.Generators` is the other — it turns a
`[UiProperty]` into a property the style system can see, and it carries VXS0320 — and a component
assembly wants both. The package ships them side by side, so outside this repository they arrive
together; inside it a hand-wired project can have one and not the other, and did, seven times.

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

`VX4001` and `VX4002` are the two diagnostics that replace both outcomes, and `VX4003` is the one for
the generator whose absence produced no outcome at all. They are `VX` codes rather than `VXML` ones
because a `VXML` code is a claim about a file's *contents*, made by a generator that has read it —
and the whole content of these three is that a generator did not run. `docs/manual/diagnostic-codes.md`
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

**`VX4003` — the file is compiler input and the *other* generator is absent.** The markup compiles
and the build is green; `Vixen.Ui.Generators.dll` is not in `@(Analyzer)`, so a `[UiProperty]` in the
same assembly generates no registration and is invisible to the cascade, and VXS0320 never runs. ⚠
This one replaces no symptom, because it never had one — which is exactly why seven projects reached
master in that state. The fix is the same line, or a second `ProjectReference` beside the first:

```xml
<ProjectReference Include="..\..\Core\Vixen.Ui.Generators\Vixen.Ui.Generators.csproj"
                  OutputItemType="Analyzer"
                  ReferenceOutputAssembly="false" />
```

All three are **errors**. A warning would print ahead of a wall of C# errors naming the wrong file,
and in the two quiet cases there is no wall to print ahead of.

**To keep a `.vxml` deliberately uncompiled**, say so:

```xml
<VixenUiMarkupCheck>false</VixenUiMarkupCheck>
```

That is for markup that is data rather than source — a scaffold a template copies, or a malformed
fixture a test feeds to the parser on purpose. It turns off all three. It is not a way to make
`VX4002` or `VX4003` go away; a project that wants its markup compiled wants the generators.

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
  three are not `VXML`.
