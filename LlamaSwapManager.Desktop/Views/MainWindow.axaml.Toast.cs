using System;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Input;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LlamaSwapManager.Desktop;
using LlamaSwapManager.ViewModels;

namespace LlamaSwapManager.Views;

public partial class MainWindow : Window
{
    private void OnDataContextChangedForToast(object? sender, EventArgs e)
        {
            WireToastFromViewModel();
        }
    
        private void WireToastFromViewModel()
        {
            if (DataContext is not MainViewModel vm) return;
            vm.ToastRequested -= OnToastRequested;
            vm.ToastRequested += OnToastRequested;
        }
    
        private void OnToastRequested(string message)
        {
            _ = ShowToastUiAsync(message);
        }
    
        private async Task ShowToastUiAsync(string message)
        {
            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                _toastBorder ??= this.FindControl<Border>("PART_Toast");
                _toastText ??= this.FindControl<TextBlock>("PART_ToastText");
                if (_toastBorder is null || _toastText is null) return;
    
                _toastCts?.Cancel();
                _toastCts = new CancellationTokenSource();
                var ct = _toastCts.Token;
    
                _toastText.Text = message;
                _toastBorder.IsVisible = true;
                try
                {
                    await Task.Delay(2400, ct);
                    if (!ct.IsCancellationRequested)
                        _toastBorder.IsVisible = false;
                }
                catch (TaskCanceledException) { }
            });
        }
    
        private async void OnExtraArgsKeyDown(object? sender, KeyEventArgs e)
        {
            if (sender is not TextBox textBox)
                return;
    
            if (IsCopyGesture(e))
            {
                e.Handled = true;
                await CopyExtraArgsTextAsync(textBox);
            }
            else if (IsPasteGesture(e))
            {
                e.Handled = true;
                await PasteExtraArgsTextAsync(textBox);
            }
        }
    
        private async void OnCopyExtraArgsClick(object? sender, RoutedEventArgs e)
        {
            e.Handled = true;
            await CopyExtraArgsTextAsync(ExtraArgsTextBox);
        }
    
        private async void OnPasteExtraArgsClick(object? sender, RoutedEventArgs e)
        {
            e.Handled = true;
            await PasteExtraArgsTextAsync(ExtraArgsTextBox);
        }
    
        private async System.Threading.Tasks.Task CopyExtraArgsTextAsync(TextBox? textBox)
        {
            try
            {
                if (textBox is null) return;
                var text = !string.IsNullOrEmpty(textBox.SelectedText) ? textBox.SelectedText : textBox.Text;
                LlamaSwapManager.Desktop.CrashLogger.Log("clipboard.copy.begin", $"length={text?.Length ?? 0}; selectedLength={textBox.SelectedText?.Length ?? 0}");
                var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
                if (clipboard is null) return;
                await clipboard.SetValueAsync(DataFormat.Text, text ?? string.Empty);
                LlamaSwapManager.Desktop.CrashLogger.Log("clipboard.copy.end", "success");
                if (DataContext is MainViewModel vm) vm.ReportUiInfo("Extra args copied.");
            }
            catch (Exception ex)
            {
                if (DataContext is MainViewModel vm) vm.ReportUiError($"Copy failed: {ex.Message}");
            }
        }
    
        private async System.Threading.Tasks.Task PasteExtraArgsTextAsync(TextBox? textBox)
        {
            try
            {
                if (textBox is null) return;
                var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
                if (clipboard is null) return;
                LlamaSwapManager.Desktop.CrashLogger.Log("clipboard.paste.begin", "reading clipboard");
                var text = await clipboard.TryGetTextAsync();
                if (text is null) return;
    
                var current = textBox.Text ?? string.Empty;
                var selectionStart = Math.Min(textBox.SelectionStart, current.Length);
                var selectionEnd = Math.Min(textBox.SelectionEnd, current.Length);
                var start = Math.Min(selectionStart, selectionEnd);
                var end = Math.Max(selectionStart, selectionEnd);
    
                var updated = current[..start] + text + current[end..];
                var caret = start + text.Length;
    
                textBox.Text = updated;
                textBox.CaretIndex = caret;
                textBox.SelectionStart = caret;
                textBox.SelectionEnd = caret;
    
                if (DataContext is MainViewModel vm && vm.SelectedModel is not null)
                    vm.SelectedModel.ExtraArgs = updated;
                textBox.Focus();
                LlamaSwapManager.Desktop.CrashLogger.Log("clipboard.paste.end", $"insertedLength={text.Length}; totalLength={updated.Length}");
                if (DataContext is MainViewModel vm2) vm2.ReportUiInfo("Extra args pasted.");
            }
            catch (Exception ex)
            {
                if (DataContext is MainViewModel vm) vm.ReportUiError($"Paste failed: {ex.Message}");
            }
        }
}
