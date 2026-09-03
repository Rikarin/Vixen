# Vixen.Net.Transport.WebSocket

The transport a browser can use, and the one that gets through a proxy that only believes in HTTP.

Spec: [docs/plan/16-networking.md](../../docs/plan/16-networking.md) § Projects.

## The medium already keeps most of the promises

This is the shortest of the three transports, and that is the fact worth knowing about it. A WebSocket
is reliable, ordered and message-framed, so there is no reliability layer here, no sequence numbers,
no fragmentation and no reassembly — the things the UDP transport is mostly made of. What is left is
a frame kind, a byte saying which channel a payload was sent on, and the timeouts.

| | UDP transport | Here |
|---|---|---|
| Reliability | built | the medium's |
| Ordering | built | the medium's |
| Fragmentation | built | the medium's |
| Connection handshake | cookie + challenge | the HTTP upgrade |
| What is left | ~700 lines | ~300 |

## The four channels collapse to one, and that is a real cost

Everything is delivered, in order, including the payloads whose channels say they need not be. That
*satisfies* the contract — `Unreliable` and `Sequenced` say a payload **may** be dropped, not must —
but it is worth being plain about what is lost.

The reason those channels exist is head-of-line blocking. A snapshot that supersedes itself thirty
times a second should not wait behind a retransmission of one that is already stale, and over a
single TCP stream it does. **A browser client has no alternative and this is the right transport for
it; a desktop client that could use UDP should.** A server that wants both is what
`Vixen.Net.Transport.Composite` is for.

The channel byte is still carried and still reported, so nothing above behaves differently and a game
moved between transports does not change. It is the delivery guarantee that is stronger than asked
for, not the vocabulary.

## The socket is behind a seam

`IWebSocketChannel` is open, send, try-receive, close. Everything above it is tested over an
in-memory pair where a message crosses when the receiver polls and every run is the same run — the
same bargain `IDatagramSocket` makes in the UDP transport, and the reason all 24 conformance tests
here are deterministic.

`SystemWebSocketFactory` is the real one: a `TcpListener`, thirty lines of RFC 6455 upgrade, and
`WebSocket.CreateFromStream` for the framing. Deliberately not `HttpListener`, whose URL reservations
on Windows and uneven support elsewhere buy nothing here.

**The threads stop at that interface.** A WebSocket is asynchronous and the transport contract says
nothing is delivered outside `Poll`, so each channel runs its own receive and send loops and hands
over queues. Nothing above ever sees a task.

That adaptation is also where the one real bug was. The send loop originally drained a
`BlockingCollection`, and a blocking enumeration inside an `async` method *with no `await` before it*
runs on the caller's thread — so starting the send loop from the accept path blocked the accept path,
and a connection that had completed its handshake was never handed over. Nothing threw. It is a
`System.Threading.Channels` reader now, and the real-socket test is what found it.

## Owed

- **`wss`.** The client half will negotiate TLS because `ClientWebSocket` does; the listener here
  speaks plain `ws` only. A server behind a terminating proxy is the usual arrangement and works
  today; a listener that terminates TLS itself is not built.
- ~~**The browser path.**~~ Built, as `Vixen.Net.Transport.WebSocket.Browser` — one more
  `IWebSocketFactory`, which is the shape this section predicted.

  ⚠ **Two claims it rested on were wrong.** This bullet said `System.Net.WebSockets` "is not
  available" in a browser; the `browser-wasm` runtime pack ships
  `System.Net.WebSockets.Client.dll` containing a real `System.Net.WebSockets.BrowserWebSocket`
  built for `net10.0-browser` against `System.Runtime.InteropServices.JavaScript` — the page's own
  `WebSocket` behind the ordinary `ClientWebSocket` API. And doc 16 routes the browser path through
  a `Vixen.Platform.Web` `ISocket` that **has never existed in the tree**: those two documents are
  its only two mentions anywhere. So the browser transport needs no `[JSImport]`, ships no
  JavaScript, and does not depend on `Vixen.Platform.Web` at all.

  Client only — a page cannot listen, and `Listen` refuses rather than returning something that
  never accepts. A server that wants both browser and desktop clients is what
  `Vixen.Net.Transport.Composite` is for.
- **Permessage-deflate.** Snapshots are already bit-packed, so the win is small and the CPU is not
  free; worth measuring before assuming.

## The upgrade is the only part of this we parse

The framing is `WebSocket.CreateFromStream` — the runtime's, and deliberately not ours. What is left
is RFC 6455's opening handshake: find the blank line, find `Sec-WebSocket-Key` in what came before it,
answer with base64(SHA-1(key + GUID)). Thirty lines, over bytes from a stranger, reached by opening a
socket and before anything has authenticated — which makes it the most exposed code in the package.

It is a **static function over a span** for that reason: something shaped like this can be fuzzed, and
a loop reading from a `NetworkStream` cannot. Making it one turned up two defects that had no test
because they had no seam.

- **It rebuilt the whole request on every read.** Decoding and splitting the accumulated buffer each
  time bytes arrived meant a client dribbling one byte at a time cost the server about eight megabytes
  of garbage for four kilobytes sent — free for the sender, times however many sockets they open. The
  scan now steps forward from where the last read finished, less three bytes, because the terminator
  can straddle a boundary.
- **It had no timeout.** A client that connected and said nothing held a descriptor and a pending task
  until the listener stopped. There is now a five-second deadline per upgrade and a ceiling of 64 in
  flight, so the worst a stranger gets is 64 sockets for five seconds rather than as many as they can
  open for as long as the process runs.

The key is **not validated**, which is correct rather than lax: RFC 6455 has the value echoed through
SHA-1 for the *client* to check, so a nonsense key earns a nonsense accept and the client refuses it.
Rejecting here would be this server answering a question the protocol gives to the other end.
