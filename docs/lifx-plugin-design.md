# Designing a LIFX Output Plugin

A worked design for a plugin that drives **LIFX** WiFi lights (bulbs, Tube,
Beam, strips) from a DMX Core 100. Nothing here is built — this page exists
to show what building an output plugin for a new device family takes, using
LIFX as a realistic example. The
[Shelly plugin](https://github.com/DMXCore/DMXCore100.ShellyPlugin) is the
shipping reference for the same APIs; LIFX mostly differs in speaking its own
UDP protocol instead of MQTT.

## What the plugin would provide

| Piece | SDK API | LIFX specifics |
|---|---|---|
| Output protocols | `host.Outputs.RegisterOutputProtocol` | `LIFX_COLOR` (single-zone bulbs), `LIFX_PIXEL` (Tube/Beam/strips) |
| Per-device delivery | `IPluginOutputSession` | UDP datagrams to the bulb's port 56700 |
| Device discovery | `GetDestinationOptionsAsync` | LIFX's own UDP broadcast (not mDNS) |
| Fixture profiles | `host.Outputs.RegisterFixtureProfile` | "LIFX Color" with RGB / RGB 16-bit / RGB+CT personalities |

All of it fits in one source file, like the Shelly plugin.

## What the host already does for you

Before writing any protocol code, know what you do **not** implement:

- The Core slices universes, dedupes unchanged values, and rate-limits per
  device to your descriptor's `MaxUpdatesPerSecond`.
- Your session's `SendAsync` runs on a host worker with **latest-wins**
  coalescing — never on the render thread, never with a backlog.
- Failures self-heal: return `false` or throw, and the host reopens your
  session with backoff and retries with the newest values.
- The SHELLY/LIFX-style output type, the protocol dropdown, the Discover
  button, and the fixture editor integration are all rendered by the Core
  from your descriptors — no UI code in the plugin.

## The LIFX LAN protocol in five facts

1. **Transport**: binary packets over UDP, device port **56700**. No pairing,
   no cloud, no encryption on the LAN protocol.
2. **Color model**: HSBK — four `uint16` fields (hue, saturation, brightness,
   kelvin). Scale 8-bit DMX values by ×257. `SetColor` (packet 102) sets the
   whole device; set `res_required=0, ack_required=0` when streaming.
3. **Smoothing trick**: every set carries a `duration` (ms). Use roughly
   1.5× your send interval (e.g. 75 ms at 20 msg/s) so consecutive updates
   fade into each other and UDP jitter disappears.
4. **Rate limit**: community guidance is ~**20 messages/second per device** —
   that's your `MaxUpdatesPerSecond`.
5. **Two pixel families**: Beam/strips/Neon are *multizone*
   (`SetExtendedColorZones`, packet 510, 82 zones per packet); Tube, Candle,
   and Ceiling are *matrix* devices (Tile API `Set64`, packet 715). Which
   family — and how many pixels — comes from the
   [LIFX product registry](https://github.com/LIFX/products); embed a
   snapshot in the plugin.

## Sketch: protocols and sessions

```csharp
host.Outputs.RegisterOutputProtocol(
    new OutputProtocolDescriptor
    {
        Id = "LIFX_COLOR",
        DisplayName = "LIFX Color (single zone)",
        PortType = "LIFX",
        MaxUpdatesPerSecond = 20,
        SupportsDestinationDiscovery = true,
        SuggestedProfileCode = "LIFX_COLOR",
        SuggestedPersonality = "RGB",
    },
    new LifxColorProtocol(host));
```

`GetChannelCount` returns 3 for RGB (or 4 for RGB+CT, 6 for 16-bit RGB —
one protocol per layout, or personalities that agree with your profile).

The session owns one `UdpClient` per mapped device:

```csharp
public async Task<bool> SendAsync(ReadOnlyMemory<byte> ch, CancellationToken ct)
{
    // RGB -> HSBK, 8-bit -> 16-bit (×257), duration ≈ 1.5× send interval
    var (h, s, b) = RgbToHsb(ch.Span[0], ch.Span[1], ch.Span[2]);
    byte[] packet = LifxPacket.SetColor(h, s, b, kelvin: 3500, durationMs: 75);

    await this.udp.SendAsync(packet, this.deviceEndpoint, ct);

    return true; // fire-and-forget; the host retries on exceptions
}
```

For `LIFX_PIXEL`, the session queries the zone/tile layout once on open, and
`SendAsync` chunks the channel slice into `SetExtendedColorZones` or `Set64`
packets depending on the product family.

Two conversion notes that matter in practice:

- **The Core renders RGB, LIFX wants HSBK.** Convert in the session; nobody
  else needs to know. A profile with a **ColorTemperature** channel maps
  naturally onto HSBK's kelvin field — that exposes the bulb's real white
  engine instead of faking white through RGB.
- **Don't register HSBK personalities.** The Core's fixture pipeline speaks
  RGB + white channels; HSBK-as-channels only makes sense for raw DMX users,
  and they can drive the channel layout directly from a console.

## Sketch: discovery

LIFX devices don't announce over mDNS, so instead of `host.Mdns` (which the
Shelly plugin uses), broadcast LIFX's own `GetService` (packet 2) and collect
`StateService` replies — plugins are ordinary .NET and can open sockets:

```csharp
public async Task<IReadOnlyList<PluginOutputDestinationOption>?> GetDestinationOptionsAsync(
    bool refresh, CancellationToken ct)
{
    var replies = await LifxDiscovery.BroadcastGetService(timeout: 2000, ct);

    return replies
        .Select(r => new PluginOutputDestinationOption(
            r.IpAddress.ToString(),                       // stored on the mapping
            $"{r.Label} ({r.IpAddress}, {r.ProductName})"))
        .ToList();
}
```

Note the difference from Shelly: Shelly's destination is a *device id* (an
MQTT topic segment — the broker does the addressing), while LIFX's
destination is the *IP address itself*, because the plugin talks straight to
the device. A static DHCP lease per bulb is worth recommending in the
plugin's docs.

## Sketch: fixture profile

```csharp
host.Outputs.RegisterFixtureProfile(new PluginFixtureProfileDescriptor
{
    Code = "LIFX_COLOR",
    Name = "Color Bulb",
    Manufacturer = "LIFX",
    Personalities =
    [
        new() { Name = "RGB",    Channels = [Red, Green, Blue] },
        new() { Name = "RGB+CT", Channels = [Red, Green, Blue, ColorTemperature] },
    ],
});
```

With `SuggestedProfileCode`/`SuggestedPersonality` set on the protocol
descriptors, the fixture editor's **Mapped Device** selector prefills the
patch from an existing LIFX mapping — same flow as the Shelly plugin.

## The step-by-step

1. Copy the [Shelly plugin](https://github.com/DMXCore/DMXCore100.ShellyPlugin)
   repo layout (csproj, `manifest.json`, pack scripts, CI).
2. Write the LIFX packet builder (header + `SetColor`; ~100 lines — or
   reference an existing LIFX LAN library from NuGet, which ships inside your
   `.dmxplugin`).
3. Implement `IPluginOutputProtocol`/`IPluginOutputSession` for single-zone
   bulbs; register protocol + profile. At this point a bulb patched as a
   fixture follows presets, cues, and effects.
4. Add broadcast discovery behind `GetDestinationOptionsAsync`.
5. Add the pixel protocol (multizone + matrix families, product registry
   snapshot for capabilities).
6. Test against `TestPluginHost` (see this repo's `tests/`), then
   `deploy-dev.ps1` onto a real device — uploads hot-reload, no restart.
