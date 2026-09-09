// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Curves;
using Vixen.Ui.Controls.Advanced;

namespace Vixen.Editor.App;

/// <summary>One key of a saved curve, as a file can hold it.</summary>
/// <remarks>
///     ⚠ <b>The tangent mode is a <i>name</i> and not a number.</b> It is an enum somebody will
///     insert a member into, and one stored as an integer comes back meaning something else after a
///     version that edits the declaration — the failure nobody reports because it looks like the
///     editor forgetting. <c>ViewportPreferences</c> makes the same argument about show flags and a
///     view mode, and this file is beside it for the same reason.
/// </remarks>
[DataContract("CurvePresetKey")]
public sealed class CurvePresetKey {
    /// <summary>When.</summary>
    public float Time { get; set; }

    /// <summary>What.</summary>
    public float Value { get; set; }

    /// <summary>The slope coming in.</summary>
    public float InTangent { get; set; }

    /// <summary>The slope going out.</summary>
    public float OutTangent { get; set; }

    /// <summary>How the two behave, by the name <c>TangentMode</c> gives it.</summary>
    public string Mode { get; set; } = nameof(TangentMode.Auto);
}

/// <summary>A curve somebody named and kept.</summary>
[DataContract("CurvePreset")]
public sealed class CurvePreset {
    /// <summary>What they called it.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Its keys, in time order.</summary>
    public List<CurvePresetKey> Keys { get; set; } = [];
}

/// <summary>The curves somebody has kept, which travel with the person rather than with the project.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Doc 20 § B5 decided where this goes and the decision is worth keeping: a library of
///         saved presets is a <i>user-store file</i> rather than an editor surface — it belongs
///         beside the layouts and the keymap, not in an asset editor.</b> The consequence is the
///         point of saying it: presets are not project assets, get no importer, and do not travel
///         with the repository. A team-shared library is a different feature and must not be
///         smuggled in by making this one an asset.
///     </para>
///     <para>
///         ⚠ <b>The shipped shapes are <i>defaults</i> rather than entries, which is what
///         <see cref="Shipped" /> is for.</b> A store seeded with linear and the three eases is one
///         where deleting a built-in is a thing that can happen and cannot be undone — so they are
///         offered by this class and never written to the file. An emptied store still offers them.
///     </para>
///     <para>
///         ⚠ <b>Curves only, and the gradient half of that doc row is refused rather than
///         forgotten.</b> <c>GradientEditor</c> has no caller anywhere in the editor — no drawer, no
///         asset editor, no panel; the only uses in the tree are its own tests and the sample
///         gallery — so a gradient preset library would be a library with nothing to apply a preset
///         <i>to</i>, which is this repository's commonest defect written down deliberately. The
///         curve control, by contrast, is a drawer in the inspector and is in two AI asset editors.
///     </para>
///     <para>
///         ⚠ <b>That refusal was already written down before this file existed, and the record is the
///         thing to read rather than re-derive.</b>
///         <c>Core/Vixen.Ui.Controls.Advanced/README.md</c> § GradientEditor gives three reasons that
///         are all outside the control — no gradient asset, doc 48's predicted consumer shipped
///         taking a texture name, and the obvious host does not reference the controls assembly — and
///         <c>docs/overview.md</c> §1.7 carries the same sentence.
///         <a href="https://github.com/Rikarin/Vixen/issues/1147">#1147</a> re-found the sweep and
///         asked for a record that was already there, which is worth knowing: a <c>*.cs</c> plus
///         <c>*.vxml</c> sweep answers "is it called" and not "was this decided".
///     </para>
/// </remarks>
[DataContract("CurvePresetLibrary")]
public sealed class CurvePresetLibrary {
    /// <summary>What the user has saved, in the order they saved it.</summary>
    public List<CurvePreset> Curves { get; set; } = [];

