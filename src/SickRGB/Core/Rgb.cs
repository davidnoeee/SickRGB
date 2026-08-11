namespace SickRGB.Core;

/// <summary>8-bit colour as sent to the keyboard.</summary>
public readonly record struct Rgb24(byte R, byte G, byte B)
{
    public static readonly Rgb24 Black = new(0, 0, 0);

    public System.Windows.Media.Color ToMediaColor() => System.Windows.Media.Color.FromRgb(R, G, B);

    public static Rgb24 FromMediaColor(System.Windows.Media.Color c) => new(c.R, c.G, c.B);

    public string ToHex() => $"#{R:X2}{G:X2}{B:X2}";

    public static Rgb24 FromHex(string hex)
    {
        hex = hex.Trim().TrimStart('#');
        if (hex.Length == 3)
            hex = string.Concat(hex[0], hex[0], hex[1], hex[1], hex[2], hex[2]);
        if (hex.Length != 6 || !uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out uint v))
            return Black;
        return new Rgb24((byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }
}

/// <summary>
/// Linear-ish floating point colour used inside the effect pipeline, so that
/// blending, fading and summing multiple effects don't quantise badly.
/// </summary>
public struct RgbF
{
    public double R, G, B;

    public RgbF(double r, double g, double b) { R = r; G = g; B = b; }

    public static readonly RgbF Black = new(0, 0, 0);

    public static RgbF From(Rgb24 c) => new(c.R / 255.0, c.G / 255.0, c.B / 255.0);

    public static RgbF operator +(RgbF a, RgbF b) => new(a.R + b.R, a.G + b.G, a.B + b.B);
    public static RgbF operator *(RgbF a, double s) => new(a.R * s, a.G * s, a.B * s);

    public RgbF Clamped() => new(Math.Clamp(R, 0, 1), Math.Clamp(G, 0, 1), Math.Clamp(B, 0, 1));

    /// <summary>Linear interpolation towards <paramref name="other"/>.</summary>
    public RgbF Lerp(RgbF other, double t) =>
        new(R + (other.R - R) * t, G + (other.G - G) * t, B + (other.B - B) * t);

    /// <summary>Convert to 8-bit, applying master brightness and a mild gamma curve.</summary>
    public Rgb24 ToRgb24(double brightness = 1.0, double gamma = 1.0)
    {
        double r = Math.Clamp(R, 0, 1) * brightness;
        double g = Math.Clamp(G, 0, 1) * brightness;
        double b = Math.Clamp(B, 0, 1) * brightness;

        if (Math.Abs(gamma - 1.0) > 0.001)
        {
            r = Math.Pow(r, gamma);
            g = Math.Pow(g, gamma);
            b = Math.Pow(b, gamma);
        }

        return new Rgb24(
            (byte)Math.Clamp(Math.Round(r * 255.0), 0, 255),
            (byte)Math.Clamp(Math.Round(g * 255.0), 0, 255),
            (byte)Math.Clamp(Math.Round(b * 255.0), 0, 255));
    }

    /// <summary>HSV helper (h in turns 0..1, s and v in 0..1).</summary>
    public static RgbF FromHsv(double h, double s, double v)
    {
        h = h - Math.Floor(h);           // wrap into 0..1
        double i = Math.Floor(h * 6.0);
        double f = h * 6.0 - i;
        double p = v * (1 - s);
        double q = v * (1 - f * s);
        double t = v * (1 - (1 - f) * s);

        return ((int)i % 6) switch
        {
            0 => new RgbF(v, t, p),
            1 => new RgbF(q, v, p),
            2 => new RgbF(p, v, t),
            3 => new RgbF(p, q, v),
            4 => new RgbF(t, p, v),
            _ => new RgbF(v, p, q),
        };
    }

    /// <summary>Returns (hue in turns, saturation, value).</summary>
    public (double H, double S, double V) ToHsv()
    {
        double max = Math.Max(R, Math.Max(G, B));
        double min = Math.Min(R, Math.Min(G, B));
        double d = max - min;

        double h = 0;
        if (d > 1e-9)
        {
            if (max == R) h = ((G - B) / d + (G < B ? 6 : 0)) / 6.0;
            else if (max == G) h = ((B - R) / d + 2) / 6.0;
            else h = ((R - G) / d + 4) / 6.0;
        }
        double s = max <= 1e-9 ? 0 : d / max;
        return (h, s, max);
    }
}
