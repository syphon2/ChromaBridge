# ChromaBridge

Mirrors Razer Chroma game lighting (e.g. Overwatch 2's hero, ability and health effects) onto Logitech G HUB devices,
tested with a G915 X LIGHTSPEED keyboard + G502 X PLUS mouse.

```
Overwatch ──Razer Chroma SDK──▶ Razer Chroma SDK Service (Synapse) ──shared memory──▶ ChromaBridge ──Logitech LED SDK──▶ G HUB ──▶ G915 X
```

ChromaBridge does **not** inject into the game or replace any DLL. The game talks to the genuine Razer SDK;
ChromaBridge only reads the result from the Razer service (via [RazerSdkReader](https://github.com/diogotr7/RazerSdkReader), MIT),
the same way Razer's own Chroma Emulator does. No Razer hardware is needed.

## Requirements
- Razer Synapse 4 / Razer Chroma SDK service installed and running
- Logitech G HUB running, with *Settings → "Allow games & applications to control my lighting"* enabled
- .NET 8 Desktop Runtime
- In Overwatch: *Options → Video* (or Gameplay) → enable Razer Chroma lighting

## Use
Run `dist\ChromaBridge.exe`. It sits in the tray:
- **Grey icon**: idle; G HUB keeps its normal lighting
- **Green icon**: a Chroma game is active and its lighting is being mirrored

Tray menu: enable/pause, **Show test pattern**, *Start with Windows*, edit settings, open the log
(`%LOCALAPPDATA%\ChromaBridge\log.txt`, which shows the name of the active Chroma app).

### First-run check
Click **Show test pattern**. Rows should read top to bottom: **red** (ESC/F-keys), green, blue, white, yellow, cyan.
If the top row is blue instead, set `"swapRedBlue": true` in `bridge.json` (it reloads automatically).

## Settings (`dist\bridge.json`)
| key | meaning |
|---|---|
| `apps` | Only mirror these exe names, e.g. `["Overwatch.exe"]`. Empty = any Chroma game |
| `ignoreApps` | Never mirror these |
| `swapRedBlue` | Fix swapped colors |
| `brightness` | 0.0 – 1.0 |
| `maxFps` | Update-rate cap |
| `mouse` | Mirror the Razer mouse-logo color to the Logitech mouse |
| `gKeysAndLogo` | Razer M1–M5 → G1–G5, Razer logo → G logo |

## Build
```
dotnet publish ChromaBridge.csproj -c Release -o dist
```

## Files
- `Bridge.cs`: reads Razer frames and pushes them to G HUB (frame limiter, app detection, reconnects)
- `KeyMap.cs`: maps the Razer 22×6 key grid to the Logitech 21×6 bitmap (ANSI + ISO/DE keys)
- `LogitechLed.cs`: P/Invoke for the official Logitech LED SDK (`lib\LogitechLedEnginesWrapper.dll`, from Logitech's LED SDK 9.00)

## License
MIT, see [LICENSE](LICENSE). RazerSdkReader (MIT) is pulled in via NuGet.

## Optional: G HUB output mode
The default `hidpp` and `dynamicLighting` outputs need nothing extra. The `ghub` output uses Logitech's LED SDK wrapper, which is not redistributed here: download the Logitech G LED SDK (9.00) from Logitech's developer site and copy the x64 `LogitechLedEnginesWrapper.dll` into `lib\` before building, or next to `ChromaBridge.exe`.

## Windows Dynamic Lighting package
`tools\package\Build-Package.ps1` builds and signs the sparse package with a fresh local self-signed certificate and writes `ChromaBridge.cer`; run `Install-ChromaBridge.ps1` afterwards.

## Disclaimer
ChromaBridge is an independent, unofficial project. It is not affiliated with, endorsed by or sponsored by Razer Inc., Logitech International S.A. or Blizzard Entertainment. Razer, Razer Chroma and Synapse are trademarks of Razer Inc.; Logitech, Logitech G, G HUB and LIGHTSYNC are trademarks of Logitech; Overwatch is a trademark of Blizzard Entertainment. All other trademarks belong to their owners. Names are used only to describe compatibility. Use at your own risk; this tool talks to the lighting services of third-party software and hardware.
