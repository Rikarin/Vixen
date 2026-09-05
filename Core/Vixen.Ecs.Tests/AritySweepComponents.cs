// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ecs.Tests;

/// <summary>
///     Component 0 of the arity sweep. Sixteen of these exist because sixteen is
///     <c>QueryArityGenerator.MaxArity</c>.
/// </summary>
/// <remarks>
///     Deliberately identical to its fifteen siblings, and deliberately not in
///     <c>TestComponents.cs</c> — that file is one file so a test reading a chunk layout can see
///     every size that goes into it, and these are the opposite: the only thing distinguishing
///     arity <i>n</i> from arity <i>n + 1</i> in <c>QueryAritySweepTests</c> should be the number.
///     The file is named <c>*Components.cs</c> because that is how a file opts into the CA1051
///     exemption (.editorconfig § "ECS component declarations"): a component is a public mutable
///     field, since a property returning a copy would silently drop a write through a <c>ref</c>
///     into a chunk.
/// </remarks>
public struct A0 {
    /// <summary>Which entity this belongs to, so a misaligned entity reference is visible.</summary>
    public int Owner;

    /// <summary>How many times a query body has visited it.</summary>
    public int Touches;
}

/// <summary>
///     Component 1 of the arity sweep. Sixteen of these exist because sixteen is
///     <c>QueryArityGenerator.MaxArity</c>.
/// </summary>
/// <remarks>
///     Deliberately identical to its fifteen siblings, and deliberately not in
///     <c>TestComponents.cs</c> — that file is one file so a test reading a chunk layout can see
///     every size that goes into it, and these are the opposite: the only thing distinguishing
///     arity <i>n</i> from arity <i>n + 1</i> in <c>QueryAritySweepTests</c> should be the number.
///     The file is named <c>*Components.cs</c> because that is how a file opts into the CA1051
///     exemption (.editorconfig § "ECS component declarations"): a component is a public mutable
///     field, since a property returning a copy would silently drop a write through a <c>ref</c>
///     into a chunk.
/// </remarks>
public struct A1 {
    /// <summary>Which entity this belongs to, so a misaligned entity reference is visible.</summary>
    public int Owner;

    /// <summary>How many times a query body has visited it.</summary>
    public int Touches;
}

/// <summary>
///     Component 2 of the arity sweep. Sixteen of these exist because sixteen is
///     <c>QueryArityGenerator.MaxArity</c>.
/// </summary>
/// <remarks>
///     Deliberately identical to its fifteen siblings, and deliberately not in
///     <c>TestComponents.cs</c> — that file is one file so a test reading a chunk layout can see
///     every size that goes into it, and these are the opposite: the only thing distinguishing
///     arity <i>n</i> from arity <i>n + 1</i> in <c>QueryAritySweepTests</c> should be the number.
///     The file is named <c>*Components.cs</c> because that is how a file opts into the CA1051
///     exemption (.editorconfig § "ECS component declarations"): a component is a public mutable
///     field, since a property returning a copy would silently drop a write through a <c>ref</c>
///     into a chunk.
/// </remarks>
public struct A2 {
    /// <summary>Which entity this belongs to, so a misaligned entity reference is visible.</summary>
    public int Owner;

    /// <summary>How many times a query body has visited it.</summary>
    public int Touches;
}

/// <summary>
///     Component 3 of the arity sweep. Sixteen of these exist because sixteen is
///     <c>QueryArityGenerator.MaxArity</c>.
/// </summary>
/// <remarks>
///     Deliberately identical to its fifteen siblings, and deliberately not in
///     <c>TestComponents.cs</c> — that file is one file so a test reading a chunk layout can see
///     every size that goes into it, and these are the opposite: the only thing distinguishing
///     arity <i>n</i> from arity <i>n + 1</i> in <c>QueryAritySweepTests</c> should be the number.
///     The file is named <c>*Components.cs</c> because that is how a file opts into the CA1051
///     exemption (.editorconfig § "ECS component declarations"): a component is a public mutable
///     field, since a property returning a copy would silently drop a write through a <c>ref</c>
///     into a chunk.
/// </remarks>
public struct A3 {
    /// <summary>Which entity this belongs to, so a misaligned entity reference is visible.</summary>
    public int Owner;

