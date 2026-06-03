# RomStarMOD Nexus Free

RomStarMOD Nexus Free is the public, free Nexus Mods edition of RomStar for Romestead.

This GitHub repository is source-only. It is provided for Nexus Mods review and transparency.
Players should install the compiled Nexus Free release package from the Nexus Mods file page, not from this GitHub repository.

## Edition

- English UI only
- No language selector
- Manual installation only
- No one-click installer scripts

## Build

Developers can build the plugin locally. This project targets `net8.0-windows` and references the local Romestead game assemblies.

Expected local game path:

```text
C:\SteamLibrary\steamapps\common\romestead
```

Build command:

```powershell
dotnet build src\RomStar.BepInEx\RomStar.BepInEx.csproj -c Release
```

The project is compiled with the `NEXUS_FREE` constant.

## Player Installation

1. Download the Nexus Free release package from Nexus Mods.
2. Install BepInEx 6 CoreCLR for Romestead if the release page says it is required.
3. Follow the installation instructions included with the Nexus Mods download.
4. Start Romestead and press `F1` in game to open RomStar.

## Developer Output

After a local build, the compiled DLL is created under:

```text
src\RomStar.BepInEx\bin\Release\net8.0-windows\
```

Compiled DLLs are not stored in this GitHub repository.
