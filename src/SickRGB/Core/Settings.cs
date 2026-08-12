using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using SickRGB.Devices;
using SickRGB.Effects;

namespace SickRGB.Core;

/// <summary>Colours, speed and intensity for one effect.</summary>
public sealed class EffectPreset
{
    public string[] Colors { get; set; } = Array.Empty<string>();
    public double Speed { get; set; } = 0.5;
    public double Intensity { get; set; } = 0.6;
}

/// <summary>Everything remembered about one physical device.</summary>
public sealed class DeviceSettings
{
    public bool Enabled { get; set; } = true;

    /// <summary>Flips zone order for hardware mounted the other way round.</summary>
    public bool Reversed { get; set; }

    /// <summary>
    /// How often this device is updated, in times per second.
    /// Null means use whatever the provider considers sensible for the hardware.
    /// </summary>
    public int? UpdateRate { get; set; }

    /// <summary>
    /// Which part of the music this device shows when the visualiser is running.
    /// A keyboard can carry the whole spectrum while a mouse shows only the bass.
    /// </summary>
    public SickRGB.Audio.AudioRange AudioRange { get; set; } = SickRGB.Audio.AudioRange.Full;

    // ---- canvas placement ----
    public double X { get; set; }
    public double Y { get; set; }
    public double Rotation { get; set; }
    public double Scale { get; set; } = 1.0;
    public bool HasPlacement { get; set; }
    public DeviceRole? Role { get; set; }

    /// <summary>When true this device follows the global effect; otherwise it runs its own.</summary>
    public bool SyncToGlobal { get; set; } = true;

    /// <summary>Effect used when <see cref="SyncToGlobal"/> is false.</summary>
    public string EffectId { get; set; } = "static";

    /// <summary>Per-device effect presets, so an override remembers its own colours.</summary>
    public Dictionary<string, EffectPreset> Presets { get; set; } = new();
}

public sealed class AppSettings
{
    // ---- lighting ----
    public string GlobalEffectId { get; set; } = "wave";
    public double Brightness { get; set; } = 1.0;
    public int TargetFps { get; set; } = 60;
    public Dictionary<string, EffectPreset> Presets { get; set; } = new();

    // ---- devices ----
    public Dictionary<string, DeviceSettings> Devices { get; set; } = new();

    // ---- ambient ----
    public string CaptureTargetName { get; set; } = "";
    public double Saturation { get; set; } = 1.35;
    public double Smoothing { get; set; } = 0.55;
    public double AmbientFloor { get; set; } = 0.0;

    /// <summary>
    /// When true each light samples the screen region matching its position on the
    /// canvas, so a 2D arrangement maps onto the 2D screen. When false every light
    /// samples by horizontal position only.
    /// </summary>
    public bool AmbientUseCanvasMapping { get; set; } = true;

    // ---- audio visualiser ----
    /// <summary>Listen to a microphone instead of what the PC is playing.</summary>
    public bool AudioUseMicrophone { get; set; }

    public double AudioGain { get; set; } = 2.0;
    public double AudioSmoothing { get; set; } = 0.80;
    public double AudioNoiseGate { get; set; } = 0.03;
    public double AudioMinHz { get; set; } = 40;
    public double AudioMaxHz { get; set; } = 12000;

    public SickRGB.Audio.AudioColourMode AudioColourMode { get; set; } = SickRGB.Audio.AudioColourMode.Spectrum;
    public SickRGB.Audio.AudioLayout AudioLayout { get; set; } = SickRGB.Audio.AudioLayout.BassAtEdges;

    /// <summary>Keeps a little light showing in the quiet parts instead of going black.</summary>
    public double AudioFloor { get; set; }

    // ---- audio source ----
    /// <summary>
    /// Listen to one application instead of everything. Null means the whole output mix.
    /// </summary>
    public int? AudioTargetProcessId { get; set; }

    /// <summary>Remembered so the same app can be re-selected after it restarts.</summary>
    public string AudioTargetProcessName { get; set; } = "";

    // ---- directional sound ----
    /// <summary>
    /// Cancels a lopsided output balance before working out direction.
    ///
    /// Matters if you have turned one side up and the other down, which anyone with
    /// one-sided hearing loss is likely to have done. Without this, a permanently louder
    /// right channel reads as "everything is on your right", which is precisely the
    /// information the effect exists to provide.
    /// </summary>
    public bool AudioBalanceCompensation { get; set; } = true;

    /// <summary>Manual balance nudge, -1 favours the left through to +1 favours the right.</summary>
    public double AudioBalanceTrim { get; set; }

    /// <summary>Show direction on top of whatever effect is already running.</summary>
    public bool DirectionOverlay { get; set; }

    /// <summary>How strongly the overlay covers the effect underneath, 0..1.</summary>
    public double DirectionOverlayOpacity { get; set; } = 0.75;

