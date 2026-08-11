# Contributing to SickRGB

Thanks for taking a look. The most useful contribution is **support for more hardware**:
adding a device means writing one small class, and nothing above it needs to change.

---

## Getting set up

**Requires:** Windows and the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```powershell
git clone https://github.com/davidnoeee/sickrgb.git
cd sickrgb
.\build.ps1          # builds and launches
```

No administrator rights are needed. If you'd rather not install the SDK system-wide, the
official [`dotnet-install.ps1`](https://learn.microsoft.com/dotnet/core/tools/dotnet-install-script)
script puts it in your user folder.

Delete `%AppData%\SickRGB` at any time to get back to a first-run state.

---

## How the app fits together

```
src/SickRGB/
  Devices/      LightModel.cs        devices and lights, with canvas coordinates
                ILightProvider.cs    the one interface a light source implements
                DeviceRegistry.cs    aggregation, placement, spatial coordinates
                Providers/           one file per source of lights
  OpenRgb/      OpenRgbClient.cs     OpenRGB SDK network client
                OpenRgbSetup.cs      download / launch / configure OpenRGB
  Effects/      Effect.cs            LightPoint, Impulse, EffectContext
                EffectLibrary.cs     every effect
                EffectEngine.cs      render thread, grouping, device writes
  Input/        KeyboardHook.cs      global hook - position only, never key identity
                MouseHook.cs         click notification, carries no data
  Capture/      ScreenSampler.cs     screen sampling for ambient mode
  Controls/     LayoutCanvas.cs      the drag-to-arrange surface
  Views/        Devices / Layout / Effects / Settings pages
  Themes/       Fluent.xaml          design tokens and Windows 11 control styles
```

The important idea: **every light has a real position in millimetres on a shared canvas**.
Effects are functions of position and time, so a ripple reaches a distant device later
purely because it is further away. Providers supply lights and a default shape; they never
know what an effect is.

---

## Adding a device

Implement `ILightProvider`, which has six members:

```csharp
public sealed class MyKeyboardProvider : ILightProvider
{
    public string Id => "native.mykeyboard";
    public string DisplayName => "My Keyboard";
    public string Description => "Shown on the Devices page.";
    public bool IsAvailable => true;
    public string UnavailableReason => "";

    public Task<IReadOnlyList<LightDevice>> DiscoverAsync(CancellationToken ct) { ... }
    public bool Apply(LightDevice device, ReadOnlySpan<Rgb24> zoneColors) { ... }
    public void Release(LightDevice device) { ... }
    public void Dispose() { ... }
}
```

Then register it in `DeviceRegistry`'s constructor. That's the whole integration.

A few things that will save you time:

- **Sizes are millimetres.** A full-size keyboard is about 440 × 140; a mouse about 68 × 120.
  Getting this roughly right makes the default canvas layout sensible.
- **`LightDevice.StripZones` and `GridZones`** cover most layouts.
- **`Apply` must be cheap.** It is called from the render thread. If your transport can
  block (a network socket, a slow bus), hand the frame to a background writer and return
  immediately. `OpenRgbProvider` does exactly this and is worth copying.
- **Set `MaxUpdatesPerSecond`** if the hardware can't take 60 fps. Sending faster than a
  device can absorb doesn't make it smoother; it just builds a backlog so what you see lags
  behind what the effect is doing.
- **`Release` should leave the device in a sane state**: usually black, or handed back to
  its own onboard control.
- **Don't tear down a healthy handle during rediscovery.** Reuse it. Closing and reopening
  mid-session shows up as a visible flicker.

`MagmaProvider` is the simplest complete example. `HidNative.cs` has the Win32 HID interop
if you need raw USB HID.

---

## Adding an effect

Subclass `Effect` and add it to `EffectLibrary.CreateAll()`:

```csharp
public override void Render(EffectContext ctx, ReadOnlySpan<LightPoint> points, Span<RgbF> output)
{
    for (int i = 0; i < points.Length; i++)
        output[i] = RgbF.FromHsv(ctx.Time * 0.1 + points[i].X, 1, 1);
}
```

Each `LightPoint` carries normalised coordinates (`X`, `Y`, 0..1 across the arrangement) and
world coordinates in millimetres. Use normalised for gradients that should span everything;
use `ctx.Distance(point, impulse)` for anything distance-based, so speed stays consistent
whatever the canvas looks like.

For reactive effects, override `OnImpulse` and keep an `ImpulseSet`. Write the render as a
**sum over live impulses** rather than per-light accumulators. That way the effect doesn't
care how many lights exist, and devices can appear or disappear mid-animation.

---

## House style

The codebase aims to read like one person wrote it.

- **Comments explain *why*, not *what*.** If something looks odd, say what breaks without it.
  A comment restating the code earns nothing.
- Match the surrounding naming, spacing and structure.
- Prefer clarity over cleverness; this is a hobby app people will read to learn from.
- Handle failure quietly and specifically. Devices get unplugged and sockets die; that's
  normal, not exceptional. Never let it take the app down.
- No new dependencies without a good reason. The app currently has zero NuGet packages, and
  that's worth keeping.

---

## Before opening a pull request

- `.\build.ps1` completes with no warnings.
- The app launches, finds your devices, and effects run.
- If you touched device code, test **unplugging and replugging** mid-run.
- If you touched the engine, watch CPU for a minute; it should sit near 1-2%.
- Say what hardware you tested on. "Untested, written from a datasheet" is a fine thing to
  admit, and much better than leaving it to be discovered.

---

## Licensing of contributions

SickRGB is released under [PolyForm Noncommercial 1.0.0](LICENSE): free for any
noncommercial purpose, not for selling. By opening a pull request you agree your
contribution is offered under that same licence.

Because of the noncommercial restriction this is not open source by the OSI definition,
even though the source is public. That is worth knowing before you spend an evening on a
driver.

---

## Reporting a bug

Include your Windows version, which devices are connected, and whether OpenRGB is running
(and whether elevated). If lighting is the problem, say which effect was active; that is
usually enough to reproduce it.
