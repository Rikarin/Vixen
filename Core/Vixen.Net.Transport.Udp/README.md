# Vixen.Net.Transport.Udp

The transport with a socket in it. Everything the layers above rely on is built here out of a medium
that promises none of it.

Spec: [docs/plan/16-networking.md](../../docs/plan/16-networking.md) § Projects.

## What it builds, and out of what

UDP delivers a datagram, or does not, possibly twice, possibly in the wrong order. On top of that:

| Channel | Built from |
|---|---|
| `Reliable` | sequence numbers, acknowledgements, retransmission, and a receiver that holds what arrived early |
| `ReliableUnordered` | the same, minus the holding — one loss delays one message rather than the queue behind it |
| `Unreliable` | nothing. It is the medium, deduplicated. |
| `Sequenced` | the medium, deduplicated, with anything older than what has already been delivered discarded |

Plus fragmentation and reassembly up to the 64 KiB the contract promises, keep-alives, and timeouts.

**The conformance suite is what says it worked.** This transport is held to exactly the same tests as
the in-process one — that is what `TransportConformance` was written for, and it is the reason the
session, replication and RPC layers do not care which one they are running on.

## The socket is behind a seam

`IDatagramSocket` is bind, send, try-receive, and nothing else. Everything subtle is above it, and is
tested over an in-memory bus where a datagram is delivered when the receiver polls, "the third packet
is lost" is a fact rather than a probability, and every run is the same run. There is one real-socket
test, and its only claim is that the adapter can bind, send and receive — it found a genuine
cross-platform bug on its first run, which is exactly the division of labour intended.

## Two decisions worth knowing about

**A message's fragments occupy consecutive sequences.** So the set a fragment at sequence `S` with
index `i` belongs to is exactly `S - i` through `S - i + count - 1`: no reassembly table, no separate
timeout, and an incomplete set falls out of the window on its own. Ordering falls out of it too — an
ordered channel delivers a message when it reaches that message's *last* fragment, which is where the
message would have been if it had never been split.

**Connecting takes a challenge.** A datagram is trivially forged with somebody else's address on it,
so a server that allocated a connection for the first one it saw could be filled up by an attacker who
never receives a single reply. Instead the server answers a request with a number derived from the
address it claims to be at and a secret of its own, allocating nothing; only a client that is really
there can echo it back. The request is padded larger than the answer, so the exchange cannot be used
to make this server flood somebody else either. It is a cookie, not cryptography — doc 16 is explicit
that this plan does not claim a bespoke crypto layer, and DTLS is where confidentiality belongs.

## Owed

- **Adaptive congestion control.** There is a cap on unacknowledged datagrams, which bounds memory;
  a window that responds to loss is a different thing and is not built.
- **Acknowledgements ride their own datagram.** Piggybacking them onto outgoing messages would save a
  packet each way on a busy connection.
- **Path MTU discovery.** 1200 bytes is the safe assumption rather than the measured answer.
- **DTLS.** Encryption and authentication of the datagrams themselves.
