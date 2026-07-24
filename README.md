# DMX Core 100 Example Plugin

A reference plugin for the [DMX Core 100](https://docs.dmxcore.com/dmx-core-100)
lighting controller, demonstrating the full
[`DMXCore.PluginSdk`](https://www.nuget.org/packages/DMXCore.PluginSdk) surface.

> **Preview:** the plugin SDK contract is published, but host-side plugin
> loading is shipping in a future DMX Core 100 release. This example compiles
> and packs today; installing it on a device is not possible yet. The contract
> is 0.x and may still change.

## What it demonstrates

[`ExamplePlugin.cs`](src/DMXCore100.ExamplePlugin/ExamplePlugin.cs) exercises
every part of the SDK, with comments on when you'd use each piece:

| Feature | Where |
|---|---|
| Declared settings (string/integer/boolean, defaults, masked secret) | `Info.Settings`, edited on the plugin's page in the device's web UI |
| Reading settings + reacting to live edits | `host.Settings` getters, `OnChanged` |
| Logging into the device logs | `host.Logger` (standard `ILogger`) |
| MQTT subscribe (wildcards supported) | command topic → fires a trigger |
| MQTT publish, including retained messages | heartbeat + last-cue status |
| Firing input triggers (venue-configurable actions) | `host.Triggers.FireAsync` |
| Cue playback events | `host.Playback.OnCueStarted` |
| Periodic scheduling (non-overlapping) | `host.SchedulePeriodic` |
| Persistent state across restarts | `host.GetStateJsonAsync` / `SetStateJsonAsync` |

## Building

Requires the .NET 10 SDK.

```bash
./pack.sh        # Linux/macOS
```

```powershell
./pack.ps1       # Windows
```

Either script publishes the project and produces
`artifacts/example-plugin.dmxplugin` — a zip containing `manifest.json` and the
plugin assemblies, which is the format the device's admin UI accepts for
upload.

## How plugins behave on the device

- Plugins run **in-process and fully trusted**; only device administrators can
  install them.
- Handlers and callbacks are dispatched serially per plugin; an exception
  thrown from a handler is logged by the host and counted, but does not kill
  the subscription. Repeatedly faulting plugins are disabled.
- Plugin changes are **applied on device restart**; there is no hot reload.
  Settings edits, by contrast, apply live via `OnChanged`.
- The `DMXCore.PluginSdk` assemblies are provided by the host at runtime — the
  project excludes them from the build output (`ExcludeAssets="runtime"`), so
  they must not be shipped inside the `.dmxplugin`.

## License

[MIT](LICENSE)
