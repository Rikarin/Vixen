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
///     <para>
///         ⚠ <b>A <see cref="ColorInput" /> and not a <see cref="ColorPicker" />, which is what this
///         row used to embed.</b> The picker is the whole apparatus — a 150-pixel field, two bands, a
///         hex box, an intensity slider and a palette — and a material with four tints was four of
///         those stacked down the inspector with the next property somewhere past the bottom of the
///         panel. What belongs in a row is the box; the picker belongs in what the box opens.
///     </para>
///     <para>
///         <see cref="ColorUsageAttribute" /> is what decides whether the picker offers an intensity
///     slider and an alpha band, because a colour's type does not say which of those are meaningful
///         — an albedo tint and an emissive tint are both a <c>Color4</c> and want different editors.
///     </para>
/// </remarks>
public sealed class ColorDrawer : PropertyDrawer<Color4, ColorInput> {
    /// <inheritdoc />
    protected override ColorInput Build(InspectorField field, UiElement parent) {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(parent);

        var usage = field.Member.Color ?? new ColorUsage(false, true);

        var picker = parent.Add<ColorInput>();
        picker.AllowAlpha = usage.ShowAlpha;
        picker.AllowHdr = usage.Hdr;
        picker.Disabled = !field.CanWrite;

        picker.ValueChanged += (control, colour) => field.Write(usage.Hdr ? control.HdrValue : colour);

        return picker;
    }

