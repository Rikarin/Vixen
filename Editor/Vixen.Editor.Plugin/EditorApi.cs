// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Editor.Plugin;

/// <summary>Which version of the plugin contract this editor implements.</summary>
/// <remarks>
///     <para>
///         <b>A plugin declares the version it was built against and the loader refuses one it
///         cannot honour.</b> The alternative — load it and find out — is a
///         <c>MissingMethodException</c> from inside somebody else's code, on a machine that is not
///         yours, with a stack trace that names neither the plugin nor the change that broke it.
///     </para>
///     <para>
///         ⚠ <b>While the major version is zero, the minor is the breaking number.</b> That is what
///         SemVer says 0.x means, and it is the honest reading of an editor whose extension points
///         are still moving: <c>0.1</c> and <c>0.2</c> are not compatible and the loader says so
///         rather than guessing. Once this reaches <c>1.0</c> the ordinary rule applies — same
///         major, and a minor no higher than the host's, because a plugin built against
///         <c>1.4</c> may call something <c>1.2</c> has not got.
///     </para>
///     <para>
///         This is deliberately <i>not</i> the assembly version. The package version moves for a
///         bug fix in the loader; the contract version moves when what a plugin may call changes,
///         and conflating the two makes every patch release lock out every plugin.
///     </para>
/// </remarks>
public static class EditorApi {
    /// <summary>The contract version this editor implements.</summary>
    public static Version Version { get; } = new(0, 1);

    /// <summary>Whether a plugin built against a version of the contract can run here.</summary>
    /// <param name="built">What the plugin's manifest declares.</param>
    /// <returns>Whether it can.</returns>
    public static bool IsCompatible(Version built) {
        ArgumentNullException.ThrowIfNull(built);

        if (built.Major != Version.Major) {
            return false;
        }

        // Pre-1.0 the minor is the breaking number, so it has to match. After it, a plugin built
        // against an older minor runs on a newer host and not the other way round.
        return Version.Major == 0 ? built.Minor == Version.Minor : built.Minor <= Version.Minor;
    }

    /// <summary>Why a version is not compatible, in the words a plugin author needs.</summary>
    /// <param name="built">What the plugin's manifest declares.</param>
    /// <returns>The explanation, or <c>null</c> if it is compatible after all.</returns>
    public static string? Explain(Version built) {
        ArgumentNullException.ThrowIfNull(built);

        if (IsCompatible(built)) {
            return null;
        }

        return built > Version
            ? $"was built against editor API {built.ToString(2)} and this editor implements {Version.ToString(2)}. Update the editor."
            : $"was built against editor API {built.ToString(2)}, which this editor ({Version.ToString(2)}) no longer accepts. Rebuild the plugin.";
    }
}
