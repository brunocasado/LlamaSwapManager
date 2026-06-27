# Metrics Charts & Loaded Models Implementation Plan

> **For Hermes:** Implement this plan task-by-task.

**Goal:** Add real-time charts (tokens/sec, prefill vs decode) and loaded models display to the Metrics tab.

**Architecture:** Use OxyPlot (lightweight, Avalonia-native) for charts. Store rolling time-series data in MainViewModel. Query llama-swap `/running` endpoint for loaded models.

**Tech Stack:** OxyPlot.Avalonia (NuGet), CommunityToolkit.Mvvm (existing), Avalonia 12.0.4 (existing)

**Data Sources:**
- **Tokens/sec:** Extracted from upstream logs (`tg = X.XX t/s`) — already implemented, updates ~100ms
- **Prefill/Decode tokens:** `prompt_tokens_total` / `tokens_predicted_total` from `/metrics` — updates every 2s
- **Active slots:** `requests_processing` from `/metrics` — updates every 2s
- **Loaded models:** `GET http://<swap-host>:<port>/running` returns JSON array of loaded models

---

## Current UI Layout (Metrics Tab)

The Metrics tab (`MainWindow.axaml` Grid.Row 1) has 4 stat cards in a row:
- Prefill Tokens (blue #89B4FA)
- Decode Tokens (green #A6E3A1)
- Tokens/sec (orange #FAB387)
- Active Slots (pink #F5C2E7)

Below is the upstream log panel. Charts will go between stat cards and logs.

---

### Task 1: Add OxyPlot.Avalonia NuGet package

**Objective:** Install the charting library.

**Files:**
- Modify: `./projects/LlamaSwapManager/LlamaSwapManager/LlamaSwapManager.csproj`

**Step 1: Add package reference**

```xml
  <ItemGroup>
     <PackageReference Include="Avalonia" Version="12.0.4" />
     <PackageReference Include="CommunityToolkit.Mvvm" Version="8.2.1" />
     <PackageReference Include="YamlDotNet" Version="16.1.0" />
     <PackageReference Include="OxyPlot.Avalonia" Version="1.2.0" />
   </ItemGroup>
```

**Step 2: Build to verify**

```bash
cd /Users/brunocasado/projects/LlamaSwapManager
dotnet restore LlamaSwapManager/LlamaSwapManager.csproj
dotnet build LlamaSwapManager.Desktop/LlamaSwapManager.Desktop.csproj 2>&1 | grep -E "Erro|error|Compilação"
```

Expected: `Compilação com êxito. 0 Erro(s)`

**Step 3: Commit**

```bash
git add LlamaSwapManager/LlamaSwapManager.csproj
git commit -m "deps: add OxyPlot.Avalonia for metrics charts"
```

---

### Task 2: Add chart data history to MainViewModel

**Objective:** Store rolling time-series data for charts (last 300 points = 10 minutes at 2s polling).

**Files:**
- Modify: `./projects/LlamaSwapManager/LlamaSwapManager/ViewModels/MainViewModel.cs`

**Step 1: Add history fields after the existing TPS regex (around line 125)**

```csharp
    // Real-time TPS extracted from upstream logs
    private static readonly System.Text.RegularExpressions.Regex s_tpsRegex =
        new(@"n_decoded\s*=\s*\d+\s*,\s*tg\s*=\s*([\d.]+)\s*t/s",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    // Chart data history (rolling, max 300 points)
    private readonly Queue<(double Timestamp, double Value)> _tpsHistory = new();
    private readonly Queue<(double Timestamp, double Prefill, double Decode)> _tokensHistory = new();
    private readonly Queue<(double Timestamp, int Value)> _slotsHistory = new();
    private const int MaxHistoryPoints = 300;
    private double _historyBaseline; // epoch seconds at first data point
```

**Step 2: Add chart series properties (after `ActiveSlots` property, around line 165)**

```csharp
    // Chart series (OxyPlot)
    public OxyPlot.Series.LineSeries TokensPerSecondSeries { get; }
    public OxyPlot.Series.LineSeries PrefillSeries { get; }
    public OxyPlot.Series.LineSeries DecodeSeries { get; }
    public OxyPlot.Series.LineSeries ActiveSlotsSeries { get; }
```

**Step 3: Initialize series in constructor (after `ActiveSlots = 0;`)**

```csharp
        TokensPerSecondSeries = new OxyPlot.Series.LineSeries
        {
            Title = "Tokens/sec",
            Color = OxyColors.Parse("#FAB387"),
            StrokeThickness = 2,
            MarkerType = OxyPlot.Series.MarkerType.None
        };

        PrefillSeries = new OxyPlot.Series.LineSeries
        {
            Title = "Prefill",
            Color = OxyColors.Parse("#89B4FA"),
            StrokeThickness = 1.5,
            MarkerType = OxyPlot.Series.MarkerType.None
        };

        DecodeSeries = new OxyPlot.Series.LineSeries
        {
            Title = "Decode",
            Color = OxyColors.Parse("#A6E3A1"),
            StrokeThickness = 1.5,
            MarkerType = OxyPlot.Series.MarkerType.None
        };

        ActiveSlotsSeries = new OxyPlot.Series.LineSeries
        {
            Title = "Slots",
            Color = OxyColors.Parse("#F5C2E7"),
            StrokeThickness = 1.5,
            MarkerType = OxyPlot.Series.MarkerType.None
        };
```

**Step 4: Add history recording method**

```csharp
    // Record a data point and update OxyPlot series
    private void RecordMetrics(double tps, long prefill, long decode, int slots)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (_historyBaseline == 0) _historyBaseline = now;
        var t = now - _historyBaseline;

        // TPS history
        _tpsHistory.Enqueue((t, tps));
        if (_tpsHistory.Count > MaxHistoryPoints) _tpsHistory.Dequeue();
        TokensPerSecondSeries.Points.Clear();
        foreach (var p in _tpsHistory)
            TokensPerSecondSeries.Points.Add(new OxyPlot.DataPoint(p.Timestamp, p.Value));

        // Tokens history (deltas from totals — shows rate per poll)
        _tokensHistory.Enqueue((t, prefill, decode));
        if (_tokensHistory.Count > MaxHistoryPoints) _tokensHistory.Dequeue();
        PrefillSeries.Points.Clear();
        DecodeSeries.Points.Clear();
        for (int i = 0; i < _tokensHistory.Count; i++)
        {
            var curr = _tokensHistory.ElementAt(i);
            var prev = i > 0 ? _tokensHistory.ElementAt(i - 1) : curr;
            var dt = curr.Timestamp - prev.Timestamp;
            var prefillRate = dt > 0 ? (curr.Prefill - prev.Prefill) / dt : 0;
            var decodeRate = dt > 0 ? (curr.Decode - prev.Decode) / dt : 0;
            PrefillSeries.Points.Add(new OxyPlot.DataPoint(curr.Timestamp, prefillRate));
            DecodeSeries.Points.Add(new OxyPlot.DataPoint(curr.Timestamp, decodeRate));
        }

        // Slots history
        _slotsHistory.Enqueue((t, slots));
        if (_slotsHistory.Count > MaxHistoryPoints) _slotsHistory.Dequeue();
        ActiveSlotsSeries.Points.Clear();
        foreach (var p in _slotsHistory)
            ActiveSlotsSeries.Points.Add(new OxyPlot.DataPoint(p.Timestamp, p.Value));
    }
```

**Step 5: Call `RecordMetrics` in `PollMetricsAsync` after updating KPIs**

After the `Dispatcher.UIThread.Post` block, add:

```csharp
                    RecordMetrics(TokensPerSecond, PrefillTokens, DecodeTokens, ActiveSlots);
```

**Step 6: Build**

```bash
cd /Users/brunacasado/projects/LlamaSwapManager
dotnet build LlamaSwapManager.Desktop/LlamaSwapManager.Desktop.csproj 2>&1 | grep -E "Erro|error|Compilação"
```

**Step 7: Commit**

```bash
git add LlamaSwapManager/ViewModels/MainViewModel.cs
git commit -m "feat: add chart data history with OxyPlot series"
```

---

### Task 3: Add Loaded Models display to MainViewModel

**Objective:** Fetch and display loaded models from llama-swap `/running` endpoint.

**Files:**
- Modify: `./projects/LlamaSwapManager/LlamaSwapManager/ViewModels/MainViewModel.cs`
- Create: `./projects/LlamaSwapManager/LlamaSwapManager/Models/LoadedModelInfo.cs`

**Step 1: Create model class**

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LlamaSwapManager.Models;

public class LoadedModelInfo
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    [JsonPropertyName("state")]
    public string State { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("proxy")]
    public string Proxy { get; set; } = string.Empty;

    [JsonPropertyName("cmd")]
    public string Cmd { get; set; } = string.Empty;

    public string DisplayName => Name ?? Model;
    public bool IsReady => State == "ready";
}

public class RunningResponse
{
    [JsonPropertyName("running")]
    public List<LoadedModelInfo> Running { get; set; } = new();
}
```

**Step 2: Add loaded models property to MainViewModel**

After `ActiveSlots` property:

```csharp
    // Loaded models
    [ObservableProperty]
    private System.Collections.ObjectModel.ObservableCollection<LoadedModelInfo> _loadedModels = new();
```

**Step 3: Add fetch method**

```csharp
    private async Task RefreshLoadedModelsAsync()
    {
        var baseUrl = _processManager.SwapBaseUrl;
        if (string.IsNullOrEmpty(baseUrl)) return;

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var response = await http.GetAsync($"{baseUrl}/running");
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync();
                var data = JsonSerializer.Deserialize<RunningResponse>(json);
                if (data != null)
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        _loadedModels.Clear();
                        foreach (var m in data.Running)
                            _loadedModels.Add(m);
                    });
                }
            }
        }
        catch { /* ignore — models refresh silently */ }
    }
