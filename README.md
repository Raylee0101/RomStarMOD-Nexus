# RomStarMOD Nexus Free

RomStarMOD Nexus Free is the public, free Nexus Mods edition of RomStar for Romestead.

## Edition

- English UI only
- No language selector
- Manual installation only
- No one-click installer scripts

## Build

This project targets `net8.0-windows` and references the local Romestead game assemblies.

Expected local game path:

```text
C:\SteamLibrary\steamapps\common\romestead
```

Build command:

```powershell
dotnet build src\RomStar.BepInEx\RomStar.BepInEx.csproj -c Release
```

The project is compiled with the `NEXUS_FREE` constant.

## Manual Installation

1. Install BepInEx 6 CoreCLR for Romestead.
2. Build or download the Nexus Free DLL.
3. Copy `RomStar.BepInEx.dll` into:

```text
Romestead\BepInEx\plugins\RomStar\
```

4. Start Romestead and press `F1` in game to open RomStar.
