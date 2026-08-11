namespace SickRGB.Input;

/// <summary>
/// Maps virtual-key codes to a horizontal position on a full-size ANSI keyboard,
/// expressed in "key units" (1 unit = one standard key width) measured from the
/// left edge. This lets reactive effects start where you actually pressed.
///
/// Nothing here records what was typed - a key code is converted to a position
/// and then discarded.
/// </summary>
internal static class KeyZoneMap
{
    /// <summary>Total width of a full-size board (main block + nav cluster + numpad).</summary>
    public const double BoardWidth = 22.5;

    private static readonly Dictionary<int, double> X = new()
    {
        // ---- function row ----
        [0x1B] = 0.5,                                                        // Esc
        [0x70] = 2.0, [0x71] = 3.0, [0x72] = 4.0, [0x73] = 5.0,              // F1-F4
        [0x74] = 6.5, [0x75] = 7.5, [0x76] = 8.5, [0x77] = 9.5,              // F5-F8
        [0x78] = 11.0, [0x79] = 12.0, [0x7A] = 13.0, [0x7B] = 14.0,          // F9-F12
        [0x2C] = 15.5, [0x91] = 16.5, [0x13] = 17.5,                         // PrtSc ScrLk Pause

        // ---- number row ----
        [0xC0] = 0.5,                                                        // `
        [0x31] = 1.5, [0x32] = 2.5, [0x33] = 3.5, [0x34] = 4.5, [0x35] = 5.5,
        [0x36] = 6.5, [0x37] = 7.5, [0x38] = 8.5, [0x39] = 9.5, [0x30] = 10.5,
        [0xBD] = 11.5, [0xBB] = 12.5,                                        // - =
        [0x08] = 14.0,                                                       // Backspace
        [0x2D] = 15.5, [0x24] = 16.5, [0x21] = 17.5,                         // Ins Home PgUp
        [0x90] = 19.0, [0x6F] = 20.0, [0x6A] = 21.0, [0x6D] = 22.0,          // NumLk / * -

        // ---- QWERTY row ----
        [0x09] = 0.75,                                                       // Tab
        [0x51] = 2.0, [0x57] = 3.0, [0x45] = 4.0, [0x52] = 5.0, [0x54] = 6.0,
        [0x59] = 7.0, [0x55] = 8.0, [0x49] = 9.0, [0x4F] = 10.0, [0x50] = 11.0,
        [0xDB] = 12.0, [0xDD] = 13.0, [0xDC] = 14.25,                        // [ ] backslash
        [0x2E] = 15.5, [0x23] = 16.5, [0x22] = 17.5,                         // Del End PgDn
        [0x67] = 19.0, [0x68] = 20.0, [0x69] = 21.0, [0x6B] = 22.0,          // Num 7 8 9 +

        // ---- home row ----
        [0x14] = 0.875,                                                      // Caps
        [0x41] = 2.25, [0x53] = 3.25, [0x44] = 4.25, [0x46] = 5.25, [0x47] = 6.25,
        [0x48] = 7.25, [0x4A] = 8.25, [0x4B] = 9.25, [0x4C] = 10.25,
        [0xBA] = 11.25, [0xDE] = 12.25,                                      // ; '
        [0x0D] = 13.75,                                                      // Enter
        [0x64] = 19.0, [0x65] = 20.0, [0x66] = 21.0,                         // Num 4 5 6

        // ---- shift row ----
        [0xA0] = 1.125, [0x10] = 1.125,                                      // LShift / generic Shift
        [0x5A] = 2.75, [0x58] = 3.75, [0x43] = 4.75, [0x56] = 5.75, [0x42] = 6.75,
        [0x4E] = 7.75, [0x4D] = 8.75,
        [0xBC] = 9.75, [0xBE] = 10.75, [0xBF] = 11.75,                       // , . /
        [0xA1] = 13.5,                                                       // RShift
        [0x26] = 16.5,                                                       // Up
        [0x61] = 19.0, [0x62] = 20.0, [0x63] = 21.0,                         // Num 1 2 3

        // ---- bottom row ----
        [0xA2] = 0.625, [0x11] = 0.625,                                      // LCtrl / generic Ctrl
        [0x5B] = 1.875,                                                      // LWin
        [0xA4] = 3.125, [0x12] = 3.125,                                      // LAlt / generic Alt
        [0x20] = 7.0,                                                        // Space
        [0xA5] = 10.875,                                                     // RAlt
        [0x5C] = 12.125, [0x5D] = 13.375, [0xA3] = 14.625,                   // RWin Menu RCtrl
        [0x25] = 15.5, [0x28] = 16.5, [0x27] = 17.5,                         // Left Down Right
        [0x60] = 19.5, [0x6E] = 21.0,                                        // Num 0 .
    };

    /// <summary>
    /// Horizontal position of a key as a fraction of board width (0 = far left, 1 = far right).
    /// Unknown keys fall back to the centre.
    /// </summary>
    public static double NormalizedX(int virtualKey) =>
        X.TryGetValue(virtualKey, out double x) ? Math.Clamp(x / BoardWidth, 0.0, 1.0) : 0.5;

    /// <summary>Continuous zone coordinate in 0..(ZoneCount-1), suitable for smooth wave maths.</summary>
    public static double ZoneCoordinate(int virtualKey, int zoneCount) =>
        Math.Clamp(NormalizedX(virtualKey) * zoneCount - 0.5, 0.0, zoneCount - 1.0);
}