    // ---- providers ----
    /// <summary>
    /// Addressable headers that have already been given an automatic strip length.
    /// Remembered so a length you set yourself is never silently replaced.
    /// </summary>
    public HashSet<string> AutoSizedHeaders { get; set; } = new();

    public bool LogitechExperimental { get; set; }
    public string OpenRgbHost { get; set; } = "127.0.0.1";
    public int OpenRgbPort { get; set; } = 6742;

    /// <summary>
    /// Start OpenRGB automatically when it is needed and is not already running.
    ///
    /// Switched on once setup has succeeded, because until then there is nothing to
    /// start. Without it, everything OpenRGB reaches stays dark after a restart until
    /// someone opens Settings and starts it by hand.
    /// </summary>
    public bool OpenRgbAutoStart { get; set; }

    /// <summary>
    /// Whether the automatic start asks for administrator rights, remembered from setup.
    ///
    /// Memory and most motherboards sit behind SMBus, which OpenRGB can only reach when
    /// it is elevated. That means a prompt at every sign-in, so it is only ever used
    /// because the last successful setup used it.
    /// </summary>
    public bool OpenRgbLaunchElevated { get; set; } = true;

    // ---- OBS ----
    public string ObsHost { get; set; } = "127.0.0.1";
    public int ObsPort { get; set; } = 4455;

    /// <summary>
    /// The websocket password from OBS.
    ///
    /// Stored as typed. It is a local password for a server that only listens on this
    /// machine by default, and hiding it in the settings file would suggest a protection
    /// that is not there: anything able to read this file can read whatever we encrypted
    /// it with too.
    /// </summary>
    public string ObsPassword { get; set; } = "";

    /// <summary>
    /// The three indicator slots: left, middle, right.
    ///
    /// The defaults are the three things a streamer glances at, in the order they sit on a
    /// keyboard, and coloured the way stage equipment already is: red for on air, green
    /// for an open microphone.
    /// </summary>
    public List<SickRGB.Obs.ObsSlot> ObsSlots { get; set; } = new()
    {
        new SickRGB.Obs.ObsSlot { Signal = SickRGB.Obs.ObsSignal.Streaming,      Color = "#FF2D2D" },
        new SickRGB.Obs.ObsSlot { Signal = SickRGB.Obs.ObsSignal.CameraLive,     Color = "#FF8A14" },
        new SickRGB.Obs.ObsSlot { Signal = SickRGB.Obs.ObsSignal.MicrophoneLive, Color = "#1FBF5A" },
    };

    // ---- shell ----
    /// <summary>
    /// Windows 11 Mica backdrop.
    ///
    /// Off by default, and deliberately so: making a WPF window's composition surface
    /// transparent to reveal the DWM backdrop is fragile, and when it goes wrong the
    /// result is a window with no visible content at all. The solid surface always
    /// renders correctly. Turn this on if you want the translucent material and it
    /// behaves on your machine.
    /// </summary>
    public bool UseMicaBackdrop { get; set; }

    public bool MinimiseToTray { get; set; } = true;
    public bool StartMinimised { get; set; }
    public bool StartWithWindows { get; set; }

    [JsonIgnore]
    public static string ConfigDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SickRGB");

