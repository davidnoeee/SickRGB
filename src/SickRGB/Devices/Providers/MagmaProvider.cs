using SickRGB.Core;
using SickRGB.Hardware;

namespace SickRGB.Devices.Providers;

/// <summary>
/// Native driver for the Turtle Beach / ROCCAT Magma. Protocol verified against
/// real hardware - see PROTOCOL.md.
/// </summary>
public sealed class MagmaProvider : ILightProvider
{
    // Layout units are millimetres, so devices sit on the canvas at true relative scale.
    private const double KeyboardWidth = 440;
    private const double KeyboardHeight = 140;

    private MagmaDevice? _device;

    /// <summary>
    /// The device object handed to the registry, cached alongside the handle.
    ///
    /// Re-using it keeps zone colours and canvas identity stable across a rescan, so a
    /// rescan triggered by some unrelated device does not make the keyboard blink.
    /// </summary>
    private LightDevice? _lightDevice;

    public string Id => "native.magma";
    public string DisplayName => "Magma keyboard";
    public string Description => "Turtle Beach and ROCCAT Magma keyboards, controlled directly. Nothing else to install.";
    public bool IsAvailable => true;
    public string UnavailableReason => "";

    public Task<IReadOnlyList<LightDevice>> DiscoverAsync(CancellationToken ct)
    {
        var found = new List<LightDevice>();

        // A healthy handle is left completely alone. Closing and re-opening it would
        // blank the LEDs and re-run the init handshake - visible as a flicker, and a
        // ~1 second stall while WaitUntilReady polls the device.
        if (_device is { IsConnected: true } && _lightDevice is not null)
        {
            found.Add(_lightDevice);
            return Task.FromResult<IReadOnlyList<LightDevice>>(found);
        }

        _device?.Dispose();
        _lightDevice = null;
        _device = MagmaDevice.Open();

        if (_device is not null)
        {
            double zoneWidth = KeyboardWidth / MagmaDevice.ZoneCount;
            var zones = LightDevice.StripZones(MagmaDevice.ZoneCount, "Zone", zoneWidth, KeyboardHeight);

            _lightDevice = new LightDevice
            {
                Key = $"native.magma:{_device.VendorId:X4}:{_device.ProductId:X4}",
                Name = _device.ProductName,
                ProviderId = Id,
                Role = DeviceRole.Keyboard,
                Zones = zones,
                Details = $"USB {_device.VendorId:X4}:{_device.ProductId:X4}  -  firmware {_device.FirmwareVersion}",
                Width = KeyboardWidth,
                Height = KeyboardHeight,
                Tag = _device,
            };
            found.Add(_lightDevice);
        }

        return Task.FromResult<IReadOnlyList<LightDevice>>(found);
    }

    public bool Apply(LightDevice device, ReadOnlySpan<Rgb24> zoneColors)
    {
        if (device.Tag is not MagmaDevice dev || !dev.IsConnected) return false;
        return dev.SendZones(zoneColors);
    }

    public void Release(LightDevice device)
    {
        if (device.Tag is MagmaDevice dev) dev.ReleaseToHardware();
    }

    public void Dispose()
    {
        _device?.Dispose();
        _device = null;
        _lightDevice = null;
    }
}
