# Vixen.Net.Transport.Local

The transport with no socket in it: a server and its clients in one process, talking over queues.

Spec: [docs/plan/16-networking.md](../../docs/plan/16-networking.md) § Projects.

## Why this is first, and why it ships

It is not a test double. It is what offline play and host mode run on, and it is what every layer
above — session, replication, interest, RPC — is developed and tested against. That is the single
best testability decision in the design this one is derived from: because single-player, host mode
and a unit test all use the identical code path, "it works in single player" and "it works in
multiplayer" stop being two different claims.

```csharp
var network = new LocalNetwork();                 // an instance is a network
var server = new LocalTransport(network);
var client = new LocalTransport(network);

server.StartServer();
client.StartClient();                             // connects on its next Poll, or is refused
```

A listen server is one transport with both halves started:

```csharp
var host = new LocalTransport(network);
host.StartServer();
host.StartClient();                               // connected to itself
```

## Three decisions worth knowing about

**A `LocalNetwork` instance is a network.** Two of them cannot reach each other whatever they name
their addresses, so two tests running side by side in the same process are as isolated as two
machines. A static registry would have xunit's parallel test classes sharing a world.

**It is a perfect wire, on purpose.** Nothing is lost, duplicated or reordered, so all four channels
are honoured for free. Imperfection belongs to `NetworkSimulation`, which wraps this and injects it
deliberately with a seed — a transport that was unpredictably slightly wrong would make every test
above it unpredictably slightly flaky.

**Delivery still costs a poll.** A payload sent now is reported by the receiver's *next* `Poll`, not
inside the send. Making the in-process path synchronous would make it the one transport whose
ordering the layers above could not be tested against.

## The size cap

`MaxPayloadBytes` is 64 KiB. An in-process queue has no MTU and could carry any array, which is
exactly why there is a limit: 64 KiB is the largest a UDP datagram can be, so a payload that fits
here is one the fragmentation layer in `Vixen.Net.Transport.Udp` can still get across a real network.
A local transport with no cap would let that bug be discovered on the day the game first ran over
sockets.

## Tests

The contract is asserted by `TransportConformance` in `Vixen.Net.Tests`, which is run against this
transport there. What lives in `Vixen.Net.Transport.Local.Tests` is what is true of *this* transport
specifically: host-mode loopback, the address rendezvous, two networks not seeing each other, and
`Poll` refusing to be reentered.
