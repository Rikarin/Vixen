# Vixen.Shaders.Generators

Reads Raven's `.reflect.json` as `AdditionalFiles` and emits, per shader, the typed keys and the
constant-buffer writer described in
[docs/plan/07 § Generated C# bindings](../../docs/plan/07-raven-shader-pipeline.md).

## Using it

```xml
<ItemGroup>
    <ProjectReference Include="..\Vixen.Shaders.Generators\Vixen.Shaders.Generators.csproj"
                      OutputItemType="Analyzer"
                      ReferenceOutputAssembly="false" />
    <AdditionalFiles Include="Shaders\**\*.reflect.json" />
</ItemGroup>
```

The reflection comes from `raven compile --emit-reflection`, which the content build runs anyway — so
this adds no build step, only a consumer for a file that already exists. Output lands in namespace
`Vixen.Shaders.Generated`, named after the file: `Lighting.reflect.json` gives `LightingKeys`,
`LightingConstants`, and one `…Element` struct per struct array.

## Three decisions that were forced rather than chosen

**The reflection model is hand-written, not shared with `Vixen.Raven.Reflection`.** A source generator
targets netstandard2.1 and runs inside the C# compiler; `Vixen.Raven` targets net10.0. The generator
*could not* reference the compiler even if that were desirable. What crosses between them is the JSON,
which is a schema and not a type — and only the fields actually read are declared, so Raven can add to
the schema without breaking a build here.

**The JSON reader is ours.** A generator runs in the compiler's assembly load context, which holds
Roslyn and nothing this project brings with it. A `PackageReference` to `System.Text.Json` compiles
and then fails to load at build time *in the consuming project*, which is the worst place to find out.
[docs/plan/07](../../docs/plan/07-raven-shader-pipeline.md) named this cost in advance; `Json.cs` is
it, and it reads only the subset Raven emits.

**An analyzer, not an MSBuild task.** The generated code has to be visible to the code that uses it,
in the same compilation, with rename and go-to-definition working in the editor before anything is
built. A task writing `.cs` into `obj/` gets there eventually, and gets there wrong for the first
build after a shader changes.

## One design decision that was a choice

The flattened reflection reports a struct array as independent leaves — `lights[].position`,
`lights[].color` — and generating four parallel arrays from that would have been honest to the layout
and awful to use: filling a light list would mean four arrays kept in step by hand. Instead each
struct array gets an element type with an indexed writer, which is the same bytes and the loop the
caller was going to write anyway.

It is indexed rather than taking the whole array because the count is the host's every frame: a list
sized for sixteen is filled to however many reached this draw, and the elements past that are not
zeroed but simply not read.

## Diagnostics

| ID | Meaning |
|---|---|
| `VXSH0001` | A file included as shader reflection could not be read. Warning — the build continues without those bindings |
| `VXSH0002` | A file included as shader reflection is not usable. Error, naming the file and the reason |

A malformed file is reported rather than thrown from: an analyzer that throws takes the build down
with a stack trace naming Roslyn, and the file at fault is the one thing the author needs.

## Tests

`Vixen.Shaders.Tests` references this project as an ordinary assembly and calls the emitter directly —
driving the full Roslyn generator host to get at a string would test Roslyn. The end-to-end claim is
made differently: `Generated/Lighting.Bindings.g.cs` is checked in and **compiled as part of the test
project**, which is the only way to assert that the emitted C# builds and that running it puts the
right bytes in the right places. A golden-text test keeps that file from drifting from the emitter;
`VIXEN_REGENERATE=1` rewrites it, and reading the diff is the review step.
