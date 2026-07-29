// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Core;
using Vixen.Input;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Vixen.Ui.Controls.Advanced;

namespace Vixen.Editor.AssetEditors.Input;

/// <summary>What a row of the action tree is.</summary>
/// <param name="Map">The map it belongs to, by name.</param>
/// <param name="Action">The action, by name, or empty for the map's own row.</param>
/// <param name="Binding">Which binding, or −1 for the action's own row.</param>
/// <param name="Scheme">The control scheme, by name, or empty.</param>
/// <remarks>
///     ⚠ <b>Its members are nullable and every reader has to say so.</b> <c>default(InputRow)</c> is
///     what a tree with nothing selected produces — <c>TreeNode.Tag</c> is null then — and a struct's
///     <c>default</c> runs no constructor and no property initializer, so a <c>string</c> member is
///     genuinely null however the declaration is written. Coalescing in an accessor <i>looks</i> like
///     it fixes this and does not; the readers use <c>is { Length: &gt; 0 }</c>, which is correct for
///     both null and empty and is the only shape that stays correct.
/// </remarks>
public readonly record struct InputRow(string? Map, string? Action = null, int Binding = -1, string? Scheme = null);

/// <summary>An action asset, open for editing: maps, actions, bindings and control schemes.</summary>
/// <remarks>
///     <para>
///         Doc 11's row is "action-map editor", and what that means concretely is the four things a
///         person does to one of these files: name an action and say what shape its value is, hang
///         bindings off it, group those bindings into control schemes, and — the one that cannot be
///         done by typing — press the key you want.
///     </para>
///     <para>
///         ⚠ <b>Listening is a mode rather than a modal, which is <c>KeyBindingsView</c>'s argument
///         restated.</b> A dialog that swallows keystrokes to record them cannot be driven by the
///         automation harness. So the button latches, the panel takes keys on the capture leg ahead
///         of the command dispatcher, and Escape is the one control it will not record because
///         Escape is how listening ends.
///     </para>
///     <para>
///         ⚠ <b>It records a <i>control</i>, not a chord.</b> The editor's own keymap binds
///         <c>Ctrl+S</c>; a game's action binds <c>&lt;Keyboard&gt;/s</c>, and the modifier is a
///         composite with a <c>modifier</c> part. Writing a chord into a binding path would produce a
///         path <c>InputControlPath.TryParse</c> refuses, which is a binding that silently never
///         fires.
///     </para>
/// </remarks>
public sealed class InputActionsView : Control {
    InputActionsDocument? document;
    InputRow selected;

    /// <inheritdoc />
    protected override string TagName => "input-editor";

    /// <inheritdoc />
    protected override bool AcceptsFocus => true;

    /// <summary>The maps, actions and bindings.</summary>
    public TreeView Tree { get; private set; } = null!;

    /// <summary>The column beside it.</summary>
    public UiElement Side { get; private set; } = null!;

    /// <summary>The selected row's fields.</summary>
    public UiElement Fields { get; private set; } = null!;

    /// <summary>What reading the file had to say.</summary>
    public UiElement Diagnostics { get; private set; } = null!;

    /// <summary>Adds a map.</summary>
    public Button AddMap { get; private set; } = null!;

    /// <summary>Adds an action to the selected map.</summary>
    public Button AddAction { get; private set; } = null!;

    /// <summary>Adds a binding to the selected action.</summary>
    public Button AddBinding { get; private set; } = null!;

    /// <summary>Adds a control scheme.</summary>
    public Button AddScheme { get; private set; } = null!;

    /// <summary>Removes whatever is selected.</summary>
    public Button Delete { get; private set; } = null!;

    /// <summary>Records the next control pressed into the selected binding.</summary>
    public ToggleButton Listen { get; private set; } = null!;

    /// <summary>Whether the panel is waiting for a control to be pressed.</summary>
    public bool IsListening => Listen.IsChecked;

    /// <summary>What is selected.</summary>
    public InputRow Selected => selected;

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        var bar = Part("input-bar");

