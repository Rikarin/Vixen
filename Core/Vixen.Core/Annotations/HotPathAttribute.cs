// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Core;

/// <summary>
///     Declares that a member runs inside the frame loop and must not allocate. It is a contract for
///     <c>VXHP0001</c> and for whoever edits the method next, not a hint to the JIT.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The summary above named an "allocation analyzer" for a year and there was none</b>
///         (#1161), which made this attribute read from a call site exactly like a checked contract
///         and behave like a comment. <c>Core/Vixen.Core.Analyzers</c> is the analyzer now; the rule
///         is <c>VXHP0001</c> and every <c>Core/</c> project runs it, which
///         <c>TreatWarningsAsErrors</c> makes a build failure.
///     </para>
///     <para>
///         <b>What it checks is the marked body and nothing deeper.</b> A <c>new</c>, an array, a
///         boxed value, a capturing lambda or a built string written here is reported; an allocation
///         inside a method this one calls is not, and cannot be. The instrument for that half already
///         exists and is stronger — <c>Vixen.Testing.Measured</c> counts allocated bytes across real
///         work and asserts exactly zero — so the two are complements rather than substitutes: the
///         counter sees through a call on the paths a test drives, and the rule sees the edit on
///         every build, including on the members no test measures.
///     </para>
///     <para>
///         ⚠ <b>It says nothing about logging.</b> Doc 13's "no logging in the innermost loops" is a
///         convention, and a claim that this attribute enforced it was false in two files (#344).
///     </para>
/// </remarks>
[AttributeUsage(
    AttributeTargets.Method | AttributeTargets.Property | AttributeTargets.Constructor | AttributeTargets.Class |
    AttributeTargets.Struct)]
public sealed class HotPathAttribute : Attribute;
