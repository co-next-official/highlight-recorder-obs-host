# obs-host

The recording process of **Highlight Recorder**, a game clip recorder for Windows.
It uses [libobs](https://github.com/obsproject/obs-studio) (OBS Studio's engine) to capture the game
(OBS's signed game-capture hook) and audio, encodes with NVENC / AMF / x264, and streams MPEG-TS to FFmpeg,
which cuts it into 2-second segments that the app uses as its replay buffer.

This is the part of Highlight Recorder that links to libobs. Because OBS Studio is licensed under the
GNU GPL v2 or later, this program is released under the same license, and Highlight Recorder's installer
ships it together with an unmodified copy of libobs.

日本語: Highlight Recorder（ゲームの名場面を残す録画アプリ）の録画用プロセスです。OBS Studio（GPLv2）の
libobs を使うため、この部分のソースを GPL v2 以降で公開しています。

## Build

Requires the .NET 10 SDK and Windows x64.

```
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o publish
```

At runtime it needs an OBS Studio 32.x folder layout (`bin/64bit/obs.dll`, `obs-plugins/64bit/*.dll`, `data/...`),
passed as `obsDir` in the JSON config. The declarations in `Obs.cs` follow the OBS Studio 32.2.2 headers.

## Usage

```
obs-host '<config JSON>'
```

See the comment at the top of `Program.cs` for the config, the stdin commands and the stdout events.

## License

Copyright (C) 2026 Co-Next

This program is free software; you can redistribute it and/or modify it under the terms of the
GNU General Public License as published by the Free Software Foundation; either version 2 of the License,
or (at your option) any later version. See [LICENSE](LICENSE).

The bundled OBS Studio files are from the official OBS Studio 32.2.2 release
(source: https://github.com/obsproject/obs-studio/tree/32.2.2).
