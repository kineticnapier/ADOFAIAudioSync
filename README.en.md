# ADOFAI AudioSync

[日本語](README.md) | [English](README.en.md) | [한국어](README.ko.md)

ADOFAI AudioSync is a Unity Mod Manager mod that stabilizes playback in the A Dance of Fire and Ice editor. It mainly targets starts from a selected floor, reducing delayed audio starts, chart/audio desynchronization, and large jumps to an unintended position. It also includes a BPM and phase tap anchor tool and an experimental tool that estimates speed changes from play errors.

## Features

- **Selected-floor audio synchronization**
  The mod schedules the requested audio position at a future DSP time. It waits until the real AudioSource playhead starts moving, then aligns the chart clock once to the observed position. Abnormal starts can be retried, and if synchronization cannot be established, the mod safely falls back to ADOFAI's stock playback path.
- **Unmodified game start lifecycle**
  The mod does not suppress or reinvoke `scnGame.Play`, and it does not rewrite the checkpoint selected by ADOFAI. Other mods therefore observe one normal playback start.
- **High-BPM countdown folding**
  When playback starts inside an extremely fast section such as 3200 BPM, the visual lead-in can be folded to a readable speed below a configurable maximum. The multiplier is applied only to the planets' lead-in calculation; the selected floor and audio start time remain unchanged.
- **Independent Pause / Wait Beats timing**
  You can choose whether the high-BPM countdown multiplier also affects Pause-event Wait Beats. By default, Wait Beats keep the chart's normal speed.
- **OGG memory cache**
  Previously loaded OGG files can be reused as decoded AudioClips, reducing repeated decoding when restarting the same chart. Before decoding, the mod derives the PCM size from the Vorbis sample count. Files that exceed the limit remain on ADOFAI's stock streaming path instead of being cached. The cache supports a size limit, least-recently-used eviction, and manual clearing.
- **BPM and phase tap anchor**
  Select a floor and tap along with the music to estimate both BPM and beat phase. You can review the result and apply it to the chart as SetSpeed events.
- **Play-error speed correction (experimental)**
  The tool can analyze early/late timing across sections of a manual playthrough and create BPM correction suggestions from the change in error. It is disabled by default and records data only when explicitly enabled.
- **Diagnostics and failure logs**
  Compact and detailed overlays show information such as schedule residual, playhead correction, start delay, and OGG cache state. A grouped diagnostic log is written when a selected-floor start fails.

## Requirements

- Windows version of A Dance of Fire and Ice
- Unity Mod Manager
- A build environment that can reference the game's Mono/Managed DLLs

If a game update changes the structure of a patched method, the affected Harmony patch may be disabled. Patches are installed independently, so one unsupported feature does not unload the entire mod.

## Installation

Extract the release ZIP into the game's `Mods` directory. The final layout should be:

```text
A Dance of Fire and Ice/
└─ Mods/
   └─ ADOFAIAudioSync/
      ├─ ADOFAIAudioSync.dll
      └─ Info.json
```

Release packages do not contain a PDB. Settings are available from the Unity Mod Manager Mods screen. An older `Settings.xml` is migrated on startup; if it cannot be read, it is preserved with a timestamped `.broken-*` suffix.

## Shortcuts

| Input | Action |
|---|---|
| `Ctrl+F9` | Cycle diagnostics through off, compact, and detailed |
| `Ctrl+F6` | Open or close the BPM/phase tap window |
| `Ctrl+T` | Use the selected floor as a new BPM/phase measurement anchor |
| `F10` | Record a tap; configurable in the tap window |
| `Backspace` | Remove the most recent tap |
| `Enter` | Finish the current tap measurement |
| `Ctrl+Enter` | Apply the analyzed result to the chart |
| `Escape` | Cancel the current measurement |
| `Ctrl+Shift+E` | Start or stop experimental play-error recording |

Changes made with `Ctrl+F9` are saved immediately.

## Building

Requirements:

- Windows
- Visual Studio 2022 or Build Tools with the .NET desktop build tools workload
- A Dance of Fire and Ice
- The game's `Managed` directory with Unity Mod Manager installed

For the default Steam location, run this from the repository root:

```powershell
.\build.ps1
```

For another Steam library:

```powershell
.\build.ps1 -GameManagedDir "D:\SteamLibrary\steamapps\common\A Dance of Fire and Ice\A Dance of Fire and Ice_Data\Managed"
```

The path can also be provided through an environment variable:

```powershell
$env:ADOFAI_GAME_MANAGED_DIR = "D:\SteamLibrary\steamapps\common\A Dance of Fire and Ice\A Dance of Fire and Ice_Data\Managed"
.\build.ps1
```

On success, the DLL is written to `src\bin\Release` and the Unity Mod Manager ZIP is written to `artifacts`. The build script also verifies that the archive contains only `ADOFAIAudioSync/ADOFAIAudioSync.dll` and `ADOFAIAudioSync/Info.json`, both with ZIP-standard `/` separators.

To deploy to the local Mods directory while building:

```powershell
.\build.ps1 -DeployDir "C:\Program Files (x86)\Steam\steamapps\common\A Dance of Fire and Ice\Mods\ADOFAIAudioSync"
```

`-DeployDir` also copies the PDB for local diagnostics. Game and Unity DLLs are referenced from the local installation at build time and are not included in this repository.

## Known limitations

- The main target is editor playback. This mod does not change normal level-play audio behavior.
- Near the end of an audio file, if two distinct playhead updates cannot be observed, the mod avoids a risky DSP reservation and falls back to the stock Scrub path.
- OGG cache size is the decoded PCM size calculated from the Vorbis sample count and does not include every temporary allocation made by Unity. An OGG whose size cannot be calculated safely is not cached.
- Compatibility with other mods that patch audio or editor playback must be checked individually.

See [`RELEASE_CHECKLIST.md`](RELEASE_CHECKLIST.md) for release regression checks and [`CHANGELOG.md`](CHANGELOG.md) for version history.

## License

[MIT License](LICENSE)