    /// <summary>How many times a query body has visited it.</summary>
    public int Touches;
}

/// <summary>
///     Component 4 of the arity sweep. Sixteen of these exist because sixteen is
///     <c>QueryArityGenerator.MaxArity</c>.
/// </summary>
/// <remarks>
///     Deliberately identical to its fifteen siblings, and deliberately not in
///     <c>TestComponents.cs</c> — that file is one file so a test reading a chunk layout can see
///     every size that goes into it, and these are the opposite: the only thing distinguishing
///     arity <i>n</i> from arity <i>n + 1</i> in <c>QueryAritySweepTests</c> should be the number.
///     The file is named <c>*Components.cs</c> because that is how a file opts into the CA1051
///     exemption (.editorconfig § "ECS component declarations"): a component is a public mutable
///     field, since a property returning a copy would silently drop a write through a <c>ref</c>
///     into a chunk.
/// </remarks>
public struct A4 {
    /// <summary>Which entity this belongs to, so a misaligned entity reference is visible.</summary>
    public int Owner;

    /// <summary>How many times a query body has visited it.</summary>
    public int Touches;
}

/// <summary>
///     Component 5 of the arity sweep. Sixteen of these exist because sixteen is
///     <c>QueryArityGenerator.MaxArity</c>.
/// </summary>
/// <remarks>
///     Deliberately identical to its fifteen siblings, and deliberately not in
///     <c>TestComponents.cs</c> — that file is one file so a test reading a chunk layout can see
///     every size that goes into it, and these are the opposite: the only thing distinguishing
///     arity <i>n</i> from arity <i>n + 1</i> in <c>QueryAritySweepTests</c> should be the number.
///     The file is named <c>*Components.cs</c> because that is how a file opts into the CA1051
///     exemption (.editorconfig § "ECS component declarations"): a component is a public mutable
///     field, since a property returning a copy would silently drop a write through a <c>ref</c>
///     into a chunk.
/// </remarks>
public struct A5 {
    /// <summary>Which entity this belongs to, so a misaligned entity reference is visible.</summary>
    public int Owner;

    /// <summary>How many times a query body has visited it.</summary>
    public int Touches;
}

/// <summary>
///     Component 6 of the arity sweep. Sixteen of these exist because sixteen is
///     <c>QueryArityGenerator.MaxArity</c>.
/// </summary>
/// <remarks>
///     Deliberately identical to its fifteen siblings, and deliberately not in
///     <c>TestComponents.cs</c> — that file is one file so a test reading a chunk layout can see
///     every size that goes into it, and these are the opposite: the only thing distinguishing
///     arity <i>n</i> from arity <i>n + 1</i> in <c>QueryAritySweepTests</c> should be the number.
///     The file is named <c>*Components.cs</c> because that is how a file opts into the CA1051
///     exemption (.editorconfig § "ECS component declarations"): a component is a public mutable
///     field, since a property returning a copy would silently drop a write through a <c>ref</c>
///     into a chunk.
/// </remarks>
public struct A6 {
    /// <summary>Which entity this belongs to, so a misaligned entity reference is visible.</summary>
    public int Owner;

    /// <summary>How many times a query body has visited it.</summary>
    public int Touches;
}

/// <summary>
///     Component 7 of the arity sweep. Sixteen of these exist because sixteen is
///     <c>QueryArityGenerator.MaxArity</c>.
/// </summary>
/// <remarks>
///     Deliberately identical to its fifteen siblings, and deliberately not in
///     <c>TestComponents.cs</c> — that file is one file so a test reading a chunk layout can see
///     every size that goes into it, and these are the opposite: the only thing distinguishing
///     arity <i>n</i> from arity <i>n + 1</i> in <c>QueryAritySweepTests</c> should be the number.
///     The file is named <c>*Components.cs</c> because that is how a file opts into the CA1051
///     exemption (.editorconfig § "ECS component declarations"): a component is a public mutable
///     field, since a property returning a copy would silently drop a write through a <c>ref</c>
///     into a chunk.
/// </remarks>
public struct A7 {
    /// <summary>Which entity this belongs to, so a misaligned entity reference is visible.</summary>
    public int Owner;

