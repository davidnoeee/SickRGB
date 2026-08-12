using System.Diagnostics;

namespace SickRGB.Core;

/// <summary>
/// Spots other lighting software that is running.
///
/// Two programs cannot drive the same lights at once. Both keep sending their own colours
/// and the hardware shows whichever arrived last, which looks like flickering rather than
/// like a conflict, so people reasonably blame the app that is visibly misbehaving.
///
/// It is worst right after a restart, because vendor software almost always installs
/// itself to launch at sign-in, so the fight starts before anyone has opened anything.
///
/// This only reads the list of running processes. It never closes anything: which program
/// owns a keyboard is the user's decision, not the app's.
/// </summary>
public static class RivalApps
{
    /// <summary>
    /// Process name fragments, matched case-insensitively, and the product to name.
    ///
    /// Keyed on fragments rather than exact names because vendors rename their executables
    /// between versions far more often than they rebrand.
    /// </summary>
    private static readonly (string Fragment, string Product)[] Known =
    {
        ("Swarm", "ROCCAT Swarm"),
        ("ROCCAT", "ROCCAT software"),
        ("TurtleBeach", "Turtle Beach software"),
        ("Synapse", "Razer Synapse"),
        ("RzSDK", "Razer Synapse"),
        ("iCUE", "Corsair iCUE"),
        ("Corsair", "Corsair software"),
        ("lghub", "Logitech G HUB"),
        ("LogiOptions", "Logitech Options"),
        ("MysticLight", "MSI Mystic Light"),
        ("MSI_Center", "MSI Center"),
        ("LightingService", "MSI or ASUS lighting service"),
        ("Aura", "ASUS Aura"),
        ("Armoury", "ASUS Armoury Crate"),
        ("SteelSeries", "SteelSeries GG"),
        ("SignalRgb", "SignalRGB"),
        ("Wootility", "Wootility"),
        ("Sharkoon", "Sharkoon software"),
        ("CoolerMaster", "Cooler Master software"),
        ("Gigabyte", "Gigabyte RGB Fusion"),
        ("RGBFusion", "Gigabyte RGB Fusion"),
    };

    /// <summary>
    /// Products currently running, named for a person rather than by process.
    ///
    /// OpenRGB is deliberately absent: SickRGB drives it on purpose, so reporting it as a
    /// conflict would be telling the user to close the thing that makes half their
    /// hardware work.
    /// </summary>
    public static List<string> Running()
    {
        var found = new List<string>();

        try
        {
            foreach (var process in Process.GetProcesses())
            {
                string name;
                try { name = process.ProcessName; }
                catch { continue; }

                foreach (var (fragment, product) in Known)
                {
                    if (!name.Contains(fragment, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!found.Contains(product)) found.Add(product);
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RivalApps] could not read the process list: {ex.Message}");
        }

        found.Sort(StringComparer.CurrentCultureIgnoreCase);
        return found;
    }
}
