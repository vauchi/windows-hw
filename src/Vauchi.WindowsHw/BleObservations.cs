// SPDX-FileCopyrightText: 2026 Mattia Egloff <mattia.egloff@pm.me>
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Globalization;
using Vauchi.Contract;

namespace Vauchi.WindowsHw;

/// <summary>
/// What a Bluetooth backend hands the contract, in types no OS owns, so
/// the rules are tested where there is no radio.
/// </summary>
public static class BleObservations
{
    /// <summary>The most service UUIDs an observation carries (<c>src/ble_observation.rs</c>).</summary>
    public const int MaxServiceUuids = 32;

    /// <summary>
    /// One advertisement as the contract's observation. The id is the
    /// device address in decimal, the form <c>BleConnect</c> gets back.
    /// Windows hands out expanded UUIDs, so each is reported as
    /// <c>Normalized</c>, in network order; any past the contract's bound
    /// are left out before the event is built.
    /// </summary>
    public static Event Observed(ulong address, short rssi, IReadOnlyList<Guid> serviceUuids)
    {
        ArgumentNullException.ThrowIfNull(serviceUuids);
        int kept = Math.Min(serviceUuids.Count, MaxServiceUuids);
        var uuids = new BleServiceUuid[kept];
        for (int index = 0; index < kept; index++)
        {
            uuids[index] = new BleServiceUuid.Normalized(serviceUuids[index].ToByteArray(bigEndian: true));
        }
        return new Event.BleAdvertisementObserved(new BleAdvertisementObservation
        {
            Id = address.ToString(CultureInfo.InvariantCulture),
            Rssi = rssi,
            ServiceUuids = uuids,
        });
    }

    /// <summary>
    /// Core's service UUID, in the canonical 8-4-4-4-12 form only: a
    /// lenient parse would start a scan for something Core did not name.
    /// </summary>
    public static bool TryServiceUuid(string text, out Guid uuid)
    {
        // TryParseExact trims white space; the length check does not.
        if (text is { Length: CanonicalLength } && Guid.TryParseExact(text, "D", out uuid))
        {
            return true;
        }
        uuid = Guid.Empty;
        return false;
    }

    private const int CanonicalLength = 36;

    /// <summary>
    /// The central behind a Windows device id, in the decimal form
    /// <see cref="Observed"/> gives a discovered device. Windows ends the
    /// id with the remote address as six hex pairs; anything else is
    /// reported unaddressed, Core's wildcard for the link.
    /// </summary>
    public static string CentralId(string osDeviceId)
    {
        ArgumentNullException.ThrowIfNull(osDeviceId);
        int dash = osDeviceId.LastIndexOf('-');
        if (dash < 0 || osDeviceId.Length - dash - 1 != AddressLength)
        {
            return "";
        }
        ulong address = 0;
        for (int index = 0; index < AddressLength; index++)
        {
            char character = osDeviceId[dash + 1 + index];
            if (index % 3 == 2)
            {
                if (character != ':')
                {
                    return "";
                }
                continue;
            }
            if (!char.IsAsciiHexDigit(character))
            {
                return "";
            }
            address = (address << 4) | (uint)Convert.ToInt32(character.ToString(), 16);
        }
        return address.ToString(CultureInfo.InvariantCulture);
    }

    private const int AddressLength = 17;
}

/// <summary>What a Windows error code means for a port.</summary>
public static class OsFailures
{
    /// <summary>ERROR_DEVICE_NOT_READY: the watcher's answer while the radio is off.</summary>
    private const int DeviceNotReady = unchecked((int)0x800710DF);

    /// <summary>ERROR_DEVICE_NOT_CONNECTED.</summary>
    private const int DeviceNotConnected = unchecked((int)0x8007048F);

    /// <summary>ERROR_NOT_FOUND: no adapter, no reader.</summary>
    private const int NotFound = unchecked((int)0x80070490);

    /// <summary>E_ACCESSDENIED: the user or a policy refused the app.</summary>
    private const int AccessDenied = unchecked((int)0x80070005);

    public static HardwareFailure From(int hresult) => hresult switch
    {
        DeviceNotReady or DeviceNotConnected or NotFound => HardwareFailure.Unavailable,
        AccessDenied => HardwareFailure.PermissionDenied,
        _ => HardwareFailure.Failed,
    };
}
