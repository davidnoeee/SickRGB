using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SickRGB.OpenRgb;

/// <summary>How far along the OpenRGB setup is.</summary>
public enum OpenRgbState
{
    /// <summary>Nothing found on disk.</summary>
    NotInstalled,

    /// <summary>Found on disk, but not currently running.</summary>
    InstalledNotRunning,

    /// <summary>Running, but the SDK port is not answering.</summary>
    RunningNoServer,

    /// <summary>Running with a reachable SDK server.</summary>
    Ready,
}

/// <summary>
/// Downloads, unpacks and launches OpenRGB so the bridge can be set up without
/// leaving the app.
///
/// This fetches and runs third-party software, so the UI states the exact URL and
/// version up front and never starts without an explicit click. The portable build
/// is used deliberately: it unpacks into this app's own local folder and installs
/// nothing system-wide.
/// </summary>
public static class OpenRgbSetup
{
    public const string Version = "1.0rc3";

    /// <summary>Official portable build, published by the OpenRGB project.</summary>
    public const string DownloadUrl =
        "https://codeberg.org/OpenRGB/OpenRGB/releases/download/release_candidate_1.0rc3/OpenRGB_1.0rc3_Windows_64_6fbcf62.zip";

    public const string ProjectUrl = "https://openrgb.org/";

    /// <summary>Where the portable build is unpacked. Nothing is written outside this folder.</summary>
    public static string InstallDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SickRGB", "OpenRGB");

    /// <summary>Finds OpenRGB.exe, preferring a copy we unpacked over a system install.</summary>
    public static string? FindExecutable()
    {
        var candidates = new List<string>();

        if (Directory.Exists(InstallDirectory))
        {
            try { candidates.AddRange(Directory.GetFiles(InstallDirectory, "OpenRGB.exe", SearchOption.AllDirectories)); }
            catch { /* unreadable folder */ }
        }

        foreach (var root in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                 })
        {
            string path = Path.Combine(root, "OpenRGB", "OpenRGB.exe");
            if (File.Exists(path)) candidates.Add(path);
        }

