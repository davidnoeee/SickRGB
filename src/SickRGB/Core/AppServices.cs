using System.Windows.Media;
using SickRGB.Devices;
using SickRGB.Effects;

namespace SickRGB.Core;

/// <summary>
/// The objects every page needs. Set up once at startup and reached through
/// <see cref="Current"/> rather than threaded through constructors.
/// </summary>
public sealed class AppServices
{
    public required AppSettings Settings { get; init; }
    public required DeviceRegistry Registry { get; init; }
    public required EffectEngine Engine { get; init; }

    public static AppServices Current { get; private set; } = null!;

    public static void Initialise(AppServices services) => Current = services;
}

/// <summary>Reads the user's Windows accent colour so the app matches the rest of the system.</summary>
public static class AccentColors
{
    private static readonly Color Fallback = Color.FromRgb(0xFF, 0x6A, 0x2B);

    /// <summary>The accent colour, or a sensible fallback if it cannot be read.</summary>
    public static Color GetAccent()
    {
        try
        {
            // DWM stores the accent as a little-endian ABGR DWORD.
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            if (key?.GetValue("AccentColor") is int raw)
            {
                byte r = (byte)(raw & 0xFF);
                byte g = (byte)((raw >> 8) & 0xFF);
                byte b = (byte)((raw >> 16) & 0xFF);

                // Very dark or near-grey accents read poorly on a dark surface.
                if (r + g + b > 90) return Color.FromRgb(r, g, b);
            }
        }
        catch { /* fall through */ }

        return Fallback;
    }

    /// <summary>Lightens a colour by moving it toward white.</summary>
    public static Color Lighten(Color c, double amount) => Color.FromRgb(
        (byte)Math.Clamp(c.R + (255 - c.R) * amount, 0, 255),
        (byte)Math.Clamp(c.G + (255 - c.G) * amount, 0, 255),
        (byte)Math.Clamp(c.B + (255 - c.B) * amount, 0, 255));

    /// <summary>Darkens a colour by moving it toward black.</summary>
    public static Color Darken(Color c, double amount) => Color.FromRgb(
        (byte)Math.Clamp(c.R * (1 - amount), 0, 255),
        (byte)Math.Clamp(c.G * (1 - amount), 0, 255),
        (byte)Math.Clamp(c.B * (1 - amount), 0, 255));
}
