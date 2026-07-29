// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Yaml;

namespace Vixen.Editor.Assets;

/// <summary>Finds what a Vixen-authored document points at.</summary>
/// <remarks>
///     <para>
///         Shared by every importer that reads the YAML dialect, because "what does this file
///         depend on" has exactly one right answer and two importers deciding it separately is two
///         answers. <c>NativeFormatImporter</c> carries a material through knowing nothing but this;
///         <c>SceneImporter</c> compiles a scene and needs the same list for the same reason.
///     </para>
///     <para>
///         A walk of the node tree rather than a regular expression over the text, because a GUID
///         inside a comment or a quoted description is not a reference and a text scan cannot tell
///         the difference. It would produce a dependency that never changes and never breaks
///         anything — which is exactly the kind of wrongness nobody finds.
///     </para>
///     <para>
///         Iterative rather than recursive. A scene is the deepest document the engine has and a
///         deeply nested prefab hierarchy is an ordinary thing to author; a stack overflow inside an
///         importer takes the whole process with it, which is the one failure the
///         one-bad-asset-does-not-stop-the-build promise cannot catch.
///     </para>
/// </remarks>
public static class AssetReferenceScan {
    /// <summary>Declares every asset a document points at, and reports the ones that will not parse.</summary>
    /// <param name="root">The document.</param>
    /// <param name="context">What to declare them on, and where to complain.</param>
    /// <returns>How many scalars began with the reference prefix and were not references.</returns>
    /// <remarks>
    ///     ⚠ <b>A reference that does not parse fails the import.</b> A scalar beginning <c>vx:</c>
    ///     was meant to be a reference by whoever wrote it; if the GUID after it is malformed, the
    ///     alternatives are failing here — naming the file and the text — or shipping an asset whose
    ///     pointer resolves to nothing on a player's machine. Anything that does not begin with the
    ///     prefix is left alone, because a string field holding arbitrary text is ordinary.
    /// </remarks>
    public static int Declare(YamlMapping root, ImportContext context) {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(context);

        var malformed = 0;
        var pending = new Stack<YamlNode>();
        pending.Push(root);

        while (pending.Count > 0) {
            switch (pending.Pop()) {
                case YamlMapping mapping:
                    foreach (var (_, value) in mapping.Entries) {
                        pending.Push(value);
                    }

                    break;

                case YamlSequence sequence:
                    foreach (var item in sequence.Items) {
                        pending.Push(item);
                    }

                    break;

                case YamlScalar scalar when scalar.Value.StartsWith(AssetReference.Prefix, StringComparison.Ordinal):
                    if (AssetReference.TryParse(scalar.Value, out var reference)) {
                        context.DependsOn(reference.Asset);
                    } else {
                        malformed++;

                        context.Report(
                            ImportSeverity.Error,
                            $"'{scalar.Value}' begins with '{AssetReference.Prefix}' but is not a reference. It "
                            + "should be 'vx:' and 32 hex digits, optionally '#' and a sub-asset id."
                        );
                    }

                    break;
            }
        }

        return malformed;
    }
}
