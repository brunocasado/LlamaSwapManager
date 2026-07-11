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
    private void OnLoaded(object? sender, RoutedEventArgs e)
        {
            if (UpstreamLogScrollViewer != null)
            {
                UpstreamLogScrollViewer.ScrollChanged += OnUpstreamScrollChanged;
            }
    
            SubscribeToViewModel(DataContext as MainViewModel);
            UpdateScrollToBottomButtonVisibility();
            if (_upstreamStickToBottom)
                ScrollUpstreamToBottom(force: true);
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
    
            // New batch of log text arrived. Layout may not have measured yet — schedule
            // stick-to-bottom after layout so Extent reflects the new text height.
            if (_upstreamStickToBottom)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (_upstreamStickToBottom)
                        ScrollUpstreamToBottom(force: true);
                    UpdateScrollToBottomButtonVisibility();
                }, DispatcherPriority.Loaded);
            }
            else
            {
                Dispatcher.UIThread.Post(UpdateScrollToBottomButtonVisibility, DispatcherPriority.Background);
            }
        }
    
        private void OnUpstreamScrollChanged(object? sender, ScrollChangedEventArgs e)
        {
            if (sender is not ScrollViewer scroll)
                return;
    
            // Ignore cascades from our own Offset / ScrollToEnd assignments.
            if (_isProgrammaticUpstreamScroll)
            {
                UpdateScrollToBottomButtonVisibility();
                return;
            }
    
            var extentGrew = e.ExtentDelta.Y > 0.5;
            var userScrolled = Math.Abs(e.OffsetDelta.Y) > 0.5
                               || Math.Abs(e.OffsetDelta.X) > 0.5;
    
            // Content grew (new log lines). If still sticky, stay glued to the bottom.
            // Do NOT re-evaluate stick flag from Offset here — after Extent grows the old
            // Offset is still at the previous max, which would look like "scrolled up".
            if (extentGrew && _upstreamStickToBottom)
            {
                ScrollUpstreamToBottom(force: true);
                return;
            }
    
            // Only user-driven offset changes should toggle stick-to-bottom.
            if (userScrolled)
            {
                _upstreamStickToBottom = IsUpstreamAtBottom(scroll);
                UpdateScrollToBottomButtonVisibility();
    
                // Snap fully to bottom when user is within the threshold (avoids half-stuck state).
                if (_upstreamStickToBottom)
                    ScrollUpstreamToBottom(force: true);
                return;
            }
    
            // Viewport resize (window drag) while sticky: re-anchor.
            if (_upstreamStickToBottom && Math.Abs(e.ViewportDelta.Y) > 0.5)
                ScrollUpstreamToBottom(force: true);
    
            UpdateScrollToBottomButtonVisibility();
        }
    
        private static bool IsUpstreamAtBottom(ScrollViewer scroll)
        {
            var maxOffset = Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height);
            // No overflow → treat as "at bottom" (anchor engaged).
            if (maxOffset <= 0.5)
                return true;
    
            return scroll.Offset.Y >= maxOffset - BottomSnapThreshold;
        }
    
        private void ScrollUpstreamToBottom(bool force = false)
        {
            if (UpstreamLogScrollViewer is null)
                return;
    
            if (!force && !_upstreamStickToBottom)
                return;
    
            var generation = ++_programmaticScrollGeneration;
            _isProgrammaticUpstreamScroll = true;
            try
            {
                var scroll = UpstreamLogScrollViewer;
    
                // Prefer ScrollToEnd when available — more reliable than manual Offset math
                // across Avalonia versions / deferred layout passes.
                scroll.ScrollToEnd();
    
                var maxOffset = Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height);
                if (Math.Abs(scroll.Offset.Y - maxOffset) > 0.5)
                    scroll.Offset = new Vector(scroll.Offset.X, maxOffset);
    
                if (UpstreamLogTextBox is not null && UpstreamLogTextBox.Text is { Length: > 0 } text)
                    UpstreamLogTextBox.CaretIndex = text.Length;
    
                _upstreamStickToBottom = true;
            }
            finally
            {
                // Keep the programmatic guard long enough to swallow the ScrollChanged cascade
                // from Offset/ScrollToEnd, then release only if no newer scroll was started.
                Dispatcher.UIThread.Post(() =>
                {
                    if (generation == _programmaticScrollGeneration)
                    {
                        _isProgrammaticUpstreamScroll = false;
                        UpdateScrollToBottomButtonVisibility();
                    }
                }, DispatcherPriority.Render);
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
            ScrollUpstreamToBottom(force: true);
        }
}
