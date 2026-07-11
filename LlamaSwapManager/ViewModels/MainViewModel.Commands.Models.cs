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
    private void CloseModelEditor()
        {
            if (SelectedModel?.IsNew == true)
            {
                ExecuteCancelModel();
                return;
            }
            foreach (var m in Models) m.IsSelected = false;
            SelectedModel = null;
            HasSelectedModel = false;
            IsNewModel = false;
            UpdateSelectedModelSourceLabel();
            (AddModelCommand as RelayCommand)?.NotifyCanExecuteChanged();
        }
    
        /// <summary>
        /// Reorder models in the UI collection; BuildConfigFromUI writes them in this order to config.yml.
        /// </summary>
        public void MoveModel(ModelEditItem? model, int delta)
        {
            if (model is null) return;
            var i = Models.IndexOf(model);
            if (i < 0) return;
            var j = i + delta;
            if (j < 0 || j >= Models.Count) return;
            Models.Move(i, j);
            PersistConfigToDisk(silent: true);
        }
    
        /// <summary>Move <paramref name="sourceId"/> to the index of <paramref name="targetId"/> (insert before target).</summary>
        public void ReorderModel(string? sourceId, string? targetId)
        {
            if (string.IsNullOrWhiteSpace(sourceId) || string.IsNullOrWhiteSpace(targetId)) return;
            if (string.Equals(sourceId, targetId, StringComparison.Ordinal)) return;
    
            var from = -1;
            var to = -1;
            for (var i = 0; i < Models.Count; i++)
            {
                if (string.Equals(Models[i].ModelId, sourceId, StringComparison.Ordinal)) from = i;
                if (string.Equals(Models[i].ModelId, targetId, StringComparison.Ordinal)) to = i;
            }
            if (from < 0 || to < 0 || from == to) return;
    
            Models.Move(from, to);
            PersistConfigToDisk(silent: true);
        }
    
        public void ExecuteSelectModel(ModelEditItem model)
           {
               if (model == null) return;
             
               // If there's an unsaved new model, show warning and don't allow switching
               if (SelectedModel != null && SelectedModel.IsNew)
               {
                   ShowToast("Save or cancel the current model before selecting another one.");
                   return;
               }
             
               foreach (var m in Models) m.IsSelected = false;
               model.IsSelected = true;
               SelectedModel = model;
               HasSelectedModel = true;
               IsNewModel = false;
               ModelEditorSection = "essentials";
               UpdateSelectedModelSourceLabel();
           }
    
        private void ExecuteAddModel()
        {
            var newItem = new ModelEditItem
            {
                ModelId = $"model_{Models.Count + 1}",
                Name = "New Model",
                LlamaServerPath = LlamaServerPath,
                UseJinja = false,
                FitOn = false,
                NoMmap = false,
                // Fail-safe: new models force thinking off so it is not silently on by default.
                Reasoning = "off",
                IsNew = true
            };
            foreach (var m in Models) m.IsSelected = false;
            newItem.IsSelected = true;
            HookModelItem(newItem);
            Models.Add(newItem);
            SelectedModel = newItem;
            HasSelectedModel = true;
            IsNewModel = true;
            ModelEditorSection = "essentials";
            UpdateSelectedModelSourceLabel();
            EnsureMatrixModelCoverage();
            SyncEvictCostsWithCurrentVars(refreshPreview: false);
            UpdateConfigPreviewFromCurrentState();
            (AddModelCommand as RelayCommand)?.NotifyCanExecuteChanged();
        }
    
    
    
        public void ExecuteCloneModel(ModelEditItem source)
        {
            if (source == null) return;
    
            if (SelectedModel?.IsNew == true)
            {
                ShowToast("Save or cancel the current new model before cloning another one.");
                return;
            }
    
            var baseId = string.IsNullOrWhiteSpace(source.ModelId) ? "model" : source.ModelId.Trim();
            var cloneId = GetUniqueModelId($"{baseId}_copy");
            var clone = source.CloneAs(cloneId);
            clone.IsNew = true;
    
            foreach (var m in Models) m.IsSelected = false;
            clone.IsSelected = true;
            HookModelItem(clone);
            Models.Add(clone);
            SelectedModel = clone;
            HasSelectedModel = true;
            IsNewModel = true;
            UpdateSelectedModelSourceLabel();
            EnsureMatrixModelCoverage();
            SyncEvictCostsWithCurrentVars(refreshPreview: false);
            UpdateConfigPreviewFromCurrentState();
            ShowToast($"Cloned {source.ModelId}. Adjust parameters, then click Save.");
            StatusColor = "#A6E3A1";
            (AddModelCommand as RelayCommand)?.NotifyCanExecuteChanged();
        }
    
        private string GetUniqueModelId(string preferred)
        {
            var candidate = preferred;
            var suffix = 2;
            while (Models.Any(m => string.Equals(m.ModelId, candidate, StringComparison.OrdinalIgnoreCase)))
            {
                candidate = $"{preferred}_{suffix}";
                suffix++;
            }
            return candidate;
        }
    
        private void HookModelItem(ModelEditItem model)
        {
            model.PropertyChanged -= OnModelItemPropertyChanged;
            model.PropertyChanged += OnModelItemPropertyChanged;
        }
    
        private void OnModelItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (_matrixSyncDepth > 0) return;
            if (e.PropertyName is nameof(ModelEditItem.IsSelected) or nameof(ModelEditItem.IsNew)) return;
    
            _matrixSyncDepth++;
            try
            {
                if (e.PropertyName == nameof(ModelEditItem.ModelId))
                {
                    EnsureMatrixModelCoverage();
                    SyncEvictCostsWithCurrentVars(refreshPreview: false);
                }
                UpdateConfigPreviewFromCurrentState();
            }
            finally { _matrixSyncDepth--; }
        }
    
        private void ExecuteCancelModel()
        {
            if (SelectedModel == null) return;
    
            if (SelectedModel.IsNew && Models.Contains(SelectedModel))
            {
                Models.Remove(SelectedModel);
                SelectedModel.IsSelected = false;
                SelectedModel = null;
                HasSelectedModel = false;
                IsNewModel = false;
                ShowToast("New model cancelled.");
                StatusColor = "#FAB387";
                UpdateSelectedModelSourceLabel();
                EnsureMatrixModelCoverage();
                SyncEvictCostsWithCurrentVars(refreshPreview: false);
                UpdateConfigPreviewFromCurrentState();
                (AddModelCommand as RelayCommand)?.NotifyCanExecuteChanged();
                return;
            }
    
            ShowToast("Nothing to cancel: this model is already saved.");
            StatusColor = "#FAB387";
        }
    
        private void ExecuteDeleteModel()
        {
            if (SelectedModel == null || !Models.Contains(SelectedModel)) return;
    
            var deletedId = SelectedModel.ModelId;
            SelectedModel.IsSelected = false;
            Models.Remove(SelectedModel);
            SelectedModel = null;
            HasSelectedModel = false;
            IsNewModel = false;
            UpdateSelectedModelSourceLabel();
    
            // Remove deleted model from every existing combination, then rebuild YAML text.
            foreach (var combo in MatrixCombinations)
            {
                var stale = combo.Models
                    .Where(m => string.Equals(m.ModelId, deletedId, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                foreach (var item in stale) combo.Models.Remove(item);
            }
    
            EnsureMatrixModelCoverage();
            SyncMatrixTextFromCombinations();
            SyncEvictCostsWithCurrentVars(refreshPreview: false);
            PersistConfigToDisk($"Model {deletedId} deleted from config.yml.");
            (AddModelCommand as RelayCommand)?.NotifyCanExecuteChanged();
        }
    
        private void ExecuteSaveModel()
        {
            if (SelectedModel == null) return;
    
            if (string.IsNullOrWhiteSpace(SelectedModel.ModelId))
            {
                ShowToast("Error: Model ID is required.");
                return;
            }
    
            var duplicate = Models.Any(m => !ReferenceEquals(m, SelectedModel) && string.Equals(m.ModelId, SelectedModel.ModelId, StringComparison.OrdinalIgnoreCase));
            if (duplicate)
            {
                ShowToast($"Error: a model with ID {SelectedModel.ModelId} already exists.");
                return;
            }
    
            SelectedModel.IsNew = false;
            IsNewModel = false;
            EnsureMatrixModelCoverage();
            SyncEvictCostsWithCurrentVars(refreshPreview: false);
            PersistConfigToDisk("Model saved.");
            (AddModelCommand as RelayCommand)?.NotifyCanExecuteChanged();
            // Close the editor sheet after a successful save.
            CloseModelEditor();
        }
    
        private void UpdateSelectedModelSourceLabel()
        {
            if (SelectedModel == null)
            {
                SelectedModelSourceLabel = "No model selected";
                return;
            }
    
            if (!string.IsNullOrWhiteSpace(SelectedModel.ModelPath))
                SelectedModelSourceLabel = $"Local GGUF: {Path.GetFileName(SelectedModel.ModelPath)}";
            else if (!string.IsNullOrWhiteSpace(SelectedModel.HfModel))
            {
                var q = !string.IsNullOrWhiteSpace(SelectedModel.SelectedQuantization)
                    ? $" [{SelectedModel.SelectedQuantization}]"
                    : "";
                SelectedModelSourceLabel = $"Hugging Face GGUF: {SelectedModel.HfModel}{q}";
            }
            else
                SelectedModelSourceLabel = "Choose a local GGUF file or Hugging Face GGUF repo";
        }
    
        public void SetLocalModelPath(string path)
        {
            if (SelectedModel == null || string.IsNullOrWhiteSpace(path)) return;
            SelectedModel.ModelPath = path;
    
            SelectedModel.HfModel = "";
            IsModelPickerOpen = false;
            UpdateSelectedModelSourceLabel();
            ShowToast("Local model selected.");
        }
    
        private async Task ExecuteSearchHfModelsAsync()
        {
            var query = string.IsNullOrWhiteSpace(HfSearchQuery) ? SelectedModel?.HfModel : HfSearchQuery;
            if (string.IsNullOrWhiteSpace(query))
            {
                StatusText = "Type a query to search GGUF models on Hugging Face.";
                StatusColor = "#FAB387";
                return;
            }
    
            try
            {
                HfSearchResults.Clear();
                StatusText = "Searching GGUF models on Hugging Face...";
                StatusColor = "#89B4FA";
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
                var url = $"https://huggingface.co/api/models?search={Uri.EscapeDataString(query)}&filter=gguf&limit=12&sort=downloads&direction=-1";
                using var stream = await http.GetStreamAsync(url);
                var json = await JsonDocument.ParseAsync(stream);
                foreach (var item in json.RootElement.EnumerateArray())
                {
                    if (item.TryGetProperty("modelId", out var id))
                    {
                        var modelId = id.GetString();
                        if (!string.IsNullOrWhiteSpace(modelId)) HfSearchResults.Add(modelId);
                    }
                }
                StatusText = HfSearchResults.Count == 0 ? "No GGUF models found." : $"{HfSearchResults.Count} GGUF model(s) found.";
                StatusColor = HfSearchResults.Count == 0 ? "#FAB387" : "#A6E3A1";
            }
            catch (Exception ex)
            {
                StatusText = $"HF search error: {ex.Message}";
                StatusColor = "#F38BA8";
            }
        }
    
        public void SetHfModel(string modelId)
        {
            if (SelectedModel == null || string.IsNullOrWhiteSpace(modelId)) return;
            SelectedModel.HfModel = modelId;
            SelectedModel.ModelPath = "";
            SelectedModel.SelectedQuantization = "";
            HfSearchQuery = modelId;
            IsModelPickerOpen = false;
            UpdateSelectedModelSourceLabel();
            ShowToast("Hugging Face model selected.");
        }
    
        /// <summary>
        /// Apply HF model selection. <paramref name="repoFileOrQuant"/> may be a quant tag
        /// (Q4_K_M), a bare filename, or a repo-relative path (subdir/file.gguf).
        /// Not every GGUF embeds a recognizable quant token — in that case we keep the file path.
        /// </summary>
        public void SetHfModelWithQuantization(string modelId, string repoFileOrQuant)
        {
            if (SelectedModel == null || string.IsNullOrWhiteSpace(modelId)) return;
            SelectedModel.HfModel = modelId;
            SelectedModel.ModelPath = "";
    
            var raw = (repoFileOrQuant ?? string.Empty).Trim().Replace('\\', '/');
            // Prefer known quant token from the leaf filename; otherwise keep relative path/filename for -hf repo:file
            var leaf = Path.GetFileName(raw);
            var quantFromName = ExtractQuantizationLabelForModel(leaf);
            if (!string.IsNullOrWhiteSpace(quantFromName))
                SelectedModel.SelectedQuantization = quantFromName;
            else if (raw.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
                SelectedModel.SelectedQuantization = raw; // path or filename without a standard quant tag
            else if (!string.IsNullOrWhiteSpace(raw))
                SelectedModel.SelectedQuantization = Path.GetFileNameWithoutExtension(raw);
            else
                SelectedModel.SelectedQuantization = "";
    
            HfSearchQuery = modelId;
            IsModelPickerOpen = false;
            UpdateSelectedModelSourceLabel();
            ShowToast(string.IsNullOrWhiteSpace(SelectedModel.SelectedQuantization)
                ? "Hugging Face model selected."
                : $"Hugging Face model selected ({SelectedModel.SelectedQuantization}).");
        }
    
        /// <summary>Shared quant extraction used by VM (mirrors UI helper patterns).</summary>
        private static string? ExtractQuantizationLabelForModel(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName)) return null;
            var baseName = Path.GetFileNameWithoutExtension(fileName);
            var quantPatterns = new[]
            {
                "Q8_0","Q8_K","Q6_K","Q5_K_M","Q5_K_S","Q5_0","Q5_1",
                "Q4_K_M","Q4_K_S","Q4_0","Q4_1","Q3_K_M","Q3_K_S","Q3_K_L",
                "Q2_K","Q2_0","IQ4_XS","IQ4_NL","IQ3_XS","IQ3_S","IQ3_M",
                "IQ2_XS","IQ2_S","IQ2_M","IQ1_S","IQ1_M",
                "FP16","FP8_M","BF16","F32","F16","UD-Q4_K_XL","UD-Q5_K_XL","UD-Q6_K_XL","UD-Q8_K_XL"
    
            };
            foreach (var pattern in quantPatterns)
            {
                if (baseName.EndsWith(pattern, StringComparison.OrdinalIgnoreCase) ||
                    baseName.Contains("-" + pattern, StringComparison.OrdinalIgnoreCase) ||
                    baseName.Contains("_" + pattern, StringComparison.OrdinalIgnoreCase))
                    return pattern;
            }
            var match = System.Text.RegularExpressions.Regex.Match(
                baseName,
                @"[-_]((UD-)?[QIq][A-Za-z]*\d[\w_]*|FP\d+[A-Z_]*|BF\d+|F\d+)$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[1].Value : null;
        }
    
        private void ApplyHfModel(string? modelId)
        {
            SetHfModel(modelId ?? "");
        }
}
