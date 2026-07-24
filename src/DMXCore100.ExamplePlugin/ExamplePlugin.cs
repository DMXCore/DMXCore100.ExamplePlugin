using DMXCore.PluginSdk;
using Microsoft.Extensions.Logging;

namespace DMXCore100.ExamplePlugin;

/// <summary>
/// Reference plugin that exercises the full DMXCore.PluginSdk surface:
/// declared settings, logging, periodic scheduling, MQTT publish/subscribe,
/// firing input triggers, cue playback events, and the persistent state blob.
/// </summary>
public class ExamplePlugin : IPlugin
{
    private IPluginHost host = null!;
    private readonly List<IDisposable> subscriptions = [];
    private int commandCount;

    public PluginInfo Info { get; } = new()
    {
        Id = "example-plugin",
        Name = "DMX Core 100 Example Plugin",
        Version = "1.0.0",
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

        // Subscribe to a command topic. Handlers are async, run serially per
        // plugin, and a thrown exception is logged by the host without
        // killing the subscription.
        string commandTopic = host.Settings.GetString("command-topic")!;
        this.subscriptions.Add(host.Mqtt.Subscribe(commandTopic, this.HandleCommandMessage));

        // React to cue playback anywhere in the system (top-level cues only).
        this.subscriptions.Add(host.Playback.OnCueStarted(this.HandleCueStarted));

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

        // Fire a trigger and let the venue decide what it does in the normal
        // trigger configuration UI - preferable to hardcoding a cue here.
        string triggerCode = this.host.Settings.GetString("trigger-code")!;
        await this.host.Triggers.FireAsync(triggerCode, cancellationToken);

        await this.host.SetStateJsonAsync(this.commandCount.ToString(), cancellationToken);
    }

    private async Task HandleCueStarted(CuePlaybackEvent playbackEvent, CancellationToken cancellationToken)
    {
        if (this.host.Settings.GetBoolean("announce-cues") != true)
        {
            return;
        }

        string statusTopic = this.host.Settings.GetString("status-topic")!;

        // retain: true makes the broker replay the latest value to new
        // subscribers - the same mechanism Home Assistant MQTT Discovery
        // relies on for config and state topics.
        await this.host.Mqtt.PublishAsync($"{statusTopic}/last-cue", playbackEvent.CueCode, retain: true, cancellationToken);
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
