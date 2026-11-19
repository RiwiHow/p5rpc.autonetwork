# P5R Auto Network

A lightweight [Reloaded-II](https://github.com/Reloaded-Project/Reloaded-II) mod for **Persona 5 Royal (PC)** that automatically enables Thief's Union network features on startup and skips the network prompts.

## Features

- Automatically enables network features on startup without showing the confirmation dialog.
- Skips the follow-up network login result notice (both on success and failure).

## Requirements

- [Reloaded-II](https://github.com/Reloaded-Project/Reloaded-II)
- `reloaded.sharedlib.hooks`
- `Reloaded.Memory.SigScan.ReloadedII`

## Installation

1. Download the latest archive from **Releases**.
2. Drag and drop the archive into **Reloaded-II** (or extract it to `Reloaded-II/Mods/p5rpc.autonetwork`).
3. Enable **P5R Auto Network** for Persona 5 Royal.

## Building

Requires [.NET 9.0 SDK](https://dotnet.microsoft.com/download/dotnet/9.0) and Reloaded-II (`RELOADEDIIMODS` environment variable set):

```powershell
dotnet build p5rpc.autonetwork.csproj -c Release
```

## License

Licensed under [GPL-3.0](LICENSE).

## Disclaimer

This is an unofficial fan-made mod and is not affiliated with ATLUS or SEGA.