        AddMap = Push(bar, "Add Map");
        AddAction = Push(bar, "Add Action");
        AddBinding = Push(bar, "Add Binding");
        AddScheme = Push(bar, "Add Scheme");
        Delete = Push(bar, "Remove");

        Listen = bar.Add<ToggleButton>();
        Listen.Label = "Listen";

        Tree = Part<TreeView>();
        Tree.MultiSelect = false;

        Side = Part("input-side");
        Fields = Side.Add("input-fields");
        Diagnostics = Side.Add("analysis-list");

        Tree.SelectionChanged += _ => {
            selected = Tree.Selection.FirstOrDefault()?.Tag is InputRow row ? row : default;

            // Moving the selection while listening would record into a binding nobody was looking
            // at, which is the one way this mode can do something surprising.
            Listen.IsChecked = false;
            Restate();
        };

        // ⚠ On the capture leg and ahead of the dispatcher, for `KeyBindingsView`'s reason: without
        // it, pressing S to bind S would run whatever S is bound to in the editor.
        AddHandler<KeyEvent>(
            static (element, args) => ((InputActionsView) element).Keyed(args),
            RoutingStrategy.Capture,
            handledEventsToo: true
        );

        AddHandler<ClickEvent>(static (element, args) => ((InputActionsView) element).Chosen(args));
    }

    static Button Push(UiElement bar, string label) {
        var button = bar.Add<Button>();

        button.Label = label;
        button.Size = ControlSize.Small;
        button.Variant = ControlVariant.Subtle;

        return button;
    }

    /// <summary>Shows an action asset.</summary>
    /// <param name="asset">The document.</param>
    public void Show(InputActionsDocument asset) {
        ArgumentNullException.ThrowIfNull(asset);

        if (document is { } previous) {
            previous.Changed -= Reload;
        }

        document = asset;
        asset.Changed += Reload;

        Reload(asset);
    }

    /// <summary>Rebuilds the tree from the document.</summary>
    /// <param name="asset">The document.</param>
    public void Reload(InputActionsDocument asset) {
        ArgumentNullException.ThrowIfNull(asset);

        while (Tree.Root.Children.Count > 0) {
            Tree.Root.Remove(Tree.Root.Children[^1]);
        }

        foreach (var map in asset.Actions.Maps) {
            var node = Tree.Root.Add(map.Name, new InputRow(map.Name));
            Tree.Expand(node);

            foreach (var action in map.Actions) {
                var child = Tree.Root.Children[^1].Add(
                    $"{action.Name}  ·  {action.Type} / {action.ControlType}",
                    new InputRow(map.Name, action.Name)
                );

                Tree.Expand(child);

                for (var index = 0; index < action.Bindings.Count; index++) {
                    var binding = action.Bindings[index];

                    child.Add(Describe(binding), new InputRow(map.Name, action.Name, index));
                }
            }
        }

        if (asset.Actions.ControlSchemes.Count > 0) {
            var schemes = Tree.Root.Add("Control Schemes", new InputRow(string.Empty));
            Tree.Expand(schemes);

            foreach (var scheme in asset.Actions.ControlSchemes) {
                schemes.Add(
                    $"{scheme.Name}  ·  {string.Join(", ", scheme.Devices.Select(device => device.Device))}",
                    new InputRow(string.Empty, Scheme: scheme.Name)
                );
            }
        }

        Tree.Refresh();
        Restate();
        Report(asset);
    }

    /// <summary>What one binding's row says.</summary>
    static string Describe(InputBindingData binding) =>
        binding.Composite == InputCompositeKind.None
            ? binding.Path.Length > 0 ? binding.Path : "(no control)"
            : $"{binding.Composite}: {string.Join(", ", binding.Parts.Select(part => $"{part.Part}={part.Path}"))}";

    void Report(InputActionsDocument asset) {
        while (Diagnostics.Children.Count > 0) {
            Diagnostics.Children[^1].Remove();
        }

        foreach (var diagnostic in asset.LoadDiagnostics) {
            var row = Diagnostics.Add("analysis-row");
            row.AddClass("error");

            row.Add("analysis-stage").Text = $"{diagnostic.Line}:{diagnostic.Column}";
            row.Add("analysis-message").Text = diagnostic.Message;
        }
    }

    /// <summary>Rebuilds the fields for whatever is selected.</summary>
    public void Restate() {
        while (Fields.Children.Count > 0) {
            Fields.Children[^1].Remove();
        }

        if (document is not { } asset) {
            return;
        }

        if (selected.Scheme is { Length: > 0 }) {
            SchemeFields(asset, selected.Scheme!);

            return;
        }

        if (selected.Map is not { Length: > 0 }) {
            Fields.Add("text").Text = "Select a map, an action or a binding.";

            return;
        }

        if (asset.Actions.Maps.FirstOrDefault(map => string.Equals(map.Name, selected.Map, StringComparison.Ordinal))
            is not { } found) {
            return;
        }

        if (selected.Action is not { Length: > 0 }) {
            Fields.Add("input-title").Text = "Action Map";
            Line("Name", found.Name, value => Rename(asset, found.Name, value));

            return;
        }

        if (found.Actions.FirstOrDefault(entry => string.Equals(entry.Name, selected.Action, StringComparison.Ordinal))
            is not { } action) {
            return;
        }

        if (selected.Binding < 0) {
            ActionFields(asset, found.Name, action);

            return;
        }

        if (selected.Binding < action.Bindings.Count) {
            BindingFields(asset, found.Name, action, selected.Binding);
        }
    }

    void ActionFields(InputActionsDocument asset, string map, InputActionData action) {
        Fields.Add("input-title").Text = "Action";

        Line(
            "Name",
            action.Name,
            value => asset.EditAction("Rename Action", map, action.Name, found => found with { Name = value })
        );

        Choice(
            "Type",
            action.Type,
            value => asset.EditAction("Set Action Type", map, action.Name, found => found with { Type = value })
        );

        Choice(
            "Value",
            action.ControlType,
            value => asset.EditAction("Set Control Type", map, action.Name, found => found with { ControlType = value })
        );
    }

    void BindingFields(InputActionsDocument asset, string map, InputActionData action, int index) {
        var binding = action.Bindings[index];

        Fields.Add("input-title").Text = "Binding";

        Choice(
            "Composite",
            binding.Composite,
            value => asset.SetBinding(
                "Set Composite",
                map,
                action.Name,
                index,
                value == InputCompositeKind.None
                    ? binding with { Composite = value, Parts = [] }
                    : binding with { Composite = value, Path = string.Empty, Parts = Parts(value) }
            )
        );

        if (binding.Composite == InputCompositeKind.None) {
            Line(
                "Path",
                binding.Path,
                value => asset.SetBinding("Set Binding Path", map, action.Name, index, binding with { Path = value })
            );

            Resolved(binding.Path);
        } else {
            for (var part = 0; part < binding.Parts.Count; part++) {
                var position = part;
                var entry = binding.Parts[part];

                Line(
                    entry.Part,
                    entry.Path,
                    value => asset.SetBinding(
                        "Set Binding Path",
                        map,
                        action.Name,
                        index,
                        binding with {
                            Parts = [.. binding.Parts.Select((existing, slot) =>
                                slot == position ? existing with { Path = value } : existing)]
                        }
                    )
                );
            }
        }

        Line(
            "Groups",
            binding.Groups ?? string.Empty,
            value => asset.SetBinding(
                "Set Binding Groups",
                map,
                action.Name,
                index,
                binding with { Groups = value.Length > 0 ? value : null }
            )
        );

        Line(
            "Interactions",
            binding.Interactions ?? string.Empty,
            value => asset.SetBinding(
                "Set Interactions",
                map,
                action.Name,
                index,
                binding with { Interactions = value.Length > 0 ? value : null }
            )
        );

        Line(
            "Processors",
            binding.Processors ?? string.Empty,
            value => asset.SetBinding(
                "Set Processors",
                map,
                action.Name,
                index,
                binding with { Processors = value.Length > 0 ? value : null }
            )
        );
    }

    /// <summary>Says whether a path names a control this build knows, which is the check nothing else does.</summary>
    /// <remarks>
    ///     ⚠ <b>A path that does not parse is a binding that never fires and reports nothing at
    ///     runtime.</b> The loader's diagnostics catch it at load; showing it beside the field is what
    ///     catches it while somebody is still typing.
    /// </remarks>
    void Resolved(string path) {
        var row = Fields.Add("fact-row");
        row.Add("fact-name").Text = "Resolves";

        var value = row.Add("fact-value").Add("text");

        if (InputControlPath.TryParse(path, out var control)) {
            value.Text = InputControlPath.Describe(control);
        } else {
            value.Text = "no control of that name";
            row.AddClass("error");
        }
    }

    /// <summary>The parts a composite starts with, named as the runtime's reader expects.</summary>
    static InputBindingPartData[] Parts(InputCompositeKind composite) => composite switch {
        InputCompositeKind.Axis1D => [new("negative", string.Empty), new("positive", string.Empty)],
        InputCompositeKind.Vector2 => [
            new("up", string.Empty), new("down", string.Empty),
            new("left", string.Empty), new("right", string.Empty)
        ],
        InputCompositeKind.ButtonWithModifiers => [new("modifier", string.Empty), new("button", string.Empty)],
        _ => []
    };

    void SchemeFields(InputActionsDocument asset, string scheme) {
        if (asset.Actions.ControlSchemes.FirstOrDefault(entry => string.Equals(entry.Name, scheme, StringComparison.Ordinal))
            is not { } found) {
            return;
        }

        Fields.Add("input-title").Text = "Control Scheme";
        Line("Name", found.Name, value => asset.EditScheme(found.Name, entry => entry with { Name = value }));

        foreach (var device in Enum.GetValues<InputDeviceKind>()) {
            if (device == InputDeviceKind.None) {
                continue;
            }

            var row = Fields.Add("fact-row");
            row.Add("fact-name").Text = device.ToString();

            var toggle = row.Add("fact-value").Add<CheckBox>();
            toggle.IsChecked = found.Devices.Any(entry => entry.Device == device);

            toggle.CheckedChanged += (_, on) => asset.EditScheme(
                found.Name,
                entry => entry with {
                    Devices = on
                        ? [.. entry.Devices, new InputDeviceRequirementData(device)]
                        : [.. entry.Devices.Where(requirement => requirement.Device != device)]
                }
            );
        }
    }

    static void Rename(InputActionsDocument asset, string map, string name) {
        if (name.Length == 0 || string.Equals(map, name, StringComparison.Ordinal)) {
            return;
        }

        asset.Replace(
            "Rename Action Map",
            asset.Actions with {
                Maps = [.. asset.Actions.Maps.Select(entry =>
                    string.Equals(entry.Name, map, StringComparison.Ordinal) ? entry with { Name = name } : entry)]
            }
        );
    }

    void Line(string label, string value, Action<string> write) {
        var row = Fields.Add("fact-row");
        row.Add("fact-name").Text = label;

        var box = row.Add("fact-value").Add<TextBox>();

        box.Value = value;
        box.ValueChanged += (_, text) => write(text ?? string.Empty);
    }

    void Choice<T>(string label, T value, Action<T> write) where T : struct, Enum {
        var row = Fields.Add("fact-row");
        row.Add("fact-name").Text = label;

        var select = row.Add("fact-value").Add<Select>();

        foreach (var option in Enum.GetValues<T>()) {
            select.AddOption(option.ToString());
        }

        select.Value = value.ToString();

        select.SelectionChanged += (_, chosen) => {
            if (Enum.TryParse<T>(chosen, out var parsed)) {
                write(parsed);
            }
        };
    }

    void Keyed(KeyEvent args) {
        if (!IsListening || args.Action != KeyAction.Pressed) {
            return;
        }

        // Escape leaves the mode rather than being recorded, for `KeyBindingsView`'s reason: a
        // capture with no way out is one whoever started it cannot stop.
        if (args.Key == InputKey.Escape) {
            Listen.IsChecked = false;
            args.Handled = true;

            return;
        }

        Record(InputControlPath.Format(InputControl.Key(args.Key)));
        args.Handled = true;
    }

    /// <summary>Writes a recorded path into whatever is selected, and leaves the mode.</summary>
    /// <param name="path">The control's path.</param>
    /// <remarks>
    ///     ⚠ <b>Into a composite's <i>first empty</i> part when one is selected.</b> Binding a WASD
    ///     vector is four presses, and a mode that made you click each part between them would be
    ///     four times as many clicks as keys.
    /// </remarks>
    public void Record(string path) {
        ArgumentException.ThrowIfNullOrEmpty(path);

        if (document is not { } asset || selected.Binding < 0) {
            return;
        }

        if (asset.Actions.Maps.FirstOrDefault(map => string.Equals(map.Name, selected.Map, StringComparison.Ordinal))
                is not { } found
            || found.Actions.FirstOrDefault(entry => string.Equals(entry.Name, selected.Action, StringComparison.Ordinal))
                is not { } action
            || selected.Binding >= action.Bindings.Count) {
            return;
        }

        var binding = action.Bindings[selected.Binding];

        if (binding.Composite == InputCompositeKind.None) {
            asset.SetBinding("Bind Control", found.Name, action.Name, selected.Binding, binding with { Path = path });
            Listen.IsChecked = false;

            return;
        }

        var slot = binding.Parts.ToList().FindIndex(part => part.Path.Length == 0);

        if (slot < 0) {
            Listen.IsChecked = false;

            return;
        }

        asset.SetBinding(
            "Bind Control",
            found.Name,
            action.Name,
            selected.Binding,
            binding with {
                Parts = [.. binding.Parts.Select((part, index) => index == slot ? part with { Path = path } : part)]
            }
        );

        // Stays on while parts remain, which is what makes WASD four presses rather than four
        // clicks and four presses.
        Listen.IsChecked = binding.Parts.Count(part => part.Path.Length == 0) > 1;
    }

    void Chosen(ClickEvent args) {
        if (document is not { } asset) {
            return;
        }

        for (var element = args.Source; element is not null; element = element.Parent) {
            if (ReferenceEquals(element, AddMap)) {
                asset.AddMap("New Map");
                args.Handled = true;

                return;
            }

            if (ReferenceEquals(element, AddAction) && selected.Map is { Length: > 0 } target) {
                asset.AddAction(target, "New Action");
                args.Handled = true;

                return;
            }

            if (ReferenceEquals(element, AddBinding) && selected.Action is { Length: > 0 }) {
                asset.AddBinding(selected.Map!, selected.Action!, new(string.Empty));
                args.Handled = true;

                return;
            }

            if (ReferenceEquals(element, AddScheme)) {
                asset.AddScheme("New Scheme");
                args.Handled = true;

                return;
            }

            if (ReferenceEquals(element, Delete)) {
                RemoveSelection(asset);
                args.Handled = true;

                return;
            }
        }
    }

    void RemoveSelection(InputActionsDocument asset) {
        var map = selected.Map ?? string.Empty;
        var action = selected.Action ?? string.Empty;

        if (selected.Scheme is { Length: > 0 } scheme) {
            asset.RemoveScheme(scheme);
        } else if (selected.Binding >= 0) {
            asset.RemoveBinding(map, action, selected.Binding);
        } else if (action.Length > 0) {
            asset.RemoveAction(map, action);
        } else if (map.Length > 0) {
            asset.RemoveMap(map);
        }
    }
}

/// <summary>Opens an input action asset.</summary>
public sealed class InputActionsEditorFactory : IAssetEditorFactory {
    /// <inheritdoc />
    public string Name => "Input Actions";

    /// <inheritdoc />
    public IReadOnlyList<string> Extensions { get; } = [InputActionsDocument.Extension];

    /// <inheritdoc />
    public EditorDocument Open(AssetEditorRequest request) {
        ArgumentNullException.ThrowIfNull(request);

        return new InputActionsDocument(request.Project, request.Asset, request.Path);
    }

    /// <inheritdoc />
    public UiElement CreateView(EditorDocument document, UiElement panel) {
        ArgumentNullException.ThrowIfNull(panel);

        var view = panel.Add<InputActionsView>();
        view.Show((InputActionsDocument) document);

        return view;
    }
}
