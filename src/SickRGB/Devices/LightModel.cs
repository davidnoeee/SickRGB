using SickRGB.Core;

namespace SickRGB.Devices;

/// <summary>
/// What kind of hardware a device is. Drives the default canvas placement and
/// decides which device a key press or mouse click radiates from.
/// </summary>
public enum DeviceRole
{
    Keyboard,
    Mouse,
    Mousepad,
    Motherboard,
    Memory,
    Gpu,
    Cooler,
    Fan,
    Case,
    Headset,
    LightStrip,
    Other,
}

/// <summary>
/// One independently addressable light. A Magma zone, a mouse logo, a single
/// addressable LED on a fan ring - all of them are one of these.
/// </summary>
public sealed class LightZone
{
    public required string Name { get; init; }

    /// <summary>Index within the owning device, used when writing colours back.</summary>
    public required int Index { get; init; }

    /// <summary>Placement inside the device's own box, in layout units.</summary>
    public double LocalX { get; set; }
    public double LocalY { get; set; }
    public double Width { get; set; } = 24;
    public double Height { get; set; } = 24;

    /// <summary>Centre of this light on the shared canvas. Recomputed whenever the layout changes.</summary>
    public double WorldX { get; internal set; }
    public double WorldY { get; internal set; }

    /// <summary>Position within the canvas bounding box, 0..1 on each axis.</summary>
    public double NormX { get; internal set; }
    public double NormY { get; internal set; }

    /// <summary>Most recent colour written, for the UI preview.</summary>
    public Rgb24 Current { get; internal set; }
}

/// <summary>
/// An addressable header whose LED count must be entered manually - a motherboard ARGB
/// header, a fan hub channel, a case strip connector.
/// </summary>
public sealed class ResizableHeader
{
    public required int ZoneIndex { get; init; }
    public required string Name { get; init; }
    public required int CurrentLeds { get; init; }
    public required int MinLeds { get; init; }
    public required int MaxLeds { get; init; }
}

/// <summary>A piece of hardware exposing one or more <see cref="LightZone"/>s.</summary>
public sealed class LightDevice
{
    /// <summary>Stable identity across restarts. Used as the settings key.</summary>
    public required string Key { get; init; }

    public required string Name { get; init; }

    /// <summary>Which provider owns this device ("Native", "OpenRGB", ...).</summary>
    public required string ProviderId { get; init; }

    public required DeviceRole Role { get; set; }

    public required IReadOnlyList<LightZone> Zones { get; init; }

    /// <summary>Free-form detail shown in the UI (firmware, transport, vendor).</summary>
    public string Details { get; set; } = "";

    /// <summary>Provider-private state (an open handle, an OpenRGB device index, ...).</summary>
    public object? Tag { get; set; }

    /// <summary>
    /// Headers on this device whose strip length has to be stated by hand, because the
    /// hardware cannot measure it. Empty for everything else.
    /// </summary>
    public List<ResizableHeader> ResizableHeaders { get; } = new();

    /// <summary>Natural size of the device's box in layout units.</summary>
    public double Width { get; set; } = 100;
    public double Height { get; set; } = 40;

    /// <summary>Top-left of the device's box on the canvas.</summary>
    public double X { get; set; }
    public double Y { get; set; }

    /// <summary>Clockwise rotation about the device's centre, in degrees.</summary>
    public double Rotation { get; set; }

    /// <summary>
    /// Uniform size multiplier. Uniform on purpose: stretching a device out of its real
    /// proportions would make the distances effects rely on describe something that does
    /// not exist.
    /// </summary>
    public double Scale { get; set; } = 1.0;

    /// <summary>Size as drawn and as used for distance, after scaling.</summary>
    public double ScaledWidth => Width * Scale;
    public double ScaledHeight => Height * Scale;

    /// <summary>User-controlled: whether this device participates at all.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Flips zone order, for hardware mounted the other way round.</summary>
    public bool Reversed { get; set; }

    /// <summary>
    /// Cap on how often this device is written to, in updates per second. 0 means as
    /// fast as frames are produced.
    ///
    /// Some hardware sits on a slow bus and cannot keep up with a 60 fps effect. Sending
    /// faster than it can absorb does not make it smoother - it just builds a backlog, so
    /// what you see falls behind what the effect is actually doing.
    /// </summary>
    public int MaxUpdatesPerSecond { get; set; }

    /// <summary>
    /// The rate the provider considers sensible for this hardware, before any choice of
    /// yours. Shown as the "Automatic" option so it is clear what that actually means.
    /// </summary>
    public int DefaultMaxUpdatesPerSecond { get; set; }

    public int ZoneCount => Zones.Count;

    /// <summary>
    /// Recomputes every light's position on the canvas from the device's placement,
    /// scale and rotation.
    ///
    /// Effects measure distance between these points, so rotating or scaling a device
    /// genuinely changes how an effect travels across it - the picture and the behaviour
    /// stay in agreement.
    /// </summary>
    public void UpdateWorldPositions()
    {
        double radians = Rotation * Math.PI / 180.0;
        double cos = Math.Cos(radians);
        double sin = Math.Sin(radians);

        double halfWidth = ScaledWidth / 2.0;
        double halfHeight = ScaledHeight / 2.0;
        double centreX = X + halfWidth;
        double centreY = Y + halfHeight;

        foreach (var z in Zones)
        {
            double lx = Reversed ? Width - z.LocalX - z.Width : z.LocalX;

            // Centre of this light within the device box, scaled.
            double offsetX = (lx + z.Width / 2.0) * Scale - halfWidth;
            double offsetY = (z.LocalY + z.Height / 2.0) * Scale - halfHeight;

            z.WorldX = centreX + offsetX * cos - offsetY * sin;
            z.WorldY = centreY + offsetX * sin + offsetY * cos;
        }
    }

    /// <summary>Lays zones out as a single horizontal strip - the common case.</summary>
    public static List<LightZone> StripZones(int count, string namePrefix, double zoneWidth = 40, double zoneHeight = 34)
    {
        var zones = new List<LightZone>(count);
        for (int i = 0; i < count; i++)
        {
            zones.Add(new LightZone
            {
                Name = count == 1 ? namePrefix : $"{namePrefix} {i + 1}",
                Index = i,
                LocalX = i * zoneWidth,
                LocalY = 0,
                Width = zoneWidth,
                Height = zoneHeight,
            });
        }
        return zones;
    }

    /// <summary>
    /// Lays zones out on a grid. Used for devices with many LEDs (fan rings, strips,
    /// RAM sticks) so they occupy a sensible area instead of a mile-wide line.
    /// </summary>
    public static List<LightZone> GridZones(int count, string namePrefix, int maxColumns = 16, double cell = 18)
    {
        var zones = new List<LightZone>(count);
        int columns = Math.Min(Math.Max(count, 1), maxColumns);

        for (int i = 0; i < count; i++)
        {
            int col = i % columns;
            int row = i / columns;
            zones.Add(new LightZone
            {
                Name = $"{namePrefix} {i + 1}",
                Index = i,
                LocalX = col * cell,
                LocalY = row * cell,
                Width = cell,
                Height = cell,
            });
        }
        return zones;
    }
}
