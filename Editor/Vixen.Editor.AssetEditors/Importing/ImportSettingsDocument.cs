// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Yaml;
using Vixen.Core.Yaml.Meta;
using Vixen.Editor.Core;

namespace Vixen.Editor.AssetEditors.Importing;

/// <summary>What one build target does differently from the base import settings.</summary>
/// <remarks>
///     <para>
///         <b>Sparse by which members are marked, not by which are null.</b> Doc 08's override block
///         is a mapping merge — the keys that are present win — so an override is a set of member
///         names plus the values they take. Modelling it as a settings object with every member
///         nullable would make "override this to null" and "do not override this" the same thing,
///         which is exactly the distinction the block exists to draw.
///     </para>
///     <para>
///         <see cref="Settings" /> always holds a complete object: the base merged with this
///         target's patch. That is what the matrix draws — a cell that is not overridden shows the
///         value that <i>will</i> be used, which is the base's, rather than a blank the author has
///         to look somewhere else to fill in.
///     </para>
/// </remarks>
public sealed class TargetOverride {
    readonly HashSet<string> overridden = new(StringComparer.Ordinal);

    /// <summary>Which build target it applies to — <c>Android</c>, or <c>Android/Vulkan</c>.</summary>
    public string Target { get; internal set; }

    /// <summary>The settings as this target sees them: the base with the patch applied.</summary>
    public object Settings { get; }

    /// <summary>The members this target overrides, in no particular order.</summary>
    public IReadOnlyCollection<string> Members => overridden;

    internal TargetOverride(string target, object settings) {
        Target = target;
        Settings = settings;
    }

    /// <summary>Whether this target overrides a member.</summary>
    /// <param name="member">The member's name in source.</param>
    /// <returns>Whether it does.</returns>
    public bool IsOverridden(string member) {
        ArgumentException.ThrowIfNullOrEmpty(member);
        return overridden.Contains(member);
    }

    internal bool Mark(string member) => overridden.Add(member);

    internal bool Unmark(string member) => overridden.Remove(member);
}

/// <summary>An asset's import settings, open for editing: the base, the per-target overrides, and
/// where it appears in a build.</summary>
/// <remarks>
///     <para>
///         <b>It edits the sidecar's node tree, not a bound <see cref="AssetMeta" />.</b> Binding
///         and re-emitting would be shorter and would throw away two things the file carries and the
///         schema does not: the per-target <c>overrides</c> block, which
///         <see cref="TargetOverrides" /> resolves at import time and no settings type has a member
///         for, and any key a newer editor wrote that this one does not know about. A settings
///         editor that silently deleted either would turn opening a file into an edit.
///     </para>
///     <para>
///         <b>What the inspector edits is a mirror, and there is a test that says so.</b> The
///         settings records are <c>init</c>-only for the pipeline's sake — a record whose fields can
///         be written after it has been hashed into a cache key is a footgun — so the editable object
///         beside each one is a class with the same member names and types.
///         <c>ImportSettingsMirrorTests</c> compares the two by reflection, so a setting added to an
///         importer and not to its mirror is a red test rather than a knob nobody can turn.
///     </para>
///     <para>
///         ⚠ <b>Saving writes the sidecar and nothing else.</b> Changing how a texture is compressed
///         does not re-import it; what re-imports it is the import pass noticing that the settings
///         hash moved. A document that ran an import inside <c>Save</c> would make Ctrl+S take
///         thirty seconds on a model.
///     </para>
/// </remarks>
public abstract class ImportSettingsDocument : EditorDocument {
    readonly List<TargetOverride> overrides = [];
    readonly List<string> unknownKeys = [];
    readonly YamlMapping root;

    /// <summary>Where the asset itself is, absolute.</summary>
    public string AssetPath { get; }

    /// <summary>Where its sidecar is, absolute.</summary>
    public string MetaPath { get; }

    /// <summary>The base settings, which every target starts from.</summary>
    public object Settings { get; }

    /// <summary>Where the asset appears in a shipped build.</summary>
    public AddressableEdits Addressable { get; }

