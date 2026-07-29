// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Reflection;
using Vixen.Core.Yaml;

namespace Vixen.Editor.Core;

/// <summary>The settings assets under <c>ProjectSettings/</c>: one YAML file per settings type.</summary>
/// <remarks>
///     <para>
///         <b>They are assets, not a configuration file.</b> A settings type is an ordinary
///         <c>[DataContract]</c> type, so it is described by the same generator, read by the same YAML
///         binder and drawn by the same inspector as a material — which is why "add a project setting"
///         is declaring a type and not also writing a dialog.
///     </para>
///     <para>
///         <b>One file per type, named after the contract's alias</b> rather than after the C# type,
///         so renaming the type does not orphan the file. <c>ProjectSettings/</c> is committed, so the
///         files are diffed and merged by hand often enough that the dialect's byte fidelity matters
///         here more than anywhere: reading and rewriting a file nobody edited must produce no diff.
///     </para>
///     <para>
///         <b>A missing file is not an error.</b> It means the defaults, which is what a project that
///         has never opened that settings page has. Nothing is written until something asks for it,
///         so a scan of a fresh checkout does not produce a directory of files full of defaults.
///     </para>
/// </remarks>
public sealed class ProjectSettingsStore {
    /// <summary>What a settings asset is called on disk.</summary>
    public const string Extension = ".vxsettings";

    readonly HashSet<Type> changed = [];
    readonly Dictionary<Type, object> loaded = [];
    readonly ProjectPaths paths;
    readonly List<string> unknownKeys = [];

    /// <summary>
    ///     Keys found in a settings file that no member claimed, as <c>file: path.to.key</c>.
    /// </summary>
    /// <remarks>
    ///     An unknown key is ignored rather than refused — a project opened in an older editor after
    ///     someone added a setting has to load — and reported here, because dropping it silently is
    ///     the other failure. A settings page shows this as a warning; a build server treats it as one.
    /// </remarks>
    public IReadOnlyList<string> UnknownKeys => unknownKeys;

    /// <summary>Opens the store over a project's <c>ProjectSettings/</c> directory.</summary>
    /// <param name="paths">Where the project is.</param>
    public ProjectSettingsStore(ProjectPaths paths) {
        ArgumentNullException.ThrowIfNull(paths);
        this.paths = paths;
    }

    /// <summary>Where a settings type is stored.</summary>
    /// <typeparam name="T">The settings type.</typeparam>
    /// <returns>The absolute path of its file, whether or not it exists.</returns>
    public string FileFor<T>() where T : class, new() => FileFor(typeof(T));

    /// <summary>Where a settings type is stored.</summary>
    /// <param name="type">The settings type.</param>
    /// <returns>The absolute path of its file, whether or not it exists.</returns>
    public string FileFor(Type type) {
        ArgumentNullException.ThrowIfNull(type);
        return Path.Combine(paths.ProjectSettings, AliasOf(type) + Extension);
    }

    /// <summary>The project's settings of a type, read from disk the first time and cached after.</summary>
    /// <typeparam name="T">The settings type.</typeparam>
    /// <returns>The settings. Every caller gets the same instance.</returns>
    /// <remarks>
    ///     One instance, so an inspector editing it and a subsystem reading it are looking at the same
    ///     object. A file that cannot be read is a <see cref="YamlException" /> and not a silent
    ///     fall-back to defaults: settings that quietly reset themselves because of a stray tab
    ///     character are how a project's build configuration disappears.
    /// </remarks>
    public T Get<T>() where T : class, new() {
        if (loaded.TryGetValue(typeof(T), out var existing)) {
            return (T)existing;
        }

        var file = FileFor<T>();
        var value = File.Exists(file) ? Read<T>(file) : new T();
        loaded[typeof(T)] = value;
        return value;
    }

    /// <summary>Records that a settings object has been edited and needs writing.</summary>
    /// <typeparam name="T">The settings type.</typeparam>
    /// <remarks>
    ///     Explicit because a settings type is a plain data object with no change notification of its
    ///     own — which is the price of it being the same kind of type as everything else the
    ///     inspector draws.
    /// </remarks>
    public void MarkChanged<T>() where T : class, new() => changed.Add(typeof(T));

    /// <summary>Whether anything has been marked changed and not yet written.</summary>
    public bool HasUnsavedChanges => changed.Count > 0;

    /// <summary>Puts one settings type back to its declared defaults, in memory.</summary>
    /// <typeparam name="T">The settings type.</typeparam>
    /// <remarks>
    ///     <para>
    ///         What a settings window's per-category Reset does. Marked changed rather than written,
    ///         because a reset is an edit like any other and the window's Apply is what commits it —
    ///         which is also what makes Revert able to take it back.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A new instance, so anything still holding the old one is detached.</b> The same
    ///         bargain <see cref="Reload" /> makes and for the same reason: the object it was reading
    ///         no longer describes the project. Callers that keep a settings object across a reset —
    ///         a subsystem rather than a drawer — should ask for it again.
    ///     </para>
    /// </remarks>
    public void Reset<T>() where T : class, new() {
        loaded[typeof(T)] = new T();
        changed.Add(typeof(T));
    }

    /// <summary>Writes one settings type to disk.</summary>
    /// <typeparam name="T">The settings type.</typeparam>
    /// <remarks>Writes whether or not it was marked changed; <see cref="SaveAll" /> is the one that filters.</remarks>
    public void Save<T>() where T : class, new() {
        var value = Get<T>();
        Directory.CreateDirectory(paths.ProjectSettings);
        File.WriteAllText(FileFor<T>(), YamlSerializer.ToYaml(value));
        changed.Remove(typeof(T));
    }

    /// <summary>Writes everything that has been marked changed.</summary>
    /// <returns>How many files were written.</returns>
    public int SaveAll() {
        if (changed.Count == 0) {
            return 0;
        }

        Directory.CreateDirectory(paths.ProjectSettings);
        var written = 0;

        foreach (var type in changed) {
            File.WriteAllText(FileFor(type), YamlWriter.Write(YamlSerializer.Serialize(loaded[type], type)));
            written++;
        }

        changed.Clear();
        return written;
    }

    /// <summary>Forgets everything cached, so the next read comes from disk.</summary>
    /// <remarks>
    ///     What a project does when the files changed underneath it — a branch switch, a merge. Any
    ///     instance a caller is still holding is now detached, which is the honest outcome: the object
    ///     it was reading no longer describes the project.
    /// </remarks>
    public void Reload() {
        loaded.Clear();
        changed.Clear();
        unknownKeys.Clear();
    }

    static string AliasOf(Type type) {
        if (TypeRegistry.TryGet(type, out var descriptor)) {
            return descriptor.Alias;
        }

        throw new InvalidOperationException(
            $"{type.Name} has no descriptor, so it cannot be stored as a settings asset. Give it "
            + "[DataContract] and make sure the declaring assembly runs the reflection generator."
        );
    }

    T Read<T>(string file) where T : class, new() {
        var name = Path.GetFileName(file);
        var options = new YamlSerializerOptions(OnUnknownKey: key => unknownKeys.Add($"{name}: {key}"));
        return YamlSerializer.Parse<T>(File.ReadAllText(file), options);
    }
}
