// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Vixen.Core;
using Vixen.Gameplay;
using Vixen.Gameplay.Content;
using Vixen.Gameplay.Social;
using Vixen.Live;
using Vixen.Live.Gameplay;
using Vixen.Live.Realms;
using Vixen.Samples.Mmo.Rules;

namespace Vixen.Samples.Mmo.Realms;

/// <summary>One map, simulated. Twenty gameplay libraries and one shard's lifecycle.</summary>
/// <remarks>
///     <para>
///         ⚠ The base type is spelled out because this project's namespace ends in <c>.Realms</c>,
///         which would otherwise win the name lookup. Renaming either would be worse: the directory
///         should say what the project is, and the base type is called what the document calls it.
///     </para>
///     <para>
///         <b>What this class is for is composition, and composition is the whole point of the
///         sample.</b> Doc 28 ships twenty-odd packages so that a game can decline most of them; this
///         is the first thing that proves all twenty go together — that the tag table bakes across
///         the lot, that no two libraries claim one definition alias, and that a realm can hold the
///         resulting object graph without it becoming a god class.
///     </para>
///     <para>
///         ⚠ <b>Four bridges and not one, because they fail differently.</b> The identity map is the
///         join every durable write starts from; the ledger, the lockout store and the social store
///         each keep a partial view and each get a different thing wrong when the view is cold. See
///         <c>Vixen.Live.Gameplay</c> — <c>LedgerBridge.Divergences</c> should be zero and
///         <c>SocialBridge.Divergences</c> should not.
///     </para>
///     <para>
///         ⚠ <b>Nothing here awaits a grain.</b> ADR-016: the bridges answer from their views, the
///         outboxes are drained off the frame path, and <c>Host.Directory</c> is the only thing that
///         talks to the cluster at all.
///     </para>
/// </remarks>
public sealed class MmoRealm : Vixen.Live.Realms.Realm {
    readonly GameplayIdentityMap identity = new();

    LockoutBridge? lockouts;
    SocialBridge? social;

    /// <summary>Made once, because a logger created inside a log call is an allocation per tick.</summary>
    ILogger log = NullLogger.Instance;

    /// <summary>Everything the content compiled to. Null until the realm has loaded its content.</summary>
    public MmoLibraries? Libraries { get; private set; }

    /// <summary>The camps this shard keeps populated. Null until it has composed.</summary>
    /// <remarks>
    ///     <b>The one gameplay library this realm drives every tick</b>, and therefore the evidence
    ///     that the composition is not decoration. See <see cref="WorldSpawns" />.
    /// </remarks>
    public WorldSpawns? Spawns { get; private set; }

    /// <summary>Who a gameplay rule's player is, durably.</summary>
    public IGameplayIdentity Identity => identity;

    /// <summary>Doc 28's lockouts over doc 27's <c>IInstanceGrain</c>.</summary>
    public LockoutBridge Lockouts => lockouts ?? throw NotYet();

    /// <summary>Doc 28's guilds and graphs over doc 27's <c>IGuildGrain</c>.</summary>
    public SocialBridge Social => social ?? throw NotYet();

    /// <summary>Composes, compiles and builds the bridges.</summary>
    /// <param name="libraries">The compiled content, from wherever the realm read it.</param>
    /// <remarks>
    ///     <para>
    ///         Separate from <see cref="OnRealmInitialise" /> and public so that a test can drive it:
    ///         everything below this line is ordinary objects, and a realm that could only be
    ///         composed inside a running shard is a realm nobody tests. That is the same argument
    ///         doc 27 makes for keeping a grain a thin adapter over a plain class.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Content problems are refused here rather than logged.</b> A shard that starts
    ///         with a broken loot table is a shard that hands out nothing for an evening and is
    ///         diagnosed from a player complaint. The list is what the content test reads too, so a
    ///         realm and a test agree about what "broken" means.
    ///     </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">The content did not compile clean.</exception>
    public void Compose(MmoLibraries libraries) => Compose(libraries, [], seed: 0);