    /// <summary>What the last import produced inside this asset, as the sidecar recorded it.</summary>
    /// <remarks>
    ///     Read-only, and from the file rather than from a fresh import: this is what a model's part
    ///     list shows, and re-importing to populate a panel would make opening an asset expensive.
    ///     An asset that has never been imported has none, which is honest.
    /// </remarks>
    public IReadOnlyList<SubAssetEntry> SubAssets { get; }

    /// <summary>The per-target overrides, in the order the file listed them.</summary>
    public IReadOnlyList<TargetOverride> Overrides => overrides;

    /// <summary>Keys in the sidecar that matched no member, reported rather than dropped silently.</summary>
    public IReadOnlyList<string> UnknownKeys => unknownKeys;

    /// <summary>Raised when a target is added or removed, or a member's override is turned on or off.</summary>
    /// <remarks>
    ///     What the matrix rebuilds from. Not raised for an ordinary value edit — that changes a
    ///     cell's contents rather than which cells exist, and a matrix that rebuilt itself on every
    ///     keystroke would take the focus out of the field being typed into.
    /// </remarks>
    public event Action<ImportSettingsDocument>? OverridesChanged;

    /// <summary>The type of <see cref="Settings" />, which is the mirror rather than the record.</summary>
    protected abstract Type SettingsType { get; }

    /// <summary>The importer's name, which is the sidecar's type tag.</summary>
    protected abstract string ImporterTag { get; }

    /// <summary>Opens an asset's import settings.</summary>
    /// <param name="project">The project it belongs to.</param>
    /// <param name="asset">Its identity.</param>
    /// <param name="path">Where the asset is, absolute.</param>
    protected ImportSettingsDocument(EditorProject project, AssetId asset, string path)
        : base(project, asset, Path.GetFileName(path)) {
        ArgumentException.ThrowIfNullOrEmpty(path);

        AssetPath = path;
        MetaPath = AssetMetaFile.PathFor(path);

        root = ReadSidecar(MetaPath);

        var importer = root["importer"] as YamlMapping ?? new YamlMapping { Tag = ImporterTag };

        Settings = BindSettings(Base(importer));
        Addressable = BindAddressable(root["addressable"] as YamlMapping);
        SubAssets = ReadSubAssets(root["subAssets"] as YamlSequence);

        LoadOverrides(importer);
    }

    /// <summary>Adds a target with nothing overridden yet, undoably.</summary>
    /// <param name="target">The build target — <c>Android</c>, or <c>Android/Vulkan</c>.</param>
    /// <returns>The new row.</returns>
    /// <exception cref="InvalidOperationException">That target already has a row.</exception>
    public TargetOverride AddTarget(string target) {
        ArgumentException.ThrowIfNullOrEmpty(target);

        if (Find(target) is not null) {
            throw new InvalidOperationException(
                $"'{target}' already has an override row. Two rows for one target would make which one "
                + "wins depend on the order they were merged in."
            );
        }

        var command = new AddTargetCommand(this, target);

        Stack.Execute(command);
        Stack.Seal();

        return command.Row;
    }

    /// <summary>Removes a target's row, undoably.</summary>
    /// <param name="target">The target.</param>
    /// <returns>Whether it had one.</returns>
    public bool RemoveTarget(string target) {
        ArgumentException.ThrowIfNullOrEmpty(target);

        if (Find(target) is not { } row) {
            return false;
        }

        Stack.Execute(new RemoveTargetCommand(this, row, overrides.IndexOf(row)));
        Stack.Seal();

        return true;
    }

    /// <summary>Turns one member's override on or off for one target, undoably.</summary>
    /// <param name="row">The target's row.</param>
    /// <param name="member">The member's name in source.</param>
    /// <param name="overridden">Whether the target should override it.</param>
    /// <returns>Whether anything changed.</returns>
    /// <remarks>
    ///     ⚠ <b>Turning an override off does not restore the base value into the row.</b> The row
    ///     keeps what it was showing until the document is reloaded, which is what makes turning the
    ///     checkbox off and on again a no-op rather than a way to lose a number. What decides the
    ///     built result is the marked set, and only that is written.
    /// </remarks>
    public bool SetOverridden(TargetOverride row, string member, bool overridden) {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentException.ThrowIfNullOrEmpty(member);

        if (row.IsOverridden(member) == overridden) {
            return false;
        }

        Stack.Execute(new SetOverriddenCommand(this, row, member, overridden));
        Stack.Seal();

        return true;
    }

