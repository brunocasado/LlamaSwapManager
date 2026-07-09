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
    public MainWindow()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnWindowKeyDownTunnel, RoutingStrategies.Tunnel, handledEventsToo: true);

        // Intercept window closing to hide instead of exit (Tray behavior)
        Closing += OnWindowClosing;
        Loaded += OnLoaded;
        DataContextChanged += OnDataContextChanged;
    }

    private bool _isExiting;
    private Border? _toastBorder;
    private TextBlock? _toastText;
    private CancellationTokenSource? _toastCts;

    private Border? _modelDropHighlight;

    /// <summary>
    /// Tray Quit / Cmd+Q path: allows Closing to complete so the process can shut down.
    /// Window X does NOT call this — it only hides to tray.
    /// </summary>
    public void BeginExit()
    {
        _isExiting = true;
    }

    public bool IsExiting => _isExiting;

    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        // Real app exit (tray Quit / Cmd+Q after BeginExit + Shutdown).
        if (_isExiting)
            return;

        // Title-bar X: hide to tray, keep process alive.
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

    private void OnModelEditorBackdropPressed(object? sender, PointerPressedEventArgs e)
    {
        // Click outside the modal closes the editor (list-first UX).
        if (DataContext is MainViewModel vm)
        {
            e.Handled = true;
            if (vm.CloseModelEditorCommand is ICommand cmd && cmd.CanExecute(null))
                cmd.Execute(null);
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

    private void OnMoveModelUpClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (DataContext is MainViewModel vm && sender is Button button && button.DataContext is ModelEditItem model)
            vm.MoveModel(model, -1);
    }

    private void OnMoveModelDownClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (DataContext is MainViewModel vm && sender is Button button && button.DataContext is ModelEditItem model)
            vm.MoveModel(model, +1);
    }

    private void OnCloneModelPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        e.Handled = true;
    }



    
    // ── Model card drag-reorder (Avalonia 12 DataTransfer API) ───────────

    private async void OnModelCardPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border border || border.DataContext is not ModelEditItem model)
            return;
        // Don't drag when interacting with buttons (Clone).
        if (e.Source is Button || (e.Source as Control)?.FindAncestorOfType<Button>() is not null)
            return;
        if (!e.GetCurrentPoint(border).Properties.IsLeftButtonPressed)
            return;

        var id = model.ModelId ?? string.Empty;
        if (string.IsNullOrWhiteSpace(id))
            return;

        var transfer = new DataTransfer();
        transfer.Add(DataTransferItem.CreateText(id));

        var effect = await DragDrop.DoDragDropAsync(e, transfer, DragDropEffects.Move);
        ClearDropHighlight();

        // No successful drop → treat as click to open editor.
        if (effect == DragDropEffects.None && DataContext is MainViewModel vm)
        {
            Dispatcher.UIThread.Post(() =>
            {
                try { vm.ExecuteSelectModel(model); }
                catch (Exception ex) { vm.ReportUiError($"Model selection failed: {ex.Message}"); }
            }, DispatcherPriority.Background);
        }
    }

    // Kept for XAML wire-up compatibility (drag starts on press via DoDragDropAsync).
    private void OnModelCardPointerMoved(object? sender, PointerEventArgs e) { }
    private void OnModelCardPointerReleased(object? sender, PointerReleasedEventArgs e) { }
    private void OnModelCardPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e) => ClearDropHighlight();

    private void ClearDropHighlight()
    {
        if (_modelDropHighlight is not null)
        {
            _modelDropHighlight.Classes.Remove("model-card-drop-target");
            _modelDropHighlight = null;
        }
    }

    private void OnModelCardDragOver(object? sender, DragEventArgs e)
    {
        var dt = e.DataTransfer;
        var ok = dt is not null && (dt.Contains(DataFormat.Text) || !string.IsNullOrEmpty(dt.TryGetText()));
        e.DragEffects = ok ? DragDropEffects.Move : DragDropEffects.None;
        if (ok && sender is Border border)
        {
            if (!ReferenceEquals(_modelDropHighlight, border))
            {
                ClearDropHighlight();
                border.Classes.Add("model-card-drop-target");
                _modelDropHighlight = border;
            }
        }
        e.Handled = true;
    }

    private void OnModelCardDragLeave(object? sender, DragEventArgs e)
    {
        if (sender is Border border && ReferenceEquals(_modelDropHighlight, border))
            ClearDropHighlight();
    }

    private void OnModelCardDrop(object? sender, DragEventArgs e)
    {
        ClearDropHighlight();
        if (DataContext is not MainViewModel vm)
            return;
        if (sender is not Border targetBorder || targetBorder.DataContext is not ModelEditItem target)
            return;

        var sourceId = e.DataTransfer?.TryGetText();
        if (string.IsNullOrWhiteSpace(sourceId))
            return;

        vm.ReorderModel(sourceId, target.ModelId);
        e.Handled = true;
    }

