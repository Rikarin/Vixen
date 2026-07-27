# Vixen.ContentServer

Serves a content build directory over HTTP with byte-range support, so a phone can be pointed at a
laptop instead of a CDN.

```bash
vixen-content-server --root ./artifacts/content --port 8080 --any
```

Then set the game's catalog URL to `http://<laptop>:8080/catalog.bin` and it will do the rest —
`ContentUpdate` reads the hash file, downloads the catalog if it names something new, and
`BundleCache` fetches packs on demand.

**A development tool, and it says so.** No TLS, no authentication, no access control, no caching
policy. `--any` binds every interface because that is what a device on the same wifi needs; it is off
by default because that is not what a laptop in a café wants.

## What it does

**Byte ranges are the feature.** Everything else is a file copy; ranges are what makes the client's
resume work, and a server without them turns every dropped connection on a device into starting the
download again. All three forms are answered — `bytes=N-`, `bytes=N-M` and the suffix form
`bytes=-N` — because clients in the wild send all three. A range that cannot be *understood* is
ignored and the whole resource sent (RFC 9110 § 14.2); a range that is understood and starts past the
end gets a 416, because sending the whole file to a client that asked for byte 900 000 would have it
write those bytes at the wrong offset.

**A hash file is synthesised when it is not on disk.** The update client reads `catalog.bin.hash`
before `catalog.bin`, and a content build directory copied as-is does not contain one — without this,
pointing a device at a build gives a rejected update and no clue why. It is computed from the file it
names, so it cannot disagree with it. A hash file that *is* on disk wins.

**Nothing outside the root is reachable.** The request path is percent-decoded first — a traversal
written `%2e%2e%2f` is the same traversal, and a check made before decoding is a check on the wrong
string — then parsed as a `VirtualPath`, which resolves `.` and `..` and refuses whatever is left
climbing above its own root. That makes `VirtualPath`'s escape rule load-bearing for a security
property for the first time, so the test asserts it end to end here rather than trusting the layer
below to keep its promise.

## Why the socket is a separate class

`ContentServer` decides everything — what exists, what a range means, what is inside the root — and
`ContentServerHost` does nothing but accept, ask, and write back. Doc 12 § testing rules out real
network in tests, and the way to obey that without leaving the interesting half unchecked is to keep
the interesting half out of the listener. `HttpListener` rather than Kestrel, so a development tool
does not pull ASP.NET into the build graph.

Licensed under Apache-2.0.
