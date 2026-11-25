# Global Subtitle Translator

Global Subtitle Translator (GST) is a Jellyfin plugin and companion Windows app for translating text-based subtitle tracks with locally stored translation models. The Jellyfin plugin source in this snapshot is version 1.5.22.0.

## Repository layout

- `jellyfin-plugin/` — Jellyfin plugin, shared .NET code, and resilience-check source.
- `worker/` — local CPU and CUDA worker implementations and the GPU worker dashboard.
- `local-gst/` — Windows WPF app and Python translation engine.
- `assets/gst-icon.png` — GST application icon.

The plugin extracts text subtitles from MKV files, sends subtitle text to a configured local worker, and writes an external SRT beside the media file. It does not upload video or subtitle data to a hosted translation API. The Windows app can use configured subtitle providers for subtitle search/download; that is separate from local translation.

## Requirements

- Jellyfin Server 10.11 or later for the plugin project in this source snapshot.
- .NET SDK 9 for the plugin and .NET SDK 8 with Windows Desktop support for the WPF app.
- Python 3.10 or later, an NVIDIA GPU, and a CUDA-enabled PyTorch installation for GPU translation.
- A compatible translation model installed locally. Model weights are not included in this repository.
- Jellyfin FFmpeg or compatible `ffmpeg`/`ffprobe` tools to extract text tracks from MKV files.

## Build the Jellyfin plugin

```powershell
dotnet restore .\jellyfin-plugin\src\Gst.Plugin\Gst.Plugin.csproj -p:EnableGoogleProvider=false
dotnet publish .\jellyfin-plugin\src\Gst.Plugin\Gst.Plugin.csproj -c Release -p:EnableGoogleProvider=false -o .\publish\jellyfin-plugin
```

Jellyfin plugin builds must include `meta.json` beside the assemblies. `manifest.json` publishes the compatible package, checksum, owner, icon, and repository entry.

## Install the plugin from the GST repository

Add this manifest in Jellyfin Dashboard → Plugins → Repositories:

```text
https://raw.githubusercontent.com/MAFL74/Global-Subtitle-Translator/main/manifest.json
```

After saving the repository, refresh the plugin catalog. Jellyfin can then show the plugin owner and repository and resolve package details for GST. The repository contains a Jellyfin package for version 1.5.22.0 under `releases/`.

## Run the Windows app

Create a virtual environment in `local-gst`, install CUDA-enabled PyTorch using the official PyTorch selector, then install `requirements.txt`:

```powershell
py -m venv .\local-gst\.venv
.\local-gst\.venv\Scripts\python -m pip install -r .\local-gst\requirements.txt
dotnet run --project .\local-gst\Local-GST.csproj
```

Install a compatible model separately and select its local folder in the app. Model files, credentials, logs, subtitles, and generated translation output are intentionally excluded from this repository.

## Install the Windows GPU worker

The GPU worker can run in the background at Windows startup without an open Command Prompt. See [the Windows GPU worker guide](docs/GPU-WORKER-WINDOWS.md). The installer creates a dedicated CUDA-enabled Python environment, checks that CUDA and the selected local model are available, adds a firewall rule limited to the Jellyfin server IP, and registers a restartable startup task.

Translation runs locally from the model folder. The installer downloads Python packages from their package indexes, but it does not send subtitle text to a translation API or download a model.

## Security and privacy

- Configure worker addresses and filesystem paths on each installation; this repository contains no installation-specific IP addresses or media paths.
- Do not commit API keys, passwords, model weights, `.srt` files, media files, diagnostics, or generated job history.
- GPU worker access should be restricted to the Jellyfin host using the host firewall and the worker's client allowlist.
- External subtitle-provider searches contact the providers enabled by the user. Local translation itself runs with local model files.

## Project status

This repository is the source for the GST project. Inclusion in Jellyfin's official plugin catalog is a separate review and release step and is not implied by publishing this source repository.
