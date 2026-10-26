# ReelCap

Small Windows recorder for game clips. WPF (.NET 9) front end over ffmpeg 8.1.

- **Video**: one source per recording, a whole monitor or one application window, captured with Windows.Graphics.Capture (`gfxcapture`, by `hmonitor`/`hwnd`) and scaled on the GPU to a fixed output size. With NVENC the D3D11 textures go straight into the encoder (about 6% of one core at 1440p240; the `hwdownload` path cost 50-80%). NVENC converts RGB with the BT.601 matrix, so streams are tagged `smpte170m` (the capture tags frames RGB, which would mislead players).
- **Audio**: any mix of the app's own audio (WASAPI process loopback, game process tree only), other apps (process loopback excluding the game), all system audio, microphone; mixed in-app into one track. Packets are placed on the recording's QPC timeline (silence gaps stay silent, device clock drift is corrected one frame at a time), mixed 250 ms behind real time and streamed to a long-lived ffmpeg (limiter, AAC 192k).
- **Resilience**: if the capture drops (display mode switch, UAC), a new video segment starts at once. Each segment logs its first frame's offset from the recording's time zero (`print(RTCTIME-base)` in `setpts`, since ffmpeg rebases outputs to zero); at stop the segments are placed on that timeline (the lost stretch holds the last frame) and muxed with the continuous audio into one MP4, no re-encode. Segments are MKV flushed every second.
- Resolution, frame rate, clip length (until stopped or fixed), quality, H.264/HEVC/AV1. Ctrl+Alt+R starts and stops from anywhere. Clips go to `Videos\ReelCap`.
- Settings: `%APPDATA%\ReelCap\settings.json`.

Build: `cd src && dotnet publish -c Release -o ../dist`, then copy `ffmpeg.exe` and `ffprobe.exe` into `dist`.
Icon: `python -I tools/make_icon.py src`.
Checks (nothing is shown on screen):
- `dist\ReelCap.exe --selftest out.txt [exe]` records the primary monitor for 3 s with system audio and, given an exe name, that app's window with its own audio for 6 s, killing the capture at 2.5 s to exercise resuming. `REELCAP_KEEP=1` keeps the parts and a mixer trace.
- `dist\ReelCap.exe --screenshot out.png [app]` renders the window off-screen to a PNG.
