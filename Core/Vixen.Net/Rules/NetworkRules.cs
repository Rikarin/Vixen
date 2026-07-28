// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Net.Rules;

/// <summary>Which clients may do a thing. The server always may — it is the authority.</summary>
public enum RuleAudience : byte {
    /// <summary>No client. The server decides, and a client that asks is refused.</summary>
    ServerOnly = 0,

    /// <summary>The client that owns the object, and nobody else.</summary>
    Owner = 1,

    /// <summary>Any client in the session.</summary>
    Everyone = 2
}

/// <summary>What becomes of an object when the player who owned it goes away.</summary>
public enum DisconnectBehaviour : byte {
    /// <summary>It goes with them. A player's avatar, usually.</summary>
    Destroy = 0,

    /// <summary>
    ///     The server takes it. The safe default: an object nobody owns still obeys the server, where
    ///     one owned by a player who is gone obeys nothing.
    /// </summary>
    TransferToServer = 1,

    /// <summary>
    ///     It keeps its owner, so the same player resumes it when they reconnect. Only meaningful
    ///     inside the session's reconnect window, and paired with it.
    /// </summary>
    Persist = 2
}

/// <summary>
///     Who is allowed to do what to a networked object.
/// </summary>
/// <remarks>
///     <para>
///         The best idea in the design this one is derived from, and the reason it is a declaration
///         rather than a switch: a co-operative game and a competitive shooter want different answers
///         to every question below, and without this they get them by being different engines. With
///         it they are the same engine with different rules, and relaxing server authority is an
///         explicit, reviewable decision somebody wrote down rather than an accident somebody made.
///     </para>
///     <para>
///         <b>Rules never grant a client more than the code asks for.</b> Where both a rule and an
///         attribute have an opinion — an <c>[ServerRpc(RequireOwnership = true)]</c> on an object
///         whose rules say <see cref="RuleAudience.Everyone" /> — the stricter of the two wins. A
///         policy file cannot quietly widen what a method declared about itself; it can only narrow
///         it.
///     </para>
///     <para>
///         <b>What is enforced today.</b> <see cref="CallServerRpc" /> is checked by
///         <c>RpcRouter</c> before a call is dispatched, <see cref="ChangeOwner" /> by the ownership
///         transfer path, and <see cref="OnOwnerDisconnect" /> when a player leaves.
///         <see cref="Spawn" />, <see cref="Despawn" /> and <see cref="Write" /> are declared and
///         answered through <see cref="NetworkRulesRegistry" />, and have no enforcement point yet
///         because nothing can spawn a networked object from a client or write replicated state from
///         one — when those arrive they ask the same question rather than inventing a second policy.
///     </para>
/// </remarks>
public sealed record NetworkRules {
    /// <summary>Everything the server's, which is what a competitive game wants.</summary>
    public static NetworkRules ServerAuthoritative { get; } = new();

    /// <summary>
    ///     The owner may act on what is theirs: call, move, and hand it on. What a co-operative game
    ///     or a trusted-client prototype wants, and a deliberate relaxation rather than a default.
    /// </summary>
    public static NetworkRules OwnerAuthoritative { get; } = new() {
        Write = RuleAudience.Owner,
        Despawn = RuleAudience.Owner,
        ChangeOwner = RuleAudience.Owner
    };

    /// <summary>Who may ask the server to create one of these.</summary>
    public RuleAudience Spawn { get; init; } = RuleAudience.ServerOnly;

    /// <summary>Who may ask the server to destroy one.</summary>
    public RuleAudience Despawn { get; init; } = RuleAudience.ServerOnly;

    /// <summary>Who the rules allow to invoke a server call on one.</summary>
    /// <remarks>
    ///     <para>
    ///         <see cref="RuleAudience.Everyone" /> by default, which means "the rules add nothing" —
    ///         not "anybody may call anything". Safety here comes from the other end:
    ///         <c>[ServerRpc]</c> requires ownership unless a method says otherwise, so out of the box
    ///         every call is already the owner's. This field is the knob that <i>tightens</i> that,
    ///         to the owner or to nobody, for an object whose calls should be narrower than its
    ///         methods declared.
    ///     </para>
    ///     <para>
    ///         It cannot widen. A method that requires ownership stays an owner's call however
    ///         permissive the object's rules are, because a policy file quietly granting more than the
    ///         code asked for is the thing this design exists to avoid.
    ///     </para>
    /// </remarks>
    public RuleAudience CallServerRpc { get; init; } = RuleAudience.Everyone;

    /// <summary>Who may write the replicated state of one.</summary>
    public RuleAudience Write { get; init; } = RuleAudience.ServerOnly;

    /// <summary>Who may hand one to somebody else.</summary>
    public RuleAudience ChangeOwner { get; init; } = RuleAudience.ServerOnly;

    /// <summary>What becomes of one when the player who owned it goes.</summary>
    public DisconnectBehaviour OnOwnerDisconnect { get; init; } = DisconnectBehaviour.TransferToServer;

    /// <summary>Whether an audience admits a particular client.</summary>
    /// <param name="audience">Who is allowed.</param>
    /// <param name="requester">Who is asking. <see cref="Sessions.PlayerId.None" /> is the server.</param>
    /// <param name="isOwner">Whether they own the object in question.</param>
    /// <returns>Whether they may.</returns>
    public static bool Allows(RuleAudience audience, Sessions.PlayerId requester, bool isOwner) =>
        // The server is not a player and is never refused: it is the authority, and a rule that could
        // stop it would be a rule about nothing.
        !requester.IsValid
        || audience switch {
            RuleAudience.Everyone => true,
            RuleAudience.Owner => isOwner,
            _ => false
        };
}
