# ImageTagger
<<<<<<< HEAD

A Windows desktop application that uses a locally running [Ollama](https://ollama.com) vision model to automatically tag and rename JPEG photos with AI-generated descriptions stored as EXIF metadata.

![ImageTagger icon](app.ico)

## Features

- **AI-powered tagging** — sends each image to a local Ollama vision model (tested with `llava:34b`) and writes the generated description into EXIF fields (ImageDescription, UserComment, XPComment)
- **Smart renaming** — appends a condensed title to camera-default filenames (`IMG_`, `DSC`, `20181002_173843`, etc.) while leaving already-named files untouched
- **Incremental processing** — caches results so re-running the same folder only processes new or changed images
- **Resolution filter** — optionally skip images below a minimum width × height
- **Undo** — every rename and EXIF write is reversible from the Undo panel
- **Notifications** — taskbar flash + sound on completion; optional Discord webhook
- **Phone image compatibility** — automatically normalises non-standard JPEG structures (common on modern Android/iOS cameras) that standard EXIF writers reject

## Requirements

| Requirement | Notes |
|---|---|
| Windows 10/11 (x64) | WPF application |
| [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) | Required for framework-dependent builds |
| [Ollama](https://ollama.com) running locally | Default URL: `http://127.0.0.1:11434` |
| A vision-capable Ollama model | Recommended: `llava:34b` (~2 s/image on RTX 3090) |

## Quick start

1. Install Ollama and pull a vision model:
   ```
   ollama pull llava:34b
   ```
2. Install the [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) if not already present (Windows will prompt you automatically if it is missing).
3. Download the latest release from the [Releases](../../releases) page and run `ImageTagger.exe`.
3. Select your image folder, choose the model, click **▶ Start**.

## Building from source

```
git clone https://github.com/war4peace/ImageTagger.git
cd ImageTagger
dotnet build -c Release
```

To produce a framework-dependent single-file executable:

```
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish/
```

The output is `publish\ImageTagger.exe` (~7 MB). Requires the [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) to be installed on the target machine.

## Settings

All user preferences (Ollama URL, selected model, Discord webhook URL, etc.) are stored in:

```
%AppData%\ImageTagger\settings.json
```

This file is outside the repository and is never committed.

> **Note:** the image cache is stored separately in `Documents\ImageTagger\cache\`.

## Supported camera filename patterns

The following prefixes are recognised as camera-default names eligible for renaming:

`IMG_` · `DSC` · `DSCF` · `DSCN` · `MVI_` · `MOV_` · `GOPR` · `PXL_` · `PANO_` · `VID_` · `WP_` · `DCIM` · `IMAG` · `HPIM` · `STA` · `P0000000` · `YYYYMMDD_HHMMSS` · pure numeric stems

## 