```

**Step 4: Call in `PollMetricsAsync` alongside metrics fetch**

After `RecordMetrics(...)`, add:

```csharp
                await RefreshLoadedModelsAsync();
```

**Step 5: Build**

```bash
dotnet build LlamaSwapManager.Desktop/LlamaSwapManager.Desktop.csproj 2>&1 | grep -E "Erro|error|Compilação"
```

**Step 6: Commit**

```bash
git add LlamaSwapManager/Models/LoadedModelInfo.cs LlamaSwapManager/ViewModels/MainViewModel.cs
git commit -m "feat: add loaded models fetch from llama-swap /running endpoint"
```

---

### Task 4: Add charts UI to Metrics tab

**Objective:** Insert two chart panels between stat cards and log panel.

**Files:**
- Modify: `./projects/LlamaSwapManager/LlamaSwapManager.Desktop/Views/MainWindow.axaml`

**Step 1: Add OxyPlot XML namespace to MainWindow**

At the top of the `<Window>` element, add:

```xml
xmlns:oxy="http://oxyplot.org/shared"
```

**Step 2: Restructure Metrics tab Grid rows**

Change the Metrics tab Grid from:
```xml
<Grid RowDefinitions="Auto,Auto,*,Auto" Margin="20">
```

To:
```xml
<Grid RowDefinitions="Auto,Auto,Auto,Auto,*,Auto" Margin="20">
```

Row layout:
- Row 0: Header (existing)
- Row 1: Stat cards (existing)
- Row 2: Tokens/sec chart (NEW)
- Row 3: Prefill vs Decode + Slots chart (NEW)
- Row 4: Logs (existing, was Row 2)
- Row 5: Footer (existing, was Row 3)

**Step 3: Add loaded models display in header**

Update the header Border (Grid.Row="0") to show models:

```xml
          <Border Grid.Row="0" Padding="16" Background="#313244" CornerRadius="8" Margin="0,0,0,16">
            <StackPanel Spacing="6">
              <TextBlock Text="llama-server Metrics" FontSize="16" FontWeight="Bold" Foreground="#CDD6F4" />
              <ItemsControl ItemsSource="{Binding LoadedModels}">
                <ItemsControl.ItemsPanel>
                  <ItemsPanelTemplate>
                    <WrapPanel />
                  </ItemsPanelTemplate>
                </ItemsControl.ItemsPanel>
                <ItemsControl.ItemTemplate>
                  <DataTemplate>
                    <Border Padding="6,4" Background="#45475A" CornerRadius="6" Margin="0,0,8,0">
                      <StackPanel Orientation="Horizontal" Spacing="6">
                        <TextBlock Text="●" Foreground="#A6E3A1" VerticalAlignment="Center" />
                        <TextBlock Text="{Binding DisplayName}" FontSize="11" Foreground="#CDD6F4" />
                        <TextBlock Text="{Binding State}" FontSize="10" Foreground="#6C7086" />
                      </StackPanel>
                    </Border>
                  </DataTemplate>
                </ItemsControl.ItemTemplate>
              </ItemsControl>
              <TextBlock Text="{Binding StatusText}" FontSize="12" Foreground="#A6ADC8" />
            </StackPanel>
          </Border>