    /// <inheritdoc />
    protected override void Show(InspectorField field, ColorInput editor, Color4 value, bool isMixed) {
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
public sealed class Color3Drawer : PropertyDrawer<Color3, ColorInput> {
    /// <inheritdoc />
    protected override ColorInput Build(InspectorField field, UiElement parent) {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(parent);

        var usage = field.Member.Color;

        var picker = parent.Add<ColorInput>();
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
    protected override void Show(InspectorField field, ColorInput editor, Color3 value, bool isMixed) {
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
///     <para>
///         ⚠ <b>The curve is a reference type, so the editor edits the object the member holds.</b>
///         That makes every key drag a mutation the command stack never saw. What is recorded
///         instead is one command per <i>edit session</i>, holding a copy of the curve from before
///         it: the editor announces changes, and the drawer writes a snapshot back so undo has
///         something to put back.
///     </para>
///     <para>
///         <b>What "mixed" means for a curve, which had to be decided before any of this could be
///         written.</b> Two curves agree when their keys agree — the same number of them, each at
///         the same time and value with the same tangents and mode. Disagreement is <i>not</i>
///         per-key: two curves with different key counts have no third key to call mixed, so there
///         is nothing between "these are the same curve" and "they are not". The row is mixed or it
///         is not, and a mixed one shows an <b>empty</b> graph rather than one of the curves.
///     </para>
///     <para>
///         ⚠ <b>Compared key by key rather than by <c>EditProperty.Read</c>'s answer, and that is a
///         defect this fixes rather than a preference.</b> <c>Read</c> compares with
///         <c>Equals(object, object)</c>, which for a type with no equality is reference identity —
///         and <c>AnimationCurve</c> has none. So two objects holding structurally identical curves
///         read as mixed the moment they are selected together, which is every multi-selection
///         there has ever been: a member initialised <c>= AnimationCurve.Linear()</c> gives each
///         instance its own object. The comparison is here rather than on the type on purpose: an
///         <c>AnimationCurve</c> is edited in place, has a <c>Changed</c> event, and its keys live
///         in a <c>HashSet</c> inside <c>CurveEditor</c> — giving a mutable model value equality
///         and a hash code is how a selection stops containing the key that is being dragged.
///     </para>
///     <para>
///         ⚠ <b>Every write is a separate copy per object, through
///         <see cref="EditProperty.WriteEach" />, and one <see cref="EditProperty.Write" /> would
///         have been wrong for a reason that has nothing to do with mixing.</b> A single write puts
///         the <i>same instance</i> on every selected object, and twenty objects sharing one curve
///         is not "they all have the same curve" — it is "editing any of them edits all of them",
///         silently, for the rest of the session. Twenty distinct copies is the only reading of
///         "set them all to this" that survives the next edit.
///     </para>
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
            // One copy per object rather than one instance shared by all of them. See the remarks:
            // a shared curve is an alias, not an agreement.
            var written = new object?[field.Objects.Count];

            for (var index = 0; index < written.Length; index++) {
                written[index] = Copy(control.Curve);
            }

            field.WriteEach(written);
            field.Seal();
        };

        return editor;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b><paramref name="value" /> and <paramref name="isMixed" /> are recomputed rather than
    ///     used, and the base class is not wrong to have handed them over.</b> It gets them from
    ///     <c>EditProperty.Read</c>, whose comparison is <c>Equals(object, object)</c> — reference
    ///     identity for a curve — so <paramref name="isMixed" /> is true for any two objects that do
    ///     not literally share one object, however identical their curves. The class remarks say why
    ///     the fix is here and not on the type.
    /// </remarks>
    protected override void Show(InspectorField field, CurveEditor editor, AnimationCurve? value, bool isMixed) {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(editor);

        var (shared, mixed) = Agreement(field);

        // ⚠ Inside a refresh, because parking the editor at a curve none of the objects hold is
        // exactly the shape `EditProperty.Refreshing` exists for. ⚠ And it is defence rather than a
        // fix for an observed write: `CurveEditor.Curve`'s setter does not raise `CurveChanged`
        // today, so no test can redden this line. It is here because the control is one event away
        // from being able to, and because every other row in this file is shown under the same
        // guard.
        using var refreshing = field.Refreshing();

        if (mixed) {
            // ⚠ Empty rather than one of them. Showing the first object's curve is the answer this
            // has to refuse: the user would then edit "the" curve, and what they were shown was one
            // arbitrary object's. An empty graph is honest — nothing is being claimed — and it stays
            // editable, because the only thing it can produce is a curve authored in front of them,
            // which every selected object then gets a copy of.
            editor.AddClass("mixed");
            Park(editor, new AnimationCurve());

            return;
        }

        editor.RemoveClass("mixed");
        Park(editor, shared ?? new AnimationCurve());
    }

    /// <summary>Puts a curve into the editor, unless it is already showing that curve.</summary>
    /// <remarks>
    ///     ⚠ <b>The test matters more than the assignment.</b> <c>Show</c> runs on every change a
    ///     gizmo drag makes — forty times a second — and assigning <c>Curve</c> is not idempotent:
    ///     the setter no-ops only on reference equality, so a fresh copy each time swaps the object
    ///     out from under the control, which clears its selection and re-subscribes. That is a row
    ///     whose selected keys vanish while something else in the scene is being dragged, and during
    ///     the drag it is a row editing a curve that is replaced under the pointer.
    /// </remarks>
    static void Park(CurveEditor editor, AnimationCurve curve) {
        if (!SameKeys(editor.Curve, curve)) {
            editor.Curve = Copy(curve);
        }
    }

    /// <summary>The curve every selected object holds, or nothing when they disagree.</summary>
    static (AnimationCurve? Shared, bool Mixed) Agreement(InspectorField field) {
        AnimationCurve? first = null;

        for (var index = 0; index < field.Objects.Count; index++) {
            var curve = field.Member.GetBoxed(field.Objects[index]) as AnimationCurve;

            if (index == 0) {
                first = curve;
                continue;
            }

            if (!SameKeys(first, curve)) {
                return (null, true);
            }
        }

        return (first, false);
    }

    /// <summary>Whether two curves have the same keys, which is what "the same curve" means here.</summary>
    /// <remarks>
    ///     ⚠ <b>Not <c>Equals</c> on <c>AnimationCurve</c>, deliberately.</b> Value equality on a
    ///     type obliges a matching hash code, and this one is a mutable model with a <c>Changed</c>
    ///     event whose keys sit in a <c>HashSet</c> inside <c>CurveEditor</c>'s selection — a hash
    ///     that moved when a key was dragged would take the dragged key out of the set that is
    ///     tracking it. Whether two curves count as the same value is an editing question, so it is
    ///     answered where the editing is.
    /// </remarks>
    static bool SameKeys(AnimationCurve? left, AnimationCurve? right) {
        if (ReferenceEquals(left, right)) {
            return true;
        }

        if (left is null || right is null || left.Keys.Count != right.Keys.Count) {
            return false;
        }

        for (var index = 0; index < left.Keys.Count; index++) {
            var a = left.Keys[index];
            var b = right.Keys[index];

            if (!a.Time.Equals(b.Time)
                || !a.Value.Equals(b.Value)
                || !a.InTangent.Equals(b.InTangent)
                || !a.OutTangent.Equals(b.OutTangent)
                || a.Mode != b.Mode) {
                return false;
            }
        }

        return true;
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
///         ⚠ <b>An <see cref="AssetReference" /> is the same row, and leaving it out was not a
///         missing nicety.</b> A bare id can only ever name an asset's main object, so what a scene
///         actually stores is a reference — <c>MeshRenderable.Mesh</c> and every material member on
///         every component are that type, not <see cref="AssetId" />. The drawer answered for
///         <see cref="AssetId" /> alone, so all of them fell through to the read-only last resort:
///         the two most-used asset fields in the editor were grey text.
///     </para>
///     <para>
///         An unresolved GUID shows as <c>Missing (…)</c> with the id, not as empty. An asset that
///         has been deleted out from under a scene is exactly the case a person needs to see, and an
///         empty box says "nothing was ever assigned" — which is a different problem with a different
///         fix.
///     </para>
/// </remarks>
public sealed class AssetDrawer : IPropertyDrawer {
    /// <summary>The class a host puts on the row under a drag it would accept.</summary>
    /// <remarks>
    ///     Named here rather than spelled into the shell, because the stylesheet that draws it is
    ///     this assembly's and a class name agreed between two files by copying it is one that stops
    ///     matching the first time either end is renamed.
    /// </remarks>
    public const string DropTargetClass = "drop-target";

    /// <summary>The class for a drag over a field that will not take it.</summary>
    /// <remarks>
    ///     ⚠ <b>Shown rather than ignored.</b> A drag of a texture over a mesh field that lit up like
    ///     every other field, did nothing on release and said nothing about why is the interaction
    ///     people repeat three times before concluding the editor is broken. Refusal has to be
    ///     visible while the pointer is still down and the drag can still be taken somewhere else.
    /// </remarks>
    public const string DropRejectedClass = "drop-rejected";

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

        return member.MemberType == typeof(AssetId)
            || member.MemberType == typeof(AssetReference)
            || member.AssetType is not null;
    }

    /// <summary>What a field currently names, whichever of the two types it holds.</summary>
    /// <param name="field">The field.</param>
    /// <returns>The id, or <see cref="AssetId.Empty" /> for a field naming nothing or disagreeing.</returns>
    /// <remarks>
    ///     ⚠ <b>Mixed reads as empty, and callers have to ask separately.</b> This exists for the
    ///     host's drop path, which only needs "is the field already holding this" — and a mixed field
    ///     is not, whatever the answer would be for any one object.
    /// </remarks>
    public static AssetId Current(InspectorField field) {
        ArgumentNullException.ThrowIfNull(field);

        var (value, mixed) = field.Read();

        return mixed ? AssetId.Empty : Identify(value);
    }

    /// <summary>Writes an asset into a field, as whichever type the member holds.</summary>
    /// <param name="field">The field.</param>
    /// <param name="asset">The asset, or <see cref="AssetId.Empty" /> to clear it.</param>
    /// <returns>Whether anything changed.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Boxing the right type is the whole of this method and it is not optional.</b>
    ///         <see cref="InspectorField.Write" /> takes an <see cref="object" /> and hands it to a
    ///         generated setter that casts; an <see cref="AssetId" /> written into an
    ///         <see cref="AssetReference" /> member is an <see cref="InvalidCastException" /> thrown
    ///         from inside a click handler, which in a UI framework means the frame dies rather than
    ///         the field refusing.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The sub-asset is reset to <see cref="SubAssetId.Main" /> rather than kept.</b>
    ///         Assigning a different asset to a member that named <c>hero#Hero_Mesh</c> and keeping
    ///         the sub-asset id would point at a part of the new asset chosen by a hash of a name
    ///         from the old one — which resolves to nothing, or to the wrong part. Picking a part is
    ///         its own gesture and is not this one.
    ///     </para>
    /// </remarks>
    public static bool Assign(InspectorField field, AssetId asset) {
        ArgumentNullException.ThrowIfNull(field);

        return field.Write(Box(field.Member, asset));
    }

    /// <summary>An asset id as the member's own type, boxed for <see cref="InspectorField.Write" />.</summary>
    /// <param name="member">The member being written.</param>
    /// <param name="asset">The asset.</param>
    /// <returns>The boxed value.</returns>
    public static object Box(InspectorMember member, AssetId asset) {
        ArgumentNullException.ThrowIfNull(member);

        return member.MemberType == typeof(AssetReference) ? new AssetReference(asset) : asset;
    }

    static AssetId Identify(object? value) =>
        value switch {
            AssetId id => id,
            AssetReference reference => reference.Asset,
            _ => AssetId.Empty
        };

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
                if (Assign(field, AssetId.Empty)) {
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

        var id = Identify(value);

        if (id.IsEmpty) {
            label.Text = "None";
            return;
        }

        var name = Resolve?.Invoke(id) ?? $"Missing ({id})";

        // ⚠ The sub-asset is shown when there is one, because an FBX imports to a main object and a
        // mesh per part: two entities drawing `hero#Hero_Mesh` and `hero#Cape_Mesh` are two different
        // things, and a field that said "hero" for both would be a field the user cannot tell apart
        // from the one they meant to change.
        label.Text = value is AssetReference { SubAsset.IsMain: false } reference
            ? $"{name} › {reference.SubAsset}"
            : name;
    }
}
