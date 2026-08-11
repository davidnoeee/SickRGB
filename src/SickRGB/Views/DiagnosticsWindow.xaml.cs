using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using SickRGB.Hardware;

namespace SickRGB.Views;

public partial class DiagnosticsWindow : Window
{
    private readonly HidInputCapture _capture = new();
    private readonly StringBuilder _log = new();
    private List<HidNative.HidCollection> _collections = new();

    /// <summary>Selected vendor id, or null for every device.</summary>
    private ushort? _vendor;

    private DateTime _lastFlush = DateTime.MinValue;

    public DiagnosticsWindow()
    {
        InitializeComponent();

        _capture.Logged += OnCaptureLine;
        Loaded += (_, _) => PopulateVendors();
        Closed += (_, _) => _capture.Dispose();
    }

    // ================================================================== devices

    private void PopulateVendors()
    {
        try { _collections = HidNative.Enumerate(); }
        catch (Exception ex)
        {
            Append($"Could not list devices: {ex.Message}");
            return;
        }

        CmbVendor.Items.Clear();
        CmbVendor.Items.Add("Every device");

        // Group by vendor and show a recognisable name, so the right one is easy to pick.
        var vendors = _collections
            .GroupBy(c => c.VendorId)
            .OrderBy(g => g.Key)
            .Select(g => new
            {
                Vid = g.Key,
                Name = g.Select(c => c.Product).FirstOrDefault(p => !string.IsNullOrWhiteSpace(p)) ?? "",
                Count = g.Count(),
            })
            .ToList();

        _vendorIds = vendors.Select(v => v.Vid).ToList();

        foreach (var v in vendors)
        {
            string label = string.IsNullOrWhiteSpace(v.Name) ? $"{v.Vid:X4}" : $"{v.Name}  ({v.Vid:X4})";
            CmbVendor.Items.Add($"{label}  -  {v.Count} interface{(v.Count == 1 ? "" : "s")}");
        }

        CmbVendor.SelectedIndex = 0;
        StatusText.Text = $"{_collections.Count} HID interfaces found.";
    }

    private List<ushort> _vendorIds = new();

    private void Vendor_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        int index = CmbVendor.SelectedIndex;
        _vendor = index <= 0 || index - 1 >= _vendorIds.Count ? null : _vendorIds[index - 1];
    }

    // ================================================================== actions

    private void Scan_Click(object sender, RoutedEventArgs e)
    {
        BtnScan.IsEnabled = false;
        StatusText.Text = "Building report...";

        try
        {
            string report = HidDiagnostics.Build(_vendor);
            _log.Clear();
            _log.Append(report);
            Flush();
            StatusText.Text = "Report ready. Copy or save it to share.";
        }
        catch (Exception ex)
        {
            Append($"Report failed: {ex}");
        }
        finally
        {
            BtnScan.IsEnabled = true;
        }
    }

    private void Listen_Click(object sender, RoutedEventArgs e)
    {
        if (_capture.IsRunning)
        {
            _capture.Stop();
            BtnListen.Content = "Start listening";
            ListenWarning.Visibility = Visibility.Collapsed;
            Append("");
            Append("[stopped listening]");
            StatusText.Text = "Stopped.";
            return;
        }

        var wanted = _collections
            .Where(c => _vendor is null || c.VendorId == _vendor)
            .ToList();

        if (wanted.Count == 0)
        {
            Append("Nothing to listen to. Pick a device first.");
            return;
        }

        ListenWarning.Visibility = Visibility.Visible;
        Append("");
        Append("=== listening ===");

        int started = _capture.Start(wanted);

        if (started == 0)
        {
            Append("No interface could be opened for reading. Close any software from the " +
                   "device's own manufacturer and try again.");
            ListenWarning.Visibility = Visibility.Collapsed;
            return;
        }

        BtnListen.Content = "Stop listening";
        StatusText.Text = $"Listening on {started} interface{(started == 1 ? "" : "s")}. Use the device now.";
    }

    private void OnCaptureLine(string line)
    {
        // Comes from reader threads; the log is only touched on the UI thread.
        Dispatcher.BeginInvoke(() => Append(line));
    }

    private void Append(string line)
    {
        _log.AppendLine(line);

        // Batch the redraws: a busy device can produce lines faster than a TextBox can
        // usefully render them.
        if ((DateTime.UtcNow - _lastFlush).TotalMilliseconds < 120) return;
        Flush();
    }

    private void Flush()
    {
        _lastFlush = DateTime.UtcNow;
        LogBox.Text = _log.ToString();
        LogBox.ScrollToEnd();
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        Flush();
        try
        {
            Clipboard.SetText(_log.ToString());
            StatusText.Text = "Copied to the clipboard.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Could not copy: {ex.Message}";
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        Flush();
        try
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                FileName = $"sickrgb-device-report-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
                Filter = "Text file (*.txt)|*.txt|All files (*.*)|*.*",
                Title = "Save device report",
            };

            if (dialog.ShowDialog(this) != true) return;

            File.WriteAllText(dialog.FileName, _log.ToString());
            StatusText.Text = $"Saved to {dialog.FileName}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Could not save: {ex.Message}";
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        _capture.Stop();
        base.OnClosing(e);
    }
}
