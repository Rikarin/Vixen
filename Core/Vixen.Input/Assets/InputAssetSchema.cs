// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Input;

/// <summary>What an action does with the value its bindings produce.</summary>
/// <remarks>
///     Unity's three, because the distinction is real and every engine that shipped only "button"
///     and "axis" grew the third one later under a worse name. The difference is entirely about
///     <em>when the action reports</em>, not about the shape of the value.
/// </remarks>
public enum InputActionType : byte {
    /// <summary>A discrete press. Performs once when it crosses the press point, cancels on release.</summary>
    Button = 0,

    /// <summary>
    ///     A continuous value with a rest position. Starts when it leaves the rest position, performs
    ///     on every change while away from it, and cancels when it returns.
    /// </summary>
    /// <remarks>
    ///     One binding wins at a time — the most actuated — so a stick and WASD bound to the same
    ///     action do not add up to a magnitude of two when both are pushed.
    /// </remarks>
    Value = 1,

    /// <summary>
    ///     Every change from every binding, with no disambiguation and no rest position.
    /// </summary>
    /// <remarks>
    ///     What a rebinding screen, a recorder and a "any button pressed" prompt want, and what a
    ///     gameplay action almost never wants.
    /// </remarks>
    PassThrough = 2
}

/// <summary>The shape of the value an action produces.</summary>
/// <remarks>
///     Declared rather than inferred from the bindings, because it is what the generated accessor's
///     return type is built from and because an action with no bindings yet still has a shape. A
///     binding that cannot produce the declared shape is a load-time diagnostic, not a silent zero.
/// </remarks>
public enum InputControlType : byte {
    /// <summary>Pressed or not. Read as <see cref="bool" />, or as <see cref="float" /> for its
    /// analogue amount.</summary>
    Button = 0,

    /// <summary>One signed axis. Read as <see cref="float" />.</summary>
    Axis = 1,

    /// <summary>Two axes. Read as <c>Vector2</c>.</summary>
    Vector2 = 2
}

/// <summary>How several controls combine into one binding.</summary>
public enum InputCompositeKind : byte {
    /// <summary>Not a composite: one control, one value.</summary>
    None = 0,

    /// <summary>Two buttons into a signed axis: <c>negative</c> and <c>positive</c>.</summary>
    Axis1D = 1,

    /// <summary>Four buttons into a vector: <c>up</c>, <c>down</c>, <c>left</c>, <c>right</c>.</summary>
    Vector2 = 2,

    /// <summary>
    ///     A <c>button</c> that only counts while every <c>modifier</c> part is held.
    /// </summary>
    /// <remarks>
    ///     One composite rather than Unity's two, because "with one modifier" and "with two
    ///     modifiers" are the same rule with a different part count, and a shortcut with three
    ///     modifiers is a thing an editor binds.
    /// </remarks>
    ButtonWithModifiers = 3
}

/// <summary>A family of physical device.</summary>
public enum InputDeviceKind : byte {
    /// <summary>No device.</summary>
    None = 0,

    /// <summary>A keyboard.</summary>
    Keyboard = 1,

    /// <summary>A mouse, a trackpad, or anything else that moves a pointer.</summary>
    Mouse = 2,

    /// <summary>A gamepad.</summary>
    Gamepad = 3,

    /// <summary>A touchscreen.</summary>
    Touch = 4
}

/// <summary>One part of a composite binding.</summary>
/// <param name="Part">Which slot it fills — <c>up</c>, <c>negative</c>, <c>modifier</c>, <c>button</c>.</param>
/// <param name="Path">The control it reads, in <see cref="InputControlPath" /> syntax.</param>
/// <param name="Processors">The processors for this part alone, or <see langword="null" />.</param>
public sealed record InputBindingPartData(string Part, string Path, string? Processors = null);

