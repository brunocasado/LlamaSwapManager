using System;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using LlamaSwapManager.ViewModels;

namespace LlamaSwapManager.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnWindowKeyDownTunnel, RoutingStrategies.Tunnel, handledEventsToo: true);

        // Intercept window closing to hide instead of exit (Tray behavior)
        Closing += OnWindowClosing;
    }

    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        // Prevent the application from closing; just hide the window.
        // The user can quit via the Tray icon menu.
        e.Cancel = true;
        this.Hide();
    }

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

    private void OnModelItemClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm && sender is Border border && border.DataContext is ModelEditItem model)
        {
            e.Handled = true;

            // Let the currently focused TextBox commit clipboard/selection/text changes
            // before SelectedModel swaps the editor DataContext underneath it.
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    LlamaSwapManager.Desktop.CrashLogger.Log("model.select.begin", model.ModelId ?? "<null>");
                    vm.ExecuteSelectModel(model);
                    LlamaSwapManager.Desktop.CrashLogger.Log("model.select.end", model.ModelId ?? "<null>");
                }
                catch (Exception ex)
                {
                    vm.ReportUiError($"Model selection failed: {ex.Message}");
                }
            }, DispatcherPriority.Background);
        }
    }

    private void OnCloneModelClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;

        if (DataContext is MainViewModel vm && sender is Button button && button.DataContext is ModelEditItem model)
        {
            vm.ExecuteCloneModel(model);
        }
    }

    private void OnCloneModelPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        e.Handled = true;
    }

    private async void OnChooseModelClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm || vm.SelectedModel is null)
            return;

        var dialog = new Window
        {
            Title = "Choose GGUF model",
            Width = 820,
            Height = 640,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = true
        };

        var queryBox = new TextBox { PlaceholderText = "Search Hugging Face GGUF repos, e.g. Qwen3.6 Q4_K_M", Text = vm.HfSearchQuery };
        var results = new StackPanel { Spacing = 6 };
        var status = new TextBlock { Text = "Choose a local .gguf file or search Hugging Face GGUF repositories.", Foreground = Avalonia.Media.Brushes.Gray };

        async System.Threading.Tasks.Task SearchAsync()
        {
            results.Children.Clear();
            var query = queryBox.Text;
            if (string.IsNullOrWhiteSpace(query)) return;
            status.Text = "Searching...";
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                var url = $"https://huggingface.co/api/models?search={Uri.EscapeDataString(query)}&filter=gguf&limit=20&sort=downloads&direction=-1";
                using var stream = await http.GetStreamAsync(url);
                var json = await JsonDocument.ParseAsync(stream);
                var count = 0;
                foreach (var item in json.RootElement.EnumerateArray())
                {
                    if (!item.TryGetProperty("modelId", out var id)) continue;
                    var modelId = id.GetString();
                    if (string.IsNullOrWhiteSpace(modelId)) continue;
                    count++;
                    var button = new Button
                    {
                        Content = modelId,
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        HorizontalContentAlignment = HorizontalAlignment.Left,
                        Padding = new Avalonia.Thickness(12, 8)
                    };
                    button.Click += (_, _) =>
                    {
                        vm.SetHfModel(modelId);
                        dialog.Close();
                    };
                    results.Children.Add(button);
                }
                status.Text = count == 0 ? "No GGUF repositories found." : $"{count} result(s). Click one to select it.";
            }
            catch (Exception ex)
            {
                status.Text = $"Search failed: {ex.Message}";
            }
        }

        var browseButton = new Button { Content = "Browse local .gguf...", Padding = new Avalonia.Thickness(12, 8) };
        browseButton.Click += async (_, _) =>
        {
            var path = await PickGgufPathAsync();
            if (!string.IsNullOrWhiteSpace(path))
            {
                vm.SetLocalModelPath(path);
                dialog.Close();
            }
        };

        var searchButton = new Button { Content = "Search", Padding = new Avalonia.Thickness(12, 8) };
        searchButton.Click += async (_, _) => await SearchAsync();
        queryBox.KeyDown += async (_, ev) =>
        {
            if (ev.Key == Avalonia.Input.Key.Enter)
                await SearchAsync();
        };

        var titleBlock = new TextBlock { Text = "Choose model source", FontSize = 20, FontWeight = Avalonia.Media.FontWeight.Bold };

        var localBorder = new Border
        {
            Margin = new Avalonia.Thickness(0, 16, 0, 12),
            Padding = new Avalonia.Thickness(14),
            Background = Avalonia.Media.Brushes.Transparent,
            BorderThickness = new Avalonia.Thickness(1),
            BorderBrush = Avalonia.Media.Brushes.Gray,
            CornerRadius = new Avalonia.CornerRadius(8),
            Child = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = "Local GGUF file", FontWeight = Avalonia.Media.FontWeight.Bold },
                    browseButton
                }
            }
        };
        Grid.SetRow(localBorder, 1);

        var searchGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 10 };
        searchGrid.Children.Add(queryBox);
        Grid.SetColumn(searchButton, 1);
        searchGrid.Children.Add(searchButton);
        Grid.SetRow(searchGrid, 2);

        var resultsScroller = new ScrollViewer { Margin = new Avalonia.Thickness(0, 12, 0, 12), Content = results };
        Grid.SetRow(resultsScroller, 3);

        var cancelButton = new Button { Content = "Cancel", IsCancel = true, Padding = new Avalonia.Thickness(12, 8) };
        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        footer.Children.Add(status);
        Grid.SetColumn(cancelButton, 1);
        footer.Children.Add(cancelButton);
        Grid.SetRow(footer, 4);

        var rootGrid = new Grid
        {
            Margin = new Avalonia.Thickness(20),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*,Auto")
        };
        rootGrid.Children.Add(titleBlock);
        rootGrid.Children.Add(localBorder);
        rootGrid.Children.Add(searchGrid);
        rootGrid.Children.Add(resultsScroller);
        rootGrid.Children.Add(footer);
        dialog.Content = rootGrid;

        await dialog.ShowDialog(this);
    }

    private async void OnBrowseLocalModelClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
            return;

        var path = await PickGgufPathAsync();
        if (!string.IsNullOrWhiteSpace(path))
            vm.SetLocalModelPath(path);
    }

    private async System.Threading.Tasks.Task<string?> PickGgufPathAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select local GGUF model",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                FilePickerFileTypes.All,
                new FilePickerFileType("GGUF models") { Patterns = new[] { "*.gguf", "*.GGUF" }, MimeTypes = new[] { "application/octet-stream" } }
            }
        });

        var path = files.FirstOrDefault()?.Path.LocalPath;
        return string.IsNullOrWhiteSpace(path) ? null : path;
    }

    private void OnMatrixVisualChanged(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
            vm.SyncMatrixFromVisualBuilder();
    }

    private async void OnDeleteModelClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm || vm.SelectedModel is null)
            return;

        var confirmed = await ConfirmAsync(
            "Delete model",
            $"Are you sure you want to delete model '{vm.SelectedModel.ModelId}'?\n\nThis also saves the removal to config.yml.");

        if (confirmed)
            vm.DeleteModelCommand.Execute(null);
    }

    private async System.Threading.Tasks.Task<bool> ConfirmAsync(string title, string message)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 440,
            Height = 210,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(20),
                Spacing = 18,
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 10,
                        Children =
                        {
                            new Button { Content = "Cancel", MinWidth = 90, IsCancel = true },
                            new Button { Content = "Delete", MinWidth = 90, IsDefault = true, Tag = true }
                        }
                    }
                }
            }
        };

        bool result = false;
        if (dialog.Content is StackPanel root && root.Children[1] is StackPanel buttons)
        {
            foreach (var button in buttons.Children.OfType<Button>())
            {
                button.Click += (_, _) =>
                {
                    result = button.Tag is bool b && b;
                    dialog.Close();
                };
            }
        }

        await dialog.ShowDialog(this);
        return result;
    }
}