    /// <summary>The shapes the editor always offers, whatever the file holds.</summary>
    /// <remarks>
    ///     ⚠ <b>Rebuilt per call, because an <c>AnimationCurve</c> is mutable and
    ///     <c>CurveEditor.Apply</c> copies keys out of what it is handed.</b> A cached instance
    ///     handed to two rows would be one object two panels could edit — the aliasing
    ///     <c>CurveDrawer</c>'s own remarks refuse for exactly the same reason.
    /// </remarks>
    public static IReadOnlyList<(string Name, AnimationCurve Curve)> Shipped =>
    [
        ("Linear", AnimationCurve.Linear()),
        ("Ease In", AnimationCurve.EaseIn()),
        ("Ease Out", AnimationCurve.EaseOut()),
        ("Ease In Out", AnimationCurve.EaseInOut()),
        ("Constant", AnimationCurve.Step())
    ];

    /// <summary>Every shape on offer: the shipped ones first, then what the user saved.</summary>
    /// <remarks>
    ///     ⚠ <b>A saved preset with a shipped one's name wins, and is not refused.</b> Somebody who
    ///     saves their own "Ease In" has said what they mean by it; refusing the name would be the
    ///     editor arguing with them, and offering both would be two identical lines on a menu.
    /// </remarks>
    public IReadOnlyList<(string Name, AnimationCurve Curve)> Offered() {
        List<(string Name, AnimationCurve Curve)> offered = [];

        foreach (var (name, curve) in Shipped) {
            if (!Curves.Any(saved => string.Equals(saved.Name, name, StringComparison.Ordinal))) {
                offered.Add((name, curve));
            }
        }

        foreach (var saved in Curves) {
            offered.Add((saved.Name, ToCurve(saved)));
        }

        return offered;
    }

    /// <summary>Keeps a curve under a name, replacing one of that name.</summary>
    /// <param name="name">What to call it.</param>
    /// <param name="curve">The shape, copied.</param>
    /// <remarks>
    ///     ⚠ <b>Copied out of the object rather than held.</b> The curve handed in is the one an
    ///     inspector row is editing, and a library holding it would be a preset that changed every
    ///     time somebody dragged the key it was made from.
    /// </remarks>
    public void Save(string name, AnimationCurve curve) {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(curve);

        Curves.RemoveAll(saved => string.Equals(saved.Name, name, StringComparison.Ordinal));

        Curves.Add(
            new CurvePreset {
                Name = name,
                Keys = [
                    .. curve.Keys.Select(key =>
                        new CurvePresetKey {
                            Time = key.Time,
                            Value = key.Value,
                            InTangent = key.InTangent,
                            OutTangent = key.OutTangent,
                            Mode = key.Mode.ToString()
                        }
                    )
                ]
            }
        );
    }

    /// <summary>Forgets a saved curve.</summary>
    /// <param name="name">Which one.</param>
    /// <returns>Whether there was one.</returns>
    /// <remarks>
    ///     ⚠ <b>Only ever a saved one.</b> A shipped shape is not in <see cref="Curves" /> and
    ///     therefore cannot be removed, which is the whole reason it is not stored.
    /// </remarks>
    public bool Forget(string name) =>
        Curves.RemoveAll(saved => string.Equals(saved.Name, name, StringComparison.Ordinal)) > 0;

    /// <summary>Reads a saved curve back as something a control can be given.</summary>
    /// <param name="preset">The saved shape.</param>
    /// <returns>The curve.</returns>
    /// <remarks>
    ///     ⚠ <b>A mode the file names that this version does not know falls back to
    ///     <c>Auto</c>.</b> A preset written by a later editor must not be a preset that throws
    ///     while a menu is being drawn.
    /// </remarks>
    public static AnimationCurve ToCurve(CurvePreset preset) {
        ArgumentNullException.ThrowIfNull(preset);

        var curve = new AnimationCurve();

        foreach (var key in preset.Keys) {
            var mode = Enum.TryParse<TangentMode>(key.Mode, out var parsed) ? parsed : TangentMode.Auto;

            curve.Add(
                new CurveKey(key.Time, key.Value, mode) {
                    InTangent = key.InTangent,
                    OutTangent = key.OutTangent
                }
            );
        }

        return curve;
    }
}