    /// <summary>How many times a query body has visited it.</summary>
    public int Touches;
}

/// <summary>
///     Component 8 of the arity sweep. Sixteen of these exist because sixteen is
///     <c>QueryArityGenerator.MaxArity</c>.
/// </summary>
/// <remarks>
///     Deliberately identical to its fifteen siblings, and deliberately not in
///     <c>TestComponents.cs</c> — that file is one file so a test reading a chunk layout can see
///     every size that goes into it, and these are the opposite: the only thing distinguishing
///     arity <i>n</i> from arity <i>n + 1</i> in <c>QueryAritySweepTests</c> should be the number.
///     The file is named <c>*Components.cs</c> because that is how a file opts into the CA1051
///     exemption (.editorconfig § "ECS component declarations"): a component is a public mutable
///     field, since a property returning a copy would silently drop a write through a <c>ref</c>
///     into a chunk.
/// </remarks>
public struct A8 {
    /// <summary>Which entity this belongs to, so a misaligned entity reference is visible.</summary>
    public int Owner;

    /// <summary>How many times a query body has visited it.</summary>
    public int Touches;
}

/// <summary>
///     Component 9 of the arity sweep. Sixteen of these exist because sixteen is
///     <c>QueryArityGenerator.MaxArity</c>.
/// </summary>
/// <remarks>
///     Deliberately identical to its fifteen siblings, and deliberately not in
///     <c>TestComponents.cs</c> — that file is one file so a test reading a chunk layout can see
///     every size that goes into it, and these are the opposite: the only thing distinguishing
///     arity <i>n</i> from arity <i>n + 1</i> in <c>QueryAritySweepTests</c> should be the number.
///     The file is named <c>*Components.cs</c> because that is how a file opts into the CA1051
///     exemption (.editorconfig § "ECS component declarations"): a component is a public mutable
///     field, since a property returning a copy would silently drop a write through a <c>ref</c>
///     into a chunk.
/// </remarks>
public struct A9 {
    /// <summary>Which entity this belongs to, so a misaligned entity reference is visible.</summary>
    public int Owner;

    /// <summary>How many times a query body has visited it.</summary>
    public int Touches;
}

/// <summary>
///     Component 10 of the arity sweep. Sixteen of these exist because sixteen is
///     <c>QueryArityGenerator.MaxArity</c>.
/// </summary>
/// <remarks>
///     Deliberately identical to its fifteen siblings, and deliberately not in
///     <c>TestComponents.cs</c> — that file is one file so a test reading a chunk layout can see
///     every size that goes into it, and these are the opposite: the only thing distinguishing
///     arity <i>n</i> from arity <i>n + 1</i> in <c>QueryAritySweepTests</c> should be the number.
///     The file is named <c>*Components.cs</c> because that is how a file opts into the CA1051
///     exemption (.editorconfig § "ECS component declarations"): a component is a public mutable
///     field, since a property returning a copy would silently drop a write through a <c>ref</c>
///     into a chunk.
/// </remarks>
public struct A10 {
    /// <summary>Which entity this belongs to, so a misaligned entity reference is visible.</summary>
    public int Owner;

    /// <summary>How many times a query body has visited it.</summary>
    public int Touches;
}

/// <summary>
///     Component 11 of the arity sweep. Sixteen of these exist because sixteen is
///     <c>QueryArityGenerator.MaxArity</c>.
/// </summary>
/// <remarks>
///     Deliberately identical to its fifteen siblings, and deliberately not in
///     <c>TestComponents.cs</c> — that file is one file so a test reading a chunk layout can see
///     every size that goes into it, and these are the opposite: the only thing distinguishing
///     arity <i>n</i> from arity <i>n + 1</i> in <c>QueryAritySweepTests</c> should be the number.
///     The file is named <c>*Components.cs</c> because that is how a file opts into the CA1051
///     exemption (.editorconfig § "ECS component declarations"): a component is a public mutable
///     field, since a property returning a copy would silently drop a write through a <c>ref</c>
///     into a chunk.
/// </remarks>
public struct A11 {
    /// <summary>Which entity this belongs to, so a misaligned entity reference is visible.</summary>
    public int Owner;

    /// <summary>How many times a query body has visited it.</summary>
    public int Touches;
}

