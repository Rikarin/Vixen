// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;

namespace Vixen.Live;

/// <summary>Everything a realm process needs to know at boot, and nothing it can ask for later.</summary>
/// <remarks>
///     <para>
///         One value, produced by whoever decided the shard should exist and consumed by the process
///         that carries it. It crosses the boundary as <em>one argument</em>
///         (<see cref="ArgumentName" />) or <em>one environment variable</em>
///         (<see cref="EnvironmentVariable" />), which is what lets the three placement backends —
///         a pod spec, a container's command, <c>Process.Start</c> — differ in nothing but how they
///         set a string.
///     </para>
///     <para>
///         ⚠ <b>The endpoint is in here and the shard id is in here.</b> A realm that discovered
///         either by asking the orchestrator after boot would have a window in which it is running
///         and unaddressable, and every backend would need a different answer for how that window
///         ends. Handed the spec, the process is either the shard it was told to be or it exited.
///     </para>
///     <para>
///         ⚠ <b><see cref="Endpoint" /> may arrive unbound.</b> A spec with no port says "the
///         backend chooses" — a <c>hostPort</c> from the node's range, a published container port, a
///         port from a pool — and one with no host either says the backend chooses that too, which
///         is the only thing an orchestrator placing onto a Kubernetes scheduler can say.
///         <see cref="IRealmPlacement.StartAsync" /> rewrites it before the process ever sees it:
///         what the process is handed is always bound.
///     </para>
/// </remarks>
public sealed record RealmSpec {
    /// <summary>The single argument a realm is launched with.</summary>
    /// <remarks>
    ///     ⚠ <b>Not <c>--vixen-</c>-prefixed, and that matters.</b> <c>Vixen.App</c>'s
    ///     <c>AppArguments</c> reserves that prefix for the host and reports anything under it that
    ///     it does not recognise. A realm's spec is the application's argument, not the host's, so it
    ///     travels in the same space a game's own arguments do.
    /// </remarks>
    public const string ArgumentName = "--realm-spec";

    /// <summary>Where a realm looks when no argument was given.</summary>
    /// <remarks>
    ///     Kubernetes sets environment, not argv, for anything a pod template generates — and a
    ///     command line is visible in a process listing to every other container in the pod, which
    ///     is a reason to prefer this one once the spec carries anything that should not be.
    /// </remarks>
    public const string EnvironmentVariable = "VIXEN_REALM_SPEC";

    /// <summary>Which shard this process is.</summary>
    public ShardId Shard { get; init; }

    /// <summary>The map, the region and the version pair.</summary>
    public ShardKey Key { get; init; }

    /// <summary>Who is admitted and when it may stop.</summary>
    public ShardKind Kind { get; init; } = ShardKind.Public;

    /// <summary>Where clients open their sessions. Possibly unbound; see the type's remarks.</summary>
    public RealmEndpoint Endpoint { get; init; }

    /// <summary>How full it may get.</summary>
    public ShardCapacity Capacity { get; init; } = new(100, 120);

    /// <summary>Simulation ticks per second.</summary>
    public int TickRate { get; init; } = 30;

    /// <summary>
    ///     The seed for anything the shard generates — spawn jitter, weather, a roaming boss's route.
    /// </summary>
    /// <remarks>
    ///     In the spec rather than in the process, because a shard that is restarted after a crash
    ///     should come back the same shard, and because a test that reproduces a report needs the
    ///     number that produced it.
    /// </remarks>
    public ulong Seed { get; init; }

    /// <summary>
    ///     How to reach the control plane, or empty for a realm that has none.
    /// </summary>
    /// <remarks>
    ///     Empty is the ordinary case for <c>Placement.Process</c> in development and in tests, and
    ///     doc 27 § Cost's L0 is defined by it: a dedicated server with a lifecycle and no
    ///     orchestrator intelligence. A realm with no control plane still admits ticketed players,
    ///     still drains, and still reports health — it simply reports it to its own log.
    /// </remarks>
    public string ClusterEndpoint { get; init; } = "";

