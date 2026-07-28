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
- **The browser path.** Doc 16 wants this to run over `Vixen.Platform.Web`'s `ISocket` when the
  client *is* a browser, where `System.Net.WebSockets` is not available. The seam is the right shape
  for it — one more `IWebSocketFactory` — and Phase 10 is where `Vixen.Platform.Web` lands.
- **Permessage-deflate.** Snapshots are already bit-packed, so the win is small and the CPU is not
  free; worth measuring before assuming.