```

**Step 4: Add Tokens/sec chart (Grid.Row="2")**

```xml
          <!-- Tokens/sec Chart -->
          <Border Grid.Row="2" Padding="12" Background="#313244" CornerRadius="8" Margin="0,0,0,16">
            <StackPanel Spacing="6">
              <TextBlock Text="Tokens/sec (real-time)" FontSize="12" FontWeight="Bold" Foreground="#FAB387" />
              <oxy:PlotView Model="{Binding TokensPerSecondPlotModel}" Background="#1E1E2E" BorderThickness="0" />
            </StackPanel>
          </Border>
```

**Step 5: Add Prefill/Decode/Slots chart (Grid.Row="3")**

```xml
          <!-- Prefill vs Decode + Slots Chart -->
          <Border Grid.Row="3" Padding="12" Background="#313244" CornerRadius="8" Margin="0,0,0,16">
            <StackPanel Spacing="6">
              <TextBlock Text="Tokens Rate &amp; Active Slots" FontSize="12" FontWeight="Bold" Foreground="#CDD6F4" />
              <oxy:PlotView Model="{Binding TokensSlotsPlotModel}" Background="#1E1E2E" BorderThickness="0" />
            </StackPanel>
          </Border>
```

**Step 6: Update log panel row to Grid.Row="4" and footer to Grid.Row="5"**

Find the existing log grid and footer border and update their row numbers.

**Step 7: Build**

```bash
dotnet build LlamaSwapManager.Desktop/LlamaSwapManager.Desktop.csproj 2>&1 | grep -E "Erro|error|Compilação"
```

**Step 8: Commit**

```bash
git add LlamaSwapManager.Desktop/Views/MainWindow.axaml
git commit -m "ui: add charts and loaded models display to metrics tab"
```

---

### Task 5: Add PlotModel properties to MainViewModel

**Objective:** Wire up OxyPlot PlotModels that the UI binds to.

**Files:**
- Modify: `./projects/LlamaSwapManager/LlamaSwapManager/ViewModels/MainViewModel.cs`

**Step 1: Add PlotModel properties**

```csharp
    // Chart plot models
    public OxyPlot.PlotModel TokensPerSecondPlotModel { get; }
    public OxyPlot.PlotModel TokensSlotsPlotModel { get; }
