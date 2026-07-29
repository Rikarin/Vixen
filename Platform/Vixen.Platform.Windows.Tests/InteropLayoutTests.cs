// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Xunit;

namespace Vixen.Platform.Windows.Tests;

/// <summary>
///     The shapes the kernel writes into, checked against what the header says they are.
/// </summary>
/// <remarks>
///     A structure whose fields are one padding byte out of place does not fail to compile and does
///     not throw: it reports the wrong number of cores, or pins a thread to the wrong processor
///     group, on the machines that have more than one — which is to say on the machines nobody
///     tests on. The offsets below are from <c>winnt.h</c> for a 64-bit process and are the reason
///     these are structures with a documented layout rather than pointer arithmetic at the call
///     site.
/// </remarks>
public class InteropLayoutTests {
    [Fact]
    public void GroupAffinityMatchesTheHeader() {
        Assert.Equal(16, Unsafe.SizeOf<GroupAffinity>());
        Assert.Equal(0, OffsetOf<GroupAffinity>(nameof(GroupAffinity.Mask)));
        Assert.Equal(8, OffsetOf<GroupAffinity>(nameof(GroupAffinity.Group)));
    }

    [Fact]
    public void ProcessorRelationshipMatchesTheHeader() {
        Assert.Equal(0, OffsetOf<ProcessorRelationship>(nameof(ProcessorRelationship.Flags)));
        Assert.Equal(1, OffsetOf<ProcessorRelationship>(nameof(ProcessorRelationship.EfficiencyClass)));

        // Twenty reserved bytes, then the group count, then eight-byte alignment for the first mask.
        Assert.Equal(22, OffsetOf<ProcessorRelationship>(nameof(ProcessorRelationship.GroupCount)));
        Assert.Equal(24, OffsetOf<ProcessorRelationship>(nameof(ProcessorRelationship.FirstGroupMask)));
    }

    [Fact]
    public void TheProcessorInformationEntryHasItsUnionAtEight() {
        Assert.Equal(0, OffsetOf<LogicalProcessorInformation>(nameof(LogicalProcessorInformation.Relationship)));
        Assert.Equal(4, OffsetOf<LogicalProcessorInformation>(nameof(LogicalProcessorInformation.Size)));
        Assert.Equal(8, OffsetOf<LogicalProcessorInformation>(nameof(LogicalProcessorInformation.Processor)));
    }

    /// <summary>
    ///     Four bytes of status and two <c>DWORD</c>s. <c>BatteryLifeTime</c> being unsigned is what
    ///     makes "unknown" comparable with <see cref="uint.MaxValue" /> rather than with −1.
    /// </summary>
    [Fact]
    public void SystemPowerStatusMatchesTheHeader() {
        Assert.Equal(12, Unsafe.SizeOf<SystemPowerStatus>());
        Assert.Equal(3, OffsetOf<SystemPowerStatus>(nameof(SystemPowerStatus.SystemStatusFlag)));
        Assert.Equal(4, OffsetOf<SystemPowerStatus>(nameof(SystemPowerStatus.BatteryLifeTime)));
    }

    static int OffsetOf<T>(string field) => (int)Marshal.OffsetOf<T>(field);
}
