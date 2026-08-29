# Dalamud Plugins

Subscription URL:

`https://raw.githubusercontent.com/zhui-zi/DalamudPlugins/main/pluginmaster.json`

## Plugins

- **Keita Toolbox** - Provides AEAssist startup management, duty and recruitment automation, the full Occult Crescent Magic Pot Assistant, plugin and map gearset switching, trade protection, IME cleanup, portrait synchronization, local flight, sprint, and advanced movement and combat utilities.
- **Mask of Kefka** - Provides a clean OBS output window without Dalamud overlays, with synchronized non-blocking frame sharing and a Simplified Chinese interface.

Magic Pot automation uses DailyRoutines for travel and duty commands, BOCCHI for combat, and the existing AEAssist, vnavmesh, Lifestream, and EdgeTTS integrations.

## Development

KeitaToolbox source and development instructions are maintained in the [KeitaToolbox repository](https://github.com/zhui-zi/KeitaToolbox).

Run `pwsh -NoProfile -File .\scripts\verify.ps1` to validate repository manifests, release packages, the unlock Worker, and Git diffs.

## License

Project source is licensed under the [MIT License](LICENSE). Bundled third-party components remain subject to their respective licenses.
