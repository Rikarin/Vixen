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

## The congestion window

Reliable datagrams leave through an AIMD window per connection, counted in datagrams: additively wider
per acknowledgement, halved per loss event, floored by `MinWindow` so a connection that lost a burst
can still send the datagram whose acknowledgement reopens it. `MaxUnacknowledged` is still there and
still bounds memory — but it is a number that does not move, so on its own it offered a failing link
exactly as much traffic as a healthy one.

⚠ **A datagram the window has no room for is held, not dropped and not refused.** Its sequence is
already taken and its bytes are already in a pooled buffer, so the pending table is the send queue as
well as the retransmission table and ordering falls out of the sequence it was given. Unreliable
channels are never held back: a snapshot that waited for a window is a snapshot the next one has
already replaced, which is also why the window measures reliable datagrams only.

⚠ **The decrease is once per loss *event*, not once per lost datagram.** A retransmission pass
routinely finds a whole window's worth due at the same moment — they were sent together and one outage
covered all of them. Halving for each would reach the floor on the first hiccup of a match and stay
there. `UdpCongestionTests` asserts the difference as a relation between two counters of the same run
(strictly fewer halvings than retransmitted datagrams) rather than as a constant, because a constant
bound was guessed wrong once and a per-datagram sabotage stayed green under it.

⚠ **`RetransmitTimeout` cited RFC 6298 and did not implement § 5.5.** The timer did not consider how
many times a datagram had already been sent, so one whose path had gone was offered again at a fixed
interval for as long as the connection lived — the one behaviour a congested link cannot absorb. It
doubles per retry now, under the existing `MaxRetransmitTimeout` ceiling: nine attempts in two seconds
became three.

⚠ **And giving up was silent.** `Trim` drops the *oldest* unacknowledged datagram when
`MaxUnacknowledged` is reached, which is precisely the one the peer's ordered receiver is blocked
behind — so a channel that had stopped delivering looked identical to a healthy one, and `SentCount`
had already counted the datagram on its way in. `AbandonedCount` is that failure made visible. It
should read zero; it is a broken promise rather than a bad link.

## Owed

- **Ack piggybacking** and **path MTU discovery**. Both change the datagram header or what may be put
  in one, so both are wire-format decisions rather than work — see
  [#97](https://github.com/Rikarin/Vixen/issues/97).
- **DTLS**, which is a dependency and a security review as much as it is code.

**It counts what it lost, in both directions, and the two counts are not the same kind of number.**
`ITransport.Loss` comes back as four cumulative totals. Outbound: `Sent`, reliable datagrams handed
to the socket for the first time — the denominator `RetransmitCount` never had — and `Retransmitted`,
which is a *consequence* of loss and reads high, because one lost datagram resent three times counts
three and a lost acknowledgement resends one that arrived. Inbound: `Expected`, the sequences that
have fallen out of the thirty-two-deep acknowledgement window and so can no longer arrive, and
`Missing`, how many of them never did — which is loss that was **observed**, on every channel
including the unreliable ones.

⚠ **A sequence is judged when it leaves the window, not when the gap appears.** A gap a moment old
may be a datagram in flight; counting it immediately would report every reordering as a loss and
never take it back. What that costs on the hot path is one increment per reliable datagram sent and a
handful of integer operations plus a `PopCount` per datagram received, inside the bookkeeping that
already runs to de-duplicate the sequence. Nothing allocates and nothing locks; the walk over
connections happens only when somebody reads `Loss`. A closed connection's counters are folded into
the transport's totals rather than dropped, because a cumulative counter that falls is one a
collector reads as a restart. See [measuring packet loss](../../docs/guide/engine/measuring-loss.md).
- **Acknowledgements ride their own datagram.** Piggybacking them onto outgoing messages would save a
  packet each way on a busy connection.
- **Path MTU discovery.** 1200 bytes is the safe assumption rather than the measured answer.
- **DTLS.** Encryption and authentication of the datagrams themselves.
