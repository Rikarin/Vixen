// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Net.Replication;
using Vixen.Net.Sessions;

namespace Vixen.Net.Rpc;

/// <summary>Who owns which networked object.</summary>
/// <remarks>
///     <para>
///         Ownership is not authority. The server is always the authority; ownership says whose
///         input an object answers to, and it is what <c>RequireOwnership</c> checks before a call
///         is dispatched. Everything else — who may spawn, despawn, observe or write — is
///         <c>NetworkRules</c>, which is not built yet.
///     </para>
///     <para>
///         Transfers are events, because half a game reacts to them: the camera, the input binding,
///         the nameplate. A game that had to poll for "am I still driving this?" would poll it every
///         frame for the two times a match that it changes.
///     </para>
/// </remarks>
public sealed class NetworkOwnership {
    readonly Dictionary<uint, PlayerId> owners = [];

    /// <summary>How many objects have an owner.</summary>
    public int Count => owners.Count;

    /// <summary>An object's owner changed, was set, or was cleared.</summary>
    public event Action<NetworkId, PlayerId, PlayerId>? OwnerChanged;

    /// <summary>Finds who owns an object.</summary>
    /// <param name="id">The object.</param>
    /// <param name="owner">Its owner, or <see cref="PlayerId.None" />.</param>
    /// <returns>Whether anybody owns it.</returns>
    public bool TryGetOwner(NetworkId id, out PlayerId owner) => owners.TryGetValue(id.Value, out owner);

    /// <summary>Whether a player owns an object.</summary>
    /// <param name="id">The object.</param>
    /// <param name="player">The player.</param>
    /// <returns>Whether they own it.</returns>
    public bool IsOwnedBy(NetworkId id, PlayerId player) =>
        player.IsValid && owners.TryGetValue(id.Value, out var owner) && owner == player;

    /// <summary>Gives an object to a player, or takes it back with <see cref="PlayerId.None" />.</summary>
    /// <param name="id">The object.</param>
    /// <param name="owner">Its new owner.</param>
    /// <returns>Whether this changed anything.</returns>
    public bool SetOwner(NetworkId id, PlayerId owner) {
        owners.TryGetValue(id.Value, out var previous);

        if (previous == owner) {
            return false;
        }

        if (owner.IsValid) {
            owners[id.Value] = owner;
        } else {
            owners.Remove(id.Value);
        }

        OwnerChanged?.Invoke(id, previous, owner);

        return true;
    }

    /// <summary>Everything one player owns.</summary>
    /// <param name="player">The player.</param>
    /// <param name="into">Where to put them. Not cleared first.</param>
    /// <returns>How many they own.</returns>
    public int OwnedBy(PlayerId player, List<NetworkId> into) {
        ArgumentNullException.ThrowIfNull(into);
        var found = 0;

        foreach (var (id, owner) in owners) {
            if (owner == player) {
                into.Add(new(id));
                found++;
            }
        }

        return found;
    }

    /// <summary>Forgets an object, because it was destroyed.</summary>
    /// <param name="id">The object.</param>
    /// <returns>Whether it had an owner.</returns>
    public bool Forget(NetworkId id) => SetOwner(id, PlayerId.None);

    /// <summary>Takes everything a player owned away from them.</summary>
    /// <param name="player">The player who left.</param>
    /// <param name="transferTo">
    ///     Who gets their objects — the server, by passing <see cref="PlayerId.None" />, which is the
    ///     safe default: an object nobody owns still exists and still obeys the server, where an
    ///     object owned by a player who is gone obeys nothing.
    /// </param>
    /// <returns>How many objects changed hands.</returns>
    public int TransferAll(PlayerId player, PlayerId transferTo = default) {
        var moved = 0;
        var theirs = new List<uint>();

        foreach (var (id, owner) in owners) {
            if (owner == player) {
                theirs.Add(id);
            }
        }

        foreach (var id in theirs) {
            if (SetOwner(new(id), transferTo)) {
                moved++;
            }
        }

        return moved;
    }
}
