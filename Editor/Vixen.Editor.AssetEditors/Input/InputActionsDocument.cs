// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Editor.Core;
using Vixen.Input;

namespace Vixen.Editor.AssetEditors.Input;

/// <summary>A <c>.vxinput</c>, open for editing.</summary>
/// <remarks>
///     <para>
///         <b>Doc 11 calls this "doc 11's own gap": the whole action model exists and no editor
///         does.</b> The reader and the writer are <c>Vixen.Input</c>'s and are shared with the source
///         generator, so this document does not know the file format at all — it holds an
///         <see cref="InputActionAssetData" /> and asks <c>InputActionAssetWriter</c> to spell it.
///         That is what makes the file an editor writes and the file the compiler reads the same
///         file by construction.
///     </para>
///     <para>
///         ⚠ <b>Every edit replaces the whole document, and that is the natural shape here rather
///         than a shortcut.</b> <see cref="InputActionAssetData" /> and everything under it are
///         immutable records: there is no "set this binding's path", only a new record with a new
///         path. So an edit <i>is</i> a replacement, one command holds the before and the after, and
///         undo is an assignment. The tree is small — an action map is tens of records — and the
///         alternative would be a mutable editor-side mirror of a model that deliberately is not one.
///     </para>
///     <para>
///         ⚠ <b>The diagnostics from reading are kept and shown.</b> A <c>.vxinput</c> with one
///         unusable binding still yields every other action — that is the reader's own contract — so
///         opening one has to say what it dropped rather than silently presenting a document that
///         will not round-trip.
///     </para>
/// </remarks>
public sealed class InputActionsDocument : EditorDocument {
    /// <summary>What an action asset is written as.</summary>
    public const string Extension = ".vxinput";

    /// <summary>Where the file is, absolute.</summary>
    public string AssetPath { get; }

    /// <summary>The document.</summary>
    public InputActionAssetData Actions { get; private set; }

    /// <summary>What reading the file had to say.</summary>
    public IReadOnlyList<InputAssetDiagnostic> LoadDiagnostics { get; }

    /// <summary>Raised after anything changes the document.</summary>
    public event Action<InputActionsDocument>? Changed;

    /// <summary>Opens an action asset.</summary>
    /// <param name="project">The project it belongs to.</param>
    /// <param name="asset">Its identity.</param>
    /// <param name="path">Where the file is, absolute.</param>
    public InputActionsDocument(EditorProject project, AssetId asset, string path)
        : base(project, asset, Path.GetFileName(path)) {
        ArgumentException.ThrowIfNullOrEmpty(path);

        AssetPath = path;

        var name = Path.GetFileNameWithoutExtension(path);
        var text = AssetFile.Read(path);

        if (text.Trim().Length == 0) {
            // ⚠ A new asset comes with one map and one action rather than nothing. An empty
            // `.vxinput` generates an accessor class with no members, which is a compile error at
            // every use site of a file somebody has only just created — and "Player/Move" is what
            // the first action is called in every project anybody has ever started.
            Actions = new(
                name,
                [new("Player", [new("Move", InputActionType.Value, InputControlType.Vector2)])],
                [new("Keyboard", [new(InputDeviceKind.Keyboard), new(InputDeviceKind.Mouse, Optional: true)])]
            );

            LoadDiagnostics = [];

            return;
        }

        var result = InputActionAssetReader.Read(text, name);

        Actions = result.Asset ?? new(name);
        LoadDiagnostics = result.Diagnostics;
    }

    /// <summary>Replaces the document, undoably.</summary>
    /// <param name="name">What the undo history calls the edit.</param>
    /// <param name="asset">The new document.</param>
    public void Replace(string name, InputActionAssetData asset) {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(asset);

        var previous = Actions;

        Stack.Execute(
            new DelegateCommand(
                name,
                _ => {
                    Actions = asset;
                    Changed?.Invoke(this);
                },
                _ => {
                    Actions = previous;
                    Changed?.Invoke(this);
                }
            )
        );
    }

    /// <summary>Adds an action map, undoably.</summary>
    /// <param name="map">What it is called.</param>
    public void AddMap(string map) {
        ArgumentException.ThrowIfNullOrEmpty(map);

        Replace("Add Action Map", Actions with { Maps = [.. Actions.Maps, new(Unique(map, Actions.Maps.Select(entry => entry.Name)))] });
    }

    /// <summary>Removes an action map, undoably.</summary>
    /// <param name="map">Which one, by name.</param>
    public void RemoveMap(string map) =>
        Replace(
            "Remove Action Map",
            Actions with { Maps = [.. Actions.Maps.Where(entry => !string.Equals(entry.Name, map, StringComparison.Ordinal))] }
        );

    /// <summary>Adds an action to a map, undoably.</summary>
    /// <param name="map">Which map, by name.</param>
    /// <param name="action">What the action is called.</param>
    public void AddAction(string map, string action) {
        ArgumentException.ThrowIfNullOrEmpty(action);

        Edit(
            "Add Action",
            map,
            found => found with {
                Actions = [.. found.Actions, new(Unique(action, found.Actions.Select(entry => entry.Name)))]
            }
        );
    }