/// <summary>
///     Component 12 of the arity sweep. Sixteen of these exist because sixteen is
///     <c>QueryArityGenerator.MaxArity</c>.
/// </summary>
/// <remarks>
///     Deliberately identical to its fifteen siblings, and deliberately not in
///     <c>TestComponents.cs</c> — that file is one file so a test reading a chunk layout can see
///     every size that goes into it, and these are the opposite: the only thing distinguishing
///     arity <i>n</i> from arity <i>n + 1</i> in <c>QueryAritySweepTests</c> should be the number.
///     The file is named <c>*Components.cs</c> because that is how a file opts into the CA1051
///     exemption (.editorconfig § "ECS component declarations"): a component is a public mutable
///     field, since a property returning a copy would silently drop a write through a <c>ref</c>
///     into a chunk.
/// </remarks>
public struct A12 {
    /// <summary>Which entity this belongs to, so a misaligned entity reference is visible.</summary>
    public int Owner;

    /// <summary>How many times a query body has visited it.</summary>
    public int Touches;
}

/// <summary>
///     Component 13 of the arity sweep. Sixteen of these exist because sixteen is
///     <c>QueryArityGenerator.MaxArity</c>.
/// </summary>
/// <remarks>
///     Deliberately identical to its fifteen siblings, and deliberately not in
///     <c>TestComponents.cs</c> — that file is one file so a test reading a chunk layout can see
///     every size that goes into it, and these are the opposite: the only thing distinguishing
///     arity <i>n</i> from arity <i>n + 1</i> in <c>QueryAritySweepTests</c> should be the number.
///     The file is named <c>*Components.cs</c> because that is how a file opts into the CA1051
///     exemption (.editorconfig § "ECS component declarations"): a component is a public mutable
///     field, since a property returning a copy would silently drop a write through a <c>ref</c>
///     into a chunk.
/// </remarks>
public struct A13 {
    /// <summary>Which entity this belongs to, so a misaligned entity reference is visible.</summary>
    public int Owner;

    /// <summary>How many times a query body has visited it.</summary>
    public int Touches;
}

/// <summary>
///     Component 14 of the arity sweep. Sixteen of these exist because sixteen is
///     <c>QueryArityGenerator.MaxArity</c>.
/// </summary>
/// <remarks>
///     Deliberately identical to its fifteen siblings, and deliberately not in
///     <c>TestComponents.cs</c> — that file is one file so a test reading a chunk layout can see
///     every size that goes into it, and these are the opposite: the only thing distinguishing
///     arity <i>n</i> from arity <i>n + 1</i> in <c>QueryAritySweepTests</c> should be the number.
///     The file is named <c>*Components.cs</c> because that is how a file opts into the CA1051
///     exemption (.editorconfig § "ECS component declarations"): a component is a public mutable
///     field, since a property returning a copy would silently drop a write through a <c>ref</c>
///     into a chunk.
/// </remarks>
public struct A14 {
    /// <summary>Which entity this belongs to, so a misaligned entity reference is visible.</summary>
    public int Owner;

    /// <summary>How many times a query body has visited it.</summary>
    public int Touches;
}

/// <summary>
///     Component 15 of the arity sweep. Sixteen of these exist because sixteen is
///     <c>QueryArityGenerator.MaxArity</c>.
/// </summary>
/// <remarks>
///     Deliberately identical to its fifteen siblings, and deliberately not in
///     <c>TestComponents.cs</c> — that file is one file so a test reading a chunk layout can see
///     every size that goes into it, and these are the opposite: the only thing distinguishing
///     arity <i>n</i> from arity <i>n + 1</i> in <c>QueryAritySweepTests</c> should be the number.
///     The file is named <c>*Components.cs</c> because that is how a file opts into the CA1051
///     exemption (.editorconfig § "ECS component declarations"): a component is a public mutable
///     field, since a property returning a copy would silently drop a write through a <c>ref</c>
///     into a chunk.
/// </remarks>
public struct A15 {
    /// <summary>Which entity this belongs to, so a misaligned entity reference is visible.</summary>
    public int Owner;

    /// <summary>How many times a query body has visited it.</summary>
    public int Touches;
}
