// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Editor.Core;

/// <summary>Everything the editor has been told about, without a record of who told it.</summary>
/// <remarks>
///     <para>
///         <b>Doc 36 § D2.</b> Three producers write here — a source generator at compile time, a
///         plugin's <c>Activate</c> at load time, and a project's own <c>Editor/</c> scripts when a
///         project opens — and a consumer cannot tell them apart. That is the whole property: a
///         fourth producer is a new producer and not a new consumer, and a built-in feature that
///         registers here is a feature proving the same door a third party comes through.
///     </para>
///     <para>
///         ⚠ <b>A typed multimap rather than a method per kind.</b> Adding a contribution kind is
///         declaring a record in the assembly that owns it, not writing a registry and a consumer
///         and a plugin method. The alternative was tried in miniature and is what F10 reports:
///         two registries for one idea, and a reconciliation layer over them.
///     </para>
///     <para>
///         ⚠ <b>It is not the only registry, and pretending otherwise would be the same mistake.</b>
///         Commands, panels, layouts and modes belong to <c>EditorShell</c>; drawers belong to
///         <c>DrawerRegistry</c>; described types belong to <c>InspectorRegistry</c>. Each is the one
///         place its thing is declared, and copying them here would make a plugin's drawer land in
///         whichever of two registries the inspector was not reading. What comes here is what had no
///         owner: the asset kinds Create ▸ offers, custom inspectors, scene-view tools, gizmos,
///         settings pages, previews.
///     </para>
///     <para>
///         <b>Adding hands back the removal.</b> A plugin's registration scope keeps it, a test
///         disposes it, and nothing has to name what it added a second time in order to take it out.
///     </para>
/// </remarks>
public interface IEditorRegistry {
    /// <summary>Everything contributed of one kind, oldest first.</summary>
    /// <typeparam name="T">The contribution type.</typeparam>
    /// <returns>The contributions, or empty.</returns>
    IReadOnlyList<T> All<T>() where T : class;

    /// <summary>Adds a contribution.</summary>
    /// <typeparam name="T">The contribution type, which is how consumers find it.</typeparam>
    /// <param name="contribution">The contribution.</param>
    /// <returns>A scope that removes it again.</returns>
    IDisposable Add<T>(T contribution) where T : class;

    /// <summary>Raised after any contribution of a kind is added or removed, with that kind.</summary>
    /// <remarks>
    ///     ⚠ <b>What a menu bar, a mode bar or a tool strip listens to.</b> A plugin activating three
    ///     seconds after start-up is always after the presenter built its view, so a consumer that
    ///     read the registry once at construction is one a plugin cannot reach.
    /// </remarks>
    event Action<Type>? Changed;
}

/// <summary>The contribution registry.</summary>
/// <remarks>
///     ⚠ <b>Locked, because the generated producer runs on whatever thread first touched the
///     assembly.</b> A module initializer is not on the frame thread and has no way to get onto it;
///     everything else here is, so contention is a start-up cost and nothing more.
/// </remarks>
public sealed class EditorRegistry : IEditorRegistry {
    readonly Dictionary<Type, List<object>> contributions = [];
    readonly Lock gate = new();

    /// <summary>The one the editor reads unless it was handed another.</summary>
    /// <remarks>
    ///     A static because the generated registrations have nowhere else to go — a module
    ///     initializer has no editor to be handed. A test that wants isolation makes its own and
    ///     passes it in, which is <c>DrawerRegistry</c>'s arrangement and for the same reason: two
    ///     tests registering for one type must not be unable to run in the same process.
    /// </remarks>
    public static EditorRegistry Default { get; } = new();

    /// <inheritdoc />
    public event Action<Type>? Changed;

    /// <inheritdoc />
    public IReadOnlyList<T> All<T>() where T : class {
        lock (gate) {
            if (!contributions.TryGetValue(typeof(T), out var found) || found.Count == 0) {
                return [];
            }

            // A copy, because a consumer that adds while it enumerates is an ordinary thing — a
            // custom inspector whose construction registers a drawer — and the alternative is an
            // InvalidOperationException from inside somebody else's Activate.
            var typed = new T[found.Count];

            for (var index = 0; index < found.Count; index++) {
                typed[index] = (T) found[index];
            }

            return typed;
        }
    }

    /// <inheritdoc />
    public IDisposable Add<T>(T contribution) where T : class {
        ArgumentNullException.ThrowIfNull(contribution);

        lock (gate) {
            if (!contributions.TryGetValue(typeof(T), out var found)) {
                found = [];
                contributions[typeof(T)] = found;
            }

            found.Add(contribution);
        }

        Changed?.Invoke(typeof(T));

        return new Removal(this, typeof(T), contribution);
    }

    /// <summary>Forgets everything. For tests.</summary>
    public void Clear() {
        Type[] kinds;

        lock (gate) {
            kinds = [.. contributions.Keys];
            contributions.Clear();
        }

        foreach (var kind in kinds) {
            Changed?.Invoke(kind);
        }
    }

    void Remove(Type kind, object contribution) {
        bool removed;

        lock (gate) {
            removed = contributions.TryGetValue(kind, out var found) && found.Remove(contribution);
        }

        if (removed) {
            Changed?.Invoke(kind);
        }
    }

    sealed class Removal(EditorRegistry registry, Type kind, object contribution) : IDisposable {
        bool done;

        public void Dispose() {
            if (!done) {
                done = true;
                registry.Remove(kind, contribution);
            }
        }
    }
}