    /// <summary>Whatever else the game's own realm wants, carried verbatim.</summary>
    /// <remarks>
    ///     The escape hatch that stops this record from growing a field per game. Keys the engine
    ///     defines are the ones written below; anything else round-trips untouched.
    /// </remarks>
    public IReadOnlyDictionary<string, string> Options { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Whether this spec names a runnable shard.</summary>
    public bool IsValid =>
        Shard.IsValid && Key.IsValid && Capacity.IsValid && TickRate > 0
        && (Endpoint.IsValid || Endpoint.IsUnbound);

    /// <summary>The spec as the one string that crosses a process boundary.</summary>
    /// <returns>What <see cref="TryDecode" /> reads.</returns>
    public string Encode() {
        var text = new StringBuilder(160);

        KeyValueText.Write(text, "shard", Shard.Value.ToString("D", CultureInfo.InvariantCulture));
        KeyValueText.Write(text, "map", Key.Map);
        KeyValueText.Write(text, "region", Key.Region);
        KeyValueText.Write(text, "build", Key.Version.Build);
        KeyValueText.Write(text, "content", Key.Version.Content.ToString("x16", CultureInfo.InvariantCulture));
        KeyValueText.Write(text, "kind", Kind.ToString());
        KeyValueText.Write(text, "host", Endpoint.Host);
        KeyValueText.Write(text, "port", Endpoint.Port.ToString(CultureInfo.InvariantCulture));
        KeyValueText.Write(text, "soft", Capacity.SoftCap.ToString(CultureInfo.InvariantCulture));
        KeyValueText.Write(text, "hard", Capacity.HardCap.ToString(CultureInfo.InvariantCulture));
        KeyValueText.Write(text, "tick", TickRate.ToString(CultureInfo.InvariantCulture));
        KeyValueText.Write(text, "seed", Seed.ToString(CultureInfo.InvariantCulture));

        if (ClusterEndpoint.Length > 0) {
            KeyValueText.Write(text, "cluster", ClusterEndpoint);
        }

        foreach (var option in Options) {
            // Prefixed, so a game's option can never collide with a field this record grows later —
            // and so a reader can tell at a glance which half of the string is the engine's.
            KeyValueText.Write(text, "x-" + option.Key, option.Value);
        }

        return text.ToString();
    }

    /// <summary>Reads a spec back, or says what was wrong with it.</summary>
    /// <param name="text">What <see cref="Encode" /> wrote.</param>
    /// <param name="spec">The spec, on success.</param>
    /// <param name="error">Why not, otherwise. Written for whoever is reading a crashed launcher's log.</param>
    /// <returns>Whether it decoded.</returns>
    public static bool TryDecode(string? text, out RealmSpec? spec, out string error) {
        spec = null;

        if (!KeyValueText.TryRead(text, out var fields, out error)) {
            return false;
        }

        if (!ShardId.TryParse(Field(fields, "shard"), out var shard) || !shard.IsValid) {
            error = "`shard` is missing or is not a guid";

            return false;
        }

        var map = Field(fields, "map");

        if (map.Length == 0) {
            error = "`map` is missing";

            return false;
        }

        if (!ulong.TryParse(
                Field(fields, "content"),
                NumberStyles.HexNumber,
                CultureInfo.InvariantCulture,
                out var content
            )) {
            error = "`content` is missing or is not a 64-bit hexadecimal hash";

            return false;
        }

        if (!Enum.TryParse<ShardKind>(Field(fields, "kind"), ignoreCase: false, out var kind)) {
            error = "`kind` is missing or is not one of " + string.Join(", ", Enum.GetNames<ShardKind>());

            return false;
        }

        if (!int.TryParse(Field(fields, "port"), CultureInfo.InvariantCulture, out var port)) {
            error = "`port` is missing or is not a number";

            return false;
        }

        var endpoint = new RealmEndpoint(Field(fields, "host"), port);

        if (!endpoint.IsValid && !endpoint.IsUnbound) {
            error = $"`{endpoint}` is not an endpoint a client could be sent to";

            return false;
        }

        if (!int.TryParse(Field(fields, "soft"), CultureInfo.InvariantCulture, out var soft)
            || !int.TryParse(Field(fields, "hard"), CultureInfo.InvariantCulture, out var hard)) {
            error = "`soft` and `hard` are missing or are not numbers";

            return false;
        }

        var capacity = new ShardCapacity(soft, hard);

        if (!capacity.IsValid) {
            error = $"a capacity of {capacity} is not one a shard could honour";

            return false;
        }

        if (!int.TryParse(Field(fields, "tick"), CultureInfo.InvariantCulture, out var tick) || tick <= 0) {
            error = "`tick` is missing or is not a positive number";

            return false;
        }

        if (!ulong.TryParse(Field(fields, "seed"), CultureInfo.InvariantCulture, out var seed)) {
            error = "`seed` is missing or is not a number";

            return false;
        }

        var options = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var field in fields) {
            if (field.Key.StartsWith("x-", StringComparison.Ordinal)) {
                options[field.Key[2..]] = field.Value;
            }
        }

        spec = new() {
            Shard = shard,
            Key = new(map, Field(fields, "region"), new(Field(fields, "build"), content)),
            Kind = kind,
            Endpoint = endpoint,
            Capacity = capacity,
            TickRate = tick,
            Seed = seed,
            ClusterEndpoint = Field(fields, "cluster"),
            Options = options
        };

        return true;
    }

