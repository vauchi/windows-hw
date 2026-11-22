// SPDX-FileCopyrightText: 2026 Mattia Egloff <mattia.egloff@pm.me>
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Linq;
using Vauchi.Contract;
using Xunit;

namespace Vauchi.WindowsHw.Tests;

/// <summary>
/// What the Windows backends hand the contract, kept free of any Windows
/// type so it is tested where there is no Windows.
/// </summary>
public class NativeMappingTests
{
    private static readonly Guid Exchange = Guid.Parse("0000fd6f-0000-1000-8000-00805f9b34fb");

    [Fact]
    public void AnAdvertisementIsReportedWithItsAddressItsSignalAndItsServiceUuidsInNetworkOrder()
    {
        Event observed = BleObservations.Observed(0x0000_1122_3344_5566, -57, new[] { Exchange });

        Assert.Equal(
            "{\"BleAdvertisementObserved\":{\"id\":\"18838586676582\",\"rssi\":-57,\"service_uuids\":"
            + "[{\"Normalized\":[0,0,253,111,0,0,16,0,128,0,0,128,95,155,52,251]}]}}",
            observed.Wire());
    }

    [Fact]
    public void WhatIsReportedIsWhatTheContractsReaderAccepts()
    {
        Event observed = BleObservations.Observed(ulong.MaxValue, short.MinValue, Enumerable.Repeat(Exchange, 32).ToArray());

        Event read = ContractJson.DecodeEvent(ContractJson.EncodeEvent(observed));

        Assert.Equal(observed.Wire(), read.Wire());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(32)]
    [InlineData(33)]
    [InlineData(500)]
    public void NoMoreServiceUuidsAreReportedThanTheContractAccepts(int advertised)
    {
        Guid[] uuids = Enumerable.Range(0, advertised)
            .Select(index => new Guid(index, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0))
            .ToArray();

        var observed = Assert.IsType<Event.BleAdvertisementObserved>(BleObservations.Observed(1, -40, uuids));

        Assert.Equal(Math.Min(advertised, 32), observed.Value.ServiceUuids.Count);
        Assert.Equal(
            uuids.Take(32).Select(uuid => uuid.ToByteArray(bigEndian: true)),
            observed.Value.ServiceUuids.Select(uuid => Assert.IsType<BleServiceUuid.Normalized>(uuid).Value));
    }

    [Theory]
    [InlineData(unchecked((int)0x800710DF), HardwareFailure.Unavailable)]
    [InlineData(unchecked((int)0x8007048F), HardwareFailure.Unavailable)]
    [InlineData(unchecked((int)0x80070490), HardwareFailure.Unavailable)]
    [InlineData(unchecked((int)0x80070005), HardwareFailure.PermissionDenied)]
    [InlineData(unchecked((int)0x80004005), HardwareFailure.Failed)]
    [InlineData(0, HardwareFailure.Failed)]
    public void AnOperatingSystemErrorCodeIsReportedAsWhatItMeansForThePort(int hresult, HardwareFailure expected)
    {
        Assert.Equal(expected, OsFailures.From(hresult));
    }

    [Theory]
    [InlineData("0000fd6f-0000-1000-8000-00805f9b34fb", true)]
    [InlineData("0000FD6F-0000-1000-8000-00805F9B34FB", true)]
    [InlineData("{0000fd6f-0000-1000-8000-00805f9b34fb}", false)]
    [InlineData("0000fd6f00001000800000805f9b34fb", false)]
    [InlineData("fd6f", false)]
    [InlineData("", false)]
    [InlineData("0000fd6f-0000-1000-8000-00805f9b34fb ", false)]
    [InlineData("not-a-uuid", false)]
    public void AServiceUuidFromCoreIsReadInItsCanonicalFormOnly(string text, bool accepted)
    {
        Assert.Equal(accepted, BleObservations.TryServiceUuid(text, out Guid uuid));
        Assert.Equal(accepted ? Exchange : Guid.Empty, uuid);
    }
}
