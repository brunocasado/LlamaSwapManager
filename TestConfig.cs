using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LlamaSwapManager.Models;
using LlamaSwapManager.Services;

class TestConfig
{
    static int _passed = 0;
    static int _failed = 0;

    static void Main()
    {
        Console.WriteLine("=== LlamaSwapManager Version Check Tests ===\n");

        TestVersionParsing();
        TestVersionComparison();
        TestHasUpdate();
        TestStripPrefix();
        TestEdgeCases();
        TestAutoUpdateConfig();

        Console.WriteLine($"\n=== Results: {_passed} passed, {_failed} failed ===");
        Environment.Exit(_failed > 0 ? 1 : 0);
    }

    static void Assert(bool condition, string testName)
    {
        if (condition)
        {
            Console.WriteLine($"  PASS: {testName}");
            _passed++;
        }
        else
        {
            Console.WriteLine($"  FAIL: {testName}");
            _failed++;
        }
    }

    static void AssertEqual<T>(T expected, T actual, string testName)
    {
        Assert(expected!.Equals(actual), $"{testName} (expected {expected}, got {actual})");
    }

    static void TestVersionParsing()
    {
        Console.WriteLine("\n--- Version Parsing ---");

        var v1 = VersionComparer.Parse("v224");
        Assert(v1 is not null, "Parse 'v224' succeeds");
        AssertEqual(VersionType.LlamaSwap, v1!.Type, "v224 type is LlamaSwap");
        AssertEqual(224, v1.NumericValue, "v224 value is 224");

        var v2 = VersionComparer.Parse("b9616");
        Assert(v2 is not null, "Parse 'b9616' succeeds");
        AssertEqual(VersionType.LlamaCpp, v2!.Type, "b9616 type is LlamaCpp");
        AssertEqual(0x9616, v2.NumericValue, "b9616 value is 0x9616");

        var v3 = VersionComparer.Parse("v1");
        Assert(v3 is not null, "Parse 'v1' succeeds");
        AssertEqual(1, v3!.NumericValue, "v1 value is 1");

        var v4 = VersionComparer.Parse("b0001");
        Assert(v4 is not null, "Parse 'b0001' succeeds");
        AssertEqual(1ul, v4!.NumericValue, "b0001 value is 1");

        var vNull = VersionComparer.Parse(null);
        Assert(vNull is null, "Parse null returns null");

        var vEmpty = VersionComparer.Parse("");
        Assert(vEmpty is null, "Parse empty returns null");

        var vBad = VersionComparer.Parse("invalid");
        Assert(vBad is null, "Parse 'invalid' returns null");

        var vSpaces = VersionComparer.Parse("  v224  ");
        Assert(vSpaces is not null, "Parse '  v224  ' trims whitespace");
        AssertEqual("v224", vSpaces!.Raw, "Parsed raw is trimmed");
    }

    static void TestVersionComparison()
    {
        Console.WriteLine("\n--- Version Comparison ---");

        // Same version
        AssertEqual(0, VersionComparer.Compare("v224", "v224"), "v224 == v224");
        AssertEqual(0, VersionComparer.Compare("b9616", "b9616"), "b9616 == b9616");

        // LlamaSwap: newer
        AssertEqual(1, VersionComparer.Compare("v224", "v223"), "v224 > v223");
        AssertEqual(-1, VersionComparer.Compare("v223", "v224"), "v223 < v224");

        // LlamaSwap: large gap
        AssertEqual(-1, VersionComparer.Compare("v1", "v224"), "v1 < v224");
        AssertEqual(1, VersionComparer.Compare("v224", "v1"), "v224 > v1");

        // LlamaSwap: single digit vs double
        AssertEqual(-1, VersionComparer.Compare("v9", "v10"), "v9 < v10");
        AssertEqual(1, VersionComparer.Compare("v10", "v9"), "v10 > v9");

        // LlamaCpp: newer
        AssertEqual(1, VersionComparer.Compare("b9616", "b9610"), "b9616 > b9610");
        AssertEqual(-1, VersionComparer.Compare("b9610", "b9616"), "b9610 < b9616");

        // LlamaCpp: hex comparison (not decimal)
        AssertEqual(1, VersionComparer.Compare("b9616", "b9615"), "b9616 > b9615 (hex)");

        // Different types: not comparable
        AssertEqual(0, VersionComparer.Compare("v224", "b9616"), "v224 vs b9616 (different types)");
        AssertEqual(0, VersionComparer.Compare("b9616", "v224"), "b9616 vs v224 (different types)");

        // Null handling
        AssertEqual(0, VersionComparer.Compare(null, null), "null == null");
        AssertEqual(-1, VersionComparer.Compare(null, "v224"), "null < v224");
        AssertEqual(1, VersionComparer.Compare("v224", null), "v224 > null");
    }

