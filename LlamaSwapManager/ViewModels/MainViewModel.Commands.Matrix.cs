using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LlamaSwapManager.Models;
using LlamaSwapManager.Services;

namespace LlamaSwapManager.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private void OnMatrixTextChanged()
        {
            if (_isLoadingConfig || _matrixSyncDepth > 0) return;
            _matrixSyncDepth++;
            try
            {
                SyncMatrixCollectionsFromText();
                RebuildMatrixCombinationsFromSets();
                EnsureMatrixModelCoverage();
                SyncEvictCostsWithCurrentVars(refreshPreview: false);
                UpdateConfigPreviewFromCurrentState();
                AutoPersistMatrixToDisk();
            }
            finally { _matrixSyncDepth--; }
        }
    
        private void PersistConfigToDisk(string? successMessage = null, bool silent = false)
        {
            var configPath = !string.IsNullOrWhiteSpace(ConfigFilePath) ? ConfigFilePath : _processManager.ConfigPath;
            if (string.IsNullOrWhiteSpace(configPath))
            {
                if (!silent)
                    ShowToast("Error: config.yml path is not set.");
                return;
            }
    
            var config = BuildConfigFromUI();
            _configService.SaveConfig(config, configPath);
            _rawConfig = config;
            ConfigPreview = config.ToYaml();
            // Never overwrite RUNTIME StatusText here — toast only (unless silent).
            if (!silent && !string.IsNullOrWhiteSpace(successMessage))
                ShowToast(successMessage);
        }
    
        private void SyncMatrixTextFromCollections()
        {
            MatrixVarsText = string.Join(Environment.NewLine, MatrixVars.Where(v => !string.IsNullOrWhiteSpace(v.Key) || !string.IsNullOrWhiteSpace(v.Value)).Select(v => $"{v.Key} = {v.Value}"));
            MatrixSetsText = string.Join(Environment.NewLine, MatrixSets.Where(v => !string.IsNullOrWhiteSpace(v.Key) || !string.IsNullOrWhiteSpace(v.Value)).Select(v => $"{v.Key} = {v.Value}"));
            EvictCostsText = string.Join(Environment.NewLine, EvictCosts
                .Where(v => !string.IsNullOrWhiteSpace(v.Key) && int.TryParse(v.Value, out var parsed) && parsed > 0)
                .Select(v => $"{v.Key} = {v.Value}"));
        }
    
        private void SyncMatrixCollectionsFromText()
        {
            MatrixVars.Clear();
            foreach (var item in ParseKeyValueLines(MatrixVarsText))
                MatrixVars.Add(CreateMatrixEntryItem(item.Key, item.Value, MatrixVars));
    
            MatrixSets.Clear();
            foreach (var item in ParseKeyValueLines(MatrixSetsText))
                MatrixSets.Add(CreateMatrixEntryItem(item.Key, item.Value, MatrixSets));
    
            EvictCosts.Clear();
            foreach (var item in ParseKeyValueLines(EvictCostsText))
            {
                var value = int.TryParse(item.Value, out var parsed) && parsed > 0 ? item.Value : string.Empty;
                EvictCosts.Add(CreateEvictCostItem(item.Key, "", value));
            }
        }
    
        private void OnEvictCostChanged()
        {
            if (_isLoadingConfig || _matrixSyncDepth > 0) return;
            _matrixSyncDepth++;
            try
            {
                // Do NOT call RefreshConfigPreview() here. That path rebuilds EvictCosts
                // from MatrixVarsText, which clears/recreates the ItemsControl and makes
                // the focused Cost TextBox lose focus on every typed character.
                EvictCostsText = string.Join(Environment.NewLine, EvictCosts
                    .Where(v => !string.IsNullOrWhiteSpace(v.Key) && int.TryParse(v.Value, out var parsed) && parsed > 0)
                    .Select(v => $"{v.Key} = {v.Value}"));
                UpdateConfigPreviewFromCurrentState();
                AutoPersistMatrixToDisk();
            }
            finally { _matrixSyncDepth--; }
        }
    
        private void SyncEvictCostsWithCurrentVars(bool refreshPreview = true)
        {
            var existingByAlias = EvictCosts
                .Where(e => !string.IsNullOrWhiteSpace(e.Key))
                .GroupBy(e => e.Key.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            var existingByModel = EvictCosts
                .Where(e => !string.IsNullOrWhiteSpace(e.ModelId))
                .GroupBy(e => e.ModelId.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
    
            var vars = ParseKeyValueLines(MatrixVarsText).ToList();
            if (!vars.Any() && Models.Any(m => !string.IsNullOrWhiteSpace(m.ModelId)))
            {
                var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                vars = Models.Where(m => !string.IsNullOrWhiteSpace(m.ModelId))
                    .Select(m =>
                    {
                        var alias = MakeAlias(m.ModelId, used);
                        used.Add(alias);
                        return new KeyValuePair<string, string>(alias, m.ModelId);
                    })
                    .ToList();
            }
    
            EvictCosts.Clear();
            foreach (var kv in vars)
            {
                var alias = kv.Key.Trim();
                var modelId = kv.Value.Trim();
                existingByModel.TryGetValue(modelId, out var byModel);
                existingByAlias.TryGetValue(alias, out var byAlias);
                var previous = byModel ?? byAlias;
                var value = previous?.Value;
                if (!int.TryParse(value, out var parsed) || parsed <= 0) value = string.Empty;
                EvictCosts.Add(CreateEvictCostItem(alias, modelId, value));
            }
            EvictCostsText = string.Join(Environment.NewLine, EvictCosts
                .Where(v => !string.IsNullOrWhiteSpace(v.Key) && int.TryParse(v.Value, out var parsed) && parsed > 0)
                .Select(v => $"{v.Key} = {v.Value}"));
            if (refreshPreview) UpdateConfigPreviewFromCurrentState();
        }
    
        private static IEnumerable<KeyValuePair<string, string>> ParseKeyValueLines(string text)
        {
            foreach (var raw in (text ?? string.Empty).Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
            {
                var line = raw.Trim();
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#")) continue;
                var idx = line.IndexOf('=');
                if (idx < 0) continue;
                var key = line[..idx].Trim();
                var value = line[(idx + 1)..].Trim();
                if (!string.IsNullOrWhiteSpace(key))
                    yield return new KeyValuePair<string, string>(key, value);
            }
        }
    
        private void ExecuteAddMatrixVar()
        {
            var item = CreateMatrixEntryItem("", "", MatrixVars);
            MatrixVars.Add(item);
            RefreshConfigPreview();
        }
    
        private void ExecuteAddMatrixSet()
        {
            var item = CreateMatrixEntryItem("", "", MatrixSets);
            MatrixSets.Add(item);
            RefreshConfigPreview();
        }
    
        private void ExecuteAddEvictCost()
        {
            var item = CreateEvictCostItem("", "", "");
            EvictCosts.Add(item);
            RefreshConfigPreview();
        }
    
        private void ExecuteCreateSetFromVars()
        {
            if (MatrixVars.Count == 0) return;
            
            var varKeys = MatrixVars.Select(v => v.Key).ToList();
            if (varKeys.Count == 0) return;
            
            var newItem = CreateMatrixEntryItem($"set_{MatrixSets.Count + 1}", string.Join(" & ", varKeys), MatrixSets);
            MatrixSets.Add(newItem);
            RebuildMatrixCombinationsFromSets();
            RefreshConfigPreview();
        }
        public void SyncMatrixFromVisualBuilder()
        {
            RefreshConfigPreview();
        }
    
        private MatrixEntryItem CreateMatrixEntryItem(string key, string value, ObservableCollection<MatrixEntryItem> parent)
        {
            var item = new MatrixEntryItem { Key = key, Value = value, ParentCollection = parent };
            item.Changed += OnMatrixEntryChanged;
            return item;
        }
    
        private EvictCostItem CreateEvictCostItem(string key, string modelId, string value)
        {
            var item = new EvictCostItem
            {
                Key = key,
                ModelId = modelId,
                Value = value,
                Priority = EvictCostItem.PriorityFromCost(value),
                ParentCollection = EvictCosts
            };
            item.Changed += OnEvictCostChanged;
            return item;
        }
    
        private MatrixCombinationItem CreateMatrixCombinationItem(string name)
        {
            var combo = new MatrixCombinationItem { Name = name, ParentCollection = MatrixCombinations };
            combo.Changed += OnMatrixCombinationChanged;
            return combo;
        }
    
        private MatrixModelSelectionItem CreateMatrixModelSelectionItem(string modelId, string alias, bool isSelected)
        {
            var item = new MatrixModelSelectionItem { ModelId = modelId, Alias = alias, IsSelected = isSelected };
            item.Changed += OnMatrixCombinationChanged;
            return item;
        }
    
        private void OnMatrixEntryChanged()
        {
            if (_matrixSyncDepth > 0) return;
            _matrixSyncDepth++;
            try
            {
                SyncMatrixTextFromCollections();
                RebuildMatrixCombinationsFromSets();
                SyncEvictCostsWithCurrentVars(refreshPreview: false);
                UpdateConfigPreviewFromCurrentState();
            }
            finally { _matrixSyncDepth--; }
        }
    
        private void OnMatrixCombinationChanged()
        {
            RefreshConfigPreview();
        }
    
        private List<(ModelEditItem model, string alias)> GetCurrentModelAliases()
        {
            var aliasesByModel = ParseKeyValueLines(MatrixVarsText)
                .Where(kv => !string.IsNullOrWhiteSpace(kv.Value))
                .GroupBy(kv => kv.Value.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Key.Trim(), StringComparer.OrdinalIgnoreCase);
            var used = new HashSet<string>(aliasesByModel.Values, StringComparer.OrdinalIgnoreCase);
            var result = new List<(ModelEditItem model, string alias)>();
            foreach (var model in Models.Where(m => !string.IsNullOrWhiteSpace(m.ModelId)))
            {
                if (!aliasesByModel.TryGetValue(model.ModelId, out var alias) || string.IsNullOrWhiteSpace(alias))
                {
                    alias = MakeAlias(model.ModelId, used);
                    used.Add(alias);
                }
                result.Add((model, alias));
            }
            return result;
        }
    
        private void EnsureMatrixModelCoverage()
        {
            var modelAliases = GetCurrentModelAliases();
            MatrixVarsText = string.Join(Environment.NewLine, modelAliases.Select(x => $"{x.alias} = {x.model.ModelId}"));
    
            foreach (var combo in MatrixCombinations)
            {
                var selectedByModel = combo.Models
                    .Where(m => m.IsSelected)
                    .Select(m => m.ModelId)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var existingByModel = combo.Models
                    .Where(m => !string.IsNullOrWhiteSpace(m.ModelId))
                    .GroupBy(m => m.ModelId, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
    
                combo.Models.Clear();
                foreach (var (model, alias) in modelAliases)
                {
                    var selected = selectedByModel.Contains(model.ModelId);
                    combo.Models.Add(CreateMatrixModelSelectionItem(model.ModelId, alias, selected));
                }
            }
        }
    
        private void RebuildMatrixCombinationsFromSets()
        {
            MatrixCombinations.Clear();
            var aliases = ParseKeyValueLines(MatrixVarsText).ToDictionary(x => x.Key, x => x.Value);
            foreach (var set in ParseKeyValueLines(MatrixSetsText))
            {
                var selectedAliases = set.Value.Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var combo = CreateMatrixCombinationItem(set.Key);
                foreach (var model in Models)
                {
                    var alias = aliases.FirstOrDefault(x => x.Value == model.ModelId).Key;
                    if (string.IsNullOrWhiteSpace(alias)) alias = model.ModelId;
                    combo.Models.Add(CreateMatrixModelSelectionItem(model.ModelId, alias, selectedAliases.Contains(alias) || selectedAliases.Contains(model.ModelId)));
                }
                MatrixCombinations.Add(combo);
            }
        }
    
        private void SyncMatrixTextFromCombinations()
        {
            if (!MatrixCombinations.Any())
            {
                MatrixVarsText = BuildMatrixVarsTextFromModels();
                MatrixSetsText = string.Empty;
                return;
            }
            MatrixVarsText = BuildMatrixVarsTextFromModels();
            var aliasMap = ParseKeyValueLines(MatrixVarsText).ToDictionary(x => x.Value, x => x.Key);
            var setLines = new List<string>();
            foreach (var combo in MatrixCombinations.Where(c => !string.IsNullOrWhiteSpace(c.Name)))
            {
                var selected = combo.Models.Where(m => m.IsSelected).Select(m => aliasMap.TryGetValue(m.ModelId, out var a) ? a : m.ModelId).ToList();
                if (selected.Any()) setLines.Add($"{combo.Name.Trim()} = {string.Join(" & ", selected)}");
            }
            MatrixSetsText = string.Join(Environment.NewLine, setLines);
        }
    
        private string BuildMatrixVarsTextFromModels()
        {
            var aliases = new List<string>();
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var model in Models.Where(m => !string.IsNullOrWhiteSpace(m.ModelId)))
            {
                var alias = MakeAlias(model.ModelId, used);
                used.Add(alias);
                aliases.Add($"{alias} = {model.ModelId}");
            }
            return string.Join(Environment.NewLine, aliases);
        }
    
        private static string MakeAlias(string modelId, HashSet<string> used)
        {
            var chars = new string(modelId.Where(char.IsLetterOrDigit).Take(1).ToArray()).ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(chars)) chars = "m";
            var alias = chars;
            var i = 1;
            while (used.Contains(alias)) alias = $"m{i++}";
            return alias;
        }
    
        private void ExecuteAddMatrixCombination()
        {
            EnsureMatrixModelCoverage();
            var combo = CreateMatrixCombinationItem($"combo_{MatrixCombinations.Count + 1}");
            foreach (var (model, alias) in GetCurrentModelAliases())
            {
                combo.Models.Add(CreateMatrixModelSelectionItem(model.ModelId, alias, false));
            }
            MatrixCombinations.Add(combo);
            RefreshConfigPreview();
        }
    
        private void ExecuteGenerateMatrixVarsFromModels()
        {
            if (!Models.Any())
            {
                StatusText = "No models available to generate vars.";
                StatusColor = "#FAB387";
                return;
            }
    
            var lines = new List<string>();
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var index = 1;
            foreach (var model in Models.Where(m => !string.IsNullOrWhiteSpace(m.ModelId)))
            {
                var baseKey = new string(model.ModelId.Where(char.IsLetterOrDigit).Take(1).ToArray()).ToLowerInvariant();
                if (string.IsNullOrWhiteSpace(baseKey)) baseKey = $"m{index}";
                var key = baseKey;
                while (used.Contains(key)) key = $"m{index++}";
                used.Add(key);
                lines.Add($"{key} = {model.ModelId}");
            }
            MatrixVarsText = string.Join(Environment.NewLine, lines);
            SyncMatrixCollectionsFromText();
            EnsureMatrixModelCoverage();
            SyncEvictCostsWithCurrentVars(refreshPreview: false);
            UpdateConfigPreviewFromCurrentState();
            StatusText = "Vars generated from models.";
            StatusColor = "#A6E3A1";
        }
    
        private void ExecuteCreateAllModelsMatrixSet()
        {
            MatrixCombinations.Clear();
            var combo = CreateMatrixCombinationItem("all");
            foreach (var (model, alias) in GetCurrentModelAliases())
            {
                combo.Models.Add(CreateMatrixModelSelectionItem(model.ModelId, alias, true));
            }
            if (combo.Models.Any())
                MatrixCombinations.Add(combo);
            SyncMatrixTextFromCombinations();
            SyncEvictCostsWithCurrentVars(refreshPreview: false);
            UpdateConfigPreviewFromCurrentState();
            StatusText = combo.Models.Any() ? "Combination 'all' created." : "No models available for Matrix.";
            StatusColor = combo.Models.Any() ? "#A6E3A1" : "#FAB387";
        }
    
        /// <summary>
        /// Best-effort stop then forced application exit. Never hangs the Quit button:
        /// stop is budgeted, and Shutdown always runs.
        /// </summary>
}