    /// <summary>Composes, compiles and builds the bridges, over content that was read from a build.</summary>
    /// <param name="libraries">The compiled content, from wherever the realm read it.</param>
    /// <param name="loading">
    ///     What went wrong reading it — an address labelled a definition that is not one. Refused on
    ///     the same terms as a library's own problems, because from a player's side there is no
    ///     difference between a loot table that would not parse and one that would not resolve.
    /// </param>
    /// <param name="seed">What the world's spawn streams start from.</param>
    /// <remarks>
    ///     ⚠ <b>The seed is a parameter and not read from <see cref="Realm.Spec" />, and that is not a
    ///     style choice.</b> The whole argument for this method being public is that everything below
    ///     it is ordinary objects — a realm that could only be composed inside a running shard is a
    ///     realm nobody tests — and <c>Spec</c> throws until the host has bound one. Reaching for it
    ///     here made exactly that promise false, and the existing test caught it.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The content did not compile clean.</exception>
    public void Compose(MmoLibraries libraries, ImmutableArray<string> loading, ulong seed) {
        ArgumentNullException.ThrowIfNull(libraries);

        var problems = loading.IsDefaultOrEmpty
            ? libraries.Problems
            : [.. loading.Select(problem => "Content: " + problem), .. libraries.Problems];

        if (problems.Length > 0) {
            throw new InvalidOperationException(
                $"This build's content has {problems.Length} problems and a shard will not "
                + "start on it:" + Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", problems)
            );
        }

        Libraries = libraries;
        lockouts = new(identity);
        social = new(identity, libraries.Social);

        Spawns = new(libraries.Spawns, seed);
    }

    /// <inheritdoc />
    /// <remarks>
    ///     <para>
    ///         Host.Session is the server from doc 16 — replication, RPC and interest are wired by the
    ///         host exactly as they would be in a listen server. A realm is not a different kind of
    ///         server; it is one that can be told what to be and asked to stop.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The content arrives here, synchronously, and the blocking read is the right
    ///         trade.</b> Nothing in the realm lifecycle is async — every hook returns void — and this
    ///         runs after the startup scene and before the first frame, which is the only window in
    ///         which "composed before anybody plays" is a fact rather than a hope. The alternative,
    ///         asking off-thread and applying on a later tick through <c>Host.Directory</c>, buys
    ///         nothing here: the shard may admit nobody until it is composed anyway, so the wait would
    ///         simply move.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A build with no content mount composes nothing and says so.</b> That is not a
    ///         crash: a shard is often run from a source tree before anybody has built the content, and
    ///         a null <c>Services.Assets</c> with a warning is far easier to act on than a
    ///         <c>NullReferenceException</c> in a lifecycle hook. What it must not do is start
    ///         <em>quietly</em> with empty libraries, which is the state this whole class was in.
    ///     </para>
    /// </remarks>
    protected override void OnRealmInitialise() {
        log = Services.LoggerFactory.CreateLogger("Vixen.Samples.Mmo.Realm");

        if (Services.Assets is not { } assets) {
            MmoLog.NoContent(log);

            return;
        }

        // ⚠ The composition first and once, and it is handed to both halves. DefinitionContent needs
        // it to seed the tags only code knows — Event.Kill is the one that bites, and a catalog
        // without it compiles every Kill objective into something nothing can advance — and
        // MmoLibraries needs the same instance, because a tag index is a position in a pre-order walk
        // and two compositions agree about names while disagreeing about every number.
        var composition = MmoModules.Compose();

        var load = DefinitionContent
            .LoadAsync(assets, composition)
            .AsTask()
            .GetAwaiter()
            .GetResult();

        if (load.Catalog.Count == 0) {
            MmoLog.NoDefinitions(log, DefinitionContent.Label);
        }

        // A definition that is labelled and did not read is a content mistake and belongs in the same
        // list as every library's, so Compose refuses on one rather than logging it and carrying on.
        //
        // ⚠ Seeded from the shard's identity rather than from the clock, and that is what makes a
        // failure reportable: a shard restarted on the same spec repopulates identically, and two
        // shards of one map are two worlds rather than one world twice. A wall clock would make every
        // run a different world and every spawn bug an anecdote.
        Compose(MmoLibraries.From(composition, load.Catalog), load.Problems, Seed(Spec.Shard));

        MmoLog.Composed(log, composition.Modules.Count, load.Catalog.Count, assets.Catalog.Count, Spawns!.Camps);
    }