    static void TestHasUpdate()
    {
        Console.WriteLine("\n--- HasUpdate ---");

        Assert(VersionComparer.HasUpdate("v223", "v224"), "v223 -> v224 has update");
        Assert(!VersionComparer.HasUpdate("v224", "v224"), "v224 -> v224 no update");
        Assert(!VersionComparer.HasUpdate("v224", "v223"), "v224 -> v223 no update");
        Assert(VersionComparer.HasUpdate("v1", "v10"), "v1 -> v10 has update");
        // null current version: Compare returns -1, so HasUpdate returns true (conservative)
        Assert(VersionComparer.HasUpdate(null, "v224"), "null -> v224 returns true (conservative)");
        Assert(!VersionComparer.HasUpdate("v224", null), "v224 -> null no update");
        Assert(!VersionComparer.HasUpdate("v224", "b9616"), "v224 -> b9616 no update (diff type)");
    }

    static void TestStripPrefix()
    {
        Console.WriteLine("\n--- StripPrefix ---");

        AssertEqual("224", VersionComparer.StripPrefix("v224"), "Strip 'v224'");
        AssertEqual("224", VersionComparer.StripPrefix("V224"), "Strip 'V224'");
        AssertEqual("9616", VersionComparer.StripPrefix("b9616"), "Strip 'b9616'");
        AssertEqual("9616", VersionComparer.StripPrefix("B9616"), "Strip 'B9616'");
        AssertEqual("224", VersionComparer.StripPrefix("224"), "No strip '224'");
        AssertEqual("", VersionComparer.StripPrefix(""), "Strip empty");
        AssertEqual("", VersionComparer.StripPrefix(null!), "Strip null");
    }

    static void TestEdgeCases()
    {
        Console.WriteLine("\n--- Edge Cases ---");

        // Very large version numbers
        Assert(VersionComparer.HasUpdate("v1", "v999999"), "v1 -> v999999 has update");
        AssertEqual(-1, VersionComparer.Compare("v1", "v999999"), "v1 < v999999");

        // LlamaCpp boundary: b9999 vs ba000
        AssertEqual(1, VersionComparer.Compare("ba000", "b9999"), "ba000 > b9999 (hex boundary)");
        AssertEqual(-1, VersionComparer.Compare("b9999", "ba000"), "b9999 < ba000 (hex boundary)");

        // Zero versions
        AssertEqual(0, VersionComparer.Compare("v0", "v0"), "v0 == v0");
        AssertEqual(-1, VersionComparer.Compare("v0", "v1"), "v0 < v1");

        // Case insensitivity for prefixes
        var vLower = VersionComparer.Parse("V224");
        Assert(vLower is not null, "Parse 'V224' (uppercase V)");
        AssertEqual(224, vLower!.NumericValue, "V224 value is 224");

        var bLower = VersionComparer.Parse("B9616");
        Assert(bLower is not null, "Parse 'B9616' (uppercase B)");
        AssertEqual(0x9616, bLower!.NumericValue, "B9616 value is 0x9616");
    }

