using DMXCore.PluginSdk;
using DMXCore.PluginSdk.Testing;
using DMXCore100.ExamplePlugin;

// Interactive dev harness: run this project in Visual Studio (F5) to exercise
// the plugin against an in-memory host without a DMX Core 100 device. Every
// action the plugin takes (publishes, trigger fires, log lines) is echoed to
// the console.

var plugin = new ExamplePlugin();
var host = new TestPluginHost(plugin.Info);

// Seed a small entity catalog so the plugin's catalog readout has content
// (a real device populates this from its presets/cues/zones/etc.)
host.EntityCatalog.Add(new PluginEntity { Code = "system.masterdimmer", Name = "Master Dimmer", Kind = PluginEntityKind.Level });
host.EntityCatalog.Add(new PluginEntity { Code = "preset.PARTY", Name = "Party Mode", Kind = PluginEntityKind.Scene });
host.EntityCatalog.Add(new PluginEntity { Code = "cv.VOL1", Name = "Bar Volume", Kind = PluginEntityKind.Level });

// Seed discoverable mDNS services so `m discover` has something to find
// (a real device serves these from its shared long-running mDNS browser)
host.MdnsServices["_osc._udp"] =
[
    new MdnsServiceInfo
    {
        InstanceName = "DMX Core 100 - DEV123",
        Hostname = "dmxcore.local",
        Address = "192.168.1.20",
        Port = 8000,
        Properties = new Dictionary<string, string>(),
    },
    new MdnsServiceInfo
    {
        InstanceName = "Lighting Console",
        Hostname = "console.local",
        Address = "192.168.1.30",
        Port = 9000,
        Properties = new Dictionary<string, string> { ["version"] = "2.4" },
    },
];

Console.WriteLine($"=== {plugin.Info.Name} {plugin.Info.Version} dev host ===");
Console.WriteLine();

await plugin.InitializeAsync(host, CancellationToken.None);

PrintHelp();

bool running = true;
while (running)
{
    Console.Write("> ");
    string? input = Console.ReadLine()?.Trim();
    if (input == null)
        break;

    try
    {
        switch (input.Split(' ')[0].ToLowerInvariant())
        {
            case "m":
                string payload = input.Length > 2 ? input[2..] : "test payload";
                string topic = host.Settings.GetString("command-topic")!;
                await host.SimulateMqttMessageAsync(topic, payload);
                break;

            case "c":
                await host.SimulateCueStartedAsync(input.Length > 2 ? input[2..] : "CUE1");
                break;

            case "e":
                await host.SimulateCueEndedAsync(input.Length > 2 ? input[2..] : "CUE1");
                break;

            case "h":
                await host.RunPeriodicTaskAsync(0);
                break;

            case "s":
                string[] parts = input.Split(' ', 3);
                if (parts.Length < 3)
                {
                    Console.WriteLine("usage: s <key> <value>");

                    break;
                }
                host.SetSetting(parts[1], parts[2]);
                await host.TriggerSettingsChangedAsync();
                break;

            case "d":
                Console.WriteLine($"  connected:  {host.MqttConnected}");
                Console.WriteLine($"  state:      {host.StateJson ?? "(none)"}");
                Console.WriteLine($"  published:  {host.PublishedMessages.Count}");
                Console.WriteLine($"  triggers:   {string.Join(", ", host.FiredTriggers.Distinct())}");
                Console.WriteLine($"  entity cmds: {string.Join(", ", host.ExecutedEntityCommands.Select(x => $"{x.EntityCode}:{x.Command.Type}"))}");
                break;

            case "i":
                Console.WriteLine($"  device:        {host.DeviceInfo.ProductName} '{host.DeviceInfo.DeviceName}'");
                Console.WriteLine($"  serial:        {host.DeviceInfo.Serial}");
                Console.WriteLine($"  version:       {host.DeviceInfo.SoftwareVersion}");
                Console.WriteLine($"  availability:  {host.Mqtt.DeviceAvailabilityTopic}");
                Console.WriteLine($"                 {host.Mqtt.PluginAvailabilityTopic}");
                break;

            case "v":
                double level = input.Length > 2 && double.TryParse(input[2..], out double parsed) ? parsed : 0.5;
                await host.SimulateEntityStateAsync(new PluginEntityState { Code = "system.masterdimmer", Level = level });
                break;

            case "x":
                await host.SimulateMqttConnectionChangedAsync(!host.MqttConnected);
                Console.WriteLine($"  MQTT connected: {host.MqttConnected}");
                break;

            case "q":
                running = false;
                break;

            case "?":
            case "help":
                PrintHelp();
                break;

            default:
                if (input.Length > 0)
                    Console.WriteLine("unknown command, ? for help");
                break;
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  !! {ex.GetType().Name}: {ex.Message}");
    }
}

await plugin.ShutdownAsync(CancellationToken.None);
Console.WriteLine("shut down cleanly");

static void PrintHelp()
{
    Console.WriteLine("""
        Commands:
          m [payload]     simulate MQTT message on the command topic
                          (try: m dim 0.4 - drives the master dimmer entity;
                           m discover - lists seeded mDNS services via host.Mdns)
          c [cueCode]     simulate cue started (default CUE1)
          e [cueCode]     simulate cue ended (default CUE1)
          v [level]       simulate a master dimmer entity state change
          h               run the heartbeat (periodic task) once
          s <key> <value> change a setting and notify the plugin
          x               toggle MQTT connection (delivers OnConnectionChanged)
          i               show device info and availability topics
          d               dump recorded host state
          q               quit (runs plugin shutdown)
        """);
}
