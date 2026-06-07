using System;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using LlamaSwapManager.ViewModels;

namespace LlamaSwapManager.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private void OnModelItemClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm && sender is Border border && border.DataContext is ModelEditItem model)
        {
            vm.ExecuteSelectModel(model);
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
