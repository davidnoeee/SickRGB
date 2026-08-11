using SickRGB.Core;

namespace SickRGB.Devices;

/// <summary>
/// A source of controllable lights.
///
/// Providers are deliberately thin: discover devices, push colours, release them.
/// Everything above this line (layout, effects, UI) is provider-agnostic, so adding
/// new hardware never touches the effect engine.
/// </summary>
public interface ILightProvider : IDisposable
{
    /// <summary>Stable identifier stored in settings.</summary>
    string Id { get; }

    /// <summary>Shown in the UI.</summary>
    string DisplayName { get; }

    /// <summary>One line explaining what this provider covers and what it needs.</summary>
    string Description { get; }

    /// <summary>False when the provider's prerequisites are missing (e.g. OpenRGB not running).</summary>
    bool IsAvailable { get; }

    /// <summary>Why the provider is unavailable, for the UI. Empty when fine.</summary>
    string UnavailableReason { get; }

    /// <summary>Finds every device this provider can drive right now.</summary>
    Task<IReadOnlyList<LightDevice>> DiscoverAsync(CancellationToken ct);

    /// <summary>
    /// Pushes one colour per zone. Returns false if the device has gone away, which
    /// tells the registry to rescan.
    /// </summary>
    bool Apply(LightDevice device, ReadOnlySpan<Rgb24> zoneColors);

    /// <summary>Hands lighting back to the hardware's own control.</summary>
    void Release(LightDevice device);
}
