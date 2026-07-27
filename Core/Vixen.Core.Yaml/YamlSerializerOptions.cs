// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Core.Yaml;

/// <summary>What a key in a document is called, given what the C# member is called.</summary>
/// <remarks>
///     One policy, applied everywhere, so that the name in the file is derivable from the name in the
///     source and neither has to be written twice. Doc 08's schema is camelCase — <c>colorSpace</c>,
///     <c>generateMips</c>, <c>maxSize</c> — which is what the default does.
/// </remarks>
public enum YamlNamingPolicy {
    /// <summary><c>MaxSize</c> becomes <c>maxSize</c>.</summary>
    CamelCase,

    /// <summary><c>MaxSize</c> stays <c>MaxSize</c>.</summary>
    Verbatim
}

/// <summary>How the object mapper should behave.</summary>
/// <param name="Naming">What a key is called.</param>
/// <param name="OmitDefaults">
///     Whether a member whose value equals a freshly constructed instance's is left out.
/// </param>
/// <param name="OnUnknownKey">
///     Called with the dotted path of every key that matched no member, or <see langword="null" /> to
///     say nothing.
/// </param>
/// <remarks>
///     <para>
///         <b>An unknown key is ignored rather than an error, and that is not laziness.</b> A project
///         opened in an older editor after someone added an import setting must still load: refusing
///         would make every schema addition a flag day across a team. Dropping it silently is the
///         other failure, so <paramref name="OnUnknownKey" /> exists and the asset database uses it
///         to warn — the caller decides whether an unknown key is news.
///     </para>
///     <para>
///         <paramref name="OmitDefaults" /> is off by default. A <c>.meta</c> file that listed only
///         what differs from the defaults would change shape whenever a default changed, which is a
///         diff on files nobody touched. Sparseness in doc 08 is a property of the per-target
///         <c>overrides</c> block, which is a different mechanism.
///     </para>
/// </remarks>
public sealed record YamlSerializerOptions(
    YamlNamingPolicy Naming = YamlNamingPolicy.CamelCase,
    bool OmitDefaults = false,
    Action<string>? OnUnknownKey = null
) {
    /// <summary>camelCase keys, every member written, unknown keys ignored quietly.</summary>
    public static YamlSerializerOptions Default { get; } = new();

    /// <summary>What a member is called in a document.</summary>
    /// <param name="memberName">The member's name in source.</param>
    /// <returns>Its key.</returns>
    public string KeyFor(string memberName) {
        if (Naming == YamlNamingPolicy.Verbatim || memberName.Length == 0 || !char.IsUpper(memberName[0])) {
            return memberName;
        }

        return string.Create(
            memberName.Length,
            memberName,
            static (destination, source) => {
                source.CopyTo(destination);
                destination[0] = char.ToLowerInvariant(destination[0]);
            }
        );
    }
}
