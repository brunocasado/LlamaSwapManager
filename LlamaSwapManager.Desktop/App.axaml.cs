using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using CommunityToolkit.Mvvm.Input;
using LlamaSwapManager.ViewModels;
using LlamaSwapManager.Views;

namespace LlamaSwapManager;

public partial class App : Application
{
    private TrayIcon? _trayIcon;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var vm = new MainViewModel();
            var mainWindow = new MainWindow { DataContext = vm };
            desktop.MainWindow = mainWindow;

            SetupTrayIcon(mainWindow, vm);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void SetupTrayIcon(Window mainWindow, MainViewModel vm)
    {
        try
        {
            var uri = new Uri("avares://LlamaSwapManager.Desktop/Assets/llama.png");
            using var stream = AssetLoader.Open(uri);
            var icon = new WindowIcon(stream);

            _trayIcon = new TrayIcon
            {
                Icon = icon,
                ToolTipText = "LlamaSwapManager",
                Menu = new NativeMenu
                {
                    Items =
                    {
                        new NativeMenuItem("Show")
                        {
                            Command = new CommunityToolkit.Mvvm.Input.AsyncRelayCommand(() =>
                            {
                                mainWindow.Show();
                                mainWindow.WindowState = WindowState.Normal;
                                mainWindow.Activate();
                                return System.Threading.Tasks.Task.CompletedTask;
                            })
                        },
                        new NativeMenuItemSeparator(),
                        new NativeMenuItem("Quit")
                        {
                            Command = new CommunityToolkit.Mvvm.Input.AsyncRelayCommand(async () =>
                            {
                                await vm.QuitApplicationAsync();
                            })
                        }
                    }
                }
            };
        }
        catch
        {
            // Fallback se o ícone não carregar
        }
    }
}