    /// <summary>The arguments a placement backend launches a realm with.</summary>
    /// <returns><see cref="ArgumentName" /> and the encoded spec, in that order.</returns>
    public IReadOnlyList<string> ToCommandLine() => [ArgumentName, Encode()];

    /// <summary>Finds the spec a realm process was started with.</summary>
    /// <param name="arguments">The process arguments.</param>
    /// <param name="environment">
    ///     How to read an environment variable, or <see langword="null" /> for the process's own.
    ///     A function rather than a dictionary so a test can answer without setting one.
    /// </param>
    /// <param name="spec">The spec, on success.</param>
    /// <param name="error">Why not, otherwise.</param>
    /// <returns>Whether a spec was found and decoded.</returns>
    /// <remarks>
    ///     The argument wins over the environment. A launcher that sets both meant the argument —
    ///     the environment is what a pod template inherits, and inheriting a stale one is exactly the
    ///     accident this order prevents.
    /// </remarks>
    public static bool TryRead(
        IReadOnlyList<string>? arguments,
        Func<string, string?>? environment,
        out RealmSpec? spec,
        out string error
    ) {
        spec = null;

        if (arguments is not null) {
            for (var index = 0; index < arguments.Count - 1; index++) {
                if (string.Equals(arguments[index], ArgumentName, StringComparison.Ordinal)) {
                    return TryDecode(arguments[index + 1], out spec, out error);
                }
            }
        }

        var read = environment ?? Environment.GetEnvironmentVariable;
        var fromEnvironment = read(EnvironmentVariable);

        if (!string.IsNullOrEmpty(fromEnvironment)) {
            return TryDecode(fromEnvironment, out spec, out error);
        }

        error = $"no {ArgumentName} argument and no {EnvironmentVariable} environment variable";

        return false;
    }

    /// <summary>Whether two specs say the same thing, options included.</summary>
    /// <param name="other">The other spec.</param>
    /// <returns>Whether they are equal.</returns>
    /// <remarks>
    ///     Hand-written because the synthesized one would compare <see cref="Options" /> by
    ///     reference, so a spec would never equal itself after a round trip through
    ///     <see cref="Encode" /> — which is the one comparison anybody makes of these.
    /// </remarks>
    public bool Equals(RealmSpec? other) =>
        other is not null
        && Shard == other.Shard
        && Key == other.Key
        && Kind == other.Kind
        && Endpoint == other.Endpoint
        && Capacity == other.Capacity
        && TickRate == other.TickRate
        && Seed == other.Seed
        && string.Equals(ClusterEndpoint, other.ClusterEndpoint, StringComparison.Ordinal)
        && SameOptions(Options, other.Options);

    /// <inheritdoc />
    public override int GetHashCode() =>
        HashCode.Combine(Shard, Key, Kind, Endpoint, Capacity, TickRate, Seed, Options.Count);

    /// <inheritdoc />
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Kind} shard {Shard} of {Key} at {Endpoint}");

    static bool SameOptions(
        IReadOnlyDictionary<string, string> left,
        IReadOnlyDictionary<string, string> right
    ) {
        if (left.Count != right.Count) {
            return false;
        }

        foreach (var option in left) {
            if (!right.TryGetValue(option.Key, out var value)
                || !string.Equals(option.Value, value, StringComparison.Ordinal)) {
                return false;
            }
        }

        return true;
    }

    static string Field(Dictionary<string, string> fields, string key) => fields.GetValueOrDefault(key, "");
}
