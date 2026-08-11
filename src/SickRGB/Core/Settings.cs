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
    public double AudioSmoothing { get; set; } = 0.55;
    public double AudioNoiseGate { get; set; } = 0.03;
    public double AudioMinHz { get; set; } = 40;
    public double AudioMaxHz { get; set; } = 12000;

    public SickRGB.Audio.AudioColourMode AudioColourMode { get; set; } = SickRGB.Audio.AudioColourMode.Spectrum;
    public SickRGB.Audio.AudioLayout AudioLayout { get; set; } = SickRGB.Audio.AudioLayout.BassInCentre;

    /// <summary>Keeps a little light showing in the quiet parts instead of going black.</summary>
    public double AudioFloor { get; set; }

    // ---- providers ----
    /// <summary>
    /// Addressable headers that have already been given an automatic strip length.
    /// Remembered so a length you set yourself is never silently replaced.
    /// </summary>
    public HashSet<string> AutoSizedHeaders { get; set; } = new();

    public bool LogitechExperimental { get; set; }
    public string OpenRgbHost { get; set; } = "127.0.0.1";
    public int OpenRgbPort { get; set; } = 6742;

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
