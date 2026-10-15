using System.Text.Json;
using AutoSkipper.Models;

namespace AutoSkipper.Tests;

public class ConfigDataTests
{
    private static ConfigData Parse(string json) => JsonSerializer.Deserialize(json, JsonContext.Default.ConfigData)!;
    private static string Write(ConfigData config) => JsonSerializer.Serialize(config, JsonContext.Default.ConfigData);

    [Fact]
    public void Validate_ClampsOutOfRangeSettings()
    {
        var config = new ConfigData
        {
            WindowTitle = "  ",
            StandardDelayMin = 9,
            StandardDelayMax = 0.001,
            ColorTolerance = 400,
            LeftClickChance = -1
        };

        config.Validate();

        Assert.Equal("Genshin Impact", config.WindowTitle);
        Assert.Equal(0.01, config.StandardDelayMin);
        Assert.Equal(5.0, config.StandardDelayMax);
        Assert.Equal(255, config.ColorTolerance);
        Assert.Equal(0, config.LeftClickChance);
    }

    [Fact]
    public void Validate_NormalisesNullsFromHandEditedJson()
    {
        var config = Parse("""
            { "WindowTitle": null, "Macros": [ null, { "Name": null, "TriggerKeys": null,
              "Actions": [ null, { "Type": "Hold", "KeyCode": 70, "DurationMs": -5, "DelayMs": -1 } ] } ] }
            """);

        config.Validate();

        var macro = Assert.Single(config.Macros);
        Assert.Equal("New Macro", macro.Name);
        Assert.Empty(macro.TriggerKeys);
        var action = Assert.Single(macro.Actions);
        Assert.Equal(0, action.DurationMs);
        Assert.Equal(0, action.DelayMs);
        Assert.Equal("Genshin Impact", config.WindowTitle);
    }

    [Fact]
    public void Migrate_AddsMouse4MacroOnce()
    {
        var config = Parse("""{ "LeftClickChance": 0 }""");

        Assert.True(config.Migrate());
        Assert.False(config.Migrate());

        Assert.Equal(1, config.ConfigVersion);
        var macro = Assert.Single(config.Macros);
        Assert.Equal(new ushort[] { 0x05 }, macro.TriggerKeys);
        var action = Assert.Single(macro.Actions);
        Assert.Equal(MacroActionType.Press, action.Type);
        Assert.Equal(0x54, action.KeyCode); // T
    }

    [Fact]
    public void Migrate_KeepsExistingMouse4Binding()
    {
        var config = Parse("""{ "Macros": [ { "Name": "Mine", "TriggerKeys": [5], "Actions": [] } ] }""");

        config.Migrate();

        Assert.Equal("Mine", Assert.Single(config.Macros).Name);
    }

    [Fact]
    public void Json_WritesEnumNamesAndOnlyDataProperties()
    {
        var config = new ConfigData();
        config.Macros.Add(new Macro { Actions = { new MacroAction { Type = MacroActionType.Hold, KeyCode = 0x01, DurationMs = 300 } } });

        string json = Write(config);

        Assert.Contains("\"Type\": \"Hold\"", json);
        Assert.DoesNotContain("IsMouse", json);
    }

    [Fact]
    public void Json_ReadsOldIntegerEnums()
    {
        var config = Parse("""{ "Macros": [ { "Actions": [ { "Type": 2, "DelayMs": 50 } ] } ] }""");

        Assert.Equal(MacroActionType.Delay, config.Macros[0].Actions[0].Type);
    }

    [Fact]
    public void MacroClone_IsDeep()
    {
        var original = new Macro
        {
            Name = "A [B]", TriggerKeys = { 0xA2, 0x45 },
            Actions = { new MacroAction { Type = MacroActionType.Hold, KeyCode = 0x46, DurationMs = 300 } }
        };

        var clone = original.Clone();
        clone.Name = "changed";
        clone.TriggerKeys.Add(1);
        clone.Actions[0].DurationMs = 1;
        clone.Actions.Add(new MacroAction());

        Assert.Equal("A [B]", original.Name);
        Assert.Equal(2, original.TriggerKeys.Count);
        Assert.Equal(300, Assert.Single(original.Actions).DurationMs);
    }
}

public class ConfigStoreTests
{
    [Fact]
    public void Load_MissingFile_CreatesDefaultsWithMouse4Macro()
    {
        using var dir = new TempDirectory();

        var store = ConfigStore.Load(dir.Path);

        Assert.Single(store.Config.Macros);
        using var written = JsonDocument.Parse(File.ReadAllText(dir.File("config.json")));
        Assert.Equal(1, written.RootElement.GetProperty("ConfigVersion").GetInt32());
    }

    [Fact]
    public void Load_UpToDateFile_IsNotRewritten()
    {
        using var dir = new TempDirectory();
        ConfigStore.Load(dir.Path);
        var stamp = File.GetLastWriteTimeUtc(dir.File("config.json"));
        Thread.Sleep(20);

        ConfigStore.Load(dir.Path);

        Assert.Equal(stamp, File.GetLastWriteTimeUtc(dir.File("config.json")));
    }

    [Fact]
    public void Load_KeepsUserSettingsWhenMigrating()
    {
        using var dir = new TempDirectory();
        File.WriteAllText(dir.File("config.json"), """{ "LeftClickChance": 0, "ColorTolerance": 7 }""");

        var store = ConfigStore.Load(dir.Path);

        Assert.Equal(0, store.Config.LeftClickChance);
        Assert.Equal(7, store.Config.ColorTolerance);
        Assert.Contains("\"ColorTolerance\": 7", File.ReadAllText(dir.File("config.json")));
    }

    [Fact]
    public void Load_UnreadableFile_IsBackedUpBeforeDefaultsReplaceIt()
    {
        using var dir = new TempDirectory();
        const string broken = """{ "Macros": [ { "Name": "Precious" """;
        File.WriteAllText(dir.File("config.json"), broken);

        var store = ConfigStore.Load(dir.Path);

        string backup = Assert.Single(Directory.GetFiles(dir.Path, "config.broken-*.json"));
        Assert.Equal(broken, File.ReadAllText(backup));
        Assert.Single(store.Config.Macros); // defaults + Mouse4 macro
        using var _ = JsonDocument.Parse(File.ReadAllText(dir.File("config.json")));
    }

    [Fact]
    public async Task SaveAsync_WritesChangesWithoutLeavingTempFile()
    {
        using var dir = new TempDirectory();
        var store = ConfigStore.Load(dir.Path);
        store.Config.Macros.Add(new Macro { Name = "Saved macro" });

        await store.SaveAsync();

        Assert.Contains("Saved macro", File.ReadAllText(dir.File("config.json")));
        Assert.False(File.Exists(dir.File("config.json.tmp")));
    }
}
