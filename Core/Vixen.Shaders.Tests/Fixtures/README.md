# Fixtures

`Lighting.rvn` is the shader the generated bindings are pinned against, and
`Lighting.reflect.json` is Raven's own reflection for it, checked in rather than produced
during the test run.

Checked in because the generator's contract is with the **schema**, not with the compiler:
running Raven here would make a shader-language change look like a generator failure, and
the interesting question — "does the generator still read what Raven writes?" — is answered
by regenerating deliberately and reading the diff.

⚠ **The cost of that is that nothing compares the two until somebody regenerates**, and the
first regeneration in a long while found the file had been claiming `lightCount` defaults to
`2` since it was written, against a shader that has always declared it `0`. Every test here
reads the JSON, so a JSON that disagrees with the shader is a fixture nobody is checking.
Read the whole diff when you regenerate, not only the part you expected to move.

To regenerate:

```bash
dotnet run --project Raven/Vixen.Raven.Cli -- compile Core/Vixen.Shaders.Tests/Fixtures/Lighting.rvn out --emit-reflection && cp out/Lighting.reflect.json Core/Vixen.Shaders.Tests/Fixtures/
```

The shader is deliberately not realistic. It is shaped to cover every case the layout rules
treat differently, because a fixture made of `float4`s proves nothing:

| Shape | Why it is here |
|---|---|
| `float3` then `float` | std140 packs them into one 16-byte slot; writing 16 bytes for the `float3` would clear the `float` |
| `mat4` | the one matrix that must **not** be rearranged — see docs/plan/07 § E |
| `mat3` | the only type whose host bytes and shader bytes genuinely differ (3 columns of 12 bytes in 16) |
| `uint` flag | four bytes carrying one bit — it was a `bool` until `RVN2137`, and the four bytes were always the host's convention rather than the type's size |
| `float[4]` | element stride 16, not 4 |
| `PunctualLight[MaxLights]` | a struct array — what a light list is, and what the flat reflection reports as leaves |
| `Buffer<PunctualLight>` | a second binding in the same set whose members also start at offset 0 |
| `Unused` | a permutation that changes no code, so `UsedPermutationKeys` has something to exclude |
