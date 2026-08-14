using System.Text.Json;
using DMXCore.PluginSdk;
using Microsoft.Extensions.Logging;

namespace DMXCore100.ExamplePlugin;

/// <summary>
/// Reference plugin that exercises the full DMXCore.PluginSdk surface:
/// declared settings, logging, periodic scheduling, MQTT publish/subscribe,
/// firing input triggers, cue playback events, the persistent state blob,
/// the v0.2 additions — device identity, the entity catalog (observe
/// device state, execute commands), broker connection lifecycle, and QoS —
/// and mDNS/DNS-SD discovery of network devices (SDK 1.1).
/// </summary>
public class ExamplePlugin : IPlugin
{
    private IPluginHost host = null!;
    private readonly List<IDisposable> subscriptions = [];
    private int commandCount;
    private string? lastCueCode;

    public PluginInfo Info { get; } = new()
    {
        // Id/Name/Version come from the csproj (PluginId, PluginDisplayName,
        // Version) via the SDK-generated PluginBuildInfo, always in sync with
        // the generated manifest.json
        Id = PluginBuildInfo.Id,
        Name = PluginBuildInfo.Name,
        Version = PluginBuildInfo.Version,
        Description = "Reference plugin demonstrating the DMXCore.PluginSdk surface.",
        Settings =
        [
            new()
            {
                Key = "status-topic",
                Label = "Status topic",
                Type = PluginSettingType.String,
                DefaultValue = "example-plugin/status",
                Description = "MQTT topic the periodic heartbeat is published to",
            },
            new()
            {
                Key = "command-topic",
                Label = "Command topic",
                Type = PluginSettingType.String,
                DefaultValue = "example-plugin/command",
                Description = "MQTT topic this plugin listens on; any message fires the trigger below",
            },
            new()
            {
                Key = "trigger-code",
                Label = "Trigger code",
                Type = PluginSettingType.String,
                DefaultValue = "EXAMPLE",
                Description = "Input trigger fired when a command message arrives",
            },
            new()
            {
                Key = "heartbeat-interval",
                Label = "Heartbeat interval (seconds)",
                Type = PluginSettingType.Integer,
                DefaultValue = "60",
            },
            new()
            {
                Key = "announce-cues",
                Label = "Announce cue playback over MQTT",
                Type = PluginSettingType.Boolean,
                DefaultValue = "true",
            },
            new()
            {
                Key = "api-key",
                Label = "API key",
                Type = PluginSettingType.String,
                Secret = true,
                Description = "Example of a masked setting (not used by this plugin)",
            },
            new()
            {
                Key = "mdns-service-type",
                Label = "mDNS service type",
                Type = PluginSettingType.String,
                DefaultValue = "_osc._udp",
                Description = "DNS-SD service type the 'discover' command browses for (the device itself advertises _osc._udp when OSC is enabled, so the default finds it)",
            },
        ],
        Triggers =
        [
            new()
            {
                Code = "EXAMPLE",
                Label = "Command received",
                Description = "Fired when a message arrives on the command topic (the code is configurable via the Trigger code setting)",
            },
        ],
    };

    public async Task InitializeAsync(IPluginHost host, CancellationToken cancellationToken)
    {
        this.host = host;

        // Persistent state survives restarts. Settings are for admin-edited
        // configuration; the state blob is for values the plugin itself owns.
        string? state = await host.GetStateJsonAsync(cancellationToken);
        if (state != null && int.TryParse(state, out int savedCount))
        {
            this.commandCount = savedCount;
        }

        host.Logger.LogInformation("Example plugin starting; {Count} commands handled so far", this.commandCount);

        // Device identity (SDK v0.2): stable serial for external ids, the
        // branded product name (whitelabel-correct), and software version.
        var device = host.DeviceInfo;
        host.Logger.LogInformation("Running on {Product} '{DeviceName}' (serial {Serial}, {Version})",
            device.ProductName, device.DeviceName, device.Serial, device.SoftwareVersion);

        // The entity catalog (SDK v0.2): everything the device exposes to
        // integrations - presets, cues, schedules, zones, Control Values,
        // and system state - addressable by stable code.
        var catalog = await host.Entities.GetCatalogAsync(cancellationToken);
        host.Logger.LogInformation("Device exposes {Count} integration entities", catalog.Count);

        // Subscribe to a command topic. Handlers are async, run serially per
        // plugin, and a thrown exception is logged by the host without
        // killing the subscription.
        string commandTopic = host.Settings.GetString("command-topic")!;
        this.subscriptions.Add(host.Mqtt.Subscribe(commandTopic, this.HandleCommandMessage));

        // React to cue playback anywhere in the system (top-level cues only).
        this.subscriptions.Add(host.Playback.OnCueStarted(this.HandleCueStarted));

        // Entity state feed (SDK v0.2): coalesced by the host (an entity
        // settles at its final value; fade-rate intermediates are dropped).
        // This example mirrors the master dimmer onto an MQTT topic.
        this.subscriptions.Add(host.Entities.OnStateChanged(this.HandleEntityState));

        // Broker lifecycle (SDK v0.2): the handler always sees the current
        // state first, then every transition. Topic subscriptions re-apply
        // automatically on reconnect but published retained state does not -
        // republish it here (Home Assistant discovery configs would follow
        // the same pattern).
        this.subscriptions.Add(host.Mqtt.OnConnectionChanged(this.HandleConnectionChanged));

        // Settings can change while the plugin runs; re-read them on change.
        this.subscriptions.Add(host.Settings.OnChanged(this.HandleSettingsChanged));

        // Periodic work; invocations never overlap.
        int interval = host.Settings.GetInteger("heartbeat-interval") ?? 60;
        this.subscriptions.Add(host.SchedulePeriodic(TimeSpan.FromSeconds(interval), this.PublishHeartbeat));
    }

