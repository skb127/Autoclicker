# Autoclicker

Cross-platform desktop autoclicker built with **Avalonia UI** and **.NET 8** for **Windows**, **Linux (X11)**, and **Linux (Wayland)**.

## Features

- **Coordinate Pattern:** Capture multiple screen coordinates (`ESC` to cancel) and reorder them via **Drag & Drop** or the **▲ / ▼** buttons.
- **Actions & Buttons:**
  - **Buttons:** `Left`, `Right`, `Middle`.
  - **Actions:** `Click`, `DoubleClick`, `Hold` (press for X ms), and `Drag` (left-button drag across ≥ 2 points).
- **Natural Timing & Movement:**
  - Fixed interval or randomized `[Min, Max]` ms range (measured start-to-start).
  - Optional **Human-like movement (WindMouse)** (`Slow`, `Normal`, `Fast`) and **Coordinate Jitter** (`± px`).
- **Auto-Save:** Automatically saves your settings and pattern to `~/.config/Autoclicker/settings.json` (Linux) or `%APPDATA%\Autoclicker\settings.json` (Windows).

## Run

```bash
dotnet run --project Autoclicker.Desktop/Autoclicker.Desktop.csproj
```

## Wayland Notes

- **Screen Sharing Permission:** On first start, select the monitor where your pattern coordinates are located and enable pointer control.
- **Emergency Stop Key:** Configure an emergency stop shortcut via the portal button (if supported by your desktop) or by binding the `busctl` command shown in the app inside your system keyboard settings.

## Docs

- [`Docs/windmouse_explanation.md`](Docs/windmouse_explanation.md) — How the WindMouse movement algorithm works.
- [`Docs/analysis_wayland_hotkey.md`](Docs/analysis_wayland_hotkey.md) — Technical notes on Wayland portals and shortcuts.
- [`Docs/ROADMAP.md`](Docs/ROADMAP.md) — Future enhancements backlog.
