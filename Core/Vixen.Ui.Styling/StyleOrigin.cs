// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ui.Styling;

/// <summary>Who a declaration came from, which is the cascade's first question.</summary>
/// <remarks>
///     <para>
///         The order here is the order of increasing power for <i>normal</i> declarations, and the
///         cascade reverses it for <c>!important</c> ones. That reversal is the whole point of the
///         mechanism: a user stylesheet marking something important is a user who means it, and it
///         has to be able to beat a page that also means it — an accessibility guarantee rather than
///         a curiosity, and the reason importance is not simply "wins".
///     </para>
///     <para>
///         Vixen has all three. The engine's own control themes are
///         <see cref="UserAgent" />, a game's stylesheets are <see cref="Author" />, and
///         <see cref="User" /> is what a player's accessibility overrides load as.
///     </para>
/// </remarks>
public enum StyleOrigin : byte {
    /// <summary>Vixen's built-in styles for its own controls.</summary>
    UserAgent,

    /// <summary>A player's own overrides.</summary>
    User,

    /// <summary>The game's stylesheets. Where nearly everything lives.</summary>
    Author
}
