// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Core.Yaml.Meta;

/// <summary>Applies a settings block's per-target overrides for one build target.</summary>
/// <remarks>
///     <para>
///         <code>
///     maxSize: 2048
///     compression: Bc7
///     overrides:
///       - target: Android
///         compression: Astc6x6
///         maxSize: 1024
///       - target: Android/Vulkan
///         maxSize: 2048
///     </code>
///         Resolved for <c>Android/Vulkan</c>: <c>compression: Astc6x6</c> from the first,
///         <c>maxSize: 2048</c> from the second, everything else from the base.
///     </para>
///     <para>
///         <b>A node-level merge, not a partial record.</b> Doc 08 sketched
///         <c>TargetOverride&lt;TSettings&gt;</c> — a sparse copy of the settings type with every
///         member nullable — which needs a generic type per settings type, and the reflection
///         generator describes closed types only. It is also more machinery than the problem needs:
///         "the keys that are present win" is exactly a mapping merge, so the base and the overrides
///         are merged as nodes and the result is bound once. Sparseness falls out of a key being
///         absent rather than out of a member being null, which also means an override cannot
///         accidentally set something to null by omitting it.
///     </para>
///     <para>
///         <b>Most general first.</b> <c>Android</c> is applied before <c>Android/Vulkan</c>, so the
///         more specific target wins where they disagree — and an override for a target that is not
///         a prefix of the one being built is not applied at all.
///     </para>
/// </remarks>
public static class TargetOverrides {
    /// <summary>The key holding the overrides.</summary>
    public const string OverridesKey = "overrides";

    /// <summary>The key inside an override naming what it applies to.</summary>
    public const string TargetKey = "target";

    /// <summary>Produces the settings as they apply to one target.</summary>
    /// <param name="settings">The settings block, with or without an <c>overrides</c> key.</param>
    /// <param name="target">The build target — <c>Android</c>, or <c>Android/Vulkan</c>.</param>
    /// <returns>A new mapping with the overrides applied and the <c>overrides</c> key gone.</returns>
    /// <exception cref="YamlBindingException">An override has no <c>target</c>.</exception>
    public static YamlMapping Resolve(YamlMapping settings, string target) {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrEmpty(target);

        var resolved = new YamlMapping { Tag = settings.Tag, Style = settings.Style };

        foreach (var (key, value) in settings.Entries) {
            if (!string.Equals(key, OverridesKey, StringComparison.Ordinal)) {
                resolved.Set(key, value);
            }
        }

        if (settings[OverridesKey] is not YamlSequence overrides) {
            return resolved;
        }

        // Ordered by how specific the target is, so 'Android' lands before 'Android/Vulkan' whatever
        // order the file lists them in — a file's order is the author's convenience, not a ranking.
        var applicable = new List<(int Depth, YamlMapping Patch)>();

        for (var index = 0; index < overrides.Count; index++) {
            if (overrides[index] is not YamlMapping patch) {
                throw new YamlBindingException($"{OverridesKey}[{index}]", "An override must be a mapping.");
            }

            if (patch[TargetKey] is not YamlScalar name) {
                throw new YamlBindingException(
                    $"{OverridesKey}[{index}]",
                    $"An override needs a '{TargetKey}' saying what it applies to."
                );
            }

            if (Applies(name.Value, target)) {
                applicable.Add((name.Value.Count(character => character == '/'), patch));
            }
        }

        foreach (var (_, patch) in applicable.OrderBy(entry => entry.Depth)) {
            foreach (var (key, value) in patch.Entries) {
                if (!string.Equals(key, TargetKey, StringComparison.Ordinal)) {
                    resolved.Set(key, value);
                }
            }
        }

        return resolved;
    }

    /// <summary>
    ///     Whether an override written for <paramref name="overrideTarget" /> applies when building
    ///     <paramref name="target" />.
    /// </summary>
    /// <remarks>
    ///     A prefix match on whole segments. <c>Android</c> applies to <c>Android/Vulkan</c>;
    ///     <c>Android/Vulkan</c> does not apply to <c>Android</c>, and <c>And</c> applies to nothing —
    ///     a plain <c>StartsWith</c> would have made it apply to both.
    /// </remarks>
    static bool Applies(string overrideTarget, string target) {
        if (string.Equals(overrideTarget, target, StringComparison.OrdinalIgnoreCase)) {
            return true;
        }

        return target.Length > overrideTarget.Length
            && target[overrideTarget.Length] == '/'
            && target.AsSpan(0, overrideTarget.Length).Equals(overrideTarget, StringComparison.OrdinalIgnoreCase);
    }
}
