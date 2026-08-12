namespace SickRGB.Obs;

/// <summary>What a light can be told to follow.</summary>
public enum ObsSignal
{
    /// <summary>Always off. Used for a slot the user does not want lit.</summary>
    Nothing,

    /// <summary>The stream is live.</summary>
    Streaming,

    /// <summary>Recording to disk.</summary>
    Recording,

    /// <summary>Recording, but paused.</summary>
    RecordingPaused,

    /// <summary>The virtual camera output is running.</summary>
    VirtualCamera,

    /// <summary>The chosen audio input is not muted.</summary>
    MicrophoneLive,

    /// <summary>The chosen video input is on screen.</summary>
    CameraLive,

    /// <summary>The chosen scene is the one on air.</summary>
    SceneSelected,

    /// <summary>OBS is reachable at all. Useful as a "connected" lamp.</summary>
    ObsConnected,
}

/// <summary>
/// A snapshot of what OBS is doing.
///
/// Immutable and replaced whole rather than mutated in place. The render loop reads it
/// sixty times a second from a different thread to the one that receives websocket
/// messages, and swapping a reference is atomic, so neither side ever has to take a lock
/// and the renderer can never see a half-updated mixture of old and new state.
/// </summary>
public sealed record ObsSnapshot
{
    public bool Connected { get; init; }
    public bool Streaming { get; init; }
    public bool Recording { get; init; }
    public bool RecordingPaused { get; init; }
    public bool VirtualCamera { get; init; }

    /// <summary>The scene currently on air.</summary>
    public string ProgramScene { get; init; } = "";

    /// <summary>Mute state per audio input, keyed by the name OBS shows.</summary>
    public IReadOnlyDictionary<string, bool> InputMuted { get; init; } =
        new Dictionary<string, bool>();

    /// <summary>Whether each video input is currently on screen.</summary>
    public IReadOnlyDictionary<string, bool> InputActive { get; init; } =
        new Dictionary<string, bool>();

    /// <summary>Inputs OBS reported, so the settings UI can offer real names to pick from.</summary>
    public IReadOnlyList<ObsInput> Inputs { get; init; } = Array.Empty<ObsInput>();

    /// <summary>Scene names, likewise.</summary>
    public IReadOnlyList<string> Scenes { get; init; } = Array.Empty<string>();

    /// <summary>Why there is no connection, in words a person can act on.</summary>
    public string Status { get; init; } = "Not connected to OBS.";

    public static readonly ObsSnapshot Disconnected = new();

    /// <summary>
    /// Resolves one signal against this snapshot.
    ///
    /// A name is needed for the microphone, camera and scene signals because OBS has no
    /// notion of "the microphone": it has a list of inputs the user named. An empty name
    /// reads as false rather than guessing, so a slot that was never configured stays dark
    /// instead of following something arbitrary.
    /// </summary>
    public bool IsTrue(ObsSignal signal, string name) => signal switch
    {
        ObsSignal.Nothing => false,
        ObsSignal.ObsConnected => Connected,
        _ when !Connected => false,

        ObsSignal.Streaming => Streaming,
        ObsSignal.Recording => Recording,
        ObsSignal.RecordingPaused => RecordingPaused,
        ObsSignal.VirtualCamera => VirtualCamera,

        // Muted is the state OBS reports, and the light follows the opposite: a lit
        // microphone lamp should mean the microphone is live.
        ObsSignal.MicrophoneLive => name.Length > 0
                                 && InputMuted.TryGetValue(name, out bool muted) && !muted,

        ObsSignal.CameraLive => name.Length > 0
                             && InputActive.TryGetValue(name, out bool active) && active,

        ObsSignal.SceneSelected => name.Length > 0
                                && string.Equals(ProgramScene, name, StringComparison.Ordinal),

        _ => false,
    };
}

/// <summary>
/// One indicator, as the user configured it.
///
/// A slot answers three questions: what to watch, whether the light is on or off while
/// that is true, and what colour to use. The on/off choice exists because "dark while
/// live" is a legitimate thing to want, particularly for a light in shot.
/// </summary>
public sealed class ObsSlot
{
    public ObsSignal Signal { get; set; } = ObsSignal.Nothing;

    /// <summary>Which input or scene this slot refers to, for the signals that need one.</summary>
    public string Target { get; set; } = "";

    /// <summary>True to light up when the signal is true, false to go dark instead.</summary>
    public bool LitWhenTrue { get; set; } = true;

    /// <summary>Colour as "#RRGGBB".</summary>
    public string Color { get; set; } = "#FF2D2D";
}

/// <summary>One OBS input, as offered in the settings UI.</summary>
public sealed record ObsInput(string Name, string Kind)
{
    /// <summary>
    /// Whether this looks like a microphone.
    ///
    /// Matched on the input kind rather than the name, because people name their
    /// microphone anything at all and the kind is what OBS actually guarantees.
    /// </summary>
    public bool IsAudioInput =>
        Kind.Contains("input_capture", StringComparison.OrdinalIgnoreCase)
     || Kind.Contains("wasapi_input", StringComparison.OrdinalIgnoreCase)
     || Kind.Contains("coreaudio_input", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether this looks like a camera or other video source.</summary>
    public bool IsVideoInput =>
        Kind.Contains("dshow_input", StringComparison.OrdinalIgnoreCase)
     || Kind.Contains("av_capture", StringComparison.OrdinalIgnoreCase)
     || Kind.Contains("video_capture", StringComparison.OrdinalIgnoreCase);
}