/// <summary>One binding of an action, as the document holds it.</summary>
/// <param name="Path">
///     The control, in <see cref="InputControlPath" /> syntax. Empty for a composite, whose value
///     comes from its <paramref name="Parts" />.
/// </param>
/// <param name="Name">A display name for a rebinding screen, or <see langword="null" />.</param>
/// <param name="Groups">
///     The control schemes this binding belongs to, semicolon-separated. <see langword="null" /> or
///     empty means every scheme.
/// </param>
/// <param name="Interactions">The interactions, semicolon-separated, or <see langword="null" />.</param>
/// <param name="Processors">The processors, semicolon-separated, or <see langword="null" />.</param>
/// <param name="Composite">Which composite this is, or <see cref="InputCompositeKind.None" />.</param>
/// <param name="Parts">The composite's parts, empty for a plain binding.</param>
/// <remarks>
///     Composite parts are <em>nested</em> here rather than flattened into the binding list behind an
///     <c>isPartOfComposite</c> flag, which is how Unity's asset stores them. The flat form makes
///     every consumer reconstruct the tree, and makes a hand-edited file that reorders two lines
///     mean something different — which is exactly the sort of breakage a text asset in version
///     control must not have.
/// </remarks>
public sealed record InputBindingData(
    string Path,
    string? Name = null,
    string? Groups = null,
    string? Interactions = null,
    string? Processors = null,
    InputCompositeKind Composite = InputCompositeKind.None,
    IReadOnlyList<InputBindingPartData>? Parts = null
) {
    /// <summary>The composite's parts, or an empty list.</summary>
    public IReadOnlyList<InputBindingPartData> Parts { get; init; } = Parts ?? [];
}

/// <summary>One action, as the document holds it.</summary>
/// <param name="Name">Its name, unique within its map.</param>
/// <param name="Type">What it does with its value.</param>
/// <param name="ControlType">The shape of its value.</param>
/// <param name="Bindings">Its bindings.</param>
public sealed record InputActionData(
    string Name,
    InputActionType Type = InputActionType.Button,
    InputControlType ControlType = InputControlType.Button,
    IReadOnlyList<InputBindingData>? Bindings = null
) {
    /// <summary>Its bindings, or an empty list.</summary>
    public IReadOnlyList<InputBindingData> Bindings { get; init; } = Bindings ?? [];
}

/// <summary>One action map, as the document holds it.</summary>
/// <param name="Name">Its name, unique within the asset.</param>
/// <param name="Actions">Its actions.</param>
public sealed record InputActionMapData(string Name, IReadOnlyList<InputActionData>? Actions = null) {
    /// <summary>Its actions, or an empty list.</summary>
    public IReadOnlyList<InputActionData> Actions { get; init; } = Actions ?? [];
}

/// <summary>One device a control scheme wants.</summary>
/// <param name="Device">Which family.</param>
/// <param name="Optional">Whether the scheme is usable without it.</param>
public sealed record InputDeviceRequirementData(InputDeviceKind Device, bool Optional = false);

/// <summary>One control scheme, as the document holds it.</summary>
/// <param name="Name">Its name, which is what a binding's <c>groups</c> names.</param>
/// <param name="Devices">What it needs.</param>
public sealed record InputControlSchemeData(string Name, IReadOnlyList<InputDeviceRequirementData>? Devices = null) {
    /// <summary>What it needs, or an empty list.</summary>
    public IReadOnlyList<InputDeviceRequirementData> Devices { get; init; } = Devices ?? [];
}

/// <summary>A whole <c>.vxinput</c> document.</summary>
/// <param name="Name">
///     The asset's name, which is the name of the generated accessor class. Defaults to the file's.
/// </param>
/// <param name="Maps">The action maps.</param>
/// <param name="ControlSchemes">The control schemes.</param>
/// <param name="Version">The schema version this document was written against.</param>
public sealed record InputActionAssetData(
    string Name,
    IReadOnlyList<InputActionMapData>? Maps = null,
    IReadOnlyList<InputControlSchemeData>? ControlSchemes = null,
    int Version = InputActionAssetData.CurrentVersion
) {
    /// <summary>The schema version this build writes.</summary>
    public const int CurrentVersion = 1;

    /// <summary>The action maps, or an empty list.</summary>
    public IReadOnlyList<InputActionMapData> Maps { get; init; } = Maps ?? [];

    /// <summary>The control schemes, or an empty list.</summary>
    public IReadOnlyList<InputControlSchemeData> ControlSchemes { get; init; } = ControlSchemes ?? [];
}

/// <summary>Something wrong with a document, and where.</summary>
/// <param name="Message">What is wrong, in the terms the author wrote it in.</param>
/// <param name="Line">The one-based line.</param>
/// <param name="Column">The one-based column.</param>
public readonly record struct InputAssetDiagnostic(string Message, int Line, int Column) {
    /// <inheritdoc />
    public override string ToString() => $"({Line},{Column}): {Message}";
}
