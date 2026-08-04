// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Vixen.Core.Serialization;
using Vixen.Rendering.Compositor;

namespace Vixen.Rendering.PostFx;

/// <summary>The one hand-written serializer this assembly needs, and why it is hand-written.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>An enum member is serialised inline by whatever holds it — a <em>nullable</em> enum
///         member is not.</b> The generator emits <c>WriteNullable&lt;QualityTier&gt;</c> for
///         <see cref="StandardFrameAsset.Quality" />, and the nullable path goes through
///         <see cref="SerializerRegistry.Get{T}" /> for its inner value, which is a registry the
///         generator never puts enums into. Every other document enum in this assembly is a plain
///         member and never touches the registry; <c>Quality</c> is nullable on the opinion model's
///         terms — a document that writes nothing has left the tier to the host — so it is the one
///         that does.
///     </para>
///     <para>
///         Found by the first <c>!StandardFrame</c> document to go through the content build:
///         <c>CompositorImporter</c> compiles a document to the binary chunk, sample 12's and 13's
///         hand-authored documents carry no nullable enum, and the YAML path the tests parse
///         through never runs the binary serializer at all. Written as its underlying
///         <see cref="int" />, which is what the generator's inline enum path writes too.
///     </para>
/// </remarks>
sealed class QualityTierSerializer : DataSerializer<QualityTier> {
    /// <inheritdoc />
    public override void Serialize(ref SerializationWriter writer, in QualityTier value) =>
        writer.WriteInt32((int)value);

    /// <inheritdoc />
    public override void Deserialize(ref SerializationReader reader, ref QualityTier value) =>
        value = (QualityTier)reader.ReadInt32();
}

/// <summary>Registers <see cref="QualityTierSerializer" /> when the assembly is first touched.</summary>
/// <remarks>
///     A module initializer, beside the generated one that registers every <c>[DataContract]</c> in
///     this assembly — so any process that can bind a document (the host, the CLI's importer, a
///     test) has the serializer by the time it could need it, with no registration call to forget.
/// </remarks>
static class QualityTierSerialization {
    [ModuleInitializer]
    [SuppressMessage(
        "Usage",
        "CA2255:The 'ModuleInitializer' attribute should not be used in libraries",
        Justification =
            "The rule's concern is a library doing observable work on load. This adds one entry to the "
            + "serializer registry and touches nothing else — the same thing the generated initializer "
            + "beside it does for every [DataContract], for the same reason: the alternative is asking "
            + "every consumer to call a registration method the generated code cannot call for them."
    )]
    internal static void Register() => SerializerRegistry.Register(new QualityTierSerializer());
}
