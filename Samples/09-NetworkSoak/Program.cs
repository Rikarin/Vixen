// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Globalization;
using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Net;
using Vixen.Net.Diagnostics;
using Vixen.Net.Motion;
using Vixen.Net.Replication;
using Vixen.Net.Sessions;

namespace Vixen.Samples.NetworkSoak;

/// <summary>Phase 9's soak criterion, measured rather than asserted.</summary>
/// <remarks>
///     <para>
///         A hundred connections and five thousand entities, for as long as you like, reporting what
///         it cost in bandwidth, in time, and in allocation. The roadmap's exit criterion for the
///         phase is that those three hold for thirty minutes; this is the thing that says whether
///         they do, and it exits non-zero when they do not.
///     </para>
///     <para>
///         <b>It measures the replication pipeline, not the transport.</b> There are no sockets and
///         no sessions here: a hundred sessions over a hundred transports would mostly measure the
///         transports, which have conformance suites of their own. What is under test is capture,
///         differencing, per-connection baselines and the budget — the part whose cost grows with the
///         product of connections and entities, and the part where a regression is invisible until
///         it is enormous.
///     </para>
///     <para>
///         <b>Interest management is the variable that matters most</b>, which is why it is a flag
///         rather than a constant. Telling every connection about every entity is five hundred
///         thousand records a tick before anything is even encoded, and no amount of bit-packing
///         rescues that. The default is a slice per connection, which is what any real interest
///         resolver would produce; <c>--interest all</c> is there to show the difference and is not a
///         configuration anybody should ship.
///     </para>
/// </remarks>
public static class Program {
    /// <summary>Runs the soak.</summary>
    /// <param name="arguments">See <c>--help</c>.</param>
    /// <returns>Zero if every budget held.</returns>
    public static int Main(string[] arguments) {
        ArgumentNullException.ThrowIfNull(arguments);

        if (Array.IndexOf(arguments, "--help") >= 0) {
            Help();

            return 0;
        }

        var settings = new SoakSettings {
            Entities = Number(arguments, "--entities", 5_000),
            Clients = Number(arguments, "--clients", 100),
            Ticks = Number(arguments, "--ticks", 1_800),
            Observed = Number(arguments, "--observed", 250),
            MovingPercent = Number(arguments, "--moving", 20),
            AcknowledgeLag = Number(arguments, "--ack-lag", 4),
            SeesEverything = Text(arguments, "--interest", "slice") == "all",
            BandwidthBudget = Number(arguments, "--kbit", 128),
            AllocationBudget = Number(arguments, "--alloc", 4_096)
        };

        using var soak = new Soak(settings);

        return soak.Run();
    }

    static void Help() =>
        Console.Out.WriteLine(
            """
            09-NetworkSoak — the phase's soak criterion, measured.

              --entities N   how many networked entities        (5000)
              --clients N    how many connections                (100)
              --ticks N      how many ticks to run              (1800, a minute at 30 Hz)
              --observed N   entities each connection sees       (250)
              --moving N     percent of entities that move a tick (20)
              --ack-lag N    ticks before a connection acknowledges (4)
              --interest X   `slice` or `all`                  (slice)
              --kbit N       bandwidth budget per client, kbit/s  (128)
              --alloc N      allocation budget per tick, bytes   (4096)

            Thirty minutes at the default rate:

              dotnet run -c Release --project Samples/09-NetworkSoak -- --ticks 54000
            """
        );

    static int Number(string[] arguments, string name, int fallback) {
        for (var index = 0; index < arguments.Length - 1; index++) {
            if (arguments[index] == name
                && int.TryParse(arguments[index + 1], CultureInfo.InvariantCulture, out var value)) {
                return value;
            }
        }

        return fallback;
    }

    static string Text(string[] arguments, string name, string fallback) {
        for (var index = 0; index < arguments.Length - 1; index++) {
            if (arguments[index] == name) {
                return arguments[index + 1];
            }
        }

        return fallback;
    }
}

/// <summary>What to run.</summary>
internal readonly record struct SoakSettings {
    /// <summary>How many networked entities.</summary>
    public int Entities { get; init; }

    /// <summary>How many connections.</summary>
    public int Clients { get; init; }

    /// <summary>How many ticks.</summary>
    public int Ticks { get; init; }

    /// <summary>How many entities each connection is told about.</summary>
    public int Observed { get; init; }

    /// <summary>What fraction of entities move on a tick.</summary>
    public int MovingPercent { get; init; }

    /// <summary>How many ticks a connection takes to acknowledge, standing in for a round trip.</summary>
    public int AcknowledgeLag { get; init; }

    /// <summary>Whether every connection sees everything.</summary>
    public bool SeesEverything { get; init; }

    /// <summary>What a connection may cost, in kilobits a second.</summary>
    public int BandwidthBudget { get; init; }

    /// <summary>What a tick may allocate, in bytes.</summary>
    public int AllocationBudget { get; init; }
}

/// <summary>Gives each connection a slice of the world, the way a real resolver would.</summary>
/// <remarks>
///     Not a distance grid — this is a soak, and a grid would make the measurement depend on how the
///     entities were laid out. A fixed slice per connection has the property that matters: the cost
///     of a tick is proportional to connections times what each one observes, rather than to
///     connections times the whole world.
/// </remarks>
internal sealed class SliceResolver(int perConnection, bool everything) : IInterestResolver {
    static readonly QueryDescription Networked = new QueryDescription().WithAll<NetworkId>();

    readonly List<Entity> all = [];

    /// <inheritdoc />
    public void Resolve(World world, PlayerId player, List<Entity> observed) {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(observed);

        if (all.Count == 0) {
            foreach (var chunk in world.Chunks(Networked)) {
                all.AddRange(chunk.Entities);
            }
        }

        if (everything || perConnection >= all.Count) {
            observed.AddRange(all);

            return;
        }

        var start = (int)(player.Value * 37 % (uint)all.Count);

        for (var i = 0; i < perConnection; i++) {
            observed.Add(all[(start + i) % all.Count]);
        }
    }
}
