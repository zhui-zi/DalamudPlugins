# BOCCHI North

Independently installable Dalamud API 15 build of BOCCHI with Occult Crescent South Horn and North Horn support.

The plugin uses the distinct `BOCCHINorth` identity, assembly, configuration directory, and `/bocchinorth` command family, so it can be installed alongside the original BOCCHI plugin.

## North Horn coverage

- Territory, FATE, magic pot FATE, critical encounter, and Forked Tower event tracking
- All six aetherytes with zone-aware return and teleport routing
- Event navigation and Illegal Mode selection
- Treasure and carrot detection and radar

Precomputed treasure and carrot hunt routes are available only for South Horn. North Horn hunt automation remains disabled until validated vnavmesh route data is available.

## Build

Set `DALAMUD_HOME` to the active Dalamud `Hooks/dev` directory, then run:

```powershell
dotnet build .\source\BOCCHINorth\BOCCHINorth.csproj -c Release
```

The package is generated at `source\BOCCHINorth\bin\Release\BOCCHINorth\latest.zip`.

## Data sources

- [OccultCrescentHelper](https://github.com/NiGuangOwO/OccultCrescentHelper)
- [EurekaTrackerAutoPopper](https://github.com/Infiziert90/EurekaTrackerAutoPopper)
- [DailyRoutines OccultCrescentHelper](https://github.com/Dalamud-DailyRoutines/DailyRoutines.ModulesPublic/tree/main/Duty/OccultCrescentHelper)

Source code is distributed under AGPL-3.0-or-later. See `source\LICENSE.md`.
