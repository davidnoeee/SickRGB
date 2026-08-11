using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SickRGB.Audio;

/// <summary>One application that is playing, or has recently played, sound.</summary>
public sealed class AudioSession
{
    public int ProcessId { get; init; }

    /// <summary>The executable name, without the extension.</summary>
    public string ProcessName { get; init; } = "";

    /// <summary>What to show in the picker: the window title where there is one.</summary>
    public string DisplayName { get; init; } = "";

    /// <summary>True while it is actually making sound, as opposed to merely holding the device open.</summary>
    public bool Active { get; init; }

    public override string ToString() => DisplayName;
}

/// <summary>
/// Lists the applications currently using the speakers.
///
/// Only used to fill the picker for per-application capture. Nothing here reads audio;
/// it asks Windows which processes have an audio session and what they are called.
/// </summary>
public static class AudioSessions
{
    private const string ClsidMmDeviceEnumerator = "BCDE0395-E52F-467C-8E3D-C4579291692E";

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        int Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
                     [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        // IAudioSessionManager, which this extends.
        int GetAudioSessionControl(IntPtr sessionGuid, int streamFlags, out IntPtr sessionControl);
        int GetSimpleAudioVolume(IntPtr sessionGuid, int streamFlags, out IntPtr audioVolume);

        int GetSessionEnumerator(out IAudioSessionEnumerator sessionEnum);
        int RegisterSessionNotification(IntPtr notification);
        int UnregisterSessionNotification(IntPtr notification);
        int RegisterDuckNotification([MarshalAs(UnmanagedType.LPWStr)] string sessionId, IntPtr duckNotification);
        int UnregisterDuckNotification(IntPtr duckNotification);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        int GetCount(out int sessionCount);
        int GetSession(int sessionIndex, out IAudioSessionControl2 session);
    }

    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        // IAudioSessionControl, which this extends. Declared in full because the vtable
        // order is what matters; the inherited entries come first.
        int GetState(out int state);
        int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid eventContext);
        int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
        int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid eventContext);
        int GetGroupingParam(out Guid groupingParam);
        int SetGroupingParam(ref Guid grouping, ref Guid eventContext);
        int RegisterAudioSessionNotification(IntPtr newNotifications);
        int UnregisterAudioSessionNotification(IntPtr newNotifications);

        int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        int GetProcessId(out int processId);
        int IsSystemSoundsSession();
        int SetDuckingPreference(bool optOut);
    }

    private const int DataFlowRender = 0;
    private const int RoleConsole = 0;
    private const int ClsCtxAll = 0x17;
    private const int SessionStateActive = 1;
    private const int SessionStateExpired = 2;

    /// <summary>
    /// Every application with a session on the default output, newest-sounding first.
    ///
    /// Returns an empty list rather than throwing if audio is unavailable: a picker with
    /// nothing in it is a better outcome than an error where a list should be.
    /// </summary>
    public static List<AudioSession> List()
    {
        var results = new List<AudioSession>();
        var seen = new HashSet<int>();

        try
        {
            var enumeratorType = Type.GetTypeFromCLSID(new Guid(ClsidMmDeviceEnumerator));
            if (enumeratorType is null) return results;

            var enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(enumeratorType)!;
            if (enumerator.GetDefaultAudioEndpoint(DataFlowRender, RoleConsole, out var device) != 0) return results;

            var managerIid = new Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");
            if (device.Activate(ref managerIid, ClsCtxAll, IntPtr.Zero, out object managerObj) != 0) return results;

            var manager = (IAudioSessionManager2)managerObj;
            if (manager.GetSessionEnumerator(out var sessions) != 0) return results;
            if (sessions.GetCount(out int count) != 0) return results;

            for (int i = 0; i < count; i++)
            {
                try
                {
                    if (sessions.GetSession(i, out var session) != 0) continue;

                    // The system sounds session reports process zero, which is also the
                    // only thing that needs excluding here. IsSystemSoundsSession is not
                    // used for it: on this hardware it answers S_OK for every session,
                    // which would throw the entire list away.
                    if (session.GetProcessId(out int pid) != 0 || pid <= 0) continue;
                    if (pid == Environment.ProcessId) continue;
                    if (!seen.Add(pid)) continue;

                    session.GetState(out int state);
                    if (state == SessionStateExpired) continue;

                    string processName = "";
                    string title = "";

                    try
                    {
                        using var process = Process.GetProcessById(pid);
                        processName = process.ProcessName;
                        title = process.MainWindowTitle;
                    }
                    catch
                    {
                        // Gone between listing and asking, or not ours to look at.
                        continue;
                    }

                    string display = !string.IsNullOrWhiteSpace(title) ? title : processName;
                    if (string.IsNullOrWhiteSpace(display)) continue;

                    results.Add(new AudioSession
                    {
                        ProcessId = pid,
                        ProcessName = processName,
                        DisplayName = display,
                        Active = state == SessionStateActive,
                    });
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Audio] session {i} unreadable: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Audio] could not list sessions: {ex.Message}");
        }

        // Whatever is making sound right now goes to the top, since that is almost always
        // what someone is looking for.
        results.Sort((a, b) =>
        {
            if (a.Active != b.Active) return a.Active ? -1 : 1;
            return string.Compare(a.DisplayName, b.DisplayName, StringComparison.CurrentCultureIgnoreCase);
        });

        return results;
    }

    /// <summary>
    /// Finds the process a saved choice refers to, by name, after a restart.
    ///
    /// Process IDs are reused, so a stored ID alone could point at something else entirely
    /// by the next boot. The name is what actually identifies the app.
    /// </summary>
    public static int Resolve(int savedProcessId, string savedProcessName)
    {
        if (string.IsNullOrWhiteSpace(savedProcessName)) return 0;

        var sessions = List();

        foreach (var session in sessions)
            if (session.ProcessId == savedProcessId
             && string.Equals(session.ProcessName, savedProcessName, StringComparison.OrdinalIgnoreCase))
                return session.ProcessId;

        // Same app, restarted since. Prefer one that is actually playing.
        foreach (var session in sessions)
            if (session.Active
             && string.Equals(session.ProcessName, savedProcessName, StringComparison.OrdinalIgnoreCase))
                return session.ProcessId;

        foreach (var session in sessions)
            if (string.Equals(session.ProcessName, savedProcessName, StringComparison.OrdinalIgnoreCase))
                return session.ProcessId;

        return 0;
    }
}
