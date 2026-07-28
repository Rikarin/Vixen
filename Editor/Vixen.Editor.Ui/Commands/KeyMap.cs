// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Yaml;

namespace Vixen.Editor.Ui;

/// <summary>What happens when a chord and a command disagree about who has it.</summary>
public enum BindResult : byte {
    /// <summary>The command has the chord.</summary>
    Bound,

    /// <summary>Another command already had it, and still does.</summary>
    Conflict,

    /// <summary>Another command had it and has been unbound.</summary>
    Replaced,

    /// <summary>Nothing changed, because it was already bound that way.</summary>
    Unchanged
}

/// <summary>Which chord runs which command.</summary>
/// <remarks>
///     <para>
///         <b>Two layers: the defaults the application ships and the overrides the user made.</b>
///         Only the second is saved, which is what makes "we moved Save All to Ctrl+Alt+S in 0.3"
///         reach everyone who had not deliberately rebound it — a keymap file holding every binding
///         freezes the defaults at the version the user first ran, and every editor that shipped one
///         has a support burden to prove it.
///     </para>
///     <para>
///         ⚠ <b>Conflicts are detected, not resolved.</b> A chord belongs to at most one command, so
///         binding an occupied chord fails and says who has it — the caller decides whether to ask
///         the user and rebind with <see cref="Bind" />'s <c>replace</c>. Two commands sharing a
///         chord would be an editor where the same keystroke does different things depending on
///         which handler happened to be registered first.
///     </para>
///     <para>
///         <b>Bindings survive the commands they name.</b> A chord bound to a plugin's command is
///         kept while the plugin is unloaded, so reinstalling it restores the user's shortcut
///         instead of quietly dropping it — the same reason a saved dock layout keeps a panel it
///         cannot currently show.
///     </para>
/// </remarks>
public sealed class KeyMap {
    readonly Dictionary<string, KeyChord> defaults = new(StringComparer.Ordinal);
    readonly Dictionary<string, KeyChord> bindings = new(StringComparer.Ordinal);
    readonly Dictionary<KeyChord, string> byChord = [];

    /// <summary>Raised after anything changes a binding.</summary>
    /// <remarks>The moment to save it, and the moment for a menu to re-label its shortcuts.</remarks>
    public event Action<KeyMap>? Changed;

    /// <summary>Every binding, as command id to chord.</summary>
    public IReadOnlyDictionary<string, KeyChord> Bindings => bindings;

    /// <summary>Declares what a command is bound to out of the box.</summary>
    /// <param name="commandId">The command.</param>
    /// <param name="chord">Its chord.</param>
    /// <returns>What happened.</returns>
    /// <remarks>
    ///     Called while the application registers its commands, before any user keymap is loaded.
    ///     A default that collides with another default is a bug in the application rather than
    ///     something to ask the user about, so it comes back as <see cref="BindResult.Conflict" />
    ///     and a test asserts there are none.
    /// </remarks>
    public BindResult SetDefault(string commandId, KeyChord chord) {
        ArgumentException.ThrowIfNullOrEmpty(commandId);

        var result = Apply(commandId, chord, replace: false);

        if (result is BindResult.Bound or BindResult.Unchanged) {
            defaults[commandId] = chord;
        }

        return result;
    }

    /// <summary>Gives a command a chord, as the user asking for it.</summary>
    /// <param name="commandId">The command.</param>
    /// <param name="chord">The chord, or <see cref="KeyChord.None" /> to unbind it.</param>
    /// <param name="replace">Whether to take the chord off whatever has it.</param>
    /// <returns>What happened.</returns>
    public BindResult Bind(string commandId, KeyChord chord, bool replace = false) {
        ArgumentException.ThrowIfNullOrEmpty(commandId);
        return Apply(commandId, chord, replace);
    }

    /// <summary>What has a chord, if anything.</summary>
    /// <param name="chord">The chord.</param>
    /// <returns>The command's id, or <c>null</c>.</returns>
    public string? CommandFor(KeyChord chord) => chord.IsBound ? byChord.GetValueOrDefault(chord) : null;

