// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Vixen.Ui.Controls.Advanced;

namespace Vixen.Editor.Inspector.Drawers;

/// <summary>A colour swatch that opens a picker.</summary>
/// <remarks>
///     <see cref="ColorUsageAttribute" /> is what decides whether the picker offers an intensity
///     slider and an alpha band, because a colour's type does not say which of those are meaningful
///     — an albedo tint and an emissive tint are both a <c>Color4</c> and want different editors.
/// </remarks>
public sealed class ColorDrawer : PropertyDrawer<Color4, ColorPicker> {
    /// <inheritdoc />
    protected override ColorPicker Build(InspectorField field, UiElement parent) {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(parent);

        var usage = field.Member.Color ?? new ColorUsage(false, true);

        var picker = parent.Add<ColorPicker>();
        picker.AllowAlpha = usage.ShowAlpha;
        picker.AllowHdr = usage.Hdr;
        picker.Disabled = !field.CanWrite;

        picker.ValueChanged += (control, colour) => field.Write(usage.Hdr ? control.HdrValue : colour);

        return picker;
    }

    /// <inheritdoc />
    protected override void Show(InspectorField field, ColorPicker editor, Color4 value, bool isMixed) {
        ArgumentNullException.ThrowIfNull(editor);

        // A mixed colour shows as transparent black and says so through the row, because a picker
        // has no third state and any colour it showed would be one of the objects' actual values
        // presented as though it were all of them.
        editor.Value = isMixed ? Color4.Transparent : value;

        if (isMixed) {
            editor.AddClass("mixed");
        } else {
            editor.RemoveClass("mixed");
        }
    }
}

/// <summary>The same swatch and picker, over a colour with no alpha.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>A separate drawer rather than a coercion in <see cref="ColorDrawer" />, because a
///         drawer is chosen by the member's exact type.</b> <c>Color3</c> had none at all, so every
///         member of that type fell through to the read-only last resort — which for a light's colour
///         meant the one property people open a light to change was a line of grey text. It is the
///         type the renderer uses for anything with no meaningful alpha, so <c>Light.Colour</c> is
///         not the only member this reaches.
///     </para>
///     <para>
///         ⚠ <b>Alpha is refused rather than defaulted.</b> <see cref="ColorUsage" /> can still ask
///         for HDR — an emissive tint wants an intensity above one, and <c>Color3</c> is exactly the
///         type such a tint has — but a picker offering an alpha band for a value with nowhere to put
///         one is a control that silently drops what it was told.
///     </para>
/// </remarks>
public sealed class Color3Drawer : PropertyDrawer<Color3, ColorPicker> {
    /// <inheritdoc />
    protected override ColorPicker Build(InspectorField field, UiElement parent) {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(parent);

        var usage = field.Member.Color;

        var picker = parent.Add<ColorPicker>();
        picker.AllowAlpha = false;
        picker.AllowHdr = usage?.Hdr ?? false;
        picker.Disabled = !field.CanWrite;

        picker.ValueChanged += (control, colour) => {
            var value = picker.AllowHdr ? control.HdrValue : colour;

            field.Write(new Color3(value.R, value.G, value.B));
        };

        return picker;
    }

    /// <inheritdoc />
    protected override void Show(InspectorField field, ColorPicker editor, Color3 value, bool isMixed) {
        ArgumentNullException.ThrowIfNull(editor);

        // Opaque, because the type has no alpha and a swatch drawn at the picker's default would be
        // transparent black for a colour that is nothing of the sort.
        editor.Value = isMixed ? Color4.Transparent : new Color4(value.R, value.G, value.B, 1f);

        if (isMixed) {
            editor.AddClass("mixed");
        } else {
            editor.RemoveClass("mixed");
        }
    }
}

/// <summary>A curve editor over the member's own curve.</summary>
/// <remarks>
///     ⚠ <b>The curve is a reference type, so the editor edits the object the member holds.</b> That
///     makes every key drag a mutation the command stack never saw. What is recorded instead is one
///     command per <i>edit session</i>, holding a copy of the curve from before it: the editor
///     announces changes, and the drawer writes a snapshot back through
///     <see cref="InspectorField.Write" /> so undo has something to put back.
/// </remarks>
public sealed class CurveDrawer : PropertyDrawer<AnimationCurve, CurveEditor> {
    /// <inheritdoc />
    public override bool CanDraw(InspectorMember member) {
        ArgumentNullException.ThrowIfNull(member);

        return member.MemberType == typeof(AnimationCurve);
    }