    /// <summary>The row for a target, if it has one.</summary>
    /// <param name="target">The target.</param>
    /// <returns>The row, or <see langword="null" />.</returns>
    public TargetOverride? Find(string target) {
        ArgumentException.ThrowIfNullOrEmpty(target);

        foreach (var row in overrides) {
            if (string.Equals(row.Target, target, StringComparison.OrdinalIgnoreCase)) {
                return row;
            }
        }

        return null;
    }

    /// <summary>The sidecar as this document would write it, without writing it.</summary>
    /// <returns>The YAML.</returns>
    /// <remarks>
    ///     Separate from <see cref="EditorDocument.Save" /> so a test can assert on the bytes and so
    ///     a diff view has something to show. Nothing here touches the disk.
    /// </remarks>
    public string ToYaml() {
        var importer = root["importer"] as YamlMapping;

        if (importer is null) {
            importer = new YamlMapping { Tag = ImporterTag };
            root.Set("importer", importer);
        }

        // The tag is asserted rather than assumed: a sidecar whose importer block lost its tag binds
        // to nothing, and the one moment we know which importer this is, is now.
        importer.Tag = ImporterTag;

        foreach (var (key, value) in Emit(Settings)) {
            importer.Set(key, value);
        }

        importer.Remove(TargetOverrides.OverridesKey);

        if (overrides.Count > 0) {
            var sequence = new YamlSequence();

            foreach (var row in overrides) {
                var patch = new YamlMapping();
                patch.Set(TargetOverrides.TargetKey, new YamlScalar(row.Target));

                foreach (var (key, value) in Emit(row.Settings)) {
                    if (row.IsOverridden(MemberOf(key))) {
                        patch.Set(key, value);
                    }
                }

                sequence.Add(patch);
            }

            importer.Set(TargetOverrides.OverridesKey, sequence);
        }

        WriteAddressable();

        return YamlWriter.Write(root);
    }

    /// <inheritdoc />
    protected override void SaveCore() => AssetFile.Write(MetaPath, ToYaml());

    /// <summary>Says the set of rows or of marked members has changed.</summary>
    internal void RaiseOverridesChanged() => OverridesChanged?.Invoke(this);

    internal void Insert(TargetOverride row, int index) {
        overrides.Insert(Math.Clamp(index, 0, overrides.Count), row);
        RaiseOverridesChanged();
    }

    internal void Detach(TargetOverride row) {
        overrides.Remove(row);
        RaiseOverridesChanged();
    }

    /// <summary>A fresh mirror holding the type's defaults.</summary>
    /// <returns>The mirror.</returns>
    /// <remarks>
    ///     Through the binder on an empty mapping rather than through <c>Activator</c>: the mirror is
    ///     a described type either way, and going through the one path means a mirror that cannot be
    ///     constructed fails the same way whether it is being read or being defaulted.
    /// </remarks>
    internal object NewSettings() => BindSettings(new());

    object BindSettings(YamlMapping node) =>
        YamlSerializer.Deserialize(node, SettingsType, Options())!;

    AddressableEdits BindAddressable(YamlMapping? node) {
        if (node is null) {
            return new();
        }

        var info = YamlSerializer.Deserialize<AddressableInfo>(node, Options());

        return new() {
            Address = info.Address ?? string.Empty,
            Group = info.Group ?? string.Empty,
            Labels = string.Join(", ", info.Labels)
        };
    }

    YamlSerializerOptions Options() =>
        YamlSerializerOptions.Default with { OnUnknownKey = key => unknownKeys.Add(key) };

    YamlMapping Emit(object settings) =>
        (YamlMapping) YamlSerializer.Serialize(settings, SettingsType);

