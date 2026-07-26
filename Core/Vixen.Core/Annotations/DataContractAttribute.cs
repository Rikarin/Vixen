// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Core;

/// <summary>
///     Marks a type as serialisable. <c>Vixen.Core.Serialization.Generators</c> walks every
///     annotated type at compile time and emits a serializer for it; nothing is discovered by
///     reflection at run time (ADR-002).
/// </summary>
/// <remarks>
///     Serialisation is opt-in at the type level and opt-out at the member level: an annotated
///     type serialises its public fields and read/write properties unless they carry
///     <see cref="DataMemberIgnoreAttribute" />.
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Interface | AttributeTargets.Enum)]
public sealed class DataContractAttribute : Attribute {
    /// <summary>
    ///     Stable name written to the serialised form. Defaults to the type's name, which means a
    ///     type rename breaks existing data — set this once and the type can be renamed freely.
    /// </summary>
    public string? Alias { get; set; }

    /// <summary>
    ///     Whether types deriving from this one are contracts too without repeating the attribute.
    ///     Off by default: silently serialising a subclass that was never designed for it is the
    ///     more expensive mistake.
    /// </summary>
    public bool Inherited { get; set; }

    /// <summary>
    ///     Schema version of the contract, written into the serialised form so a reader can
    ///     migrate older data. <c>0</c> means unversioned.
    /// </summary>
    public int SerializedVersion { get; set; }

    /// <summary>Marks the type as serialisable under its own name.</summary>
    public DataContractAttribute() { }

    /// <summary>Marks the type as serialisable under <paramref name="alias" />.</summary>
    /// <param name="alias">The stable name written to the serialised form.</param>
    public DataContractAttribute(string alias) => Alias = alias;
}
