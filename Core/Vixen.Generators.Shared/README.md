<!--
SPDX-FileCopyrightText: Copyright (c) Rikarin
SPDX-License-Identifier: Apache-2.0
-->

# Vixen.Generators.Shared

Source shared by the repository's Roslyn generators. **This is a folder, not a project** — there
is no `.csproj` here and nothing builds it on its own.

## Why not an assembly

A source generator is loaded by the compiler from the analyzer path, and
`OutputItemType="Analyzer"` contributes exactly one DLL: a `ProjectReference` from a generator does
not travel with it. Sharing these types through a real assembly would mean shipping and resolving a
second analyzer DLL per generator, which is a packaging problem in exchange for four small files.
Linking the source costs nothing and cannot fail at load time.

## How to consume it

Add the specific files a generator needs — not a glob over the folder. `ArgumentNullException.cs`
carries a compilation-wide `global using` alias, so linking it where it is not needed silently
changes what `throw new ArgumentNullException(...)` constructs in that project.

```xml
<ItemGroup>
    <Compile Include="..\Vixen.Generators.Shared\IsExternalInit.cs" LinkBase="Linked\Shared" />
</ItemGroup>
```

From `Editor/**` the path is `..\..\Core\Vixen.Generators.Shared\`.

`EquatableArray.cs` additionally needs its namespace imported, since the generators each have their
own root namespace:

```xml
<Using Include="Vixen.Generators.Shared" />
```

## What is here

| File | Consumers | Notes |
| --- | --- | --- |
| `IsExternalInit.cs` | all 8 generators | Compiler contract for `init` accessors, absent from netstandard2.1. |
| `EquatableArray.cs` | Input, Ui.Markup, Net | Value-equal array. Without it an incremental generator silently re-runs everything. |
| `CallerArgumentExpressionAttribute.cs` | Input, Ui.Markup | Compiler contract the throw helpers below read. |
| `ArgumentNullException.cs` | Input, Ui.Markup | .NET 6 throw helper. Carries a `global using` alias — link deliberately. |

`Vixen.Ui.Markup.Generators` keeps a local `Compat/Netstandard.cs` for its
`ArgumentOutOfRangeException` helpers, which only it needs.
