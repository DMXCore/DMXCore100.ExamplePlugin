using DMXCore.PluginSdk.Testing;
using DMXCore100.ExamplePlugin;

// Interactive dev harness: run this project in Visual Studio (F5) to exercise
// the plugin against an in-memory host without a DMX Core 100 device. Every
// action the plugin takes (publishes, trigger fires, log lines) is echoed to
// the console.

var plugin = new ExamplePlugin();
var host = new TestPluginHost(plugin.Info);

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
                break;

            case "x":
                host.MqttConnected = !host.MqttConnected;
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
          c [cueCode]     simulate cue started (default CUE1)
          e [cueCode]     simulate cue ended (default CUE1)
          h               run the heartbeat (periodic task) once
          s <key> <value> change a setting and notify the plugin
          x               toggle simulated MQTT connection state
          d               dump recorded host state
          q               quit (runs plugin shutdown)
        """);
}
