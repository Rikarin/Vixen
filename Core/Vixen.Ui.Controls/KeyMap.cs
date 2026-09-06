// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui;

namespace Vixen.Ui.Controls;

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

/// <summary>Which layer a binding came from, which is what the keybinding editor's last column says.</summary>
public enum BindingSource : byte {
    /// <summary>The application's, declared beside the command.</summary>
    Default,

    /// <summary>The chosen <see cref="KeyMapPreset" />'s.</summary>
    Preset,

    /// <summary>The user's own, and the only one that is saved.</summary>
    User
}

/// <summary>Which chord runs which command.</summary>
/// <remarks>
///     <para>
///         <b>Three layers: the defaults the application ships, a preset, and the overrides the user
///         made.</b> Only the last is saved, which is what makes "we moved Save All to Ctrl+Alt+S in
///         0.3" reach everyone who had not deliberately rebound it — a keymap file holding every
///         binding freezes the defaults at the version the user first ran, and every editor that
///         shipped one has a support burden to prove it.
///     </para>
///     <para>
///         ⚠ <b>The preset is a layer and not an edit, and doc 20's A5 says why that is the work.</b>
///         Choosing Unreal and then rebinding one key has to leave the other two hundred following
///         the preset; a preset applied by copying its bindings into the user's file would make the
///         next preset update reach nobody who had ever rebound anything. See
///         <see cref="KeyMapPreset" />.
///     </para>
///     <para>
///         ⚠ <b>The layers are composed, so a higher one takes a chord off a lower one.</b> A command
///         whose effective chord has already been claimed by a more specific layer ends up unbound
///         rather than sharing it — which is what makes a preset able to move Play onto the palette's
///         key without the preset having to say where the palette went for every command it displaces.
///         Within one layer the order is the command id's, so the composition is the same every time.
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
    readonly Dictionary<string, KeyChord> overrides = new(StringComparer.Ordinal);
    readonly Dictionary<string, KeyChord> bindings = new(StringComparer.Ordinal);
    readonly Dictionary<(KeyChord Chord, string? Context), string> byChord = [];
    readonly HashSet<string> reserved = new(StringComparer.Ordinal);

    KeyMapPreset? preset;

    /// <summary>Raised after anything changes a binding.</summary>
    /// <remarks>The moment to save it, and the moment for a menu to re-label its shortcuts.</remarks>
    public event Action<KeyMap>? Changed;

    /// <summary>Every binding in force, as command id to chord.</summary>
    public IReadOnlyDictionary<string, KeyChord> Bindings => bindings;

    /// <summary>What the application declared, underneath whatever is on top of it.</summary>
    /// <remarks>
    ///     The layer a preset and the user's overrides shadow. Exposed so that "choosing a preset
    ///     does not <i>edit</i> the defaults" is something a test can assert rather than something
    ///     the composition is trusted about.
    /// </remarks>
    public IReadOnlyDictionary<string, KeyChord> Defaults => defaults;

    /// <summary>Which preset is in force, or <see langword="null" /> for the shipped defaults.</summary>
    public KeyMapPreset? Preset => preset;

    /// <summary>The name <see cref="PresetName" /> gives to no preset at all.</summary>
    /// <remarks>
    ///     ⚠ <b>A written-down constant because it is written down in the user's file.</b> A keymap
    ///     that records <c>preset: "Vixen"</c> means "the bindings the application itself declares",
    ///     which is the absence of a layer rather than a layer that happens to be empty — so the
    ///     name has to survive a round trip even though nothing resolves it.
    /// </remarks>
    public const string NoPreset = "Vixen";

    /// <summary>What <see cref="Preset" /> is called, or <see cref="NoPreset" />.</summary>
    public string PresetName => preset?.Name ?? NoPreset;

    /// <summary>Where a preset named in a keymap file is looked up.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A delegate rather than a table here, because a team's own preset is a file rather
    ///         than a constant.</b> Doc 20 asks for three shipped presets and says the format is the
    ///         one the override layer already reads — which means a studio can ship a fourth, and the
    ///         only thing standing between them and it is where the name is resolved.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Left unset it finds nothing, and that is not a stub.</b> The presets an
    ///         application ships are the application's — <c>Vixen.Editor.Ui.KeyMapPresets</c> is the
    ///         editor's three, and <c>EditorShell</c> is what points this at them. A default that
    ///         reached for a particular set would make this class know one application's names.
    ///     </para>
    /// </remarks>
    public Func<string, KeyMapPreset?> PresetSource { get; set; } = _ => null;

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

        if (defaults.TryGetValue(commandId, out var was) && was == chord) {
            return BindResult.Unchanged;
        }

        // ⚠ Asked before anything is written, rather than by composing and seeing what survived. A
        // default that lost the composition to a command it happens to sort before would take that
        // command's key with it and report success — which is the one shape of this bug nothing
        // downstream can notice.
        if (Held(commandId, chord) is not null) {
            return BindResult.Conflict;
        }

        defaults[commandId] = chord;
        Recompose();

        Changed?.Invoke(this);
        return BindResult.Bound;
    }

    /// <summary>Gives a command a chord, as the user asking for it.</summary>
    /// <param name="commandId">The command.</param>
    /// <param name="chord">The chord, or <see cref="KeyChord.None" /> to unbind it.</param>
    /// <param name="replace">Whether to take the chord off whatever has it.</param>
    /// <returns>What happened.</returns>
    public BindResult Bind(string commandId, KeyChord chord, bool replace = false) {
        ArgumentException.ThrowIfNullOrEmpty(commandId);

        if (bindings.GetValueOrDefault(commandId) == chord && Effective(commandId) == chord) {
            return BindResult.Unchanged;
        }

        var displaced = Held(commandId, chord);

        if (displaced is not null) {
            if (!replace) {
                return BindResult.Conflict;
            }

            // ⚠ Written as a deliberate unbind rather than left to the composition to shadow. Where
            // the displaced command's chord came from the *same* layer — the user bound both — the
            // composition would give the key to whichever id sorts first, which is not the one they
            // just pressed the button for. Saying so explicitly is also what makes the unbind
            // survive a save, which is what the user asked for by choosing Replace.
            overrides[displaced] = KeyChord.None;
        }

        overrides[commandId] = chord;
        Recompose();

        Changed?.Invoke(this);
        return displaced is not null ? BindResult.Replaced : BindResult.Bound;
    }

    /// <summary>Which other command has a chord in this command's context, if any.</summary>
    string? Held(string commandId, KeyChord chord) {
        if (!chord.IsBound) {
            return null;
        }

        var holder = byChord.GetValueOrDefault((chord, Scope(commandId)));

        return holder is null || string.Equals(holder, commandId, StringComparison.Ordinal) ? null : holder;
    }

    /// <summary>Drops the user's override for a command, so it follows the preset or the default again.</summary>
    /// <param name="commandId">The command.</param>
    /// <returns>Whether it had one.</returns>
    /// <remarks>
    ///     The keybinding editor's per-row reset, and the reason it is not "bind it back to the
    ///     default": a row put back to the default by <i>writing</i> the default would keep following
    ///     the user's file rather than the layer underneath it, so the next release's change to that
    ///     binding — or the next preset — would not reach it.
    /// </remarks>
    public bool ResetBinding(string commandId) {
        ArgumentException.ThrowIfNullOrEmpty(commandId);

        if (!overrides.Remove(commandId)) {
            return false;
        }

        Recompose();
        Changed?.Invoke(this);

        return true;
    }

    /// <summary>Puts a preset in force, keeping the user's own overrides on top of it.</summary>
    /// <param name="chosen">The preset, or <see langword="null" /> for the shipped defaults.</param>
    /// <remarks>
    ///     ⚠ <b>The user's overrides are kept, deliberately.</b> Somebody who moved one key and then
    ///     tried the Unreal preset has not asked for their one deliberate choice to be thrown away —
    ///     and <see cref="Reset" /> is the verb that does mean that.
    /// </remarks>
    public void UsePreset(KeyMapPreset? chosen) {
        if (ReferenceEquals(preset, chosen)) {
            return;
        }

        preset = chosen;
        Recompose();

        Changed?.Invoke(this);
    }

    /// <summary>Puts a preset in force by name, through <see cref="PresetSource" />.</summary>
    /// <param name="name">Its name. <see cref="NoPreset" /> means no preset.</param>
    /// <returns>Whether a preset by that name was found, which <c>Vixen</c> answers true to.</returns>
    public bool UsePreset(string? name) {
        if (string.IsNullOrEmpty(name) || string.Equals(name, NoPreset, StringComparison.Ordinal)) {
            UsePreset((KeyMapPreset?)null);
            return true;
        }

        if (PresetSource(name) is not { } found) {
            return false;
        }

        UsePreset(found);
        return true;
    }

    /// <summary>What has a chord in a context, if anything.</summary>
    /// <param name="chord">The chord.</param>
    /// <param name="context">Which context has the focus, or <see langword="null" /> for none.</param>
    /// <returns>The command's id, or <c>null</c>.</returns>
    /// <remarks>
    ///     ⚠ <b>The context's own binding first and the global one second.</b> That order is what
    ///     makes a panel able to claim a key the editor already uses without taking it away from
    ///     everywhere else — and the fallback is what stops every panel having to re-declare Ctrl+S.
    ///     <para>
    ///         ⚠ <b>Unless the global one is <see cref="Reserve" />d</b>, in which case it wins
    ///         wherever the focus is.
    ///     </para>
    /// </remarks>
    public string? CommandFor(KeyChord chord, string? context = null) {
        if (!chord.IsBound) {
            return null;
        }

        var global = byChord.GetValueOrDefault((chord, null));

        // ⚠ Before the context lookup rather than after. A reserved command is one whose key gets
        // pressed without looking at what has the focus, and for such a key "usually works" is the
        // same as "does not work".
        if (global is not null && reserved.Contains(global)) {
            return global;
        }

        if (context is not null && byChord.TryGetValue((chord, context), out var scoped)) {
            return scoped;
        }

        return global;
    }

    /// <summary>Makes a command's key beat any context that binds the same chord.</summary>
    /// <param name="commandId">The command.</param>
    /// <remarks>
    ///     <para>
    ///         <b>For the few keys that have to mean one thing everywhere.</b> Focus Selection is the
    ///         case that motivated it: pressed several times a minute in every mode, and blockout's
    ///         Fill Hole had claimed <c>F</c> for its own context — so the key stopped working in the
    ///         mode where somebody is doing the most looking around.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The <i>command</i> is reserved, not the chord.</b> Somebody who rebinds Focus
    ///         Selection to <c>G</c> reserves <c>G</c> and releases <c>F</c> by the same act. A
    ///         reserved chord would have frozen the original key and gone on protecting whatever
    ///         later moved onto it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Sparingly, and never for a chord a text field needs.</b> Every reservation is a
    ///         key no panel can repurpose, and a long list of them is a keymap with a second set of
    ///         rules — which is the thing contexts exist to avoid.
    ///     </para>
    /// </remarks>
    public void Reserve(string commandId) {
        ArgumentException.ThrowIfNullOrEmpty(commandId);
        reserved.Add(commandId);
    }

    /// <summary>Whether a command's key beats any context.</summary>
    /// <param name="commandId">The command.</param>
    /// <returns>Whether it is reserved.</returns>
    public bool IsReserved(string commandId) {
        ArgumentException.ThrowIfNullOrEmpty(commandId);
        return reserved.Contains(commandId);
    }

    /// <summary>What a command is bound to.</summary>
    /// <param name="commandId">The command.</param>
    /// <returns>Its chord, or <see cref="KeyChord.None" />.</returns>
    public KeyChord ChordFor(string commandId) {
        ArgumentNullException.ThrowIfNull(commandId);
        return bindings.GetValueOrDefault(commandId);
    }

    /// <summary>Which layer a command's binding came from.</summary>
    /// <param name="commandId">The command.</param>
    /// <returns>The layer.</returns>
    /// <remarks>
    ///     The keybinding editor's Source column, and the one piece of information that makes the
    ///     panel readable: "Default" and "Unreal" and "yours" are three different answers to "why is
    ///     this key what it is", and a grid that showed only the chord leaves the user to guess.
    /// </remarks>
    public BindingSource SourceOf(string commandId) {
        ArgumentNullException.ThrowIfNull(commandId);

        if (overrides.ContainsKey(commandId)) {
            return BindingSource.User;
        }

        return preset is not null && preset.Bindings.ContainsKey(commandId)
            ? BindingSource.Preset
            : BindingSource.Default;
    }

    /// <summary>Whether the user has bound a command themselves.</summary>
    /// <param name="commandId">The command.</param>
    /// <returns>Whether they have.</returns>
    /// <remarks>
    ///     ⚠ <b>Against the layer underneath rather than against the shipped default.</b> With the
    ///     Unreal preset in force, a command sitting where Unreal puts it is not customised — the
    ///     user chose the preset, not the key — and a panel that marked two hundred rows as edited
    ///     the moment a preset was chosen would make "what have I changed" unanswerable.
    /// </remarks>
    public bool IsCustomised(string commandId) {
        ArgumentNullException.ThrowIfNull(commandId);
        return overrides.ContainsKey(commandId);
    }

    /// <summary>Every command that any layer has an opinion about, in id order.</summary>
    /// <remarks>What the keybinding editor lists beside the registry's own commands.</remarks>
    public IEnumerable<string> Ids() =>
        overrides.Keys
            .Concat(preset?.Bindings.Keys ?? [])
            .Concat(defaults.Keys)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);

    /// <summary>Throws the user's overrides away, leaving the preset and the defaults.</summary>
    /// <remarks>
    ///     ⚠ <b>The preset survives.</b> "Reset" means "undo what I changed", and somebody who chose
    ///     the Unreal preset and then made a mess of three keys is asking for Unreal back rather than
    ///     for a keymap they have never used. <see cref="UsePreset(string?)" /> with
    ///     <see cref="NoPreset" /> is the other verb.
    /// </remarks>
    public void Reset() {
        overrides.Clear();
        Recompose();

        Changed?.Invoke(this);
    }

    /// <summary>Just the bindings the user moved themselves, as command id to chord.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The layer that gets saved, and the only one that does.</b> A keymap file holding
    ///         every binding freezes the defaults at the version the user first ran, so "we moved
    ///         Save All in 0.3" reaches nobody — every editor that shipped one has a support burden
    ///         to prove it. A command deliberately unbound is in here as
    ///         <see cref="KeyChord.None" /> rather than absent, because absent means "use the layer
    ///         underneath" and the user said the opposite.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Exposed rather than written out here, because the format is not this
    ///         assembly's.</b> See <see cref="KeyMapPreset" />: persisting a keymap is a choice about
    ///         where an application keeps preferences, and the editor makes it in
    ///         <c>Vixen.Editor.Ui.KeyMapYaml</c>. This property and <see cref="Restore" /> are the
    ///         two halves of the round trip, and nothing between them names a file format.
    ///     </para>
    /// </remarks>
    public IReadOnlyDictionary<string, KeyChord> Overrides => overrides;

    /// <summary>Puts a saved preset choice and set of overrides back, replacing whatever is there.</summary>
    /// <param name="presetName">
    ///     The preset's name, or <see langword="null" /> for the application's own defaults. One
    ///     <see cref="PresetSource" /> does not know is dropped, which is what happens to a team
    ///     preset on a machine that has not got it.
    /// </param>
    /// <param name="bindings">The user's own bindings, as <see cref="Overrides" /> reported them.</param>
    /// <remarks>
    ///     ⚠ <b>One <see cref="Changed" /> for the whole restore, not one per binding.</b> A keymap
    ///     is loaded while the menus are already listening, and raising the event per entry would
    ///     re-label every shortcut in the application a few hundred times on startup.
    /// </remarks>
    public void Restore(string? presetName, IEnumerable<KeyValuePair<string, KeyChord>> bindings) {
        ArgumentNullException.ThrowIfNull(bindings);

        overrides.Clear();
        preset = string.IsNullOrEmpty(presetName) ? null : PresetSource(presetName);

        foreach (var (commandId, chord) in bindings) {
            overrides[commandId] = chord;
        }

        Recompose();
        Changed?.Invoke(this);
    }

    /// <summary>What a command's chord would be if nothing else had claimed it.</summary>
    KeyChord Effective(string commandId) {
        if (overrides.TryGetValue(commandId, out var mine)) {
            return mine;
        }

        if (preset is not null && preset.Bindings.TryGetValue(commandId, out var chosen)) {
            return chosen;
        }

        return defaults.GetValueOrDefault(commandId);
    }

    /// <summary>Rebuilds the composed map from the three layers.</summary>
    /// <remarks>
    ///     ⚠ <b>Most specific layer first, and within a layer in id order.</b> That is what makes a
    ///     preset able to take a chord from a default without saying so, and what makes the answer
    ///     the same on every machine — a composition that walked a dictionary's own order would put
    ///     a contested chord on whichever command the hash happened to reach first.
    /// </remarks>
    void Recompose() {
        bindings.Clear();
        byChord.Clear();

        Claim(overrides.Keys);

        if (preset is not null) {
            Claim(preset.Bindings.Keys);
        }

        Claim(defaults.Keys);
    }

    void Claim(IEnumerable<string> ids) {
        foreach (var commandId in ids.Order(StringComparer.Ordinal)) {
            if (bindings.ContainsKey(commandId)) {
                continue;
            }

            var chord = Effective(commandId);

            if (!chord.IsBound) {
                continue;
            }

            var context = Scope(commandId);

            // A chord a more specific layer already claimed leaves this command unbound, which is
            // what "the preset moved Play onto the palette's key" has to mean.
            if (byChord.ContainsKey((chord, context))) {
                continue;
            }

            bindings[commandId] = chord;
            byChord[(chord, context)] = commandId;
        }
    }

    /// <summary>Which context a command's binding is filed under.</summary>
    string? Scope(string commandId) => ContextOf?.Invoke(commandId);
}