    /// <inheritdoc />
    protected override CurveEditor Build(InspectorField field, UiElement parent) {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(parent);

        var editor = parent.Add<CurveEditor>();
        editor.Disabled = !field.CanWrite;

        editor.CurveChanged += control => {
            field.Write(Copy(control.Curve));
            field.Seal();
        };

        return editor;
    }

    /// <inheritdoc />
    protected override void Show(InspectorField field, CurveEditor editor, AnimationCurve? value, bool isMixed) {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(editor);

        // A curve is only ever edited one object at a time. Merging twenty curves has no answer that
        // is not a guess, and "apply this one to all" is a button rather than a state of the editor.
        if (isMixed) {
            editor.AddClass("mixed");
            return;
        }

        editor.RemoveClass("mixed");
        editor.Curve = Copy(value ?? new AnimationCurve());
    }

    static AnimationCurve Copy(AnimationCurve source) {
        var keys = new CurveKey[source.Keys.Count];

        for (var index = 0; index < keys.Length; index++) {
            var key = source.Keys[index];

            keys[index] = new(key.Time, key.Value, key.Mode) {
                InTangent = key.InTangent,
                OutTangent = key.OutTangent
            };
        }

        return new(keys);
    }
}

/// <summary>A field naming an asset, with a button that opens a picker and a place to drop one.</summary>
/// <remarks>
///     <para>
///         <b>The member holds an <see cref="AssetId" />, and this shows a name.</b> Everything
///         stored in a file is a GUID — doc 08's whole identity model — so the drawer is the layer
///         that turns one into something readable, and it asks the project rather than caching a
///         name that a rename would make wrong.
///     </para>
///     <para>
///         An unresolved GUID shows as <c>Missing (…)</c> with the id, not as empty. An asset that
///         has been deleted out from under a scene is exactly the case a person needs to see, and an
///         empty box says "nothing was ever assigned" — which is a different problem with a different
///         fix.
///     </para>
/// </remarks>
public sealed class AssetDrawer : IPropertyDrawer {
    /// <summary>Turns an asset id into what the field says.</summary>
    /// <remarks>
    ///     Set by the host, because this assembly has no project. Unset, the field shows the id —
    ///     which is honest and is what a test sees.
    /// </remarks>
    public Func<AssetId, string?>? Resolve { get; set; }

    /// <summary>Raised when the picker button is pressed. The host opens whatever it opens.</summary>
    public event Action<InspectorField>? PickRequested;

    /// <inheritdoc />
    public bool CanDraw(InspectorMember member) {
        ArgumentNullException.ThrowIfNull(member);

        return member.MemberType == typeof(AssetId) || member.AssetType is not null;
    }

    /// <inheritdoc />
    public UiElement Build(InspectorField field, UiElement parent) {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(parent);

        var row = parent.Add("asset-field");
        row.Add<TextBlock>().AddClass("asset-name");

        var pick = row.Add<IconButton>();
        pick.LeadingIcon.Geometry = ControlIcons.Search;
        pick.Label = "Pick";
        pick.Variant = ControlVariant.Subtle;
        pick.Size = ControlSize.Small;
        pick.Disabled = !field.CanWrite;

        pick.AddHandler<ClickEvent>((_, args) => {
            PickRequested?.Invoke(field);
            args.Handled = true;
        });

        if (field.Member.AllowNull) {
            var clear = row.Add<IconButton>();
            clear.LeadingIcon.Geometry = ControlIcons.Close;
            clear.Label = "Clear";
            clear.Variant = ControlVariant.Subtle;
            clear.Size = ControlSize.Small;
            clear.Disabled = !field.CanWrite;

            clear.AddHandler<ClickEvent>((_, args) => {
                if (field.Write(AssetId.Empty)) {
                    field.Seal();
                }

                args.Handled = true;
            });
        }

        return row;
    }

    /// <inheritdoc />
    public void Show(InspectorField field, UiElement editor) {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(editor);

        if (editor.Children.Count == 0 || editor.Children[0] is not TextBlock label) {
            return;
        }

        var (value, mixed) = field.Read();

        if (mixed) {
            label.Text = "—";
            return;
        }

        if (value is not AssetId id || id.IsEmpty) {
            label.Text = "None";
            return;
        }

        label.Text = Resolve?.Invoke(id) ?? $"Missing ({id})";
    }
}
