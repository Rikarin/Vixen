// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Live.Gate;

/// <summary>What an authority decided about a credential.</summary>
/// <param name="Ok">Whether it recognised them.</param>
/// <param name="Handle">Who it says they are. Unique within the authority, and opaque to the gate.</param>
/// <param name="Detail">Why not, when it did not. Shown to the player, so say something useful.</param>
public readonly record struct AuthorityResult(bool Ok, string Handle, string Detail) {
    /// <summary>They are who they say.</summary>
    /// <param name="handle">What to call them. Prefix it with the scheme — <c>steam:7656…</c>.</param>
    /// <returns>The result.</returns>
    public static AuthorityResult Accept(string handle) => new(true, handle, "");

    /// <summary>They are not.</summary>
    /// <param name="detail">Why, for the player.</param>
    /// <returns>The result.</returns>
    public static AuthorityResult Refuse(string detail) => new(false, "", detail);
}

/// <summary>Turns a credential into an account handle. <b>The one thing the engine does not ship.</b></summary>
/// <remarks>
///     <para>
///         ⚠ <b>There is no credential store in this repository and there is not going to be one.</b>
///         A game engine that shipped one would ship a liability its authors do not operate: hashing
///         parameters that age, breach response, password reset, multi-factor, account recovery,
///         and a regulatory surface that differs per market. Every one of those is a product decision.
///     </para>
///     <para>
///         What the gate actually needs is much smaller — <i>which account is this request for</i> —
///         and every deployment already has something that answers it: an OIDC provider, Steam, EOS,
///         a console platform SDK, or the studio's existing account service. This interface is that
///         answer's shape, and <c>Vixen.Live.Persistence</c> maps the handle it returns onto the
///         account the world knows.
///     </para>
///     <para>
///         Same position doc 16 took on Steam and EOS transports and doc 27 M-Q1 restated: the engine
///         ships the seam and one honest development implementation, not the integration.
///     </para>
/// </remarks>
public interface IAccountAuthority {
    /// <summary>Which <see cref="SignInRequest.Scheme" /> this one answers for.</summary>
    string Scheme { get; }

    /// <summary>Decides who a credential belongs to.</summary>
    /// <param name="credential">Whatever this authority understands. Never read by the engine.</param>
    /// <param name="cancellation">Gives up.</param>
    /// <returns>The handle, or a refusal.</returns>
    Task<AuthorityResult> AuthenticateAsync(string credential, CancellationToken cancellation);
}

/// <summary>
///     Trusts whatever it is told. <b>For a developer's machine and a test, and it says so loudly.</b>
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Anyone who can reach this gate can sign in as anybody.</b> That is not a weakness to
///         be tightened later, it is the entire behaviour: the credential <em>is</em> the handle.
///         It exists so that <c>vixen live up</c> on a laptop, the sample, and the test suite do not
///         each need an identity provider to log two characters in.
///     </para>
///     <para>
///         ⚠ <b>It must be constructed deliberately.</b> Nothing registers it by default — a gate
///         with no authority configured refuses every sign-in, which is loud, rather than accepting
///         every sign-in, which is not. That is the same judgement <c>RealmHost.DevelopmentSigner</c>
///         made about a missing cluster key.
///     </para>
/// </remarks>
public sealed class DevelopmentAuthority : IAccountAuthority {
    /// <summary>The scheme a client names to reach it.</summary>
    public const string Name = "development";

    /// <inheritdoc />
    public string Scheme => Name;

    /// <inheritdoc />
    public Task<AuthorityResult> AuthenticateAsync(string credential, CancellationToken cancellation) =>
        Task.FromResult(
            string.IsNullOrWhiteSpace(credential)
                ? AuthorityResult.Refuse("A development sign-in needs a handle to be. Any handle.")
                : AuthorityResult.Accept($"{Name}:{credential.Trim()}")
        );
}
