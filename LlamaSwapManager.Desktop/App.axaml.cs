using System;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using CommunityToolkit.Mvvm.Input;
using LlamaSwapManager.Services;
using LlamaSwapManager.ViewModels;
using LlamaSwapManager.Views;

namespace LlamaSwapManager;

public partial class App : Application
{
    private TrayIcon? _trayIcon;
    private NativeMenuItem? _startMenuItem;
    private NativeMenuItem? _stopMenuItem;

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

            // Keep menu items synced with process state
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(MainViewModel.Status))
                    UpdateTrayMenuState(vm);
            };
            UpdateTrayMenuState(vm);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void SetupTrayIcon(Window mainWindow, MainViewModel vm)
     {
         try
         {
             // macOS tray icons require template (monochrome + alpha) images.
             // Use the grayscale version for the tray; the color version stays
             // as the window icon in MainWindow.axaml.
             var uri = new Uri("avares://LlamaSwapManager.Desktop/Assets/llama_tray_gray_44.png");
             using var stream = AssetLoader.Open(uri);
             var icon = new WindowIcon(stream);

             // ---- Tray Menu Items ----
             var showItem = new NativeMenuItem("Show")
            {
                Command = new AsyncRelayCommand(() =>
                {
                    ShowWindow(mainWindow);
                    return System.Threading.Tasks.Task.CompletedTask;
                })
            };

            _startMenuItem = new NativeMenuItem("Start")
            {
                Command = new AsyncRelayCommand(async () =>
                {
                    if (vm.StartCommand is AsyncRelayCommand cmd)
                        await cmd.ExecuteAsync(null);
                    ShowWindow(mainWindow);
                })
            };

            _stopMenuItem = new NativeMenuItem("Stop")
            {
                Command = new AsyncRelayCommand(async () =>
                {
                    if (vm.StopCommand is AsyncRelayCommand cmd)
                        await cmd.ExecuteAsync(null);
                })
            };

            var restartItem = new NativeMenuItem("Restart")
            {
                Command = new AsyncRelayCommand(async () =>
                {
                    if (vm.RestartCommand is AsyncRelayCommand cmd)
                        await cmd.ExecuteAsync(null);
                    ShowWindow(mainWindow);
                })
            };

            var quitItem = new NativeMenuItem("Quit")
            {
                Command = new AsyncRelayCommand(async () =>
                {
                    await vm.QuitApplicationAsync();
                })
            };

            _trayIcon = new TrayIcon
            {
                Icon = icon,
                ToolTipText = "LlamaSwapManager",
                Menu = new NativeMenu
                {
                    Items =
                    {
                        showItem,
                        new NativeMenuItemSeparator(),
                        _startMenuItem,
                        _stopMenuItem,
                        restartItem,
                        new NativeMenuItemSeparator(),
                        quitItem
                    }
                }
            };

            // Double-click / click on tray icon opens the window
            _trayIcon.Clicked += (_, _) =>
            {
                ShowWindow(mainWindow);
            };
        }
        catch
        {
            // Fallback se o ícone não carregar
        }
    }

    private static void ShowWindow(Window mainWindow)
    {
        mainWindow.Show();
        mainWindow.WindowState = WindowState.Normal;
        mainWindow.Topmost = true;
        mainWindow.Activate();
        mainWindow.Topmost = false;
    }

    private void UpdateTrayMenuState(MainViewModel vm)
    {
        if (_startMenuItem == null || _stopMenuItem == null) return;

        var isRunning = vm.Status == LlamaSwapStatus.Running || vm.Status == LlamaSwapStatus.Starting;
        var isStopped = vm.Status == LlamaSwapStatus.Stopped || vm.Status == LlamaSwapStatus.Error;

        _startMenuItem.IsEnabled = isStopped;
         _stopMenuItem.IsEnabled = isRunning;
        }
        }
