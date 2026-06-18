using System;
using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace LlamaSwapManager.Models;

/// <summary>
/// Represents a single model configuration item in the UI.
/// </summary>
public partial class ModelItem : ObservableObject
{
    [ObservableProperty] private string _modelId = "";
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _description = "";
    [ObservableProperty] private string _llamaServerPath = "";
    [ObservableProperty] private string _hfModel = "";
    [ObservableProperty] private string _host = "127.0.0.1";
    [ObservableProperty] private string _temperature = "0.2";
    [ObservableProperty] private string _topK = "80";
    [ObservableProperty] private string _repeatPenalty = "1.0";
    [ObservableProperty] private string _presencePenalty = "0";
    [ObservableProperty] private bool _useJinja;
    [ObservableProperty] private bool _fitOn;
    [ObservableProperty] private bool _noMmap;
    [ObservableProperty] private string _ttl = "0";
    [ObservableProperty] private string _aliasesText = "";

    public string CmdPreview => $"llama-server --hf {HfModel} --port ${{PORT}}";

    public ModelItem() { }

    public ModelItem(string modelId, string name, string hfModel, string llamaServerPath)
    {
        ModelId = modelId;
        Name = name;
        HfModel = hfModel;
        LlamaServerPath = llamaServerPath;
    }

    // =====================================================================
    // M7: Input validation helpers
    // =====================================================================

    /// <summary>
    /// Validates a path segment: only alphanumeric, dots, hyphens, underscores, slashes allowed.
    /// </summary>
    private static bool IsValidPathSegment(string segment)
    {
        if (string.IsNullOrEmpty(segment)) return true; // Empty is OK (will use default)
        return System.Text.RegularExpressions.Regex.IsMatch(segment, @"^[a-zA-Z0-9._/\\\-]+$");
    }

    /// <summary>
    /// Validates a Hugging Face model ID: org/name format with optional quantization suffix.
    /// Only alphanumeric, slashes, dots, hyphens, underscores allowed.
    /// </summary>
    private static bool IsValidHfModelId(string modelId)
    {
        if (string.IsNullOrEmpty(modelId)) return true;
        // Reject any shell metacharacters or injection attempts
        return System.Text.RegularExpressions.Regex.IsMatch(modelId, @"^[a-zA-Z0-9._/\-]+$");
    }

    /// <summary>
    /// Generate the cmd string for llama-swap config.
    /// M7: Validates model ID and HfModel to prevent command injection.
    /// </summary>
    public string GenerateCmd()
    {
        var path = LlamaServerPath ?? "llama-server";

        // M7: Validate path — only allow alphanumeric, dots, slashes, hyphens, underscores
        if (!IsValidPathSegment(path))
        {
            throw new ArgumentException("Invalid llama-server path: contains unauthorized characters");
        }

        var parts = new System.Collections.Generic.List<string> { path };

        if (!string.IsNullOrEmpty(HfModel))
        {
            // M7: Validate HfModel — only allow alphanumeric, slashes, dots, hyphens, underscores (Hugging Face repo format)
            if (!IsValidHfModelId(HfModel))
            {
                throw new ArgumentException($"Invalid HfModel ID: '{HfModel}' contains unauthorized characters");
            }
            parts.Add($"--hf {HfModel}");
        }

        if (UseJinja)
            parts.Add("--jinja");

        parts.Add($"--host {Host}");
        parts.Add("--port ${PORT}");

        if (!string.IsNullOrEmpty(Temperature))
            parts.Add($"--temp {Temperature}");
        if (!string.IsNullOrEmpty(TopK))
            parts.Add($"--top-k {TopK}");
        if (!string.IsNullOrEmpty(RepeatPenalty))
            parts.Add($"--repeat-penalty {RepeatPenalty}");
        if (!string.IsNullOrEmpty(PresencePenalty))
            parts.Add($"--presence-penalty {PresencePenalty}");

        if (FitOn)
            parts.Add("--fit on");
        if (NoMmap)
            parts.Add("--no-mmap");

        return string.Join(" \\\n  ", parts);
    }
}

/// <summary>
/// Matrix variable key-value pair.
/// </summary>
public partial class MatrixVarItem : ObservableObject
{
    [ObservableProperty] private string _key = "";
    [ObservableProperty] private string _value = "";

    public ICommand RemoveCommand { get; }
    public ObservableCollection<MatrixVarItem> ParentCollection { get; set; } = null!;

    public MatrixVarItem()
    {
        RemoveCommand = new RelayCommand(() => ParentCollection?.Remove(this));
    }
}

/// <summary>
/// Matrix set (set_name = model1,model2,...).
/// </summary>
public partial class MatrixSetItem : ObservableObject
{
    [ObservableProperty] private string _key = "";
    [ObservableProperty] private string _value = "";

    public ICommand RemoveCommand { get; }
    public ObservableCollection<MatrixSetItem> ParentCollection { get; set; } = null!;

    public MatrixSetItem()
    {
        RemoveCommand = new RelayCommand(() => ParentCollection?.Remove(this));
    }
}

/// <summary>
/// Evict cost entry.
/// </summary>
public partial class EvictCostItem : ObservableObject
{
    [ObservableProperty] private string _key = "";
    [ObservableProperty] private string _value = "0";

    public ICommand RemoveCommand { get; }
    public ObservableCollection<EvictCostItem> ParentCollection { get; set; } = null!;

    public EvictCostItem()
    {
        RemoveCommand = new RelayCommand(() => ParentCollection?.Remove(this));
    }
}
