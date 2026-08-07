// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Core.Yaml.Meta;

/// <summary>A file written by a newer editor than the one trying to read it.</summary>
public sealed class MetaVersionException(int fileVersion, int currentVersion)
    : Exception(
        $"This .meta was written against envelope version {fileVersion}, and this build understands "
        + $"{currentVersion}. It came from a newer editor; upgrading is the only way forward, because "
        + "guessing what a key means is how a project gets quietly corrupted."
    ) {
    /// <summary>What the file says.</summary>
    public int FileVersion { get; } = fileVersion;

    /// <summary>What this build understands.</summary>
    public int CurrentVersion { get; } = currentVersion;
}

/// <summary>
///     The steps that take a <c>.meta</c> document from an older envelope schema to the current one.
/// </summary>
/// <remarks>
///     <para>
///         <c>metaVersion</c> is a real version with a real chain behind it, not a magic constant
///         that never changes. Each step takes a document from version <c>n</c> to <c>n + 1</c> and
///         is applied in order, so a file five versions old is upgraded by five small, individually
///         reviewable functions rather than by one that has to know every historical shape at once.
///     </para>
///     <para>
///         <b>Steps work on the node tree, not on the model.</b> A migration exists precisely because
///         the old document does not fit the current type; binding it first is impossible by
///         construction. Working on nodes also means a step can move a key, rename it, or split it,
///         and the byte-fidelity emitter writes the result out with everything the step did not
///         touch — including comments — exactly as it found it.
///     </para>
///     <para>
///         <b>There are no steps yet, and that is the point of shipping the mechanism now.</b> The
///         chain is exercised by tests with steps of their own, so that the first real migration is
///         one function rather than a design.
///     </para>
/// </remarks>
public sealed class MetaMigrationChain {
    /// <summary>The envelope schema version this build writes.</summary>
    public const int CurrentVersion = 1;

    readonly Dictionary<int, Action<YamlMapping>> steps = [];

    /// <summary>The chain the engine uses.</summary>
    public static MetaMigrationChain Current { get; } = new();

    /// <summary>What this chain upgrades to.</summary>
    public int TargetVersion { get; }

    /// <summary>Creates a chain.</summary>
    /// <param name="targetVersion">What it upgrades to. Defaults to <see cref="CurrentVersion" />.</param>
    public MetaMigrationChain(int targetVersion = CurrentVersion) => TargetVersion = targetVersion;

    /// <summary>Adds a step.</summary>
    /// <param name="fromVersion">The version it upgrades from. It produces <c>fromVersion + 1</c>.</param>
    /// <param name="step">What it does to the document.</param>
    /// <returns>This chain, for chaining.</returns>
    /// <exception cref="ArgumentException">There is already a step from that version.</exception>
    public MetaMigrationChain Add(int fromVersion, Action<YamlMapping> step) {
        ArgumentNullException.ThrowIfNull(step);

        if (!steps.TryAdd(fromVersion, step)) {
            throw new ArgumentException(
                $"There is already a step from version {fromVersion}. Two steps claiming one version means one "
                + "of them silently never runs.",
                nameof(fromVersion)
            );
        }

        return this;
    }

    /// <summary>Brings a document up to date, in place.</summary>
    /// <param name="root">The document.</param>
    /// <returns>How many steps ran.</returns>
    /// <exception cref="MetaVersionException">The file is newer than this build.</exception>
    /// <exception cref="YamlBindingException">A version in between has no step.</exception>
    public int Apply(YamlMapping root) {
        ArgumentNullException.ThrowIfNull(root);

        var version = VersionOf(root);

        if (version > TargetVersion) {
            throw new MetaVersionException(version, TargetVersion);
        }

        var applied = 0;

        while (version < TargetVersion) {
            if (!steps.TryGetValue(version, out var step)) {
                throw new YamlBindingException(
                    "metaVersion",
                    $"There is no step from envelope version {version} to {version + 1}, so a file at {version} "
                    + $"cannot be brought to {TargetVersion}. The chain has a hole in it."
                );
            }

            step(root);
            version++;
            applied++;

            // Written back after every step, so a step that inspects the version sees its own input
            // rather than where the chain started.
            root.Set("metaVersion", new YamlScalar(version.ToString(), YamlScalarStyle.Plain));
        }

        return applied;
    }

    /// <summary>What version a document says it is.</summary>
    /// <param name="root">The document.</param>
    /// <returns>Its version, or <c>1</c> if it does not say.</returns>
    /// <remarks>
    ///     <para>
    ///         A document with no <c>metaVersion</c> is treated as the first version rather than as
    ///         broken: hand-written files exist, and the value it would have had is the one that was
    ///         current when hand-writing it was plausible.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The key is found case-insensitively, and the indexer that used to find it is not —
    ///         which turned "a newer file is refused" into "a newer file is silently misread".</b>
    ///         <see cref="YamlMapping" />'s lookup is ordinal and correctly so, because a YAML mapping's
    ///         keys are case-sensitive; but <c>YamlSerializer</c> binds <i>members</i>
    ///         case-insensitively, so a sidecar saying <c>MetaVersion: 5</c> bound to the record as five
    ///         and arrived here as the default one. The guard never fired, no migration ran, and a
    ///         document from a newer editor was read as though it were the oldest — which is exactly the
    ///         corruption <see cref="MetaVersionException" />'s own message says it exists to prevent.
    ///         Three readers of one envelope, so the envelope's keys get one rule and it is the
    ///         serializer's.
    ///     </para>
    /// </remarks>
    public static int VersionOf(YamlMapping root) {
        ArgumentNullException.ThrowIfNull(root);

        foreach (var entry in root.Entries) {
            if (!string.Equals(entry.Key, "metaVersion", StringComparison.OrdinalIgnoreCase)) {
                continue;
            }

            return entry.Value is YamlScalar scalar && int.TryParse(scalar.Value, out var version)
                ? version
                : 1;
        }

        return 1;
    }
}