    public Task ShutdownAsync(CancellationToken cancellationToken)
    {
        foreach (var subscription in this.subscriptions)
        {
            subscription.Dispose();
        }

        this.host.Logger.LogInformation("Example plugin stopped");

        return Task.CompletedTask;
    }

    private async Task HandleCommandMessage(MqttMessage message, CancellationToken cancellationToken)
    {
        this.commandCount++;
        this.host.Logger.LogInformation("Command #{Count} received on {Topic}: {Payload}", this.commandCount, message.Topic, message.Payload);

        // "dim <0..1>" drives the master dimmer through the entity catalog
        // (SDK v0.2) - the same pipeline every other control surface uses.
        string[] parts = message.Payload.Split(' ', 2);
        if (string.Equals(parts[0], "dim", StringComparison.OrdinalIgnoreCase) &&
            parts.Length == 2 &&
            double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double level))
        {
            await this.host.Entities.ExecuteAsync("system.masterdimmer", PluginEntityCommand.SetLevel(level), cancellationToken);
        }
        else if (string.Equals(parts[0], "discover", StringComparison.OrdinalIgnoreCase))
        {
            // mDNS/DNS-SD discovery (SDK 1.1): find devices on the local
            // network through the host's shared browser - no mDNS stack in
            // the plugin. Refresh forces a fresh query, which fits an
            // explicit user command; background polling should use
            // GetServicesAsync instead (reads the live cache, no wait).
            string serviceType = parts.Length == 2 ? parts[1] : this.host.Settings.GetString("mdns-service-type")!;
            var services = await this.host.Mdns.RefreshServicesAsync(serviceType, cancellationToken);

            this.host.Logger.LogInformation("Discovered {Count} {ServiceType} service(s)", services.Count, serviceType);

            string statusTopic = this.host.Settings.GetString("status-topic")!;
            string json = JsonSerializer.Serialize(services.Select(s => new
            {
                name = s.InstanceName,
                address = s.Address,
                port = s.Port,
            }));
            await this.host.Mqtt.PublishAsync($"{statusTopic}/mdns", json, retain: false, cancellationToken);
        }
        else
        {
            // Otherwise fire a trigger and let the venue decide what it does
            // in the normal trigger configuration UI - preferable to
            // hardcoding a cue here.
            string triggerCode = this.host.Settings.GetString("trigger-code")!;
            await this.host.Triggers.FireAsync(triggerCode, cancellationToken);
        }

        await this.host.SetStateJsonAsync(this.commandCount.ToString(), cancellationToken);
    }

    private async Task HandleCueStarted(CuePlaybackEvent playbackEvent, CancellationToken cancellationToken)
    {
        this.lastCueCode = playbackEvent.CueCode;

        await PublishLastCue(cancellationToken);
    }

    private async Task PublishLastCue(CancellationToken cancellationToken)
    {
        if (this.lastCueCode == null || this.host.Settings.GetBoolean("announce-cues") != true)
        {
            return;
        }

        string statusTopic = this.host.Settings.GetString("status-topic")!;

        // retain: true makes the broker replay the latest value to new
        // subscribers - the same mechanism Home Assistant MQTT Discovery
        // relies on for config and state topics. AtLeastOnce because a
        // retained value that silently goes missing is worse than a
        // duplicate.
        await this.host.Mqtt.PublishAsync($"{statusTopic}/last-cue", this.lastCueCode, retain: true, MqttQos.AtLeastOnce, cancellationToken);
    }

    private async Task HandleEntityState(PluginEntityState state, CancellationToken cancellationToken)
    {
        // Mirror one entity as a taste of the state feed; a real integration
        // would map the whole catalog (see the platform integration plan).
        if (!string.Equals(state.Code, "system.masterdimmer", StringComparison.OrdinalIgnoreCase) || !state.Level.HasValue)
        {
            return;
        }

        string statusTopic = this.host.Settings.GetString("status-topic")!;
        await this.host.Mqtt.PublishAsync($"{statusTopic}/master-dimmer",
            state.Level.Value.ToString("F2", System.Globalization.CultureInfo.InvariantCulture),
            retain: true, MqttQos.AtLeastOnce, cancellationToken);
    }

    private async Task HandleConnectionChanged(bool connected, CancellationToken cancellationToken)
    {
        this.host.Logger.LogInformation("Broker connection: {State}", connected ? "up" : "down");

        if (connected)
        {
            // Republish retained state after a reconnect; the broker may have
            // restarted and lost it (subscriptions are re-applied by the
            // host, published values are this plugin's job)
            await PublishLastCue(cancellationToken);
        }
    }

    private Task HandleSettingsChanged(CancellationToken cancellationToken)
    {
        this.host.Logger.LogInformation("Settings changed; command topic is now {Topic}", this.host.Settings.GetString("command-topic"));

        // A real plugin would re-create its subscriptions here when topics
        // change; kept simple in this example.
        return Task.CompletedTask;
    }

    private async Task PublishHeartbeat(CancellationToken cancellationToken)
    {
        if (!this.host.Mqtt.IsConnected)
        {
            return;
        }

        string statusTopic = this.host.Settings.GetString("status-topic")!;
        await this.host.Mqtt.PublishAsync(statusTopic, $"{{\"commands\":{this.commandCount}}}", retain: false, cancellationToken);
    }
}
