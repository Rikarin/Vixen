// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Editor.Core;

/// <summary>Opening a link or a folder in whatever the operating system opens it with.</summary>
/// <remarks>
///     <para>
///         <b>Published so a module can link out</b> (#1296). The Help menu's five lines, Show in
///         Explorer and a console line's reveal all went through one private <c>Browse</c> on the
///         application over the platform's URL opener; the Help menu is <c>HelpModule</c>'s now, and
///         this is the one verb it needed from the host.
///     </para>
///     <para>
///         ⚠ <b>Failure is said, not returned.</b> A link that did not open is a notification naming
///         the link — every caller showed the same one — so <see cref="Open" /> says it and the
///         caller has nothing to decide. <see cref="CanOpen" /> is for greying a menu line on a host
///         that has no opener at all.
///     </para>
/// </remarks>
public interface IExternalLinks {
    /// <summary>Whether this host can open anything at all.</summary>
    bool CanOpen { get; }

    /// <summary>Opens a URL — a web page, or a <c>file://</c> folder — and says so if it could not.</summary>
    /// <param name="url">What to open.</param>
    void Open(string url);
}