    void LoadOverrides(YamlMapping importer) {
        if (importer[TargetOverrides.OverridesKey] is not YamlSequence sequence) {
            return;
        }

        var baseline = Base(importer);

        for (var index = 0; index < sequence.Count; index++) {
            if (sequence[index] is not YamlMapping patch
                || patch[TargetOverrides.TargetKey] is not YamlScalar target
                || target.Value.Length == 0) {
                // A row with no target applies to nothing — TargetOverrides.Resolve refuses the whole
                // file for one. Dropping it here would delete it on the next save, so it is left in
                // the tree and simply not shown; the import is what reports it.
                continue;
            }

            var merged = new YamlMapping();

            foreach (var (key, value) in baseline) {
                merged.Set(key, value);
            }

            List<string> marked = [];

            foreach (var (key, value) in patch) {
                if (string.Equals(key, TargetOverrides.TargetKey, StringComparison.Ordinal)) {
                    continue;
                }

                merged.Set(key, value);
                marked.Add(MemberOf(key));
            }

            // Bound after the patch, so the row shows what the target will actually be built with —
            // which is the base with this target's keys on top, exactly as TargetOverrides.Resolve
            // would produce it at import time.
            var row = new TargetOverride(target.Value, BindSettings(merged));

            foreach (var member in marked) {
                row.Mark(member);
            }

            overrides.Add(row);
        }
    }

    static YamlMapping Base(YamlMapping importer) {
        var baseline = new YamlMapping();

        foreach (var (key, value) in importer) {
            if (!string.Equals(key, TargetOverrides.OverridesKey, StringComparison.Ordinal)) {
                baseline.Set(key, value);
            }
        }

        return baseline;
    }

    void WriteAddressable() {
        var address = Addressable.Address.Trim();
        var group = Addressable.Group.Trim();

        var labels = Addressable.Labels
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (address.Length == 0 && group.Length == 0 && labels.Length == 0) {
            // An asset that is not shipped by name has no block at all, which is the ordinary state
            // of most files in a project. Writing an empty one would put three null keys in every
            // sidecar in the project the first time somebody opened it.
            root.Remove("addressable");
            return;
        }

        var node = root["addressable"] as YamlMapping ?? new YamlMapping();

        Put(node, "address", address);
        Put(node, "group", group);

        if (labels.Length == 0) {
            node.Remove("labels");
        } else {
            var sequence = new YamlSequence { Style = YamlCollectionStyle.Flow };

            foreach (var label in labels) {
                sequence.Add(new YamlScalar(label));
            }

            node.Set("labels", sequence);
        }

        root.Set("addressable", node);

        static void Put(YamlMapping node, string key, string value) {
            if (value.Length == 0) {
                node.Remove(key);
            } else {
                node.Set(key, new YamlScalar(value));
            }
        }
    }

    static YamlMapping ReadSidecar(string path) {
        var text = AssetFile.Read(path);

        if (text.Length == 0) {
            return new();
        }

        // ⚠ A sidecar that will not parse opens as an empty one rather than throwing. The file is
        // still on disk and untouched until a save; refusing to open it would leave the one panel
        // that can show what is wrong with it unreachable.
        try {
            return YamlReader.Read(text) as YamlMapping ?? new YamlMapping();
        } catch (YamlParseException) {
            return new();
        }
    }

    static IReadOnlyList<SubAssetEntry> ReadSubAssets(YamlSequence? node) {
        if (node is null) {
            return [];
        }

        List<SubAssetEntry> parts = [];

        for (var index = 0; index < node.Count; index++) {
            if (node[index] is YamlMapping entry) {
                parts.Add(YamlSerializer.Deserialize<SubAssetEntry>(entry));
            }
        }

        return parts;
    }

    /// <summary>The member name a document key came from.</summary>
    /// <remarks>
    ///     The inverse of <see cref="YamlSerializerOptions.KeyFor" />, which lower-cases the first
    ///     letter and nothing else — so upper-casing it back is exact rather than a guess.
    /// </remarks>
    static string MemberOf(string key) =>
        key.Length == 0 ? key : string.Concat(char.ToUpperInvariant(key[0]).ToString(), key.AsSpan(1));
}
