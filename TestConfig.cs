
using System;
using System.Collections.Generic;
using LlamaSwapManager.Models;
using LlamaSwapManager.Services;

class TestConfig
{
    static void Main()
    {
        Console.WriteLine("=== Test 1: Create config and validate ===");
        var config = new LlamaSwapConfig
        {
            HealthCheckTimeout = 500,
            LogLevel = "info",
            StartPort = 5800,
            GlobalTTL = 0,
            SendLoadingState = true,
            Models = new Dictionary<string, ModelConfig>
            {
                ["mellum2-12b"] = new ModelConfig
                {
                    Name = "Mellum2 12B",
                    Cmd = "~/.llama/llama-server --hf JetBrains/Mellum2-12B-A2.5B-Thinking-GGUF-Q4_K_M --jinja --host 127.0.0.1 --port ${PORT} --temp 0.2 --top-k 80 --repeat-penalty 1.0 --presence-penalty 0 --fit on --no-mmap",
                    CheckEndpoint = "/health"
                },
                ["llama3.1-8b"] = new ModelConfig
                {
                    Name = "Llama 3.1 8B",
                    Cmd = "~/.llama/llama-server --hf meta-llama/Llama-3.1-8B-Instruct-GGUF:Q4_K_M --jinja --host 127.0.0.1 --port ${PORT} --temp 0.7 --top-p 0.95",
                    CheckEndpoint = "none"
                }
            },
            Macros = new Dictionary<string, object>
            {
                ["server_path"] = "~/.llama/llama-server",
                ["default_ctx"] = 4096
            }
        };

        // Validate
        var (isValid, errors) = ConfigValidator.Validate(config);
        Console.WriteLine($"Validation: {(isValid ? "PASS" : "FAIL")}");
        if (!isValid)
        {
            foreach (var e in errors) Console.WriteLine($"  ERROR: {e}");
        }

        // Generate YAML
        var yaml = config.ToYaml();
        Console.WriteLine("\n=== Generated YAML ===");
        Console.WriteLine(yaml);
        Console.WriteLine($"\nYAML length: {yaml.Length} chars");

        // Test round-trip
        Console.WriteLine("\n=== Test 2: Round-trip ===");
        var reparsed = LlamaSwapConfig.FromYaml(yaml);
        Console.WriteLine($"Models count: {reparsed.Models?.Count}");
        Console.WriteLine($"HealthCheckTimeout: {reparsed.HealthCheckTimeout}");
        Console.WriteLine($"LogLevel: {reparsed.LogLevel}");
        Console.WriteLine($"StartPort: {reparsed.StartPort}");
        Console.WriteLine($"GlobalTTL: {reparsed.GlobalTTL}");
        Console.WriteLine($"SendLoadingState: {reparsed.SendLoadingState}");

        if (reparsed.Models != null)
        {
            foreach (var kvp in reparsed.Models)
            {
                Console.WriteLine($"  Model '{kvp.Key}': Name={kvp.Value?.Name}, CheckEndpoint={kvp.Value?.CheckEndpoint}");
                Console.WriteLine($"    Cmd length: {(kvp.Value?.Cmd?.Length ?? 0)}");
            }
        }

        // Test ConfigService
        Console.WriteLine("\n=== Test 3: ConfigService ===");
        var service = new ConfigService("/tmp/test-config.yml");
        service.SetConfig(config);
        Console.WriteLine($"HasChanges: {service.HasChanges}");
        Console.WriteLine($"DirtyFields: {string.Join(", ", service.GetDirtyFields())}");

        // Compare configs (should have changes since SetConfig was called)
        var diffs = service.CompareConfigs();
        Console.WriteLine($"Differences from snapshot: {diffs.Count}");

        // Save with validation
        var (success, saveErrors) = service.SaveConfigWithValidation();
        Console.WriteLine($"Save with validation: {(success ? "PASS" : "FAIL")}");
        if (!success)
        {
            foreach (var e in saveErrors) Console.WriteLine($"  ERROR: {e}");
        }

        // Verify file exists
        Console.WriteLine($"File exists: {System.IO.File.Exists("/tmp/test-config.yml")}");

        // Reload and verify
        Console.WriteLine("\n=== Test 4: Reload ===");
        var service2 = new ConfigService("/tmp/test-config.yml");
        Console.WriteLine($"Reloaded models: {service2.Config.Models?.Count}");
        Console.WriteLine($"HasChanges after reload: {service2.HasChanges}");

        // Test AddModel with custom CheckEndpoint
        Console.WriteLine("\n=== Test 5: AddModel ===");
        service2.AddModel("new-model", "New Model", "echo hello", checkEndpoint: "/custom-health");
        Console.WriteLine($"Models after add: {service2.Config.Models?.Count}");
        var newModel = service2.Config.Models?["new-model"];
        Console.WriteLine($"New model CheckEndpoint: {newModel?.CheckEndpoint}");

        // Test AddModel with "none" CheckEndpoint
        service2.AddModel("no-check-model", "No Check Model", "echo world", checkEndpoint: "none");
        var noCheckModel = service2.Config.Models?["no-check-model"];
        Console.WriteLine($"No-check model CheckEndpoint: {noCheckModel?.CheckEndpoint}");

        // Test invalid endpoint
        Console.WriteLine("\n=== Test 6: Invalid endpoint ===");
        var (epValid, epError) = ConfigValidator.ValidateCheckEndpoint("invalid");
        Console.WriteLine($"Validate 'invalid': valid={epValid}, error={epError}");

        var (epValid2, epError2) = ConfigValidator.ValidateCheckEndpoint("/health");
        Console.WriteLine($"Validate '/health': valid={epValid2}, error={epError2}");

        var (epValid3, epError3) = ConfigValidator.ValidateCheckEndpoint("none");
        Console.WriteLine($"Validate 'none': valid={epValid3}, error={epError3}");

        Console.WriteLine("\n=== All tests complete ===");
    }
}
