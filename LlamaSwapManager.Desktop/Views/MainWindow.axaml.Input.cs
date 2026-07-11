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
    private static bool IsCopyGesture(KeyEventArgs e)
            => e.Key == Key.C && e.KeyModifiers.HasFlag(KeyModifiers.Control);
    
        private static bool IsPasteGesture(KeyEventArgs e)
            => e.Key == Key.V && e.KeyModifiers.HasFlag(KeyModifiers.Control);
    
        private static bool IsNavigationOrEditingKey(Key key)
            => key is Key.Back or Key.Delete or Key.Left or Key.Right or Key.Tab or Key.Home or Key.End;
    
        private static bool IsDigitKey(Key key)
            => (key >= Key.D0 && key <= Key.D9) || (key >= Key.NumPad0 && key <= Key.NumPad9);
    
        private void OnEvictCostKeyDown(object? sender, KeyEventArgs e)
        {
            if (IsNavigationOrEditingKey(e.Key) || IsCopyGesture(e) || IsPasteGesture(e))
                return;
    
            // Evict cost accepts only non-negative decimal digits. Minus signs,
            // letters, punctuation and spaces are blocked before they enter the TextBox.
            if (!IsDigitKey(e.Key))
                e.Handled = true;
        }
    
        private void OnEvictCostTextInput(object? sender, TextInputEventArgs e)
        {
            if (string.IsNullOrEmpty(e.Text)) return;
            if (e.Text.Any(ch => !char.IsDigit(ch)))
                e.Handled = true;
        }
    
        private void OnEvictCostTextChanged(object? sender, TextChangedEventArgs e)
        {
            if (sender is not TextBox textBox)
                return;
    
            var original = textBox.Text ?? string.Empty;
            var digits = new string(original.Where(char.IsDigit).ToArray());
            if (digits == original)
                return;
    
            var caret = Math.Min(textBox.CaretIndex, digits.Length);
            textBox.Text = digits;
            textBox.CaretIndex = caret;
    
            if (textBox.DataContext is EvictCostItem item)
                item.Value = digits;
        }
    
        private void OnExtraArgsGotFocus(object? sender, RoutedEventArgs e)
        {
            // Keep an explicit focus hook so the Advanced text editor owns Ctrl+C/Ctrl+V
            // on Windows instead of a previously focused ComboBox/TextBlock.
        }
    
        private async void OnWindowKeyDownTunnel(object? sender, KeyEventArgs e)
        {
            // Esc closes model editor (and does not require a focused field).
            if (e.Key == Key.Escape && DataContext is MainViewModel escVm && escVm.HasSelectedModel)
            {
                e.Handled = true;
                if (escVm.CloseModelEditorCommand is ICommand closeCmd && closeCmd.CanExecute(null))
                    closeCmd.Execute(null);
                return;
            }
    
            if (ExtraArgsTextBox is null || !ExtraArgsTextBox.IsKeyboardFocusWithin)
                return;
    
            if (IsCopyGesture(e))
            {
                e.Handled = true;
                await CopyExtraArgsTextAsync(ExtraArgsTextBox);
            }
            else if (IsPasteGesture(e))
            {
                e.Handled = true;
                await PasteExtraArgsTextAsync(ExtraArgsTextBox);
            }
        }
}
