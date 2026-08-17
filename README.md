# DMX Core 100 Example Plugin

A reference plugin for the [DMX Core 100](https://docs.dmxcore.com/dmx-core-100)
lighting controller, demonstrating the full
[`DMXCore.PluginSdk`](https://www.nuget.org/packages/DMXCore.PluginSdk) surface.

The SDK contract is stable (1.x); new capabilities are added minor-version
additively, and a plugin's `manifest.json` declares the lowest contract it
needs via `minSdkVersion`. The manifest is generated at build time from the
project file (`<PluginId>`, `<Version>`, `<PluginMinSdkVersion>`) — see this
repo's csproj; there is no checked-in `manifest.json` to keep in sync.

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
| mDNS/DNS-SD discovery of network devices (SDK 1.1) | `host.Mdns` — send `discover` (optionally `discover _hue._tcp`) on the command topic |

## Output plugins

Plugins can also register **output protocols** (`host.Outputs`, SDK 1.2+):
drivers that turn a slice of DMX channel data into commands for networked
lighting devices — WiFi bulbs, relays, and similar. That side of the SDK is
not exercised here; the reference for it is the
[**Shelly plugin**](https://github.com/DMXCore/DMXCore100.ShellyPlugin), a
complete, shipping output plugin in one small file:

- registering output protocols with their own output type in the device UI
- the per-device session model (latest-wins delivery, host-side rate
  limiting, reconnect on failure)
- destination discovery behind the UI's **Discover** button (SDK 1.3)
- plugin-provided fixture profiles so the device patches like any fixture
  (SDK 1.3)

For a worked design of a hypothetical LIFX output plugin built on the same
APIs — what it takes, which SDK pieces it uses, and protocol notes — see
[docs/lifx-plugin-design.md](docs/lifx-plugin-design.md).

## Building

Requires the .NET 10 SDK.

```bash
./pack.sh        # Linux/macOS
```

```powershell
./pack.ps1       # Windows
```

Either script runs `dotnet pack`, which (through the SDK's pack targets)
produces two things in `artifacts/`:

- `DMXCore.Plugin.Example.<version>.nupkg` — the **plugin registry package**.
  DMX Core 100 devices browse nuget.org for packages of type `DmxCorePlugin`
  and install/update them directly, so pushing this to nuget.org is how a
  plugin is released. Its only payload is `content/plugin.dmxplugin`, and its
  `DMXCore.PluginSdk` dependency range (`[<minSdkVersion>, <next major>.0)`)
  tells devices which versions they can load before downloading.
- `example-plugin.dmxplugin` — the bare archive (generated `manifest.json` +
  plugin assemblies) for manual upload on the device's Plugins page or via
  `deploy-dev.ps1`.

## Publishing your own plugin

1. Set `<PackageId>` (your own prefix — `DMXCore.*` is reserved for
   first-party plugins), `<Description>`, `<PackageProjectUrl>`,
   `<PackageLicenseExpression>` and `<PackageReadmeFile>` in the csproj.
2. `dotnet pack` and push the `.nupkg`:
   `dotnet nuget push artifacts/*.nupkg --source https://api.nuget.org/v3/index.json --api-key <your key>`.
   From GitHub Actions, prefer keyless [Trusted Publishing](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing)
   (register the repo + workflow file on nuget.org once, then):

   ```yaml
   permissions:
     id-token: write
   steps:
     - run: ./pack.sh
     - uses: NuGet/login@v1
       id: login
       with:
         user: your-nuget-profile-name
     - run: dotnet nuget push artifacts/*.nupkg --source https://api.nuget.org/v3/index.json --api-key ${{ steps.login.outputs.NUGET_API_KEY }} --skip-duplicate
   ```

   With `--skip-duplicate`, re-running is harmless — bumping `<Version>` is
   what publishes a new release. (This example plugin itself is not published
   to nuget.org.)
3. It appears on every device's Plugins → Browse page within minutes; devices
   with it installed see the update.

## Developing without a device

Open `DMXCore100.ExamplePlugin.slnx` in Visual Studio:

- **DevHost** (`tools/`) — set as startup project and press F5 for an
  interactive console harness built on
  [`DMXCore.PluginSdk.Testing`](https://www.nuget.org/packages/DMXCore.PluginSdk.Testing):
  simulate MQTT messages, cue events, and settings changes from the keyboard
  and watch every action the plugin takes, no device needed.
- **Tests** (`tests/`) — MSTest unit tests against `TestPluginHost`, showing
  how to assert on fired triggers, published messages, and persisted state.

## How plugins behave on the device

- Plugins run **in-process and fully trusted**; only device administrators can
  install them.
- Handlers and callbacks are dispatched serially per plugin; an exception
  thrown from a handler is logged by the host and counted, but does not kill
  the subscription. Repeatedly faulting plugins are disabled.
- Plugin changes are applied by the **Reload** action on the device's Plugins
  page (or a device restart). Settings edits apply live via `OnChanged`.
- The `DMXCore.PluginSdk` assemblies are provided by the host at runtime — the
  project excludes them from the build output (`ExcludeAssets="runtime"`), so
  they must not be shipped inside the `.dmxplugin`.

## License

[MIT](LICENSE)
