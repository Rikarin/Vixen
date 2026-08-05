// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Buffers;
using Vixen.Core.Serialization;

namespace Vixen.Gameplay;

/// <summary>A definition's bytes, self-describing, because the reader does not know its type.</summary>
/// <remarks>
///     <para>
///         <b>Every other artefact in the engine is read back through a known type.</b>
///         <c>Serializer.Read&lt;BehaviorTreeContent&gt;</c> works because the caller knows what it
///         asked for. A definition catalog does not: it reads a directory of <c>.vxdef</c> files whose
///         only statement of type is the <c>!ItemDefinition</c> a designer wrote, and the whole point
///         of the seam is that a game can add a definition type without the loader learning about it.
///     </para>
///     <para>
///         So the payload leads with the <c>[DataContract]</c> alias, and the reader resolves that
///         through <see cref="SerializerRegistry" /> — the same alias table
///         <c>Vixen.Core.Serialization</c>'s own dynamic paths use for a member of a base type. What
///         this adds is that a <em>whole artefact</em> can be one, which
///         <c>Serializer.ToBytes&lt;T&gt;</c> cannot do for an abstract <c>T</c>.
///     </para>
///     <para>
///         ⚠ <b>The alias is content, so renaming one is a content break.</b> Same rule as renaming an
///         address, and <c>[DataContract]</c>'s former-alias list is the migration path — which is
///         also what <c>ADR-005</c> specifies for a <c>.meta</c>.
///     </para>
/// </remarks>
public static class DefinitionSerialization {
    /// <summary>Writes a definition, whatever kind it is.</summary>
    /// <param name="definition">The definition.</param>
    /// <returns>The bytes.</returns>
    /// <exception cref="SerializationException">Its type has no <c>[DataContract]</c>, so nothing can read it back.</exception>
    public static byte[] ToBytes(Definition definition) {
        ArgumentNullException.ThrowIfNull(definition);

        var type = definition.GetType();

        if (!SerializerRegistry.TryGetAlias(type, out var alias)) {
            throw new SerializationException(
                $"'{type}' has no serialised name, so a catalog could not read it back. Annotate it with "
                + "[DataContract], which is also what makes the !Tag in a .vxdef resolve to it."
            );
        }

        if (!SerializerRegistry.TryGetByAlias(alias, out var serializer)) {
            throw new SerializationException($"'{alias}' has a name and no serializer, which cannot happen.");
        }

        var buffer = new ArrayBufferWriter<byte>();
        var writer = new SerializationWriter(buffer);

        writer.WriteString(alias);
        serializer.SerializeObject(ref writer, definition);
        writer.Flush();

        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>Reads a definition back.</summary>
    /// <param name="bytes">What <see cref="ToBytes" /> wrote.</param>
    /// <returns>The definition, with no address on it — the catalog stamps that.</returns>
    /// <exception cref="SerializationException">The alias names nothing this build has, or names something that is not a definition.</exception>
    public static Definition FromBytes(ReadOnlySpan<byte> bytes) {
        var reader = new SerializationReader(bytes);
        var alias = reader.ReadString();

        if (string.IsNullOrEmpty(alias)) {
            throw new SerializationException("A definition's bytes must lead with its type name, and these do not.");
        }

        if (!SerializerRegistry.TryGetByAlias(alias, out var serializer)) {
            throw new SerializationException(
                $"These bytes name type '{alias}', which nothing in this build claims. Either the module "
                + "that owns it was not used, or this content was built against a different game."
            );
        }

        // Before using it, not after. The alias could name anything at all, and a Texture read as a
        // definition would be a cast exception a long way from here.
        return serializer.DeserializeObject(ref reader) as Definition
            ?? throw new SerializationException(
                $"These bytes name type '{alias}' ({serializer.SerializedType}), which is not a Definition."
            );
    }

    /// <summary>Adds a definition to a catalog straight from its bytes.</summary>
    /// <param name="builder">The catalog being composed.</param>
    /// <param name="address">Where the content build found it.</param>
    /// <param name="bytes">What <see cref="ToBytes" /> wrote.</param>
    /// <returns>The builder, so loads chain.</returns>
    public static DefinitionCatalogBuilder Add(
        this DefinitionCatalogBuilder builder,
        string address,
        ReadOnlySpan<byte> bytes
    ) {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.Add(address, FromBytes(bytes));
    }
}
