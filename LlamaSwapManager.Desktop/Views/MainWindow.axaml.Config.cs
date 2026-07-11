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
        private int _programmaticScrollGeneration;
        private MainViewModel? _subscribedVm;
        private const double BottomSnapThreshold = 40.0;
}
