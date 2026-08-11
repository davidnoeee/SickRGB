# Turtle Beach Magma RGB lighting protocol

Reverse-engineered and verified against a **Turtle Beach Magma, USB `10F5:5024`, firmware 1.08**.

This is the same protocol ROCCAT used on the Magma before Turtle Beach absorbed the brand.
Only the USB IDs changed, which is exactly why OpenRGB does not light this keyboard: OpenRGB
registers `1E7D:3124` (ROCCAT Magma) and a handful of Turtle Beach Vulcan IDs, but has no entry
for `10F5:5024`.

---

## 1. USB / HID topology

The keyboard is a composite device exposing four interfaces:

| Interface | Usage page | Usage | Reports | Purpose |
|---|---|---|---|---|
| MI_00 | 0x0001 | 0x0006 | In 9, Out 2 | Boot keyboard |
| MI_01 col01 | 0x0001 | 0x0002 | In 7 | Mouse/pointer collection |
| MI_01 col02 | 0x0001 | 0x0080 | In 2 | System control |
| MI_01 col03 | 0x000C | 0x0001 | In 3 | Consumer control (media keys) |
| MI_01 col04 | 0xFF02 | 0x0001 | In 8 | Vendor events |
| **MI_01 col05** | **0xFF01** | 0x0001 | **Feature 1044** | **Control channel** |
| MI_02 | 0x0001 | 0x0006 | In 25 | NKRO keyboard |
| **MI_03** | **0xFF00** | 0x0001 | **In 65, Out 65** | **LED channel** |

Two handles are needed: the **control** collection on usage page `0xFF01` for feature reports,
and the **LED** collection on usage page `0xFF00` for the 65-byte colour writes.

---

## 2. Reading device information

`HidD_GetFeature` on the control handle, report id `0x09`, 9-byte buffer:

```
request : 09 00 00 00 00 00 00 00 00
response: 09 09 6C 65 64 01 09 00 00
                ^^ firmware version as an integer: 0x6C = 108 => "1.08"
```

Reading with a larger buffer returns the device's current backlight configuration
appended after the header, which is a handy way to see what the keyboard is doing.

### Ready check

Report id `0x04` polls readiness. Byte 1 equals `1` when the keyboard is ready to accept
commands. Buffers shorter than 9 bytes are rejected with `ERROR_INVALID_PARAMETER` (87).

```
response: 04 01 00 00 00 05 01 00 FC
             ^^ 1 = ready
```

---

## 3. Enabling direct (software) control

`HidD_SetFeature` on the control handle, exactly 5 bytes, with **no padding needed**:

```
0E 05 01 00 00     enable direct mode
0E 05 00 00 00     disable, keyboard returns to its onboard profile
```

---

## 4. Pushing colours

The Magma has **5 lighting zones** (10 LEDs, two per zone) running left to right.
Zone 0 is the leftmost strip.

Write a single 65-byte output report to the **LED** handle:

```
offset  value        meaning
------  -----------  -----------------------------------------
0       0x00         HID report id
1       0xA1         direct-colour command
2       0x01         packet index, 1-based
3       0x40         declared payload length (64)
4..8    R0 R1 .. R4  red   for zones 0..4
9..13   G0 G1 .. G4  green for zones 0..4
14..18  B0 B1 .. B4  blue  for zones 0..4
19..64  0x00         zero padding
```

The colour data is **planar**, not interleaved: all five red bytes, then all five green,
then all five blue.

### Where that layout comes from

OpenRGB's `RoccatVulcanKeyboardController::SendColors` treats the Magma as
`packet_length = 64`, `column_length = 5`, `protocol_version = 2`. Substituting those:

- `packet_num = ceil(64 / 64) = 1` → one report
- `header_length_first = 3` (payload ≤ 255)
- for LED `v`: `column = v / 5 = 0`, `row = v % 5 = v`, `offset = v`
- red at `offset + 4`, green at `offset + 5 + 4`, blue at `offset + 10 + 4`

which is the table above.

Example (all zones red):

```
00 A1 01 40 FF FF FF FF FF 00 00 00 00 00 00 00 00 00 00 00 ... 00
```

---

## 5. Onboard effect / mode packet

Not needed for direct control, but documented because Swarm II ships a working example.
Its embedded default (`MagmaDefaultBacklight`) decodes cleanly:

```
11 1A 00 | 09 06 45 | 000000 FF0000 FF0000 00FF00 00FFFF FFFFFF | 77 08
^^          ^^ ^^ ^^   six RGB colour slots                       ^^^^^
|           |  |  |                                               checksum
|           |  |  brightness (0x45 = max)
|           |  speed (0x06 = default)
|           mode id
command 0x11 (protocol v2), length 0x001A = 26
```

**Checksum**: sum of every byte except the final two, stored little-endian.
For the packet above the bytes sum to 2167 = `0x0877`, and the trailing bytes are `77 08`. ✔

Sent with `HidD_SetFeature` on the control handle.

---

## 6. Notes and gotchas

- **Swarm II fights you.** It runs a background LED update thread. Close it before taking
  direct control. The `Turtle Beach Device Service` runs as SYSTEM and cannot be stopped
  without admin rights, but in practice it does not block direct-mode writes.
- **Handles are shareable.** Open with `FILE_SHARE_READ | FILE_SHARE_WRITE`; the device does
  not demand exclusive access.
- **No elevation required.** Everything here works as a normal user.
- **Direct mode is transient.** It does not write to onboard profile storage, so nothing is
  permanently altered: unplug and replug and the keyboard is back to its stored profile.
- Feature reports are accepted at their natural length; only the *read* path insists on a
  buffer of at least 9 bytes.

---

## 7. Adding this to OpenRGB

Upstream support needs one line in
`Controllers/RoccatController/RoccatControllerDetect.cpp`:

```cpp
#define TURTLE_BEACH_MAGMA_PID 0x5024
REGISTER_HID_DETECTOR_IPU("Turtle Beach Magma", DetectRoccatVulcanKeyboardControllers,
                          TURTLE_BEACH_VID, TURTLE_BEACH_MAGMA_PID, 1, 0xFF01, 1);
```

plus adding `TURTLE_BEACH_MAGMA_PID` alongside `ROCCAT_MAGMA_PID` in the `InitDeviceInfo`,
`EnableDirect` and `SendColors` switch statements, since the behaviour is identical.
