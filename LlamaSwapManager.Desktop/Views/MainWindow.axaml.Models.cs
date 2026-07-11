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
    
    
    
        
        
        // ── Model card reorder (custom pointer drag; no OS DnD — macOS crash safe) ─
    
        private void OnModelCardPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (sender is not Border border || border.DataContext is not ModelEditItem model)
                return;
            if (e.Source is Button || (e.Source as Control)?.FindAncestorOfType<Button>() is not null)
                return;
            if (!e.GetCurrentPoint(border).Properties.IsLeftButtonPressed)
                return;
    
            _reorderPressPoint = e.GetPosition(this);
            _reorderPressInCard = e.GetPosition(border);
            _reorderModel = model;
            _reorderSourceBorder = border;
            _reorderDragging = false;
            e.Pointer.Capture(border);
            e.Handled = true;
        }
    
        private void OnModelCardPointerMoved(object? sender, PointerEventArgs e)
        {
            if (_reorderPressPoint is null || _reorderModel is null || _reorderSourceBorder is null)
                return;
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                return;
    
            var pos = e.GetPosition(this);
            var dx = pos.X - _reorderPressPoint.Value.X;
            var dy = pos.Y - _reorderPressPoint.Value.Y;
    
            if (!_reorderDragging)
            {
                if ((dx * dx + dy * dy) < 64)
                    return;
                BeginReorderGhost(_reorderSourceBorder, _reorderModel);
                _reorderDragging = true;
            }
    
            MoveReorderGhost(pos);
            UpdateDropHighlightAt(pos);
            e.Handled = true;
        }
    
        private void OnModelCardPointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            try
            {
                if (_reorderDragging && _reorderModel is not null && DataContext is MainViewModel vm)
                {
                    var target = HitTestModelCard(e.GetPosition(this));
                    if (target?.DataContext is ModelEditItem targetModel)
                        vm.ReorderModel(_reorderModel.ModelId, targetModel.ModelId);
                }
                else if (!_reorderDragging && _reorderModel is not null && DataContext is MainViewModel vm2)
                {
                    var model = _reorderModel;
                    Dispatcher.UIThread.Post(() =>
                    {
                        try { vm2.ExecuteSelectModel(model); }
                        catch (Exception ex) { vm2.ReportUiError($"Model selection failed: {ex.Message}"); }
                    }, DispatcherPriority.Background);
                }
            }
            finally
            {
                EndReorderSession(e.Pointer);
                e.Handled = true;
            }
        }
    
        private void OnModelCardPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
            => EndReorderSession(null);
    
        private void BeginReorderGhost(Border source, ModelEditItem model)
        {
            source.Opacity = 0.3;
    
            var layer = this.FindControl<Canvas>("PART_DragGhostLayer");
            if (layer is null)
                return;
    
            var w = source.Bounds.Width > 0 ? source.Bounds.Width : 300;
            var h = source.Bounds.Height > 0 ? source.Bounds.Height : 80;
    
            _reorderGhostBorder = new Border
            {
                Width = w,
                Height = h,
                CornerRadius = new CornerRadius(12),
                Background = new SolidColorBrush(Color.Parse("#DD1E1E2E")),
                BorderBrush = Brush("#89B4FA"),
                BorderThickness = new Thickness(2),
                Padding = new Thickness(12, 10),
                IsHitTestVisible = false,
                Child = new StackPanel
                {
                    Spacing = 4,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = model.ModelId ?? "model",
                            FontSize = 13,
                            FontWeight = FontWeight.Bold,
                            Foreground = Brush("#CDD6F4")
                        },
                        new TextBlock
                        {
                            Text = string.IsNullOrWhiteSpace(model.Name) ? " " : model.Name!,
                            FontSize = 11,
                            Foreground = Brush("#A6ADC8"),
                            TextTrimming = TextTrimming.CharacterEllipsis
                        }
                    }
                }
            };
    
            layer.Children.Clear();
            layer.Children.Add(_reorderGhostBorder);
            layer.IsVisible = true;
        }
    
        private void MoveReorderGhost(Point windowPos)
        {
            if (_reorderGhostBorder is null) return;
            Canvas.SetLeft(_reorderGhostBorder, windowPos.X - _reorderPressInCard.X);
            Canvas.SetTop(_reorderGhostBorder, windowPos.Y - _reorderPressInCard.Y);
        }
    
        private void UpdateDropHighlightAt(Point windowPos)
        {
            var target = HitTestModelCard(windowPos);
            if (ReferenceEquals(target, _reorderSourceBorder))
                target = null;
    
            if (!ReferenceEquals(_modelDropHighlight, target))
            {
                ClearDropHighlight();
                if (target is not null)
                {
                    target.Classes.Add("model-card-drop-target");
                    _modelDropHighlight = target;
                }
            }
        }
    
        private Border? HitTestModelCard(Point windowPos)
        {
            if (this.InputHitTest(windowPos) is not Visual hit)
                return null;
    
            Control? c = hit as Control ?? hit.FindAncestorOfType<Control>();
            while (c is not null)
            {
                if (c is Border b && b.Classes.Contains("model-card") && b.DataContext is ModelEditItem)
                    return b;
                c = c.GetVisualParent() as Control;
            }
            return null;
        }
    
        private void EndReorderSession(IPointer? pointer)
        {
            if (_reorderSourceBorder is not null)
            {
                _reorderSourceBorder.Opacity = 1;
                _reorderSourceBorder = null;
            }
    
            var layer = this.FindControl<Canvas>("PART_DragGhostLayer");
            if (layer is not null)
            {
                layer.Children.Clear();
                layer.IsVisible = false;
            }
            _reorderGhostBorder = null;
            ClearDropHighlight();
            _reorderPressPoint = null;
            _reorderModel = null;
            _reorderDragging = false;
            pointer?.Capture(null);
        }
    
        private void ClearDropHighlight()
        {
            if (_modelDropHighlight is not null)
            {
                _modelDropHighlight.Classes.Remove("model-card-drop-target");
                _modelDropHighlight = null;
            }
        }
}