    /// <inheritdoc />
    protected override void OnRealmUpdate(GameTime time) {
        // Host.Update has already run: control-plane answers applied, launcher signals read, the map
        // checked, the session polled.
        //
        // ⚠ Purging is the realm's own housekeeping and is not a write. Dropping a lifted lockout
        // from memory is not releasing it — what decides it lifted is the reset the cluster holds.
        lockouts?.Purge(time.Total.TotalSeconds);

        // ⚠ And this is the world itself, which is the half that was missing. A shard with nobody on
        // it still owes its camps: something died, its table's timer ran out, and it is due back. The
        // orders say what to make and never where — see WorldSpawns.
        if (Spawns?.Tick((float)time.Total.TotalSeconds) > 0) {
            MmoLog.Spawned(log, Spawns.Issued, Spawns.Camps, Spawns.Alive, time.Total.TotalSeconds);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>The order here is load-bearing and every step of it is a bug if skipped.</b> The
    ///     identity join first, because every warm below is keyed by what it returns. Then the
    ///     lockouts, because admission to an instance reads them and a cold read is a player let into
    ///     a raid they are already saved to. Then the social graph, and then
    ///     <c>SocialBridge.Admitted</c> — which is what stops a block leaking: somebody blocked while
    ///     they were offline is a key in a durable set and nothing in any graph, so
    ///     <c>IsSevered</c> answers false until they are seated.
    /// </remarks>
    protected override void OnPlayerAdmitted(RealmPlayer player) {
        ArgumentNullException.ThrowIfNull(player);

        var gameplay = identity.Admit(player.Key, player.Id);

        // Empty is a real answer and marks them warm — "saved to nothing" and "nobody has asked" are
        // the same absence in a view and must not be the same fact. The real lists arrive from the
        // grains, asked for at admission and applied when they come back.
        lockouts?.Warmed(gameplay, []);
        social?.Warmed(gameplay, row: null);
        social?.Warmed(player.Key, []);
        social?.Admitted(player.Key, gameplay);
    }

    /// <inheritdoc />
    /// <remarks>⚠ Forgetting drops the view and keeps the outbox: the last write is the one worth not losing.</remarks>
    protected override void OnPlayerReleased(RealmPlayer player) {
        ArgumentNullException.ThrowIfNull(player);

        var gameplay = GameplayIdentityMap.From(player.Id);

        lockouts?.Forget(gameplay);
        social?.Forget(gameplay);
        identity.Release(gameplay);
    }

    /// <inheritdoc />
    /// <remarks>
    ///     <b>The hook that stops a rollout ending a raid.</b> The engine deliberately does not guess
    ///     at it — "in a scripted encounter" is a sentence only a game can finish — and
    ///     <c>Blocked</c> escalates to a live-ops alert at the hard deadline rather than to a
    ///     disconnect. Nothing is force-disconnected by a drain.
    /// </remarks>
    protected override TransferReadiness ReadinessOf(RealmPlayer player) {
        ArgumentNullException.ThrowIfNull(player);

        // ⚠ Somebody in an instance is never ready, because moving them mid-encounter is what the
        // hook exists to prevent. Everybody else is, and a battleground is deliberately *not* in this
        // list: a match is short enough to wait out and long enough to block a rollout for an hour.
        return Spec.Key.Map == MmoAddresses.BarrowdeepAddress ? TransferReadiness.Blocked : TransferReadiness.Ready;
    }

    /// <summary>Turns a shard's identity into a spawn seed.</summary>
    /// <remarks>
    ///     ⚠ <b><c>Guid.GetHashCode</c> would not do</b>: it is randomised per process, so a shard
    ///     restarted on the same spec would come back as a different world. Folding the sixteen bytes
    ///     is the whole of it, and it only has to be stable — not uniform.
    /// </remarks>
    static ulong Seed(ShardId shard) {
        Span<byte> bytes = stackalloc byte[16];

        shard.Value.TryWriteBytes(bytes);

        var seed = 14695981039346656037UL;

        foreach (var value in bytes) {
            seed ^= value;
            seed *= 1099511628211UL;
        }

        return seed;
    }

    static InvalidOperationException NotYet() =>
        new("This realm has not composed its content yet. Call Compose(libraries) before OnRealmInitialise.");
}