    /// <summary>What a command is bound to.</summary>
    /// <param name="commandId">The command.</param>
    /// <returns>Its chord, or <see cref="KeyChord.None" />.</returns>
    public KeyChord ChordFor(string commandId) {
        ArgumentNullException.ThrowIfNull(commandId);
        return bindings.GetValueOrDefault(commandId);
    }

    /// <summary>Whether a command is bound to something other than its default.</summary>
    /// <param name="commandId">The command.</param>
    /// <returns>Whether it is.</returns>
    public bool IsCustomised(string commandId) {
        ArgumentNullException.ThrowIfNull(commandId);
        return bindings.GetValueOrDefault(commandId) != defaults.GetValueOrDefault(commandId);
    }

    /// <summary>Puts every binding back to the shipped default.</summary>
    public void Reset() {
        bindings.Clear();
        byChord.Clear();

        foreach (var (commandId, chord) in defaults) {
            if (chord.IsBound) {
                bindings[commandId] = chord;
                byChord[chord] = commandId;
            }
        }

        Changed?.Invoke(this);
    }

    /// <summary>The user's overrides, as YAML.</summary>
    /// <returns>The text.</returns>
    /// <remarks>
    ///     Only what differs from the defaults, including a command deliberately unbound — which is
    ///     written as an empty chord rather than omitted, because omitting it would mean "use the
    ///     default" and the user said the opposite.
    /// </remarks>
    public string Save() {
        var overrides = new YamlMapping();

        foreach (var commandId in Ids()) {
            var chord = bindings.GetValueOrDefault(commandId);
            var shipped = defaults.GetValueOrDefault(commandId);

            if (chord != shipped) {
                overrides.Set(commandId, new YamlScalar(chord.Save(), YamlScalarStyle.DoubleQuoted));
            }
        }

        return YamlWriter.Write(new YamlMapping().Set("bindings", overrides));
    }

    /// <summary>Applies a user keymap over the defaults.</summary>
    /// <param name="yaml">What <see cref="Save" /> wrote.</param>
    /// <remarks>
    ///     ⚠ <b>Never throws on a keymap that has gone stale.</b> A chord that will not parse, or a
    ///     chord two commands both claim, is dropped — the alternative is an editor that will not
    ///     start because somebody mistyped a line in a preferences file, and the binding they lose
    ///     is the one they can see is missing.
    /// </remarks>
    public void Load(string yaml) {
        ArgumentNullException.ThrowIfNull(yaml);
        Reset();

        if (YamlReader.Read(yaml) is not YamlMapping document || document["bindings"] is not YamlMapping overrides) {
            return;
        }

        foreach (var (commandId, node) in overrides) {
            if (node is not YamlScalar scalar) {
                continue;
            }

            if (string.IsNullOrEmpty(scalar.Value)) {
                Apply(commandId, KeyChord.None, replace: false);
                continue;
            }

            if (KeyChord.TryParse(scalar.Value, out var chord)) {
                // Replacing, because the file is the user's last word: a chord they moved to a new
                // command has to leave the one that shipped with it, and a file loaded over
                // defaults would otherwise fail on every binding they had moved.
                Apply(commandId, chord, replace: true);
            }
        }

        Changed?.Invoke(this);
    }

    /// <summary>Every command that has ever been bound here, in id order.</summary>
    IEnumerable<string> Ids() => bindings.Keys.Concat(defaults.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);

    BindResult Apply(string commandId, KeyChord chord, bool replace) {
        var existing = bindings.GetValueOrDefault(commandId);

        if (existing == chord) {
            return BindResult.Unchanged;
        }

        var displaced = chord.IsBound ? byChord.GetValueOrDefault(chord) : null;

        if (displaced is not null && !replace) {
            return BindResult.Conflict;
        }

        if (existing.IsBound) {
            byChord.Remove(existing);
        }

        if (displaced is not null) {
            bindings.Remove(displaced);
        }

        if (chord.IsBound) {
            bindings[commandId] = chord;
            byChord[chord] = commandId;
        } else {
            bindings.Remove(commandId);
        }

        Changed?.Invoke(this);
        return displaced is not null ? BindResult.Replaced : BindResult.Bound;
    }
}
