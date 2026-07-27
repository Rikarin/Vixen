# 07 — Addressables over HTTP

A content update, end to end and in one process: build, serve, download, change one asset, download
again — and count the bytes.

```
dotnet run --project Samples/07-AddressablesRemote
```

```
  Published v1  4 files, 144.9 KB
  Serving       http://localhost:57530/

  First run — nothing cached
    catalog       Updated
    characters/hero      96.1 KB   "Hello from the hero"
    props/torch          48.1 KB   "A torch, burning"
      ← catalog.bin.hash                                     32 B
      ← catalog.bin                                         447 B
      ← Characters_8b43fd4dbad9_aad0069f3462e386.bundle    96.1 KB
      ← Props_ba8d6f0eee34_bf247de3e471c6ae.bundle         48.1 KB

  Published v2  only props/torch changed; characters/hero is byte-identical

  Second run — same cache, one asset changed
    catalog       Updated
    characters/hero    cache hit   "Hello from the hero"
    props/torch          48.1 KB   "A torch, burning brighter"
      ← catalog.bin.hash                                     32 B
      ← catalog.bin                                         447 B
      ← Props_ba8d6f0eee34_0e5ca6d76f65559e.bundle         48.1 KB

  Cold start          144.6 KB
  After the update     48.6 KB   (34 % of a full download)
```

## What it is for

[Doc 14](../../docs/plan/14-roadmap.md)'s Phase 3 exit criterion says *a remote content update fetches
only the changed bundles, asserted by byte count*. There is a test that asserts it. This is the
version a person can watch, which is a different and also necessary thing: a passing test says the
property held once; a sample says what the property **is**.

Everything here is shipping code. The server is the one `vixen content serve` runs — pointing a phone
at a laptop and pointing this sample at itself are the same path. The client is `ContentUpdate`,
`BundleCache`, `RemoteBundleSource` and `AssetManager` as a game uses them. The only thing written for
the sample is the byte counter, and it exists because the claim is about bytes.

## Why the second run is cheap

**Bundles are named by their content hash.** `Characters` did not change between v1 and v2, so it
builds to identical bytes, so it gets an identical file name, so the client already has it. No
diffing, no manifest of changes — the name *is* the comparison. It is also why a CDN cannot serve a
stale bundle: a changed bundle is a different URL.

**The catalog is fetched only when a 32-byte file says to.** `catalog.bin.hash` is step 1 of
[doc 08](../../docs/plan/08-asset-pipeline-and-addressables.md)'s boot sequence. On a run where
nothing was published, those 32 bytes are the whole cost of starting up. Here it changes both times,
because both runs follow a publish.

**`PackSeparately`.** Two groups, two bundles. Put both assets in one bundle and every update is a
full download — which is what `BundlePacking` exists to let you choose.

## What it got wrong first, since each is a real trap

**The hash file is the hash of the catalog *file*, not the catalog's `BuildHash`.** They are different
numbers. Using the wrong one is not a silent failure: the client fetches the catalog, hashes what
arrived, finds it disagrees with what was advertised, and reports `Rejected` — which is exactly what a
tampered or half-published CDN looks like.

**The local catalog must be the current format version.** A version-0 placeholder is refused with
"merging across versions would need a migration nobody has written", which is the right answer to a
genuinely different format and a confusing one to a stand-in.

**The build has to know its own CDN URL.** A group's `RemoteUrl` is what turns a bundle name into
something fetchable; without it the catalog holds relative paths and the first download fails with an
invalid URI. So the port is chosen *before* the build, which is also how a real pipeline works.

**Write bundles under the name the builder chose.** With `FilenameHash` naming that is
`<group>_<hash16>.bundle`, and it is the same string the catalog puts in the URL. Composing it a
second time on the publishing side is how a build serves 404s for files that are sitting right there.

**The payload has to be incompressible to measure anything.** The first version filled it with one
repeated byte; LZ4 turned 96 KB into 484, leaving the catalog as the largest thing on the wire and the
saving invisible. A deterministic xorshift keeps "unchanged asset → identical bundle" true while
measuring the update rather than the compressor.

## What it does not show

**No partial-download resume, no corrupt-bundle rejection, no offline fallback.** All three are built
and all three are covered by `Vixen.Assets.Tests` against a transport that can be told to drop the
connection, ignore ranges and serve wrong bytes. Demonstrating them needs a server that misbehaves on
purpose, which is a different sample.

**Nothing is loaded on a device.** The same code runs on Android and iOS — `RemoteBundleSource` has
no platform in it — but this sample is a console program.

Licensed under Apache-2.0.
