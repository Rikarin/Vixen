<!--
SPDX-FileCopyrightText: Copyright (c) Rikarin
SPDX-License-Identifier: Apache-2.0
-->

# Vixen.Net.Transport.WebSocket.Browser

The transport a web build uses, because it is the only one it can. A browser cannot open a UDP
socket and cannot listen on anything, so this is `Vixen.Net.Transport.WebSocket` with one more
`IWebSocketFactory` behind it and half the transport removed.

```csharp
var transport = new WebSocketTransport(
    new BrowserWebSocketFactory(),
    new() { RemoteAddress = new("wss://play.example.com/") }
);

using var session = new NetworkSession(transport, ownsTransport: true);
session.StartClient();          // StartServer() throws. See below.
```

Everything above the seam — the channel byte, the frame kinds, the timeouts, the connection ids —
comes from `Vixen.Net.Transport.WebSocket` unchanged, and is held to the transport conformance suite
in that project. This is the socket adapter and nothing else.

## ⚠ It is `ClientWebSocket`, and that is the finding

Two documents said it could not be.

`Vixen.Net.Transport.WebSocket`'s own README said the browser path was owed because
`System.Net.WebSockets` "is not available" there. It is: the `browser-wasm` runtime pack ships
`System.Net.WebSockets.Client.dll`, and inside it is a real
`System.Net.WebSockets.BrowserWebSocket`, compiled for `net10.0-browser` against
`System.Runtime.InteropServices.JavaScript`. It is the page's own `WebSocket` behind the ordinary
API, written and maintained by the runtime team.

`docs/plan/16-networking.md` routes the browser path through a `Vixen.Platform.Web` `ISocket`.
**That type has never existed.** Those two documents are its only two mentions in the repository.

So this project has no `[JSImport]`, ships no JavaScript, has no `wwwroot`, and does not reference
`Vixen.Platform.Web`. It is about two hundred lines, and most of them are the async-to-polled
adaptation the seam asks for.

## What it does not do, and why each is a refusal rather than a gap

- **It cannot listen.** `Listen` throws `NotSupportedException` with a message naming
  `Vixen.Net.Transport.Composite`, which is how one server takes both browser and desktop clients.
  A listener that accepted nothing would be the failure this repository keeps writing gates
  against: an instrument that never runs and reports success.
- **It has no threads.** The receive and send loops are started with `_ = …` and never touch
  `Task.Run`. A single-threaded WebAssembly build has one thread and a cooperative scheduler tied to
  the JavaScript event loop, so a continuation runs when the frame yields — which `WebFrameLoop`
  does every `requestAnimationFrame`. The transport contract only asks that nothing is delivered
  outside `Poll`, and the queues keep that on one thread as well as on two.
- **It does not catch `SocketException`.** The desktop factory's filter names it; there is no socket
  under a browser WebSocket to raise one, and naming a `System.Net.Sockets` type in a filter that
  never matches is how an assembly nobody needs survives trimming.
- **The four channels still collapse to one.** That is the parent transport's property and its
  README explains the cost — head-of-line blocking over a single stream. A browser client has no
  alternative; a desktop client that could use UDP should.

## ⚠ How it is tested without a browser

This is the part worth reading before changing anything here.

`Vixen.Net.Transport.WebSocket.Browser.Tests` targets `net10.0` and **links
`BrowserWebSocketFactory.cs` as source**, then drives it against a real `SystemWebSocketFactory`
server over loopback — connect, upgrade, a payload each way, and a refused connection reported as an
event rather than thrown. Six tests, no browser, no `nuke BrowserSmoke`, in a tenth of a second.

That works for exactly one reason: `ClientWebSocket` is the same API on both targets, and only the
implementation the runtime binds differs. **So the file must contain no conditional compilation**,
or the suite would be testing the `net10.0` arm of a file whose browser arm no gate in this
repository compiles — `Test`, `CheckFormat`, `CheckApi` and `Pack` never see this project at all.
`LinkedSourceTests` asserts that absence rather than trusting it, and says so in its failure
message.

What this does **not** cover is `BrowserWebSocket` itself, which is the runtime's code and not this
repository's to prove. A published head talking to a real server is still the only thing that would,
and that belongs with `nuke BrowserSmoke`.

## Where it sits in the build

Not in `Vixen.slnx` — `net10.0-browser`, like `Vixen.Platform.Web`,
`Vixen.Graphics.WebGPU.Browser`, `Vixen.Audio.Backend.WebAudio` and `Tools/Vixen.WebProbe`. It is
named in `build/Build.cs`'s `BrowserProjects`, so `nuke CompileWeb` builds it; a browser project
that is not in that list is built by nothing, which has happened before.

The **tests** project is in the solution, so `nuke Test` runs it like any other.
