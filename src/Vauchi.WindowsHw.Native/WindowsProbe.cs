// SPDX-FileCopyrightText: 2026 Mattia Egloff <mattia.egloff@pm.me>
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Threading.Tasks;
using Vauchi.Contract;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;
using Windows.Devices.SmartCards;

namespace Vauchi.WindowsHw.Native;

/// <summary>
/// What this machine has, asked of Windows once at start (ADR-077 item
/// 7). The shell then claims no more than this and no more than its
/// backends run. A device whose question fails is reported as absent: a
/// probe that cannot ask must not claim.
/// </summary>
public static class WindowsProbe
{
    public static async Task<DeviceCapabilities> ProbeAsync() => new()
    {
        HasBle = await Has(BluetoothLowEnergy).ConfigureAwait(false),
        HasNfc = await Has(SmartCardReader).ConfigureAwait(false),
        HasCamera = await Has(Camera).ConfigureAwait(false),
        // As linux-hw reports it: whether the network is there and what it
        // costs reaches Core as its own events.
        HasInternet = true,
        Platform = Platform.Desktop,
    };

    private static async Task<bool> Has(Func<Task<bool>> ask)
    {
        try
        {
            return await ask().ConfigureAwait(false);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static async Task<bool> BluetoothLowEnergy()
    {
        BluetoothAdapter? adapter = await BluetoothAdapter.GetDefaultAsync();
        // Both roles: Core dials and is dialled in one exchange.
        return adapter is { IsLowEnergySupported: true, IsCentralRoleSupported: true, IsPeripheralRoleSupported: true };
    }

    private static async Task<bool> SmartCardReader()
    {
        DeviceInformationCollection readers =
            await DeviceInformation.FindAllAsync(Windows.Devices.SmartCards.SmartCardReader.GetDeviceSelector());
        return readers.Count > 0;
    }

    private static async Task<bool> Camera()
    {
        DeviceInformationCollection cameras = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);
        return cameras.Count > 0;
    }
}
