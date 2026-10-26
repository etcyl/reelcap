# ReelCap

Small Windows screen recorder for game clips. WPF (.NET 9) front end over ffmpeg's `ddagrab` (Desktop Duplication) capture with GPU encoding (NVENC, AMF or Quick Sync, x264/x265 fallback).

- Pick a display (live previews), resolution (native or downscaled), frame rate (presets or custom), clip length (until stopped, presets or custom m:ss), quality, H.264/HEVC.
- Ctrl+Alt+R starts and stops from anywhere. Clips go to `Videos\ReelCap` by default as MP4.
- Settings: `%APPDATA%\ReelCap\settings.json`.

Build: `cd src && dotnet publish -c Release -o ../dist`, then copy `ffmpeg.exe` into `dist` (otherwise it's found on PATH or in the winget packages folder).
Icon: `python -I tools/make_icon.py src`.
Check: `dist\ReelCap.exe --selftest out.txt` lists displays and encoders and records 2 s of each display next to `out.txt`.
