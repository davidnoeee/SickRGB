namespace SickRGB.Audio;

/// <summary>
/// Works out which side a sound came from, by comparing the two channels.
///
/// Games pan positional audio between left and right, so the difference in energy between
/// the channels is a direct read on where something is. That is the same cue a person uses
/// to place a sound, which is what makes this usable as an accessibility aid: it puts the
/// information on the lights for anyone who cannot get it by ear.
///
/// Deliberately simple. Phase and time-of-arrival differences would give a finer bearing on
/// real recordings, but game audio is panned by amplitude, so amplitude is the honest signal
/// and a more elaborate method would mostly add lag.
/// </summary>
public sealed class DirectionAnalyzer
{
    /// <summary>
    /// Roughly 20 ms at 48 kHz. Long enough to be steady, short enough that a footstep
    /// still moves it while the sound is playing.
    /// </summary>
    private const int WindowSize = 1024;

    private readonly float[] _left = new float[WindowSize];
    private readonly float[] _right = new float[WindowSize];

    /// <summary>Where the sound is, -1 hard left through 0 centre to +1 hard right.</summary>
    public double Direction { get; private set; }

    /// <summary>How loud it is, 0..1.</summary>
    public double Level { get; private set; }

    /// <summary>
    /// How lopsided the sound is, 0..1. Near 0 means it is centred or spread across both
    /// sides, so the direction reading means little and the effect can say so.
    /// </summary>
    public double Confidence { get; private set; }

    public void Update(AudioCapture capture, AudioOptions options, double delta)
    {
        if (!capture.ReadLatestStereo(_left, _right, WindowSize))
        {
            Decay(options, delta);
            return;
        }

        double gain = Math.Max(0.1, options.Gain);

        // Energy per side. Root-mean-square rather than peak, because a single sample
        // spike should not swing the whole reading.
        double sumLeft = 0, sumRight = 0;
        for (int i = 0; i < WindowSize; i++)
        {
            double l = _left[i] * gain;
            double r = _right[i] * gain;
            sumLeft += l * l;
            sumRight += r * r;
        }

        double rmsLeft = Math.Sqrt(sumLeft / WindowSize);
        double rmsRight = Math.Sqrt(sumRight / WindowSize);
        double total = rmsLeft + rmsRight;

        double level = Math.Clamp(Math.Sqrt(Math.Max(rmsLeft, rmsRight)), 0, 1);

        // Below the gate there is nothing worth pointing at.
        if (level < options.NoiseGate || total < 1e-6)
        {
            Decay(options, delta);
            return;
        }

        level = (level - options.NoiseGate) / Math.Max(1e-6, 1 - options.NoiseGate);

        double balance = (rmsRight - rmsLeft) / total;
        double confidence = Math.Clamp(Math.Abs(balance) * 2.0, 0, 1);

        // Level rises instantly so a gunshot lands the moment it happens; direction and
        // confidence are eased, because a bearing that jitters is worse than useless.
        double smoothing = Math.Clamp(options.Smoothing, 0, 0.995);
        double ease = 1.0 - Math.Pow(smoothing, Math.Max(delta, 1e-4) * 60.0);

        Level = level >= Level ? level : Level + (level - Level) * ease;
        Direction += (Math.Clamp(balance, -1, 1) - Direction) * ease;
        Confidence += (confidence - Confidence) * ease;
    }

    private void Decay(AudioOptions options, double delta)
    {
        double smoothing = Math.Clamp(options.Smoothing, 0, 0.995);
        double ease = 1.0 - Math.Pow(smoothing, Math.Max(delta, 1e-4) * 60.0);

        Level += (0 - Level) * ease;
        Confidence += (0 - Confidence) * ease;
        // Direction is left where it was: fading it to centre would look like the sound
        // moved, when in truth it simply stopped.
    }
}