```

**Step 2: Initialize in constructor (after series initialization)**

```csharp
        TokensPerSecondPlotModel = new OxyPlot.PlotModel
        {
            Background = OxyColors.Parse("#1E1E2E"),
            PlotAreaBackground = OxyColors.Parse("#1E1E2E"),
            PlotBorderColor = OxyColors.Parse("#45475A"),
        };
        TokensPerSecondPlotModel.Axes.Add(new OxyPlot.Axes.LinearAxis
        {
            Position = OxyPlot.Axes.AxisPosition.Bottom,
            Title = "",
            TextColor = OxyColors.Parse("#6C7086"),
            TickColor = OxyColors.Parse("#45475A"),
            MinorTickColor = OxyColors.Parse("#313244"),
            AxislineColor = OxyColors.Parse("#45475A"),
            StringFormat = "s",
            GridlineColor = OxyColors.Parse("#313244"),
            GridlineStyle = OxyPlot.LineStyle.Dot
        });
        TokensPerSecondPlotModel.Axes.Add(new OxyPlot.Axes.LinearAxis
        {
            Position = OxyPlot.Axes.AxisPosition.Left,
            Title = "tokens/s",
            TextColor = OxyColors.Parse("#FAB387"),
            TickColor = OxyColors.Parse("#45475A"),
            MinorTickColor = OxyColors.Parse("#313244"),
            AxislineColor = OxyColors.Parse("#45475A"),
            GridlineColor = OxyColors.Parse("#313244"),
            GridlineStyle = OxyPlot.LineStyle.Dot
        });
        TokensPerSecondPlotModel.Series.Add(TokensPerSecondSeries);

        TokensSlotsPlotModel = new OxyPlot.PlotModel
        {
            Background = OxyColors.Parse("#1E1E2E"),
            PlotAreaBackground = OxyColors.Parse("#1E1E2E"),
            PlotBorderColor = OxyColors.Parse("#45475A"),
        };
        TokensSlotsPlotModel.Axes.Add(new OxyPlot.Axes.LinearAxis
        {
            Position = OxyPlot.Axes.AxisPosition.Bottom,
            Title = "",
            TextColor = OxyColors.Parse("#6C7086"),
            TickColor = OxyColors.Parse("#45475A"),
            MinorTickColor = OxyColors.Parse("#313244"),
            AxislineColor = OxyColors.Parse("#45475A"),
            GridlineColor = OxyColors.Parse("#313244"),
            GridlineStyle = OxyPlot.LineStyle.Dot
        });
        TokensSlotsPlotModel.Axes.Add(new OxyPlot.Axes.LinearAxis
        {
            Position = OxyPlot.Axes.AxisPosition.Left,
            Title = "tokens/s",
            TextColor = OxyColors.Parse("#A6ADC8"),
            TickColor = OxyColors.Parse("#45475A"),
            MinorTickColor = OxyColors.Parse("#313244"),
            AxislineColor = OxyColors.Parse("#45475A"),
            GridlineColor = OxyColors.Parse("#313244"),
            GridlineStyle = OxyPlot.LineStyle.Dot
        });
        TokensSlotsPlotModel.Axes.Add(new OxyPlot.Axes.LinearAxis
        {
            Position = OxyPlot.Axes.AxisPosition.Right,
            Title = "slots",
            TextColor = OxyColors.Parse("#F5C2E7"),
            TickColor = OxyColors.Parse("#45475A"),
            MinorTickColor = OxyColors.Parse("#313244"),
            AxislineColor = OxyColors.Parse("#45475A"),
            GridlineColor = OxyColors.Transparent,
            Minimum = 0
        });
        TokensSlotsPlotModel.Series.Add(PrefillSeries);
        TokensSlotsPlotModel.Series.Add(DecodeSeries);
        TokensSlotsPlotModel.Series.Add(ActiveSlotsSeries);