        return candidates.FirstOrDefault(File.Exists);
    }

    public static bool IsProcessRunning()
    {
        try { return Process.GetProcessesByName("OpenRGB").Length > 0; }
        catch { return false; }
    }

    /// <summary>True when something is listening on the SDK port.</summary>
    public static bool IsServerReachable(string host, int port, int timeoutMs = 500)
    {
        try
        {
            using var tcp = new TcpClient();
            return tcp.ConnectAsync(host, port).Wait(timeoutMs) && tcp.Connected;
        }
        catch { return false; }
    }

    public static OpenRgbState GetState(string host, int port)
    {
        if (IsServerReachable(host, port)) return OpenRgbState.Ready;
        if (IsProcessRunning()) return OpenRgbState.RunningNoServer;
        return FindExecutable() is not null ? OpenRgbState.InstalledNotRunning : OpenRgbState.NotInstalled;
    }

    /// <summary>
    /// Closes any running OpenRGB and waits for it to exit, so its files can be replaced.
    /// </summary>
    public static async Task<bool> StopOpenRgbAsync(CancellationToken ct)
    {
        Process[] running;
        try { running = Process.GetProcessesByName("OpenRGB"); }
        catch { return true; }

        if (running.Length == 0) return true;

        foreach (var p in running)
        {
            try
            {
                if (!p.CloseMainWindow()) p.Kill();
            }
            catch { /* already gone, or elevated and out of reach */ }
        }

        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < deadline)
        {
            if (ct.IsCancellationRequested) return false;
            if (!IsProcessRunning()) return true;
            await Task.Delay(300, ct).ConfigureAwait(false);
        }

        return !IsProcessRunning();
    }

    /// <summary>Deletes our unpacked copy so a reinstall genuinely starts from scratch.</summary>
    public static void RemoveInstall()
    {
        if (!Directory.Exists(InstallDirectory)) return;
        try { Directory.Delete(InstallDirectory, recursive: true); }
        catch { /* a locked leftover file is not fatal; extraction overwrites */ }
    }

    /// <summary>
    /// Downloads the portable ZIP and unpacks it, reporting progress as 0..1.
    /// When <paramref name="clean"/> is set, any previous copy is removed first so a
    /// reinstall cannot silently keep stale files.
    /// </summary>
    public static async Task<string> DownloadAndExtractAsync(
        IProgress<(string Status, double? Fraction)> progress, CancellationToken ct, bool clean = false)
    {
        if (clean)
        {
            progress.Report(("Removing the previous copy...", null));
            await StopOpenRgbAsync(ct).ConfigureAwait(false);
            RemoveInstall();
        }

        Directory.CreateDirectory(InstallDirectory);
        string zipPath = Path.Combine(InstallDirectory, $"OpenRGB_{Version}.zip");

        progress.Report(($"Contacting {new Uri(DownloadUrl).Host}...", null));

        using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) })
        {
            http.DefaultRequestHeaders.UserAgent.ParseAdd("SickRGB/1.0");

            using var response = await http.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct)
                                           .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            long? total = response.Content.Headers.ContentLength;

            await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var target = File.Create(zipPath);

            var buffer = new byte[81920];
            long received = 0;
            int read;

            while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                received += read;

                double? fraction = total is > 0 ? (double)received / total.Value : null;
                string size = total is > 0
                    ? $"{received / 1024.0 / 1024.0:0.0} of {total.Value / 1024.0 / 1024.0:0.0} MB"
                    : $"{received / 1024.0 / 1024.0:0.0} MB";
                progress.Report(($"Downloading OpenRGB {Version} - {size}", fraction));
            }
        }

        progress.Report(("Unpacking...", null));

        string extractDir = Path.Combine(InstallDirectory, Version);
        if (Directory.Exists(extractDir))
        {
            try { Directory.Delete(extractDir, recursive: true); }
            catch { /* a previous copy may be partially locked; extraction overwrites anyway */ }
        }
        Directory.CreateDirectory(extractDir);

        await Task.Run(() => ZipFile.ExtractToDirectory(zipPath, extractDir, overwriteFiles: true), ct)
                  .ConfigureAwait(false);

        try { File.Delete(zipPath); } catch { /* leaving the zip behind is harmless */ }

        string? exe = FindExecutable();
        if (exe is null) throw new FileNotFoundException("OpenRGB.exe was not found in the downloaded archive.");

        progress.Report(("Unpacked.", 1.0));
        return exe;
    }

    /// <summary>Where OpenRGB keeps its own configuration.</summary>
    private static string OpenRgbConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OpenRGB", "OpenRGB.json");

    /// <summary>
    /// Writes the two OpenRGB preferences that keep it out of the user's way.
    ///
    /// Without these, OpenRGB shows a "Resize the zones" prompt on first run, and
    /// closing its window quits the whole app - which silently takes the connection
    /// with it. Both are ordinary user preferences; everything else in the file is
    /// left untouched, and an unreadable config is left alone rather than replaced.
    /// </summary>
    public static void EnsureConfigured()
    {
        try
        {
            string path = OpenRgbConfigPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            JsonObject root;
            if (File.Exists(path))
            {
                try
                {
                    root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject();
                }
                catch
                {
                    // Never overwrite a config we cannot parse - it is the user's.
                    return;
                }
            }
            else
            {
                root = new JsonObject();
            }

            if (root["UserInterface"] is not JsonObject ui)
            {
                ui = new JsonObject();
                root["UserInterface"] = ui;
            }

            ui["minimize_on_close"] = true;   // closing its window must not stop the server
            ui["run_zone_checks"] = false;    // skip the "Resize the zones" prompt

            File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[OpenRGB] could not pre-configure: {ex.Message}");
        }
    }

    /// <summary>
    /// Starts OpenRGB with its SDK server on, minimised and out of the way.
    /// Elevation is only needed for devices behind SMBus/I2C - most memory and boards.
    /// </summary>
    public static bool Launch(int port, bool elevated, out string error)
    {
        error = "";
        string? exe = FindExecutable();
        if (exe is null) { error = "OpenRGB is not installed yet."; return false; }

        EnsureConfigured();

        try
        {
            // --gui is essential. Given any command line option, OpenRGB runs headless
            // unless the GUI is explicitly requested. Without a main window, its
            // first-run dialogs become the only window on screen, so dismissing one
            // triggers Qt's "quit when the last window closes" rule and OpenRGB exits,
            // taking the SDK server with it.
            //
            // --startminimized is deliberately not used: it hides the main window and
            // reintroduces exactly that problem.
            string arguments = $"--gui --server --server-port {port}";

            if (elevated)
            {
                // Windows will not let a normal-privilege process move or minimise an
                // elevated window, so the minimising has to happen inside the elevated
                // side. A small helper starts OpenRGB, waits for it to come up, and
                // tucks it away - all at the same privilege level, from one UAC prompt.
                string helper = WriteLauncherHelper(exe, arguments, port);

                Process.Start(new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{helper}\"",
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Hidden,
                });
            }
            else
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = exe,
                    WorkingDirectory = Path.GetDirectoryName(exe) ?? "",
                    Arguments = arguments,
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Minimized,
                });

                // Same privilege level here, so we can tuck it away ourselves.
                _ = Task.Run(() => MinimiseOpenRgbWindowsAsync(CancellationToken.None));
            }

            return true;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            error = "The administrator prompt was declined, so OpenRGB was not started.";
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    // =====================================================================================
    //  PawnIO - the driver OpenRGB needs for SMBus / I2C devices
    // =====================================================================================

    public const string PawnIoSiteUrl = "https://pawnio.eu/";

    /// <summary>Official signed installer, published by the PawnIO project.</summary>
    public const string PawnIoInstallerUrl =
        "https://github.com/namazso/PawnIO.Setup/releases/latest/download/PawnIO_setup.exe";

    /// <summary>
    /// True when the PawnIO kernel driver appears to be installed.
    ///
    /// Reading the service key under HKLM does not require elevation.
    /// </summary>
    public static bool IsPawnIoInstalled()
    {
        try
        {
            using var service = Microsoft.Win32.Registry.LocalMachine
                .OpenSubKey(@"SYSTEM\CurrentControlSet\Services\PawnIO");
            if (service is not null) return true;
        }
        catch { /* fall through to the file check */ }

        foreach (var root in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                 })
        {
            try
            {
                if (Directory.Exists(Path.Combine(root, "PawnIO"))) return true;
            }
            catch { /* unreadable path */ }
        }

        return false;
    }

    /// <summary>
    /// Downloads the official PawnIO installer and hands it to the user to run.
    ///
    /// This installs a kernel-mode driver, so the installer is launched interactively
    /// and elevated rather than run silently - the user sees exactly what is happening
    /// and can back out. Nothing here installs anything on its own.
    /// </summary>
    public static async Task<string> DownloadPawnIoAsync(
        IProgress<(string Status, double? Fraction)> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(InstallDirectory);
        string path = Path.Combine(InstallDirectory, "PawnIO_setup.exe");

        progress.Report(("Downloading the PawnIO installer...", null));

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("SickRGB/1.0");

        using var response = await http.GetAsync(PawnIoInstallerUrl, HttpCompletionOption.ResponseHeadersRead, ct)
                                       .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        long? total = response.Content.Headers.ContentLength;

        await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var target = File.Create(path))
        {
            var buffer = new byte[81920];
            long received = 0;
            int read;

            while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                received += read;
                double? fraction = total is > 0 ? (double)received / total.Value : null;
                progress.Report(($"Downloading PawnIO - {received / 1024.0 / 1024.0:0.0} MB", fraction));
            }
        }

        return path;
    }

    /// <summary>Runs the PawnIO installer elevated. Returns false if the user declines UAC.</summary>
    public static bool RunPawnIoInstaller(string installerPath, out string error)
    {
        error = "";
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = installerPath,
                UseShellExecute = true,
                Verb = "runas",
            });
            return true;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            error = "The administrator prompt was declined, so PawnIO was not installed.";
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    // =====================================================================================
    //  Keeping the OpenRGB window out of sight
    // =====================================================================================

    private const int SW_MINIMIZE = 6;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    /// <summary>
    /// Minimises OpenRGB once it has finished starting.
    ///
    /// Minimising rather than hiding is deliberate: a minimised window still counts as
    /// open, so if OpenRGB does put up a dialog, dismissing it will not trip Qt's
    /// "quit when the last window closes" rule.
    /// </summary>
    public static async Task MinimiseOpenRgbWindowsAsync(CancellationToken ct)
    {
        // Keep at it for a while: the main window appears some seconds after launch,
        // and device detection can raise it again afterwards.
        var deadline = DateTime.UtcNow.AddSeconds(45);

        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            try
            {
                foreach (var p in Process.GetProcessesByName("OpenRGB"))
                {
                    using (p)
                    {
                        p.Refresh();
                        IntPtr h = p.MainWindowHandle;
                        if (h != IntPtr.Zero && IsWindowVisible(h)) ShowWindow(h, SW_MINIMIZE);
                    }
                }
            }
            catch
            {
                // An elevated OpenRGB is out of reach from here; the helper handles that.
            }

            try { await Task.Delay(750, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>
    /// Writes the elevated launcher. It starts OpenRGB, waits for the SDK port, then
    /// minimises the window from inside the elevated session.
    /// </summary>
    private static string WriteLauncherHelper(string exe, string arguments, int port)
    {
        Directory.CreateDirectory(InstallDirectory);
        string path = Path.Combine(InstallDirectory, "start-openrgb.ps1");

        string script = $@"
# Started elevated by SickRGB. Launches OpenRGB and keeps its window out of the way.
$exe  = '{exe.Replace("'", "''")}'
$args = '{arguments.Replace("'", "''")}'
$port = {port}

Start-Process -FilePath $exe -ArgumentList $args -WorkingDirectory (Split-Path -Parent $exe) -WindowStyle Minimized

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class W {{
    [DllImport(""user32.dll"")] public static extern bool ShowWindow(IntPtr h, int c);
    [DllImport(""user32.dll"")] public static extern bool IsWindowVisible(IntPtr h);
}}
'@

$deadline = (Get-Date).AddSeconds(90)
while ((Get-Date) -lt $deadline) {{
    foreach ($p in (Get-Process OpenRGB -ErrorAction SilentlyContinue)) {{
        $p.Refresh()
        $h = $p.MainWindowHandle
        if ($h -ne 0) {{
            if ([W]::IsWindowVisible($h)) {{ [void][W]::ShowWindow($h, 6) }}
        }}
    }}
    Start-Sleep -Milliseconds 700
}}
";

        File.WriteAllText(path, script);
        return path;
    }

    /// <summary>Waits for the SDK port to start answering after a launch.</summary>
    public static async Task<bool> WaitForServerAsync(string host, int port, int timeoutMs, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (ct.IsCancellationRequested) return false;
            if (IsServerReachable(host, port, 400)) return true;
            await Task.Delay(500, ct).ConfigureAwait(false);
        }
        return false;
    }
}
