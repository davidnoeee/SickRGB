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

    /// <summary>
    /// Slow-moving average energy per side, used to cancel a permanently lopsided output.
    ///
    /// The idea: over minutes, a game sends roughly as much sound to each side, so any
    /// lasting imbalance is the listener's own volume settings rather than the game. Divide
    /// each channel by its own long-run average and that setting disappears, while the
    /// short-term differences that actually carry direction survive untouched.
    ///
    /// This is what makes the effect work for someone who has turned one side almost off.
    /// </summary>
    private double _baselineLeft = 1e-4;
    private double _baselineRight = 1e-4;

    /// <summary>Seconds for the baseline to follow a change. Long, so real sounds do not move it.</summary>
    private const double BaselineSeconds = 12.0;

    /// <summary>
    /// How far the automatic correction is allowed to go.
    ///
    /// Generous on purpose. Someone who has silenced one side almost completely can be
    /// 40 dB or more apart, and a cap below that would leave the reading permanently
    /// pinned to the loud side, which is the exact failure this exists to prevent. The
    /// limit is only here to stop the correction running away during near-silence.
    /// </summary>
    private const double MaxCompensationDb = 60.0;

    /// <summary>
    /// How far apart the two sides must be before a sound is treated as fully to one side.
    ///
    /// Twenty decibels is about where a panned game sound stops feeling like it is between
    /// the ears and starts feeling like it is beside you. Larger and real panning barely
    /// moves the spot; smaller and everything slams to an edge.
    /// </summary>
    private const double FullSideDb = 20.0;

    /// <summary>
    /// How far the manual nudge reaches, end to end. Matched to the span above, so the far
    /// end of the slider is exactly enough to carry a centred sound fully to one side.
    /// </summary>
    public const double TrimRangeDb = 40.0;

    /// <summary>The correction currently being applied, for the UI to show.</summary>
    public double MeasuredImbalance { get; private set; }

    /// <summary>
    /// The same measurement in decibels, right minus left, which is the unit anything
    /// else that measures a channel balance will report.
    /// </summary>
    public double MeasuredImbalanceDb { get; private set; }

    /// <summary>
    /// Throws away what has been measured so far and starts again.
    ///
    /// Worth having because the measurement is deliberately slow: after changing a volume
    /// slider it would otherwise take a minute or so to catch up, and there is no way to
    /// tell from looking whether the number on screen is the new setting or the old one.
    /// </summary>
    public void ResetBalance()
    {
        _baselineLeft = 1e-4;
        _baselineRight = 1e-4;
        MeasuredImbalance = 0;
        MeasuredImbalanceDb = 0;
        _settleSeconds = 0;
    }

    /// <summary>
    /// How long the baselines have been building. Until they have had a few seconds of
    /// sound, the measurement means nothing and no correction is applied from it.
    /// </summary>
    private double _settleSeconds;

    private const double SettleSeconds = 3.0;

    /// <summary>True once the measurement has had enough sound to be worth acting on.</summary>
    public bool BalanceSettled => _settleSeconds >= SettleSeconds;

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

        // Loudness is judged before any correction, so the reading reflects what was
        // actually played rather than what the correction made of it.
        double level = Math.Clamp(Math.Sqrt(Math.Max(rmsLeft, rmsRight)), 0, 1);

        UpdateBaselines(rmsLeft, rmsRight, delta);

        // Below the gate there is nothing worth pointing at.
        if (level < options.NoiseGate || rmsLeft + rmsRight < 1e-6)
        {
            Decay(options, delta);
            return;
        }

        level = (level - options.NoiseGate) / Math.Max(1e-6, 1 - options.NoiseGate);

        // The difference between the sides, in decibels.
        //
        // Everything below works in this unit rather than in raw amplitude. Comparing the
        // two as a ratio, (right - left) / total, runs out of room almost immediately: it
        // is already at 0.8 by the time one side is five times the other, so any real
        // correction pushed the answer flat against one edge and every sound read as
        // hard left or hard right. Decibels stay linear across the whole range one of
        // these can cover, which is what makes both corrections below behave sensibly.
        double diffDb = 20.0 * Math.Log10((rmsRight + 1e-9) / (rmsLeft + 1e-9));

        // Cancel the lasting offset: whatever the two sides have averaged apart over the
        // last few seconds is the listener's volume settings, not the game.
        if (options.BalanceCompensation && BalanceSettled)
            diffDb -= Math.Clamp(MeasuredImbalanceDb, -MaxCompensationDb, MaxCompensationDb);

        // Manual nudge on top, for anything the automatic side cannot know about. A shift
        // of the answer rather than a gain on a channel: sliding it 6 dB left moves the
        // reading 6 dB left, and nothing gets amplified into clipping on the way.
        diffDb += Math.Clamp(options.BalanceTrim, -1, 1) * (TrimRangeDb * 0.5);

        // How far apart the sides have to be before a sound counts as fully to one side.
        double balance = Math.Clamp(diffDb / FullSideDb, -1, 1);
        double confidence = Math.Clamp(Math.Abs(balance) * 2.0, 0, 1);

        // Level rises instantly so a gunshot lands the moment it happens; direction and
        // confidence are eased, because a bearing that jitters is worse than useless.
        double smoothing = Math.Clamp(options.Smoothing, 0, 0.995);
        double ease = 1.0 - Math.Pow(smoothing, Math.Max(delta, 1e-4) * 60.0);

        Level = level >= Level ? level : Level + (level - Level) * ease;
        Direction += (Math.Clamp(balance, -1, 1) - Direction) * ease;
        Confidence += (confidence - Confidence) * ease;
    }

    /// <summary>
    /// Tracks the long-run energy of each side.
    ///
    /// Only updated while there is something to hear: letting silence pull the baselines
    /// down would make the first sound after a quiet spell read as wildly off-centre.
    /// </summary>
    private void UpdateBaselines(double rmsLeft, double rmsRight, double delta)
    {
        if (rmsLeft + rmsRight < 1e-5) return;

        _settleSeconds += Math.Max(delta, 0);

        double follow = 1.0 - Math.Exp(-Math.Max(delta, 1e-4) / BaselineSeconds);

        _baselineLeft += (rmsLeft - _baselineLeft) * follow;
        _baselineRight += (rmsRight - _baselineRight) * follow;

        _baselineLeft = Math.Max(_baselineLeft, 1e-6);
        _baselineRight = Math.Max(_baselineRight, 1e-6);

        double total = _baselineLeft + _baselineRight;
        MeasuredImbalance = total > 1e-9 ? (_baselineRight - _baselineLeft) / total : 0;
        MeasuredImbalanceDb = 20.0 * Math.Log10(_baselineRight / _baselineLeft);
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
