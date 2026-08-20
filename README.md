# Dalamud Plugins

Subscription URL:

`https://raw.githubusercontent.com/zhui-zi/DalamudPlugins/main/pluginmaster.json`

## Plugins

- **Keita Toolbox** - Provides AEAssist startup management, duty and recruitment automation, the full Occult Crescent Magic Pot Assistant, plugin and map gearset switching, trade protection, IME cleanup, portrait synchronization, and advanced movement and combat utilities.

Magic Pot automation uses DailyRoutines for travel and duty commands, BOCCHI for combat, and the existing AEAssist, vnavmesh, Lifestream, and EdgeTTS integrations.

## Development

Run `powershell -ExecutionPolicy Bypass -File .\scripts\verify.ps1` for the plugin build, core tests, unlock Worker checks, architecture limits, and diff validation. The plugin build requires a local Dalamud CN development runtime.

## License

Project source is licensed under the [MIT License](LICENSE). Bundled third-party components remain subject to their respective licenses.