    static void TestAutoUpdateConfig()
    {
        Console.WriteLine("\n--- Auto-Update Config ---");

        // Test 1: Create config with auto-update settings
        var config = new LlamaSwapConfig
        {
            Models = new Dictionary<string, ModelConfig>
            {
                ["test"] = new ModelConfig { Cmd = "test" }
            },
            AutoUpdate = new AutoUpdateConfig
            {
                Enabled = true,
                CheckOnStartup = true,
                CheckInterval = "weekly",
                AutoDownload = false
            },
            Binaries = new Dictionary<string, BinaryConfig>
            {
                ["llamaSwap"] = new BinaryConfig { Enabled = true, Version = "v142" },
                ["llamaCpp"] = new BinaryConfig { Enabled = true, Version = "b9611" }
            }
        };

        var (valid, errors) = ConfigValidator.Validate(config);
        Assert(valid, "Auto-update config validates correctly");
        AssertEqual(0, errors.Count, "No validation errors");

        // Test 2: Serialization
        var yaml = config.ToYaml();
        Assert(yaml.Contains("autoUpdate"), "YAML contains autoUpdate key");
        Assert(yaml.Contains("binaries"), "YAML contains binaries key");
        Assert(yaml.Contains("checkInterval"), "YAML contains checkInterval");
        Assert(yaml.Contains("weekly"), "YAML contains weekly interval");

        // Test 3: Round-trip
        var reparsed = LlamaSwapConfig.FromYaml(yaml);
        Assert(reparsed.AutoUpdate != null, "AutoUpdate not null after round-trip");
        AssertEqual(true, reparsed.AutoUpdate!.Enabled, "Enabled round-trips");
        AssertEqual(true, reparsed.AutoUpdate.CheckOnStartup, "CheckOnStartup round-trips");
        AssertEqual("weekly", reparsed.AutoUpdate.CheckInterval, "CheckInterval round-trips");
        AssertEqual(false, reparsed.AutoUpdate.AutoDownload, "AutoDownload round-trips");
        AssertEqual(2, reparsed.Binaries?.Count, "Binaries count round-trips");
        AssertEqual(true, reparsed.Binaries!["llamaSwap"].Enabled, "llamaSwap Enabled round-trips");
        AssertEqual("v142", reparsed.Binaries!["llamaSwap"].Version, "llamaSwap Version round-trips");
        AssertEqual("b9611", reparsed.Binaries!["llamaCpp"].Version, "llamaCpp Version round-trips");

        // Test 4: Invalid check interval
        var badInterval = new LlamaSwapConfig
        {
            Models = new Dictionary<string, ModelConfig> { ["test"] = new ModelConfig { Cmd = "test" } },
            AutoUpdate = new AutoUpdateConfig { CheckInterval = "hourly" }
        };
        var (badValid, badErrors) = ConfigValidator.Validate(badInterval);
        Assert(!badValid, "Invalid check interval fails validation");
        Assert(badErrors.Any(e => e.Contains("checkInterval")), "Error mentions checkInterval");

        // Test 5: Unknown binary name
        var badBinaries = new LlamaSwapConfig
        {
            Models = new Dictionary<string, ModelConfig> { ["test"] = new ModelConfig { Cmd = "test" } },
            Binaries = new Dictionary<string, BinaryConfig>
            {
                ["unknownBinary"] = new BinaryConfig { Enabled = true }
            }
        };
        var (binValid, binErrors) = ConfigValidator.Validate(badBinaries);
        Assert(!binValid, "Unknown binary name fails validation");
        Assert(binErrors.Any(e => e.Contains("unknownBinary")), "Error mentions unknownBinary");

        // Test 6: ConfigService auto-update helpers
        var service = new ConfigService("/tmp/test-au.yml");
        service.SetConfig(reparsed);
        service.EnsureAutoUpdateDefaults();
        var au = service.GetAutoUpdateConfig();
        Assert(au != null, "GetAutoUpdateConfig returns non-null");
        AssertEqual("weekly", au.CheckInterval, "CheckInterval preserved after GetAutoUpdateConfig");

        service.SetAutoUpdateEnabled(false);
        service.SetCheckInterval("monthly");
        service.SetAutoDownload(true);
        Assert(service.HasChanges, "HasChanges after SetAutoUpdateEnabled");
        Assert(service.GetDirtyFields().Contains("autoUpdate"), "autoUpdate in dirty fields");

        service.SetBinaryConfig("llamaCpp", true, "b9612");
        Assert(service.GetDirtyFields().Contains("binaries.llamaCpp"), "binaries.llamaCpp in dirty fields");

        // Test 7: Migration - null AutoUpdate gets defaults
        var migrationConfig = new LlamaSwapConfig
        {
            Models = new Dictionary<string, ModelConfig> { ["test"] = new ModelConfig { Cmd = "test" } }
            // AutoUpdate is null
        };
        var service2 = new ConfigService("/tmp/test-migrate.yml");
        service2.SetConfig(migrationConfig);
        service2.EnsureAutoUpdateDefaults();
        var migrated = service2.GetAutoUpdateConfig();
        Assert(migrated != null, "AutoUpdate initialized after migration");
        AssertEqual(true, migrated.Enabled, "Default Enabled is true");
        AssertEqual(true, migrated.CheckOnStartup, "Default CheckOnStartup is true");
        AssertEqual("daily", migrated.CheckInterval, "Default CheckInterval is daily");
        AssertEqual(false, migrated.AutoDownload, "Default AutoDownload is false");

        // Test 8: Service save with auto-update
        var (saveOk, saveErrs) = service.SaveConfigWithValidation();
        Assert(saveOk, "Save with auto-update config succeeds");
        Assert(File.Exists("/tmp/test-au.yml"), "Config file created");

        // Reload and verify
        var service3 = new ConfigService("/tmp/test-au.yml");
        service3.EnsureAutoUpdateDefaults();
        var reloaded = service3.GetAutoUpdateConfig();
        AssertEqual(false, reloaded.Enabled, "Reloaded Enabled matches");
        AssertEqual("monthly", reloaded.CheckInterval, "Reloaded CheckInterval matches");
        AssertEqual(true, reloaded.AutoDownload, "Reloaded AutoDownload matches");
    }
}
