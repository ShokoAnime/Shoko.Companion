using System;

namespace Shoko.Companion.Configuration;

/// <summary>
/// Single source of truth for the device identifier sent to the server when
/// authenticating. This is intentionally not configurable.
/// </summary>
public static class DeviceInfo
{
    /// <summary>
    /// The device name sent to the server for authentication/identification.
    /// </summary>
    public static string DeviceName => $"Shoko Companion ({Environment.MachineName})";
}