private async void OnChooseModelClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm || vm.SelectedModel is null)
            return;

        var bg = Brush("#0F0F18");
        var surface = Brush("#161622");
        var border = Brush("#252536");
        var text = Brush("#CDD6F4");
        var muted = Brush("#6C7086");
        var accent = Brush("#89B4FA");

        var dialog = new Window
        {
            Title = "Choose model",
            Width = 860,
            Height = 680,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = true,
            Background = bg
        };

        // --- Header ---
        var title = new TextBlock
        {
            Text = "Choose model source",
            FontSize = 18,
            FontWeight = FontWeight.SemiBold,
            Foreground = text
        };
        var closeX = new Button
        {
            Content = new TextBlock { Text = "✕", FontSize = 16, FontWeight = FontWeight.SemiBold, Foreground = text, HorizontalAlignment = HorizontalAlignment.Center },
            Width = 36,
            Height = 36,
            Padding = new Thickness(0),
            Background = surface,
            BorderBrush = border,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        closeX.Click += (_, __) => dialog.Close();
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 16) };
        DockPanel.SetDock(closeX, Dock.Right);
        header.Children.Add(closeX);
        header.Children.Add(title);

        // --- Local card ---
        var browseButton = new Button
        {
            Content = "📁  Browse local .gguf…",
            Padding = new Thickness(14, 9),
            Background = surface,
            BorderBrush = border,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Foreground = text
        };
        browseButton.Click += async (_, _) =>
        {
            var path = await PickGgufPathAsync();
            if (!string.IsNullOrWhiteSpace(path))
            {
                vm.SetLocalModelPath(path);
                dialog.Close();
            }
        };
        var localCard = new Border
        {
            Background = surface,
            BorderBrush = border,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(16),
            Margin = new Thickness(0, 0, 0, 14),
            Child = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    new TextBlock { Text = "Local GGUF", FontWeight = FontWeight.SemiBold, Foreground = text },
                    new TextBlock { Text = "Pick a file already on disk.", FontSize = 12, Foreground = muted },
                    browseButton
                }
            }
        };

        // --- Search ---
        var queryBox = new TextBox
        {
            PlaceholderText = "Search Hugging Face GGUF repos…",
            Text = vm.HfSearchQuery,
            MinHeight = 36,
            Background = surface,
            Foreground = text,
            BorderBrush = border,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 8)
        };
        var searchButton = new Button
        {
            Content = "🔍  Search",
            Padding = new Thickness(14, 9),
            MinHeight = 36,
            Background = Brush("#1B3A2A"),
            Foreground = Brush("#A6E3A1"),
            BorderBrush = Brush("#2A4F38"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8)
        };
        var searchRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 10 };
        searchRow.Children.Add(queryBox);
        Grid.SetColumn(searchButton, 1);
        searchRow.Children.Add(searchButton);

        var contentScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(0, 12, 0, 12)
        };
        var contentPanel = new StackPanel { Spacing = 8 };
        contentScroll.Content = contentPanel;

        var status = new TextBlock
        {
            Text = "Search a repo, then pick a GGUF file (quant optional).",
            Foreground = muted,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        };
        var cancelButton = new Button
        {
            Content = "Cancel",
            Padding = new Thickness(14, 8),
            IsCancel = true,
            Background = surface,
            Foreground = text,
            BorderBrush = border,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8)
        };
        cancelButton.Click += (_, __) => dialog.Close();

        async Task SearchAsync()
        {
            contentPanel.Children.Clear();
            var query = queryBox.Text;
            if (string.IsNullOrWhiteSpace(query))
            {
                status.Text = "Type a search query first.";
                return;
            }
            status.Text = "Searching Hugging Face…";
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

                    var button = MakeListButton(modelId, text, surface, border);
                    button.Click += async (_, _) =>
                    {
                        contentPanel.Children.Clear();
                        contentPanel.Children.Add(new TextBlock
                        {
                            Text = $"Loading GGUF files in {modelId}…",
                            Foreground = muted,
                            Margin = new Thickness(0, 8)
                        });
                        status.Text = "Listing GGUF files…";
                        try
                        {
                            using var http2 = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
                            // recursive tree so nested GGUFs are included
                            var treeUrl = $"https://huggingface.co/api/models/{modelId}/tree/main?recursive=1";
                            var response = await http2.GetAsync(treeUrl);
                            if (!response.IsSuccessStatusCode)
                            {
                                // fallback non-recursive
                                treeUrl = $"https://huggingface.co/api/models/{modelId}/tree/main";
                                response = await http2.GetAsync(treeUrl);
                            }
                            if (!response.IsSuccessStatusCode)
                            {
                                status.Text = $"Failed to list files: {response.StatusCode}";
                                contentPanel.Children.Clear();
                                contentPanel.Children.Add(new TextBlock { Text = status.Text, Foreground = Brush("#F38BA8") });
                                return;
                            }

                            using var stream2 = await response.Content.ReadAsStreamAsync();
                            var treeJson = await JsonDocument.ParseAsync(stream2);
                            var ggufFiles = new List<(string display, string repoPath, string? quant)>();

                            foreach (var file in treeJson.RootElement.EnumerateArray())
                            {
                                if (!file.TryGetProperty("path", out var pathProp)) continue;
                                var path = pathProp.GetString();
                                if (string.IsNullOrWhiteSpace(path)) continue;
                                if (!path.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)) continue;

                                var fileName = Path.GetFileName(path);
                                var quantLabel = ExtractQuantizationLabel(fileName);
                                // Always include the file — quant is optional metadata
                                var display = quantLabel is not null
                                    ? $"{quantLabel}  ·  {path}"
                                    : path;
                                ggufFiles.Add((display, path, quantLabel));
                            }

                            contentPanel.Children.Clear();
                            if (ggufFiles.Count == 0)
                            {
                                contentPanel.Children.Add(new TextBlock
                                {
                                    Text = "No .gguf files found in this repository.",
                                    Foreground = muted
                                });
                                status.Text = "No GGUF files found.";
                                return;
                            }

                            // Sort: known quants first (quality order), then alphabetical by path
                            ggufFiles.Sort((a, b) =>
                            {
                                var cq = CompareQuantization(a.quant ?? "", b.quant ?? "");
                                if (a.quant is null && b.quant is not null) return 1;
                                if (a.quant is not null && b.quant is null) return -1;
                                if (a.quant is not null && b.quant is not null)
                                {
                                    var c = CompareQuantization(a.quant, b.quant);
                                    if (c != 0) return c;
                                }
                                return string.Compare(a.repoPath, b.repoPath, StringComparison.OrdinalIgnoreCase);
                            });

                            contentPanel.Children.Add(new TextBlock
                            {
                                Text = modelId,
                                FontWeight = FontWeight.SemiBold,
                                Foreground = text,
                                FontSize = 14,
                                Margin = new Thickness(0, 0, 0, 8)
                            });
                            contentPanel.Children.Add(new TextBlock
                            {
                                Text = $"{ggufFiles.Count} GGUF file(s) — pick one (quant tag used when present).",
                                Foreground = muted,
                                FontSize = 12,
                                Margin = new Thickness(0, 0, 0, 8)
                            });

                            foreach (var (display, repoPath, quant) in ggufFiles)
                            {
                                var qBtn = MakeListButton(display, text, surface, border);
                                qBtn.Click += (_, __) =>
                                {
                                    // Prefer quant tag for -hf repo:Q4_K_M; else store repo-relative path
                                    var token = !string.IsNullOrWhiteSpace(quant) ? quant! : repoPath;
                                    vm.SetHfModelWithQuantization(modelId, token);
                                    dialog.Close();
                                };
                                contentPanel.Children.Add(qBtn);
                            }
                            status.Text = "Select a GGUF file.";
                        }
                        catch (Exception ex)
                        {
                            status.Text = $"Failed to list files: {ex.Message}";
                            contentPanel.Children.Clear();
                            contentPanel.Children.Add(new TextBlock { Text = status.Text, Foreground = Brush("#F38BA8") });
                        }
                    };
                    contentPanel.Children.Add(button);
                }

                status.Text = count == 0
                    ? "No GGUF repositories found."
                    : $"{count} repo(s). Click one to list GGUF files.";
                if (count == 0)
                    contentPanel.Children.Add(new TextBlock { Text = "No GGUF repositories found.", Foreground = muted });
            }
            catch (Exception ex)
            {
                status.Text = $"Search failed: {ex.Message}";
                contentPanel.Children.Add(new TextBlock { Text = status.Text, Foreground = Brush("#F38BA8") });
            }
        }

        searchButton.Click += async (_, _) => await SearchAsync();
        queryBox.KeyDown += async (_, ev) =>
        {
            if (ev.Key == Key.Enter)
            {
                ev.Handled = true;
                await SearchAsync();
            }
        };

        dialog.KeyDown += (_, ev) =>
        {
            if (ev.Key == Key.Escape)
            {
                ev.Handled = true;
                dialog.Close();
            }
        };

        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 8, 0, 0) };
        footer.Children.Add(status);
        Grid.SetColumn(cancelButton, 1);
        footer.Children.Add(cancelButton);

        var root = new Grid
        {
            Margin = new Thickness(22),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*,Auto")
        };
        root.Children.Add(header);
        Grid.SetRow(localCard, 1);
        root.Children.Add(localCard);
        Grid.SetRow(searchRow, 2);
        root.Children.Add(searchRow);
        Grid.SetRow(contentScroll, 3);
        root.Children.Add(contentScroll);
        Grid.SetRow(footer, 4);
        root.Children.Add(footer);

        dialog.Content = root;
        await dialog.ShowDialog(this);
    }

    private static IBrush Brush(string hex) =>
        SolidColorBrush.Parse(hex);

    private static Button MakeListButton(string content, IBrush foreground, IBrush background, IBrush borderBrush) =>
        new Button
        {
            Content = content,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(12, 10),
            Margin = new Thickness(0, 0, 0, 4),
            Background = background,
            Foreground = foreground,
            BorderBrush = borderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8)
        };


    /// <summary>
    /// Extract a human-readable quantization label from a GGUF filename.
    /// E.g. "Qwen2.5-7B-Instruct-Q4_K_M.gguf" → "Q4_K_M"
    /// </summary>
    private static string? ExtractQuantizationLabel(string fileName)
    {
        var baseName = System.IO.Path.GetFileNameWithoutExtension(fileName);
        // Common quantization patterns: Q4_K_M, Q5_K_M, Q8_0, IQ4_XS, FP16, BF16, F32, etc.
        var quantPatterns = new[]
        {
            @"Q8_0", @"Q8_K", @"Q6_K", @"Q5_K_M", @"Q5_K_S", @"Q5_0", @"Q5_1",
            @"Q4_K_M", @"Q4_K_S", @"Q4_0", @"Q4_1",
            @"Q3_K_M", @"Q3_K_S", @"Q3_K_L",
            @"Q2_K", @"Q2_0",
            @"IQ4_XS", @"IQ4_NL", @"IQ3_XS", @"IQ3_S", @"IQ3_M", @"IQ3_2", @"IQ3_1",
            @"IQ2_XS", @"IQ2_S", @"IQ2_M", @"IQ2_2", @"IQ2_1",
            @"IQ1_S", @"IQ1_M",
            @"FP16", @"FP8_M", @"FP8_E4M3", @"FP8_E5M2",
            @"BF16", @"F32", @"F16"
        };

        // Check for known patterns first (exact match)
        foreach (var pattern in quantPatterns)
        {
            if (baseName.EndsWith(pattern, StringComparison.OrdinalIgnoreCase) || 
                baseName.Contains($"-{pattern}", StringComparison.OrdinalIgnoreCase) ||
                baseName.Contains($"_{pattern}", StringComparison.OrdinalIgnoreCase))
            {
                return pattern;
            }
        }

        // Fallback: try to extract quantization from the end of the filename
        // Pattern: looks for quantization at the end after last - or _
        var match = System.Text.RegularExpressions.Regex.Match(baseName, @"[-_]((UD-|IQ-)?[QIq][A-Za-z]*\d[\w_]*|FP\d+[A-Z_]*|BF\d+|F\d+[A-Z]*)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (match.Success)
        {
            return match.Groups[1].Value;
        }

        // Last resort: return the part after the last - or _ if it looks like a quantization
        var lastPart = baseName.Split(new[] { '-', '_' }).LastOrDefault();
        if (!string.IsNullOrWhiteSpace(lastPart) && 
            (lastPart.Length >= 2 && 
             (lastPart.StartsWith("Q", StringComparison.OrdinalIgnoreCase) ||
              lastPart.StartsWith("IQ", StringComparison.OrdinalIgnoreCase) ||
              lastPart.StartsWith("FP", StringComparison.OrdinalIgnoreCase) ||
              lastPart.StartsWith("BF", StringComparison.OrdinalIgnoreCase) ||
              lastPart.StartsWith("UD", StringComparison.OrdinalIgnoreCase))))
        {
            return lastPart;
        }

        return null;
    }

    /// <summary>
    /// Sort quantizations by preference (higher quality first).
    /// </summary>
    private static int CompareQuantization(string a, string b)
    {
        var order = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            { "FP8_M", 1 }, { "FP8_E4M3", 1 }, { "FP8_E5M2", 1 },
            { "FP16", 2 }, { "BF16", 2 }, { "F16", 2 }, { "F32", 3 },
            { "Q8_0", 4 }, { "Q8_K", 4 },
            { "Q6_K", 5 },
            { "Q5_K_M", 6 }, { "Q5_K_S", 6 }, { "Q5_0", 6 }, { "Q5_1", 6 },
            { "Q4_K_M", 7 }, { "Q4_K_S", 7 }, { "Q4_0", 7 }, { "Q4_1", 7 },
            { "Q3_K_M", 8 }, { "Q3_K_S", 8 }, { "Q3_K_L", 8 },
            { "Q2_K", 9 }, { "Q2_0", 9 },
            { "IQ4_XS", 10 }, { "IQ4_NL", 10 },
            { "IQ3_XS", 11 }, { "IQ3_S", 11 }, { "IQ3_M", 11 },
            { "IQ2_XS", 12 }, { "IQ2_S", 12 },
            { "IQ1_S", 13 }, { "IQ1_M", 13 }
        };

        var aScore = order.TryGetValue(a, out var va) ? va : 50;
        var bScore = order.TryGetValue(b, out var vb) ? vb : 50;
        return aScore.CompareTo(bScore);
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

   // --- Smart stick-to-bottom for upstream log viewer ---
    private bool _upstreamStickToBottom = true;
    private bool _isProgrammaticUpstreamScroll;
    private MainViewModel? _subscribedVm;
    private const double BottomSnapThreshold = 24.0;

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (UpstreamLogScrollViewer != null)
        {
            UpstreamLogScrollViewer.ScrollChanged += OnUpstreamScrollChanged;
            // Layout grows when log text expands — keep sticky bottom consistent.
            UpstreamLogScrollViewer.LayoutUpdated += OnUpstreamLogLayoutUpdated;
        }

        SubscribeToViewModel(DataContext as MainViewModel);
        UpdateScrollToBottomButtonVisibility();
        if (_upstreamStickToBottom)
            ScrollUpstreamToBottom();
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        WireToastFromViewModel();
        SubscribeToViewModel(DataContext as MainViewModel);
    }

    private void SubscribeToViewModel(MainViewModel? vm)
    {
        if (ReferenceEquals(_subscribedVm, vm))
            return;

        if (_subscribedVm != null)
            _subscribedVm.PropertyChanged -= OnMainViewModelPropertyChanged;

        _subscribedVm = vm;
        if (_subscribedVm != null)
            _subscribedVm.PropertyChanged += OnMainViewModelPropertyChanged;
    }

    private void OnMainViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not nameof(MainViewModel.UpstreamLogText))
            return;

        // New batch of log text arrived. If pinned to bottom, follow; always refresh button.
        Dispatcher.UIThread.Post(() =>
        {
            if (_upstreamStickToBottom)
                ScrollUpstreamToBottom();
            UpdateScrollToBottomButtonVisibility();
        }, DispatcherPriority.Background);
    }

    private void OnUpstreamLogLayoutUpdated(object? sender, EventArgs e)
    {
        if (_upstreamStickToBottom && !_isProgrammaticUpstreamScroll)
            ScrollUpstreamToBottom();
    }

    private void OnUpstreamScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (sender is not ScrollViewer scroll)
            return;

        // Ignore our own scroll-to-bottom adjustments so they don't thrash the sticky flag.
        if (_isProgrammaticUpstreamScroll)
        {
            UpdateScrollToBottomButtonVisibility();
            return;
        }

        // Stick only when the viewport is (near) the bottom.
        _upstreamStickToBottom = IsUpstreamAtBottom(scroll);
        UpdateScrollToBottomButtonVisibility();

        if (_upstreamStickToBottom)
            ScrollUpstreamToBottom();
    }

    private static bool IsUpstreamAtBottom(ScrollViewer scroll)
    {
        var maxOffset = Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height);
        // No overflow → treat as "at bottom" (anchor engaged).
        if (maxOffset <= 0.5)
            return true;

        return scroll.Offset.Y >= maxOffset - BottomSnapThreshold;
    }

    private void ScrollUpstreamToBottom()
    {
        if (UpstreamLogScrollViewer is null)
            return;

        _isProgrammaticUpstreamScroll = true;
        try
        {
            var scroll = UpstreamLogScrollViewer;
            var maxOffset = Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height);
            scroll.Offset = new Vector(scroll.Offset.X, maxOffset);

            if (UpstreamLogTextBox is not null && UpstreamLogTextBox.Text is { Length: > 0 } text)
                UpstreamLogTextBox.CaretIndex = text.Length;

            _upstreamStickToBottom = true;
        }
        finally
        {
            // Defer clearing so cascading ScrollChanged from Offset assign is still treated as programmatic.
            Dispatcher.UIThread.Post(() =>
            {
                _isProgrammaticUpstreamScroll = false;
                UpdateScrollToBottomButtonVisibility();
            }, DispatcherPriority.Background);
        }
    }

    private void UpdateScrollToBottomButtonVisibility()
    {
        if (ScrollToBottomButton is null || UpstreamLogScrollViewer is null)
            return;

        var scroll = UpstreamLogScrollViewer;
        var hasOverflow = scroll.Extent.Height > scroll.Viewport.Height + 1;
        ScrollToBottomButton.IsVisible = hasOverflow && !_upstreamStickToBottom;
    }

    private void OnScrollToBottomClick(object? sender, RoutedEventArgs e)
    {
        _upstreamStickToBottom = true;
        ScrollUpstreamToBottom();
    }
}