    /// <summary>Removes an action, undoably.</summary>
    /// <param name="map">Which map, by name.</param>
    /// <param name="action">Which action, by name.</param>
    public void RemoveAction(string map, string action) =>
        Edit(
            "Remove Action",
            map,
            found => found with {
                Actions = [.. found.Actions.Where(entry => !string.Equals(entry.Name, action, StringComparison.Ordinal))]
            }
        );

    /// <summary>Replaces one action, undoably.</summary>
    /// <param name="name">What the undo history calls the edit.</param>
    /// <param name="map">Which map, by name.</param>
    /// <param name="action">Which action, by name.</param>
    /// <param name="change">What to replace it with.</param>
    public void EditAction(string name, string map, string action, Func<InputActionData, InputActionData> change) {
        ArgumentNullException.ThrowIfNull(change);

        Edit(
            name,
            map,
            found => found with {
                Actions = [.. found.Actions.Select(entry =>
                    string.Equals(entry.Name, action, StringComparison.Ordinal) ? change(entry) : entry)]
            }
        );
    }

    /// <summary>Adds a binding to an action, undoably.</summary>
    /// <param name="map">Which map, by name.</param>
    /// <param name="action">Which action, by name.</param>
    /// <param name="binding">The binding.</param>
    public void AddBinding(string map, string action, InputBindingData binding) {
        ArgumentNullException.ThrowIfNull(binding);

        EditAction("Add Binding", map, action, found => found with { Bindings = [.. found.Bindings, binding] });
    }

    /// <summary>Removes a binding by its position, undoably.</summary>
    /// <param name="map">Which map, by name.</param>
    /// <param name="action">Which action, by name.</param>
    /// <param name="index">Which binding.</param>
    public void RemoveBinding(string map, string action, int index) =>
        EditAction(
            "Remove Binding",
            map,
            action,
            found => found with { Bindings = [.. found.Bindings.Where((_, position) => position != index)] }
        );

    /// <summary>Replaces one binding by its position, undoably.</summary>
    /// <param name="name">What the undo history calls the edit.</param>
    /// <param name="map">Which map, by name.</param>
    /// <param name="action">Which action, by name.</param>
    /// <param name="index">Which binding.</param>
    /// <param name="binding">The replacement.</param>
    public void SetBinding(string name, string map, string action, int index, InputBindingData binding) {
        ArgumentNullException.ThrowIfNull(binding);

        EditAction(
            name,
            map,
            action,
            found => found with {
                Bindings = [.. found.Bindings.Select((entry, position) => position == index ? binding : entry)]
            }
        );
    }

    /// <summary>Adds a control scheme, undoably.</summary>
    /// <param name="scheme">What it is called.</param>
    public void AddScheme(string scheme) {
        ArgumentException.ThrowIfNullOrEmpty(scheme);

        Replace(
            "Add Control Scheme",
            Actions with {
                ControlSchemes = [
                    .. Actions.ControlSchemes,
                    new(Unique(scheme, Actions.ControlSchemes.Select(entry => entry.Name)))
                ]
            }
        );
    }

    /// <summary>Removes a control scheme, undoably.</summary>
    /// <param name="scheme">Which one, by name.</param>
    public void RemoveScheme(string scheme) =>
        Replace(
            "Remove Control Scheme",
            Actions with {
                ControlSchemes = [
                    .. Actions.ControlSchemes.Where(entry => !string.Equals(entry.Name, scheme, StringComparison.Ordinal))
                ]
            }
        );

    /// <summary>Replaces one control scheme, undoably.</summary>
    /// <param name="scheme">Which one, by name.</param>
    /// <param name="change">What to replace it with.</param>
    public void EditScheme(string scheme, Func<InputControlSchemeData, InputControlSchemeData> change) {
        ArgumentNullException.ThrowIfNull(change);

        Replace(
            "Edit Control Scheme",
            Actions with {
                ControlSchemes = [.. Actions.ControlSchemes.Select(entry =>
                    string.Equals(entry.Name, scheme, StringComparison.Ordinal) ? change(entry) : entry)]
            }
        );
    }

    void Edit(string name, string map, Func<InputActionMapData, InputActionMapData> change) =>
        Replace(
            name,
            Actions with {
                Maps = [.. Actions.Maps.Select(entry =>
                    string.Equals(entry.Name, map, StringComparison.Ordinal) ? change(entry) : entry)]
            }
        );

    /// <summary>A name nothing in a set already has, by appending a number.</summary>
    /// <remarks>
    ///     ⚠ <b>Rather than refusing a duplicate.</b> A map's actions are addressed by name and two
    ///     called <c>Fire</c> make a generated accessor that will not compile — but the moment
    ///     somebody presses Add is the wrong moment to say so, because they have not typed the name
    ///     yet. Giving them <c>Fire 2</c> to rename is the version of this that does not interrupt.
    /// </remarks>
    internal static string Unique(string wanted, IEnumerable<string> taken) {
        var used = new HashSet<string>(taken, StringComparer.Ordinal);

        if (!used.Contains(wanted)) {
            return wanted;
        }

        for (var index = 2; ; index++) {
            var candidate = $"{wanted} {index}";

            if (!used.Contains(candidate)) {
                return candidate;
            }
        }
    }

    /// <summary>The document as it would be written, without writing it.</summary>
    /// <returns>The text.</returns>
    public string ToText() => InputActionAssetWriter.Write(Actions);

    /// <inheritdoc />
    protected override void SaveCore() => AssetFile.Write(AssetPath, ToText());
}