```

**Step 3: Refresh plot axes after data update**

After `RecordMetrics(...)` in `PollMetricsAsync`, add:

```csharp
                    TokensPerSecondPlotModel.InvalidatePlot(false);
                    TokensSlotsPlotModel.InvalidatePlot(false);
```

**Step 4: Build**

```bash
dotnet build LlamaSwapManager.Desktop/LlamaSwapManager.Desktop.csproj 2>&1 | grep -E "Erro|error|Compilação"
```

**Step 5: Commit**

```bash
git add LlamaSwapManager/ViewModels/MainViewModel.cs
git commit -m "feat: wire up OxyPlot PlotModels with Catppuccin Mocha theme"
```

---

### Task 6: Add WrapPanel to XAML resources

**Objective:** WrapPanel is needed for the loaded models badge layout but may not be in the default Avalonia toolkit.

**Files:**
- Modify: `./projects/LlamaSwapManager/LlamaSwapManager.Desktop/Views/MainWindow.axaml` (or App.axaml)

**Step 1: Check if WrapPanel is available**

Avalonia 12 includes WrapPanel. If the build fails with `WrapPanel not found`, add:

```xml
xmlns:panels="using:Avalonia.Controls.Primitives"
```

And replace `<WrapPanel />` with `<panels:WrapPanel />`.

**Step 2: Build and verify**

```bash
dotnet build LlamaSwapManager.Desktop/LlamaSwapManager.Desktop.csproj 2>&1 | grep -E "Erro|error|Compilação"
```

**Step 3: Commit**

```bash
git add -A
git commit -m "fix: ensure WrapPanel namespace for loaded models badges"
```

---

### Task 7: Final build, test, and push

**Objective:** Verify everything builds and push the branch.

**Step 1: Clean build**

```bash
cd /Users/brunacasado/projects/LlamaSwapManager
dotnet build LlamaSwapManager.Desktop/LlamaSwapManager.Desktop.csproj
```

**Step 2: Push**

```bash
git push
```

---

## Summary of changes

| File | Changes |
|------|---------|
| `LlamaSwapManager.csproj` | Add OxyPlot.Avalonia NuGet |
| `MainViewModel.cs` | Chart history, PlotModels, LoadedModels, RecordMetrics, RefreshLoadedModels |
| `LoadedModelInfo.cs` | New model class for /running endpoint |
| `MainWindow.axaml` | Charts (2 PlotViews), loaded models badges, updated row layout |

## Visual result

```
┌─────────────────────────────────────────────────────┐
│ llama-server Metrics                                │
│ ● Qwen3.6-27B-MTP-GGUF ready                       │
│ Connecting...                                       │
├──────────┬──────────┬──────────┬───────────────────┤
│ Prefill  │ Decode   │ Tokens/s │ Active Slots      │
│ 83,144   │ 2,728    │ 38.9     │ 0                 │
├─────────────────────────────────────────────────────┤
│ Tokens/sec (real-time)                              │
│ ╱╲   ╱╲╱╲   ╱╲                                       │
│    ╱╲╱    ╱╲╱    ╱╲                                 │
├─────────────────────────────────────────────────────┤
│ Tokens Rate & Active Slots                          │
│ ╱╲╱╲   ╱╲╱╲   ╱╲         ╱╲                         │
│    ╱╲╱    ╱╲╱    ╱╲╱╲╱╲╱    ╲                      │
├─────────────────────────────────────────────────────┤
│ 🔍 Filter upstream logs (regex)  ✕                 │
│ Upstream Logs                                       │
│ 12.08.638.089 I slot print_timing: ... tg = 38.89 │
│ ...                                                 │
└─────────────────────────────────────────────────────┘
```

## Risks & Tradeoffs

- **OxyPlot Avalonia 12 compatibility:** OxyPlot.Avalonia 1.2.0 may need a newer version for Avalonia 12. If build fails, try `OxyPlot.Avalonia 1.3.0` or `1.4.0`.
- **Chart performance:** Clearing and rebuilding DataPoints each poll is simple but O(N). For 300 points at 2s intervals, this is negligible.
- **WrapPanel:** May need `Avalonia.Controls` package or explicit namespace import.
- **`/running` endpoint:** Assumes llama-swap exposes this. If the remote swap uses a different port, the existing `SwapBaseUrl` property handles it.
