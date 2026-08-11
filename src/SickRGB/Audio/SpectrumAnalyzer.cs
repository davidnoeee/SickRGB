namespace SickRGB.Audio;

/// <summary>How the visualiser turns levels into colour.</summary>
public enum AudioColourMode
{
    /// <summary>A rainbow across the frequencies, bass through treble.</summary>
    Spectrum,

    /// <summary>Your own five colours, one per part of the frequency range.</summary>
    Palette,

    /// <summary>One colour, brightness follows the level.</summary>
    Single,

    /// <summary>Green through amber to red as it gets louder, like a level meter.</summary>
    Meter,
}

/// <summary>Which part of the frequency range a device shows.</summary>
public enum AudioRange
{
    Full,
    Bass,
    LowMids,
    Mids,
    HighMids,
    Treble,
}

public static class AudioRanges
{
    /// <summary>
    /// The slice of the spectrum a range covers, as fractions from bass (0) to treble (1).
    /// The bands overlap slightly so neighbouring devices feel connected rather than
    /// cutting off abruptly at a boundary.
    /// </summary>
    public static (double Low, double High) Bounds(AudioRange range) => range switch
    {
        AudioRange.Bass => (0.00, 0.22),
        AudioRange.LowMids => (0.15, 0.42),
        AudioRange.Mids => (0.35, 0.62),
        AudioRange.HighMids => (0.55, 0.82),
        AudioRange.Treble => (0.75, 1.00),
        _ => (0.00, 1.00),
    };

    public static string Label(AudioRange range) => range switch
    {
        AudioRange.Bass => "Bass only",
        AudioRange.LowMids => "Low mids",
        AudioRange.Mids => "Mids",
        AudioRange.HighMids => "High mids",
        AudioRange.Treble => "Treble only",
        _ => "Whole range",
    };
}

/// <summary>Where the low frequencies sit across your layout.</summary>
public enum AudioLayout
{
    /// <summary>Bass on the left, treble on the right.</summary>
    LeftToRight,

    /// <summary>Bass on the right, treble on the left.</summary>
    RightToLeft,

    /// <summary>Bass in the middle, spreading outwards.</summary>
    BassInCentre,

    /// <summary>Bass at both edges, treble in the middle.</summary>
    BassAtEdges,
}

/// <summary>Tunable parts of the audio analysis, surfaced in the UI.</summary>
public sealed class AudioOptions
{
    /// <summary>Multiplies the signal before it is measured. Quiet sources need more.</summary>
    public double Gain = 2.0;

    /// <summary>How quickly bands are allowed to fall. Higher is smoother and lazier.</summary>
    public double Smoothing = 0.80;

    /// <summary>Level below which a band is treated as silence, to stop idle shimmer.</summary>
    public double NoiseGate = 0.03;

    /// <summary>Lowest frequency shown. Raising it drops rumble and room noise.</summary>
    public double MinHz = 40;

    /// <summary>Highest frequency shown. Most music has little of interest above ~12 kHz.</summary>
    public double MaxHz = 12000;
}

/// <summary>
/// Turns captured audio into a set of frequency bands.
///
/// The bands are spaced logarithmically because hearing is: an even split across
/// frequency would give almost every band to the treble and squash all the interesting
/// bass into the first one.
///
/// Rise is immediate and fall is smoothed, which is what makes a visualiser feel
/// connected to the music. Smoothing the rise as well would blunt every transient.
/// </summary>
public sealed class SpectrumAnalyzer
{
    /// <summary>Resolution of the analysis. A power of two, as the FFT requires.</summary>
    private const int FftSize = 2048;

    /// <summary>Bands produced. More than any keyboard needs, so lights can interpolate.</summary>
    public const int BandCount = 64;

    private readonly float[] _samples = new float[FftSize];
    private readonly double[] _real = new double[FftSize];
    private readonly double[] _imag = new double[FftSize];
    private readonly double[] _window = new double[FftSize];

    private readonly double[] _bands = new double[BandCount];
    private readonly double[] _smoothed = new double[BandCount];

    /// <summary>Per-band level, 0..1, after smoothing.</summary>
    public IReadOnlyList<double> Bands => _smoothed;

    /// <summary>Overall loudness, 0..1. Useful for effects that just want "how loud".</summary>
    public double Level { get; private set; }

    public SpectrumAnalyzer()
    {
        // Hann window: tapering the ends stops the FFT seeing a discontinuity where the
        // sample window wraps, which would smear energy across every band.
        for (int i = 0; i < FftSize; i++)
            _window[i] = 0.5 * (1.0 - Math.Cos(2.0 * Math.PI * i / (FftSize - 1)));
    }

