# RomStarMOD Nexus Free

RomStarMOD Nexus Free is the public, free Nexus Mods edition of RomStar for Romestead.

This GitHub repository provides both public source code and a prebuilt Nexus Free DLL for RomStar.

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

1. Install BepInEx 6 CoreCLR for Romestead.
2. Download `dist/RomStar.BepInEx.dll` from this repository, or use the DLL included in the Nexus Mods release package.
3. Create this folder if it does not already exist:

```text
Romestead\BepInEx\plugins\RomStar\
```

4. Copy `RomStar.BepInEx.dll` into that folder.
5. Start Romestead and press `F1` in game to open RomStar.

## Developer Output

After a local build, the compiled DLL is created under:

```text
src\RomStar.BepInEx\bin\Release\net8.0-windows\
```
