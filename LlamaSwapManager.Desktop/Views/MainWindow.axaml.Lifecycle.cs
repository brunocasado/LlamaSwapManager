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
        private Point? _reorderPressPoint;
        private ModelEditItem? _reorderModel;
        private Border? _reorderSourceBorder;
        private bool _reorderDragging;
        private Border? _reorderGhostBorder;
        private Point _reorderPressInCard;
    
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
}
