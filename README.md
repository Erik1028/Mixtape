# 🎵 Mixtape

A friendly, standalone Windows app for managing **classic click-wheel iPods** — copy music, videos and photos, build playlists, choose cover art, and preview media — all by reading and writing the iPod's `iTunesDB` / Photo Database **directly**. No iTunes, no Apple Devices app, no libgpod.

Built because iTunes is effectively gone and Apple's newer apps handle old click-wheel iPods poorly.

> ⚠️ **Mixtape writes to your iPod's database.** It backs up before every change and verifies the result, but this is community software, not Apple's. Read the **Safety** section before writing to a device you care about.

## Features

- **Browse & manage** your library — songs, albums, artists, playlists
- **Copy music on** (auto-transcodes formats the iPod can't play, when FFmpeg is available)
- **Copy music off** to your PC
- **Create / edit / reorder playlists** ("make a mixtape")
- **Edit tags & star ratings**
- **Photos & videos** — import (recursively from folders), and preview right in the app
- **Cover art** — pick from generated covers, or show album art on the iPod's own screen
- **In-app preview** — play songs, watch videos, view photos straight off the iPod
- **Automatic device detection** with a per-model iPod illustration and capacity breakdown
- Dark, Apple-Music-style UI with a custom window chrome

## Supported iPods

Click-wheel models that mount as a USB drive on Windows:

- iPod 1G–5G, iPod photo, **iPod Classic**
- **iPod mini** (1G/2G)
- **iPod nano** 1G–7G
- iPod shuffle

**Read/browse/export works on all of them.** *Writing* depends on the device's signature scheme:

| Scheme | Devices | Writable? |
| --- | --- | --- |
| None | iPod 1G–5G, photo, mini, nano 1G/2G | ✅ yes |
| hash58 | Classic, nano 3G/4G | ✅ if the device's FireWire GUID can be read (Mixtape can read it over USB) |
| hash72 / hashAB | nano 5G+, Touch | ❌ read-only (signature not supported) |

iOS devices (iPod Touch / iPhone) don't mount as a disk and are out of scope.

## Build & run

Requirements: **.NET 8 SDK** on Windows.

```sh
git clone https://github.com/fgs8z2n9qh-tech/Mixtape.git
cd Mixtape
dotnet build -c Release
```

Run the built exe (`bin\Release\net8.0-windows\Mixtape.exe`), or publish a single self-contained file that needs no .NET install:

```sh
dotnet publish -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:EnableCompressionInSingleFile=true -p:DebugType=none -o publish
```

Optional: install [FFmpeg](https://ffmpeg.org/) (or point to it in Settings) to enable audio/video transcoding.

## Safety

- Before any write, Mixtape makes a rolling backup (`iTunesDB.bak`) **and** a one-time pristine backup (`iTunesDB.original`), then re-reads and verifies the new database; it rolls back on mismatch.
- For **hash58** devices it runs a known-answer signature check first and stays **read-only** if its signing doesn't match the device — so a signing bug can't corrupt your library.
- Restore is available from the device page if anything looks wrong.
- Still: **back up your iPod's contents yourself before writing**, and test with one song first on a device you care about. hash58 writing has had limited real-world testing.

## Credits

- The hash58 signature tables/algorithm are derived from the open-source [**libgpod**](http://www.gtkpod.org/libgpod/) and **ipod-sharp** projects — credit to their authors for the reverse-engineering.
- Audio tags via [**TagLib#**](https://github.com/mono/taglib-sharp).
- Database formats documented by the libgpod project and the iPod community.

## Status

Hobby project. Reading is solid and widely tested; writing is safe-by-design but newer signature schemes (hash58 on some devices) have had limited hardware testing. Issues and PRs welcome.

## License

MIT — see [LICENSE](LICENSE). Note that the hash58 portions derive from libgpod (LGPL); keep the attribution above.
