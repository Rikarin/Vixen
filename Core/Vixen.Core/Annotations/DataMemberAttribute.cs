// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Core;

/// <summary>
///     Includes a member in its type's serialised form, optionally under a different name or at a
///     chosen position. Only needed to override a default: a public field or read/write property of
///     a <see cref="DataContractAttribute" /> type is already serialised.
/// </summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
public sealed class DataMemberAttribute : Attribute {
    /// <summary>
    ///     Sort key for the emitted member order. Members sharing an order keep their declaration
    ///     order, so the default of <c>0</c> leaves the whole type in declaration order.
    /// </summary>
    public int Order { get; set; }

    /// <summary>
    ///     Name written to the serialised form. Defaults to the member's name; setting it lets the
    ///     member be renamed in C# without touching existing data.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>Serialises the member under its own name, in declaration order.</summary>
    public DataMemberAttribute() { }

    /// <summary>Serialises the member with an explicit sort key.</summary>
    /// <param name="order">The sort key; see <see cref="Order" />.</param>
    public DataMemberAttribute(int order) => Order = order;

    /// <summary>Serialises the member under an explicit name.</summary>
    /// <param name="name">The name written to the serialised form.</param>
    public DataMemberAttribute(string name) => Name = name;
}
