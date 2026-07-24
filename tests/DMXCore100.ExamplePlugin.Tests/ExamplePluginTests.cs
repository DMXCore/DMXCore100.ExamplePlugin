using DMXCore.PluginSdk.Testing;
using DMXCore100.ExamplePlugin;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DMXCore100.ExamplePlugin.Tests;

[TestClass]
public class ExamplePluginTests
{
    private static async Task<(ExamplePlugin Plugin, TestPluginHost Host)> CreateInitializedAsync(Action<TestPluginHost>? configure = null)
    {
        var plugin = new ExamplePlugin();
        var host = new TestPluginHost(plugin.Info, logOutput: _ => { });

        configure?.Invoke(host);

        await plugin.InitializeAsync(host, CancellationToken.None);

        return (plugin, host);
    }

    [TestMethod]
    public async Task Initialize_SubscribesCommandTopicAndSchedulesHeartbeat()
    {
        var (_, host) = await CreateInitializedAsync();

        Assert.AreEqual(1, host.PeriodicTasks.Count);
        Assert.AreEqual(TimeSpan.FromSeconds(60), host.PeriodicTasks[0].Interval);
    }

    [TestMethod]
    public async Task CommandMessage_FiresTriggerAndPersistsCount()
    {
        var (_, host) = await CreateInitializedAsync();

        await host.SimulateMqttMessageAsync("example-plugin/command", "pour!");

        Assert.AreEqual("EXAMPLE", host.FiredTriggers.Single());
        Assert.AreEqual("1", host.StateJson);
    }

    [TestMethod]
    public async Task CommandMessage_UsesConfiguredTriggerCode()
    {
        var (_, host) = await CreateInitializedAsync(h => h.SetSetting("trigger-code", "POUR-STARTED"));

        await host.SimulateMqttMessageAsync("example-plugin/command", "go");

        Assert.AreEqual("POUR-STARTED", host.FiredTriggers.Single());
    }

    [TestMethod]
    public async Task CommandCount_ResumesFromPersistedState()
    {
        var (_, host) = await CreateInitializedAsync(h => h.StateJson = "41");

        await host.SimulateMqttMessageAsync("example-plugin/command", "go");

        Assert.AreEqual("42", host.StateJson);
    }

    [TestMethod]
    public async Task CueStarted_PublishesRetainedLastCue()
    {
        var (_, host) = await CreateInitializedAsync();

        await host.SimulateCueStartedAsync("CUE9");

        var message = host.PublishedMessages.Single();
        Assert.AreEqual("example-plugin/status/last-cue", message.Topic);
        Assert.AreEqual("CUE9", message.Payload);
        Assert.IsTrue(message.Retain);
    }

    [TestMethod]
    public async Task CueStarted_WhenAnnounceDisabled_PublishesNothing()
    {
        var (_, host) = await CreateInitializedAsync(h => h.SetSetting("announce-cues", "false"));

        await host.SimulateCueStartedAsync("CUE9");

        Assert.AreEqual(0, host.PublishedMessages.Count);
    }

    [TestMethod]
    public async Task Heartbeat_PublishesCommandCount()
    {
        var (_, host) = await CreateInitializedAsync();
        await host.SimulateMqttMessageAsync("example-plugin/command", "go");
        host.PublishedMessages.Clear();

        await host.RunPeriodicTaskAsync(0);

        var message = host.PublishedMessages.Single();
        Assert.AreEqual("example-plugin/status", message.Topic);
        Assert.AreEqual("{\"commands\":1}", message.Payload);
        Assert.IsFalse(message.Retain);
    }

    [TestMethod]
    public async Task Heartbeat_SkipsWhenDisconnected()
    {
        var (_, host) = await CreateInitializedAsync(h => h.MqttConnected = false);

        await host.RunPeriodicTaskAsync(0);

        Assert.AreEqual(0, host.PublishedMessages.Count);
    }

    [TestMethod]
    public async Task Shutdown_DisposesSubscriptions()
    {
        var (plugin, host) = await CreateInitializedAsync();

        await plugin.ShutdownAsync(CancellationToken.None);
        await host.SimulateMqttMessageAsync("example-plugin/command", "go");

        Assert.AreEqual(0, host.FiredTriggers.Count);
    }
}
