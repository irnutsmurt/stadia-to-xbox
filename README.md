# Stadia → Xbox

A small Windows app that reads a Google Stadia controller (USB or Bluetooth) and
presents it to the system as a virtual Xbox 360 controller through the ViGEmBus
driver. Games that expect an Xbox pad (XInput) then see the Stadia controller as
one. It also has a built-in tester so you can watch every button, stick and
trigger live.

## Requirements

- **[ViGEmBus](https://github.com/nefarius/ViGEmBus/releases)** driver (creates the
  virtual Xbox pad).
- **.NET 10 desktop runtime** — the build targets `net10.0-windows`.
- **[HidHide](https://github.com/nefarius/HidHide/releases)** (optional) — only needed
  for the "hide the Stadia controller" option below. Tested with 1.5.

## Build & run

```
dotnet build -c Release
```

The executable is at `bin\Release\net10.0-windows\StadiaXbox.exe`. It needs no
administrator rights. It starts the bridge automatically; use the Stop/Start
button to toggle it.

## Calibration

**Calibrate…** walks through four steps that each finish on their own: let go of the
sticks (resting center and drift), roll each stick around its edge (full range, plus
where it settles when let go), squeeze the triggers, then review. The review shows what
games will see and has a dead-zone slider per stick. Nothing changes until **Save**;
any step can be skipped to keep its current values.

## Tray and startup

- Closing the window (X) keeps the app running in the notification-area tray, so
  the controller keeps working. Click the tray icon to reopen the window;
  right-click it to start/stop the bridge, toggle startup, or **Exit**.
- The tray icon shows the state: green dot = controller bridged, amber dot =
  searching, grey icon = bridge stopped.
- **Start with Windows** (checkbox in the window, or in the tray menu) adds a
  per-user startup entry that launches the app straight into the tray with
  `--tray`. It can also be switched off from Windows Settings → Apps → Startup.
- Only one copy runs at a time; launching the exe again just reopens the window.

## Hiding the Stadia controller (HidHide)

Some games and launchers (Steam, for one) read the Stadia controller directly as
well as the virtual Xbox pad, so every press counts twice. Tick **Hide the Stadia
controller from other apps** to fix that. Through HidHide's command-line tool it:

1. adds `StadiaXbox.exe` to HidHide's allowed apps, so the bridge can still read it,
2. hides the Stadia controller's connections that are plugged in or connected right
   now (USB and/or Bluetooth),
3. switches HidHide's device hiding on.

Unticking only un-hides the Stadia controller; other devices you hid in HidHide, and
its on/off switch, are left alone. If HidHide asks for administrator rights you get
one UAC prompt per change. Apps that already had the controller open keep seeing it
until they're restarted or the controller is replugged.

The box shows ticked only while every connection that's present is hidden, so after
switching between USB and Bluetooth it may need ticking again. If you move the app's
folder, tick it again too: HidHide allows apps by their full path.

The audio jack isn't affected: HidHide only hides the controller's input interface.

## Connecting the controller

- **USB (recommended / most reliable):** plug the controller in. It exposes its
  HID game-controller interface right away and the app flips to
  "connected via USB".
- **Bluetooth:** the controller must be turned **on and actively connected**, not
  just paired. A paired-but-idle controller shows no input interface to Windows,
  so wake it (press the Stadia button) before expecting input. If Windows doesn't
  present the gamepad interface over Bluetooth, use USB.

## Button mapping

| Stadia            | Xbox 360        |
|-------------------|-----------------|
| A / B / X / Y     | A / B / X / Y   |
| L1 / R1           | LB / RB         |
| L2 / R2 (analog)  | LT / RT         |
| L3 / R3           | Left/Right stick click |
| D-pad             | D-pad           |
| Options (⋯)       | View (Back)     |
| Menu (≡)          | Menu (Start)    |
| Stadia (logo)     | Guide           |
| Assistant         | — (no Xbox equivalent; shown in tester only) |
| Capture           | — (no Xbox equivalent; shown in tester only) |

## Notes

- The HID report layout is the one documented by SDL's open-source hidapi Stadia
  driver (report id `0x03`, 10–11 bytes).
- Stick Y axes are inverted to match XInput's "positive = up" convention. If up/down
  feels reversed in a game, remove the `ny = -ny` line in `XboxOutput.ApplyStick`.
- Rumble is not forwarded in this version (the Stadia rumble output report is
  `0x05` + two little-endian 16-bit magnitudes if you want to add it).

## Files

- `StadiaReader.cs` — finds and reads the Stadia HID device, decodes reports.
- `StadiaState.cs` — decoded input snapshot.
- `XboxOutput.cs` — the ViGEm virtual Xbox 360 pad and the Stadia→Xbox mapping.
- `ControllerView.cs` / `MainForm.cs` — the tester UI and tray icon.
- `Calibration.cs` / `CalibrationForm.cs` — saved calibration and the calibration wizard.
- `StartupRegistration.cs` — the "Start with Windows" entry.
- `AppIcon.cs` — the app and tray icons, drawn in code.
- `HidHide.cs` — the optional "hide the Stadia controller" setting, via HidHide's CLI.