    public void Update(AudioCapture capture, AudioOptions options, double delta)
    {
        if (!capture.ReadLatest(_samples, FftSize))
        {
            Decay(options, delta);
            return;
        }

        double gain = Math.Max(0.1, options.Gain);

        for (int i = 0; i < FftSize; i++)
        {
            _real[i] = _samples[i] * _window[i] * gain;
            _imag[i] = 0;
        }

        Fft(_real, _imag);

        int sampleRate = Math.Max(8000, capture.SampleRate);
        double binHz = (double)sampleRate / FftSize;
        double minHz = Math.Clamp(options.MinHz, 20, 2000);
        double maxHz = Math.Clamp(options.MaxHz, minHz + 100, sampleRate / 2.0);

        double rms = 0;

        for (int b = 0; b < BandCount; b++)
        {
            // Logarithmic edges, so each band covers a constant musical interval.
            double lowHz = minHz * Math.Pow(maxHz / minHz, (double)b / BandCount);
            double highHz = minHz * Math.Pow(maxHz / minHz, (b + 1.0) / BandCount);

            int lowBin = Math.Max(1, (int)(lowHz / binHz));
            int highBin = Math.Min(FftSize / 2 - 1, Math.Max(lowBin, (int)(highHz / binHz)));

            double peak = 0;
            for (int bin = lowBin; bin <= highBin; bin++)
            {
                double magnitude = Math.Sqrt(_real[bin] * _real[bin] + _imag[bin] * _imag[bin]) / (FftSize / 2.0);
                if (magnitude > peak) peak = magnitude;
            }

            // Work in decibels: sound is logarithmic, and a linear magnitude leaves
            // everything but the loudest peak invisible.
            double db = 20.0 * Math.Log10(peak + 1e-9);
            double level = Math.Clamp((db + 70.0) / 70.0, 0, 1);

            if (level < options.NoiseGate) level = 0;
            else level = (level - options.NoiseGate) / Math.Max(1e-6, 1 - options.NoiseGate);

            _bands[b] = level;
            rms += level * level;
        }

        Level = Math.Sqrt(rms / BandCount);
        Smooth(options, delta);
    }

    /// <summary>Instant rise, smoothed fall.</summary>
    private void Smooth(AudioOptions options, double delta)
    {
        // Stops just short of 1: at exactly 1 the bands would never fall at all.
        double smoothing = Math.Clamp(options.Smoothing, 0, 0.995);
        double fall = 1.0 - Math.Pow(smoothing, Math.Max(delta, 1e-4) * 60.0);

        for (int b = 0; b < BandCount; b++)
        {
            if (_bands[b] >= _smoothed[b]) _smoothed[b] = _bands[b];
            else _smoothed[b] += (_bands[b] - _smoothed[b]) * fall;
        }
    }

    private void Decay(AudioOptions options, double delta)
    {
        // Stops just short of 1: at exactly 1 the bands would never fall at all.
        double smoothing = Math.Clamp(options.Smoothing, 0, 0.995);
        double fall = 1.0 - Math.Pow(smoothing, Math.Max(delta, 1e-4) * 60.0);

        double total = 0;
        for (int b = 0; b < BandCount; b++)
        {
            _smoothed[b] += (0 - _smoothed[b]) * fall;
            total += _smoothed[b] * _smoothed[b];
        }
        Level = Math.Sqrt(total / BandCount);
    }

    /// <summary>
    /// Samples a band at a fractional position, so any number of lights can be mapped
    /// across the spectrum without steps between them.
    /// </summary>
    public double SampleAt(double position)
    {
        position = Math.Clamp(position, 0, 1) * (BandCount - 1);
        int low = (int)Math.Floor(position);
        int high = Math.Min(low + 1, BandCount - 1);
        double t = position - low;
        return _smoothed[low] * (1 - t) + _smoothed[high] * t;
    }

    /// <summary>
    /// Average level across a slice of the spectrum.
    ///
    /// Used for devices with too few lights to show a spectrum: a two-LED mouse cannot
    /// draw a shape, so it shows how loud its slice is instead.
    /// </summary>
    public double AverageBetween(double low, double high)
    {
        low = Math.Clamp(low, 0, 1);
        high = Math.Clamp(high, low, 1);

        int first = (int)Math.Floor(low * (BandCount - 1));
        int last = (int)Math.Ceiling(high * (BandCount - 1));
        if (last < first) last = first;

        double total = 0;
        for (int b = first; b <= last; b++) total += _smoothed[b];
        return total / (last - first + 1);
    }

    /// <summary>In-place iterative radix-2 Cooley-Tukey FFT.</summary>
    private static void Fft(double[] real, double[] imag)
    {
        int n = real.Length;

        // Bit-reversal permutation.
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;

            if (i < j)
            {
                (real[i], real[j]) = (real[j], real[i]);
                (imag[i], imag[j]) = (imag[j], imag[i]);
            }
        }

        for (int length = 2; length <= n; length <<= 1)
        {
            double angle = -2.0 * Math.PI / length;
            double wReal = Math.Cos(angle);
            double wImag = Math.Sin(angle);

            for (int i = 0; i < n; i += length)
            {
                double curReal = 1.0, curImag = 0.0;

                for (int k = 0; k < length / 2; k++)
                {
                    int even = i + k;
                    int odd = i + k + length / 2;

                    double tReal = real[odd] * curReal - imag[odd] * curImag;
                    double tImag = real[odd] * curImag + imag[odd] * curReal;

                    real[odd] = real[even] - tReal;
                    imag[odd] = imag[even] - tImag;
                    real[even] += tReal;
                    imag[even] += tImag;

                    double nextReal = curReal * wReal - curImag * wImag;
                    curImag = curReal * wImag + curImag * wReal;
                    curReal = nextReal;
                }
            }
        }
    }
}