    [JsonIgnore]
    public static string ConfigPath => Path.Combine(ConfigDirectory, "settings.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(ConfigPath), JsonOpts);
                if (s is not null) { s.FillDefaults(); return s; }
            }
        }
        catch { /* fall back to defaults rather than refusing to start */ }

        var fresh = new AppSettings();
        fresh.FillDefaults();
        return fresh;
    }

    /// <summary>
    /// Guards the dictionaries below.
    ///
    /// They are touched from both the UI thread (editing colours, dragging devices) and
    /// the render thread (reading the active preset every frame), and the accessors add
    /// entries on demand. Concurrent Dictionary writes corrupt it, and serialising while
    /// another thread inserts throws "collection was modified".
    /// </summary>
    [JsonIgnore]
    private readonly object _sync = new();

    private System.Threading.Timer? _saveTimer;

    /// <summary>
    /// Queues a write a short moment from now.
    ///
    /// Dragging a slider raises a change event per pixel; writing the file on each one
    /// hammered the disk and showed up as stutter. Callers can stay naive and just call
    /// Save() whenever something changes.
    /// </summary>
    public void Save()
    {
        lock (_sync)
        {
            _saveTimer ??= new System.Threading.Timer(_ => SaveNow(), null,
                                                      Timeout.Infinite, Timeout.Infinite);
            _saveTimer.Change(400, Timeout.Infinite);
        }
    }

    /// <summary>Writes immediately. Used on shutdown so nothing is lost.</summary>
    public void SaveNow()
    {
        try
        {
            string json;
            lock (_sync) json = JsonSerializer.Serialize(this, JsonOpts);

            Directory.CreateDirectory(ConfigDirectory);

            // Write to a temporary file first, so a crash mid-write cannot leave a
            // truncated settings file behind.
            string temp = ConfigPath + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, ConfigPath, overwrite: true);
        }
        catch { /* a failed settings write must never take the app down */ }
    }

    public void FillDefaults()
    {
        lock (_sync)
        {
            foreach (var effect in EffectLibrary.CreateAll())
                EnsurePreset(Presets, effect);
        }
    }

    private static void EnsurePreset(Dictionary<string, EffectPreset> map, Effect effect)
    {
        int slots = effect.ColorLabels.Length;

        if (!map.TryGetValue(effect.Id, out var p))
        {
            p = new EffectPreset { Colors = DefaultColorsFor(effect.Id, slots) };
            map[effect.Id] = p;
        }

        if (p.Colors.Length < slots)
        {
            var defaults = DefaultColorsFor(effect.Id, slots);
            var grown = new string[slots];
            for (int i = 0; i < slots; i++)
                grown[i] = i < p.Colors.Length ? p.Colors[i] : defaults[i];
            p.Colors = grown;
        }
    }

    private static string[] DefaultColorsFor(string effectId, int slots)
    {
        string[] preset = effectId switch
        {
            "static" => new[] { "#FF5A1F" },
            "gradient" => new[] { "#FF1E00", "#0066FF" },
            "breathing" => new[] { "#FF3300" },

            // Reactive backgrounds are pure black on purpose.
            //
            // A near-black tint like #0A0020 looks like "off" through a keyboard's
            // diffuser, but a bare RGB LED on a memory module or graphics card renders
            // it as visible purple - so idle hardware sat there glowing. Black means
            // black everywhere; pick a background colour yourself if you want a glow.
            "ripple" => new[] { "#000000", "#00E5FF" },
            "wave" => new[] { "#000000", "#FF3C00" },
            "flash" => new[] { "#000000", "#FFFFFF" },
            "heat" => new[] { "#000000", "#FF2200" },

            // Five stops from bass to treble, in the same spirit as Colour Wave.
            "audio" => new[] { "#FF2D3C", "#FF9114", "#3CE66E", "#00C8FF", "#DC46FF" },

            // Faint, moderate, loud. A restrained ramp on purpose: position is the message
            // here, and a louder palette would compete with it.
            "direction" => new[] { "#2A5FA8", "#3ECFC1", "#FFD24A" },
            _ => new[] { "#FF5A1F", "#0066FF", "#00FF88", "#FFAA00", "#AA00FF" },
        };

        var result = new string[Math.Max(slots, 0)];
        for (int i = 0; i < result.Length; i++)
            result[i] = i < preset.Length ? preset[i] : preset[^1];
        return result;
    }

    // ------------------------------------------------------------------ accessors

    public DeviceSettings DeviceFor(string key)
    {
        lock (_sync)
        {
            if (!Devices.TryGetValue(key, out var d))
            {
                d = new DeviceSettings();
                Devices[key] = d;
            }
            return d;
        }
    }

    /// <summary>Global preset for an effect.</summary>
    public EffectPreset PresetFor(string effectId)
    {
        lock (_sync)
        {
            if (!Presets.TryGetValue(effectId, out var p))
            {
                p = new EffectPreset { Colors = DefaultColorsFor(effectId, 5) };
                Presets[effectId] = p;
            }
            return p;
        }
    }

    /// <summary>Per-device preset, used when the device overrides the global effect.</summary>
    public EffectPreset PresetFor(string deviceKey, string effectId)
    {
        var dev = DeviceFor(deviceKey);

        lock (_sync)
        {
            if (!dev.Presets.TryGetValue(effectId, out var p))
            {
                // Seed an override from the global look so it does not start black.
                if (!Presets.TryGetValue(effectId, out var global))
                {
                    global = new EffectPreset { Colors = DefaultColorsFor(effectId, 5) };
                    Presets[effectId] = global;
                }

                p = new EffectPreset
                {
                    Colors = (string[])global.Colors.Clone(),
                    Speed = global.Speed,
                    Intensity = global.Intensity,
                };
                dev.Presets[effectId] = p;
            }
            return p;
        }
    }

    /// <summary>Resolves a preset's colours into a fixed 5-entry palette.</summary>
    public static Rgb24[] PaletteOf(EffectPreset preset)
    {
        var palette = new Rgb24[5];
        for (int i = 0; i < palette.Length; i++)
            palette[i] = i < preset.Colors.Length ? Rgb24.FromHex(preset.Colors[i]) : Rgb24.Black;
        return palette;
    }
}

/// <summary>Registers the app to launch at sign-in (per-user, no admin rights needed).</summary>
public static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "SickRGB";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is not null;
        }
        catch { return false; }
    }

    public static void Set(bool enabled)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key is null) return;

            if (enabled)
            {
                string exe = Environment.ProcessPath ?? "";
                if (!string.IsNullOrEmpty(exe)) key.SetValue(ValueName, $"\"{exe}\" --minimised");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch { /* autostart is a convenience, not worth surfacing errors for */ }
    }
}
