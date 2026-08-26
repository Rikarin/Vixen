# Vixen.BcnOracle

A pipe around somebody else's BCn decoder, so `BcnReferenceDecoderTests` can check Vixen's against
one it did not write.

```sh
sh Tools/Vixen.BcnOracle/build.sh
dotnet test Core/Vixen.Core.Imaging.Tests/Vixen.Core.Imaging.Tests.csproj \
    --filter FullyQualifiedName~BcnReferenceDecoderTests
```

## Why this exists

Every other BCn test in the repository asserts against a block worked out by hand from the
specification — by the same person who wrote the decoder. That catches a misread of the spec and
cannot catch a misunderstanding of it: the fixture agrees with the bug. Running the same bits past an
implementation with no shared ancestry is the only thing that can, and the first run of it found that
BC1, BC3, BC4 and BC5 all truncated their interpolation where the specification divides reals, and
that `Unpack565` used bit replication rather than the value the spec names.

## The licence position — nothing third-party is committed

**`bcdec.h` is not in this repository and must not be added to it.** `build.sh` downloads it from
[github.com/iOrange/bcdec](https://github.com/iOrange/bcdec), pinned to a commit, into
`~/.cache/vixen/bcn-oracle/` and compiles it there. The only file here is `bcn-oracle.c`, which is
ours, is Apache-2.0 like the rest of the tree, and does nothing but move bytes between stdin, the
decoder and stdout.

bcdec is dual-licensed MIT / Unlicense, so vendoring it would be *permitted*. It is still the wrong
call. `CheckAttribution` exists so that every third-party file in the tree is accounted for, and
carrying a fifteen-hundred-line decoder — plus its notice, plus the job of tracking its upstream —
buys nothing a download does not. The verification this supports is a thing a developer runs and a
machine with the toolchain runs; it is not, and should not become, a gate that every build pays for.

**This means the check does not run by default, and that is stated rather than hidden.** With no
oracle built the suite *skips*, which xunit counts and prints, and never passes vacuously. Set
`VIXEN_REQUIRE_EXTERNAL_TOOLS=1` and every skip becomes a failure — that is what a machine which is
supposed to have the tools should set. The same variable governs `Ktx2ConformanceTests`, which needs
Khronos's `ktx` (`brew install ktx`) and is not vendored either, for the same reasons.

## The protocol

`bcn-oracle <format>`, blocks on stdin, decoded texels on stdout, until stdin ends.

| format | block | out per block |
| --- | --- | --- |
| `bc1` | 8 bytes | 64 bytes, sixteen RGBA8 texels |
| `bc3` | 16 bytes | 64 bytes, sixteen RGBA8 texels |
| `bc4` | 8 bytes | 16 bytes, sixteen R8 texels |
| `bc5` | 16 bytes | 32 bytes, sixteen RG8 texels |
| `bc6h` | 16 bytes | 96 bytes, sixteen RGB unsigned-half texels |
| `bc7` | 16 bytes | 64 bytes, sixteen RGBA8 texels |

`BCDEC_BC4BC5_PRECISE` is defined, because bcdec's default BC4/BC5 path is a deliberate speed
approximation and an oracle has no use for one.

⚠ **`bc3` and `bc4` disagree with each other inside bcdec, and Vixen sides with `bc4`.** A BC3
block's alpha half *is* a BC4 block, but `bcdec_bc3` routes it through the fast truncating path that
`BCDEC_BC4BC5_PRECISE` does not reach. For endpoints 96 and 13 at index 5 the exact value is
340/7 = 48.571: `bcdec_bc4` and Vixen say 49, `bcdec_bc3` says 48. That is the one place in the whole
comparison where the reference is the one that is wrong, so the test points BC3's alpha check at
`bc4` and says why.

Licensed under Apache-2.0.
