namespace SickRGB.Hardware;

/// <summary>
/// Turns HID usage numbers into words.
///
/// A report full of bare hex is readable only by someone who already knows the spec by
/// heart. Naming the pages and the common usages costs nothing and means the person
/// reading the report can tell at a glance which collection is the keyboard Windows owns
/// and which one is the manufacturer's own channel.
/// </summary>
internal static class HidUsageNames
{
    public static string Page(ushort page) => page switch
    {
        0x0001 => "Generic Desktop",
        0x0002 => "Simulation",
        0x0003 => "VR",
        0x0004 => "Sport",
        0x0005 => "Game",
        0x0006 => "Generic Device",
        0x0007 => "Keyboard/Keypad",
        0x0008 => "LEDs",
        0x0009 => "Button",
        0x000A => "Ordinal",
        0x000B => "Telephony",
        0x000C => "Consumer",
        0x000D => "Digitizer",
        0x000E => "Haptics",
        0x000F => "Physical Input Device",
        0x0010 => "Unicode",
        0x0012 => "Eye and Head Trackers",
        0x0014 => "Auxiliary Display",
        0x0020 => "Sensors",
        0x0040 => "Medical Instrument",
        0x0041 => "Braille Display",
        0x0059 => "Lighting and Illumination",
        0x0080 => "Monitor",
        0x0084 => "Power Device",
        0x0085 => "Battery System",
        0x008C => "Barcode Scanner",
        0x0090 => "Camera Control",
        0x00F1D0 => "FIDO Alliance",
        >= 0xFF00 => "vendor defined",
        _ => $"page 0x{page:X4}",
    };

    /// <summary>Names the top level usage of a collection, which is what identifies it.</summary>
    public static string TopLevel(ushort page, ushort usage) => (page, usage) switch
    {
        (0x0001, 0x01) => "Pointer",
        (0x0001, 0x02) => "Mouse",
        (0x0001, 0x04) => "Joystick",
        (0x0001, 0x05) => "Gamepad",
        (0x0001, 0x06) => "Keyboard",
        (0x0001, 0x07) => "Keypad",
        (0x0001, 0x08) => "Multi axis controller",
        (0x0001, 0x80) => "System Control",
        (0x000C, 0x01) => "Consumer Control",
        (0x000D, 0x04) => "Touch Screen",
        (0x000D, 0x05) => "Touch Pad",
        (0x0059, 0x01) => "Lamp Array",
        (0x0080, 0x01) => "Monitor Control",
        (0xFF60, 0x61) => "QMK raw HID (VIA)",
        _ => "",
    };

    /// <summary>
    /// A short note on what a vendor page is usually used for, where that is known.
    ///
    /// These are the pages that turn up again and again on RGB hardware. Naming them saves
    /// the reader from having to recognise the number.
    /// </summary>
    public static string VendorPageNote(ushort page, ushort vendorId) => (page, vendorId) switch
    {
        (0xFF60, _) => "QMK raw HID. VIA speaks over this.",
        (0xFF1C, _) => "EVision lighting interface, used by a large family of rebranded keyboards.",
        (0xFF00, 0x10F5) => "Turtle Beach and ROCCAT vendor channel.",
        (0xFF00, 0x046D) => "Logitech HID++ short reports.",
        (0xFF00, 0x1532) => "Razer vendor channel. Razer lighting goes over the control pipe, not this.",
        (0xFF00, _) => "generic vendor page. Often carries lighting.",
        (0xFF01, 0x046D) => "Logitech HID++ long reports.",
        (0xFF01, _) => "vendor page, often a second channel alongside 0xFF00.",
        (0xFF02, _) => "vendor page.",
        (>= 0xFF00, _) => "vendor page.",
        _ => "",
    };

    /// <summary>Decodes a HID main item bitfield, which says how a field behaves.</summary>
    public static string BitField(ushort bits)
    {
        var parts = new List<string>
        {
            (bits & 0x0001) != 0 ? "Constant" : "Data",
            (bits & 0x0002) != 0 ? "Variable" : "Array",
            (bits & 0x0004) != 0 ? "Relative" : "Absolute",
        };

        if ((bits & 0x0008) != 0) parts.Add("Wrap");
        if ((bits & 0x0010) != 0) parts.Add("NonLinear");
        if ((bits & 0x0020) != 0) parts.Add("NoPreferred");
        if ((bits & 0x0040) != 0) parts.Add("NullState");
        if ((bits & 0x0080) != 0) parts.Add("Volatile");
        if ((bits & 0x0100) != 0) parts.Add("BufferedBytes");

        return string.Join(",", parts);
    }

    public static string CollectionType(byte type) => type switch
    {
        0x00 => "Physical",
        0x01 => "Application",
        0x02 => "Logical",
        0x03 => "Report",
        0x04 => "NamedArray",
        0x05 => "UsageSwitch",
        0x06 => "UsageModifier",
        _ => $"type 0x{type:X2}",
    };

    /// <summary>
    /// The vendor behind a USB vendor id, for the ones that matter to lighting.
    ///
    /// Deliberately short. The point is to recognise an OEM identifier shared across many
    /// brands, because that is exactly when a USB id tells you less than it appears to.
    /// </summary>
    public static string Vendor(ushort id) => id switch
    {
        0x0C45 => "Sonix (OEM, many rebranded keyboards)",
        0x046D => "Logitech",
        0x04D9 => "Holtek (OEM, many rebranded keyboards)",
        0x1038 => "SteelSeries",
        0x1044 => "Chicony / Gigabyte",
        0x1462 => "MSI",
        0x1532 => "Razer",
        0x1B1C => "Corsair",
        0x1E7D => "ROCCAT",
        0x1EA7 => "SHARKOON (OEM, shared across brands)",
        0x20A0 => "Clay Logic (OEM, common on QMK boards)",
        0x2516 => "Cooler Master",
        0x2F0E => "Wooting",
        0x3151 => "Keychron",
        0x3299 => "SPC Gear / Endorfy",
        0x320F => "EVision (OEM, many rebranded keyboards)",
        0x0483 => "STMicroelectronics (OEM)",
        0x10F5 => "Turtle Beach",
        0x048D => "ITE (OEM)",
        0x0B05 => "ASUS",
        0x1B4F => "SparkFun / QMK boards",
        0xFEED => "QMK default vendor id",
        _ => "",
    };
}
