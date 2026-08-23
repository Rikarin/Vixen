// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Core.Reflection;

namespace Vixen.Samples.HelloUi;

/// <summary>Something for the inspector to inspect.</summary>
/// <remarks>
///     Its descriptor is written by hand rather than generated, and writing it out is itself worth
///     seeing: it is exactly what <c>Vixen.Core.Reflection.Generator</c> emits for a type carrying
///     <c>[DataContract]</c> — a name, a type, two lambdas over a cast, and what the inspector should
///     make of it. This sample deliberately references nothing that would bring the attribute in, so
///     the generator has nothing to run over.
/// </remarks>
public sealed class Material {
    /// <summary>Whether it is drawn at all.</summary>
    public bool Visible { get; set; } = true;

    /// <summary>What it is called.</summary>
    public string Name { get; set; } = "Standard";

    /// <summary>How rough the surface is, from nothing to entirely.</summary>
    public float Roughness { get; set; } = 0.4f;

    /// <summary>How metallic it is.</summary>
    public float Metallic { get; set; }

    /// <summary>How many samples the shading takes.</summary>
    public int Samples { get; set; } = 4;

    /// <summary>Describes it the way the generator would.</summary>
    /// <returns>The descriptor.</returns>
    public static TypeDescriptor Describe() =>
        new(
            typeof(Material),

            // ⚠ Not "Material". The registry is process-wide and `Vixen.Rendering` already claims
            // that alias for `MaterialDescriptor`.
            "SampleMaterial",
            TypeTraits.DataContract | TypeTraits.EditorVisible,
            [
                Member("Visible", typeof(bool), m => m.Visible, (m, v) => m.Visible = (bool) v!),
                Member("Name", typeof(string), m => m.Name, (m, v) => m.Name = (string) v!),
                Member(
                    "Roughness",
                    typeof(float),
                    m => m.Roughness,
                    (m, v) => m.Roughness = (float) v!,
                    new MemberPresentation(Minimum: 0, Maximum: 1, Step: 0.01, IsEditorVisible: true)
                ),
                Member(
                    "Metallic",
                    typeof(float),
                    m => m.Metallic,
                    (m, v) => m.Metallic = (float) v!,
                    new MemberPresentation(Minimum: 0, Maximum: 1, Step: 0.01, IsEditorVisible: true)
                ),
                Member("Samples", typeof(int), m => m.Samples, (m, v) => m.Samples = (int) v!)
            ],
            () => new Material()
        );

    /// <remarks>
    ///     ⚠ The presentation is spelled out. <c>MemberPresentation</c> is a record struct, so
    ///     <c>default</c> zeroes <c>IsEditorVisible</c> however the parameter is declared — a member
    ///     handed a defaulted presentation is hidden from the inspector, silently.
    /// </remarks>
    static MemberDescriptor Member(
        string name,
        Type type,
        Func<Material, object?> get,
        Action<Material, object?>? set,
        MemberPresentation? presentation = null
    ) =>
        new(
            name,
            type,
            0,
            instance => get((Material) instance),
            set is null ? null : (instance, value) => set((Material) instance, value),
            presentation ?? new MemberPresentation(IsEditorVisible: true)
        );

    /// <inheritdoc />
    public override string ToString() => Name + " (" + Roughness.ToString("0.00", CultureInfo.InvariantCulture) + ")";
}
