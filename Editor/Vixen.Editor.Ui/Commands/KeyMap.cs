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
///         ⚠ <b>Conflicts are detected, not resolved — <i>within a context</i>.</b> A chord belongs
///         to at most one command per context, so binding an occupied chord fails and says who has
///         it; the caller decides whether to ask the user and rebind with <see cref="Bind" />'s
///         <c>replace</c>. Two commands sharing a chord in one context would be an editor where the
///         same keystroke does different things depending on which handler happened to be registered
///         first.
///     </para>
///     <para>
///         ⚠ <b>Across contexts, sharing a chord is the point rather than the hazard.</b> Delete in
///         the outliner and Delete in the content browser are two commands and one key, and an
///         editor that made the second of them pick another key would be one nobody's fingers can
///         use. <see cref="ContextOf" /> is how this class finds out which context a command belongs
///         to — the registry answers it, so the declaration lives on the command and not in a second
///         table here — and a binding made in a context shadows the global one for as long as that
///         context has the focus.
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
    readonly Dictionary<(KeyChord Chord, string? Context), string> byChord = [];

    /// <summary>Raised after anything changes a binding.</summary>
    /// <remarks>The moment to save it, and the moment for a menu to re-label its shortcuts.</remarks>
    public event Action<KeyMap>? Changed;

    /// <summary>Every binding, as command id to chord.</summary>
    public IReadOnlyDictionary<string, KeyChord> Bindings => bindings;

    /// <summary>Which context a command belongs to, asked whenever a chord is claimed or resolved.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The registry answers this, so <see cref="EditorCommand.Context" /> stays the one
    ///         declaration.</b> A second table here would be a second place to keep in step, and the
    ///         way that goes wrong is a plugin's command whose context the keymap never heard about
    ///         binding over the outliner's Delete.
    ///     </para>
    ///     <para>
    ///         Left unset, every command is global and this class behaves exactly as it did before
    ///         contexts existed — which is what a keymap with no shell around it, and every test that
    ///         predates this, relies on.
    ///     </para>
    /// </remarks>
    public Func<string, string?>? ContextOf { get; set; }

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

    /// <summary>What has a chord in a context, if anything.</summary>
    /// <param name="chord">The chord.</param>
    /// <param name="context">Which context has the focus, or <see langword="null" /> for none.</param>
    /// <returns>The command's id, or <c>null</c>.</returns>
    /// <remarks>
    ///     ⚠ <b>The context's own binding first and the global one second.</b> That order is what
    ///     makes a panel able to claim a key the editor already uses without taking it away from
    ///     everywhere else — and the fallback is what stops every panel having to re-declare Ctrl+S.
    /// </remarks>
    public string? CommandFor(KeyChord chord, string? context = null) {
        if (!chord.IsBound) {
            return null;
        }

        if (context is not null && byChord.TryGetValue((chord, context), out var scoped)) {
            return scoped;
        }

        return byChord.GetValueOrDefault((chord, null));
    }

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
                byChord[(chord, Scope(commandId))] = commandId;
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

    /// <summary>Which context a command's binding is filed under.</summary>
    string? Scope(string commandId) => ContextOf?.Invoke(commandId);

    BindResult Apply(string commandId, KeyChord chord, bool replace) {
        var existing = bindings.GetValueOrDefault(commandId);

        if (existing == chord) {
            return BindResult.Unchanged;
        }

        var context = Scope(commandId);
        var displaced = chord.IsBound ? byChord.GetValueOrDefault((chord, context)) : null;

        if (displaced is not null && !replace) {
            return BindResult.Conflict;
        }

        if (existing.IsBound) {
            byChord.Remove((existing, context));
        }

        if (displaced is not null) {
            bindings.Remove(displaced);
        }

        if (chord.IsBound) {
            bindings[commandId] = chord;
            byChord[(chord, context)] = commandId;
        } else {
            bindings.Remove(commandId);
        }

        Changed?.Invoke(this);
        return displaced is not null ? BindResult.Replaced : BindResult.Bound;
    }
}
