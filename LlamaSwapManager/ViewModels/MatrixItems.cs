using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace LlamaSwapManager.ViewModels;

public partial class MatrixCombinationItem : ObservableObject
{
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private ObservableCollection<MatrixModelSelectionItem> _models = new();
    public ObservableCollection<MatrixCombinationItem>? ParentCollection { get; set; }
    public event Action? Changed;
    public ICommand RemoveCommand { get; }

    public MatrixCombinationItem()
    {
        RemoveCommand = new RelayCommand(() =>
        {
            if (ParentCollection != null && ParentCollection.Contains(this))
            {
                ParentCollection.Remove(this);
                Changed?.Invoke();
            }
        });
    }

    partial void OnNameChanged(string value) => Changed?.Invoke();
}

public partial class MatrixModelSelectionItem : ObservableObject
{
    [ObservableProperty] private string _modelId = "";
    [ObservableProperty] private string _alias = "";
    [ObservableProperty] private bool _isSelected;
    public event Action? Changed;

    partial void OnIsSelectedChanged(bool value) => Changed?.Invoke();
    partial void OnModelIdChanged(string value) => Changed?.Invoke();
    partial void OnAliasChanged(string value) => Changed?.Invoke();
}

// Matrix entry item (reusable for vars and sets)
public partial class MatrixEntryItem : ObservableObject
{
    [ObservableProperty] private string _key = "";
    [ObservableProperty] private string _value = "";

    public event Action? Changed;
    public ICommand RemoveCommand { get; }
    public ObservableCollection<MatrixEntryItem>? ParentCollection { get; set; }

    public MatrixEntryItem()
    {
        RemoveCommand = new RelayCommand(() =>
        {
            if (ParentCollection != null && ParentCollection.Contains(this))
            {
                ParentCollection.Remove(this);
                Changed?.Invoke();
            }
        });
    }

    partial void OnKeyChanged(string value) => Changed?.Invoke();
    partial void OnValueChanged(string value) => Changed?.Invoke();
}

// Evict cost item
public partial class EvictCostItem : ObservableObject
{
    public IReadOnlyList<string> PriorityOptions { get; } = new[] { "Keep longer", "Normal", "Evict sooner", "Custom" };

    [ObservableProperty] private string _key = "";
    [ObservableProperty] private string _modelId = "";
    [ObservableProperty] private string _value = "";
    [ObservableProperty] private string _priority = "Normal";
    private int _syncDepth = 0;

    /// <summary>
    /// Raised when priority or value changes, to notify the parent collection.
    /// </summary>
    public event Action? Changed;

    public ICommand RemoveCommand { get; }
    public ObservableCollection<EvictCostItem>? ParentCollection { get; set; }

    public EvictCostItem()
    {
        RemoveCommand = new RelayCommand(() =>
        {
            if (ParentCollection != null && ParentCollection.Contains(this))
            {
                ParentCollection.Remove(this);
                Changed?.Invoke();
            }
        });
    }

    partial void OnPriorityChanged(string value)
    {
        if (_syncDepth > 0) return;
        _syncDepth++;
        try
        {
            var nextValue = value switch
            {

                "Keep longer" => "1",
                "Normal" => "",
                "Evict sooner" => "100",
                _ => Value
            };
            if (Value != nextValue) Value = nextValue;
            Changed?.Invoke();
        }
        finally { _syncDepth--; }
    }

    partial void OnValueChanged(string value)
    {
        if (_syncDepth > 0) return;
        _syncDepth++;
        try
        {
            var sanitized = SanitizeCost(value);
            if (Value != sanitized)
            {
                Value = sanitized;
            }

            var next = PriorityFromCost(sanitized);
            // Manual cost edits must update the visual priority without letting
            // OnPriorityChanged map the combo selection back over the typed value.
            // Example: typing 60 should show Custom, but must keep Value=60.
            if (Priority != next)
            {
                Priority = next;
            }
            Changed?.Invoke();
        }
        finally { _syncDepth--; }
    }

    partial void OnKeyChanged(string value) => Changed?.Invoke();
    partial void OnModelIdChanged(string value) => Changed?.Invoke();

    private static string SanitizeCost(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var digits = new string(value.Where(char.IsDigit).ToArray());
        if (string.IsNullOrWhiteSpace(digits)) return string.Empty;
        if (!int.TryParse(digits, out var cost)) return string.Empty;
        return cost <= 0 ? string.Empty : cost.ToString();
    }

    public static string PriorityFromCost(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "Normal";
        if (!int.TryParse(value, out var cost)) return "Custom";
        return cost switch
        {
            <= 0 => "Normal",
            1 => "Keep longer",
            100 => "Evict sooner",
            _ => "Custom"
        };
    }
}
