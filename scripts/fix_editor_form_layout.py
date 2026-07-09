#!/usr/bin/env python3
"""Rebuild model editor forms: centered column, label-above fields, proportional * cols."""
from __future__ import annotations

from pathlib import Path

ROOT = Path("/Users/brunocasado/projects/LlamaSwapManager")
AXAML = ROOT / "LlamaSwapManager.Desktop/Views/MainWindow.axaml"


def is_grid_open(s: str, i: int) -> bool:
    return s.startswith("<Grid", i) and not s.startswith("<Grid.", i)


def find_editor_span(text: str) -> tuple[int, int]:
    start = text.find("<!-- FULL-WINDOW MODEL EDITOR")
    if start < 0:
        raise SystemExit("editor marker missing")
    g0 = text.find("<Grid", start)
    while g0 != -1 and text.startswith("<Grid.", g0):
        g0 = text.find("<Grid", g0 + 5)
    if g0 < 0:
        raise SystemExit("editor grid missing")
    depth = 0
    i = g0
    while i < len(text):
        if is_grid_open(text, i):
            depth += 1
            i = text.find(">", i) + 1
            continue
        if text.startswith("</Grid>", i):
            depth -= 1
            i2 = i + len("</Grid>")
            if depth == 0:
                return start, i2
            i = i2
            continue
        i += 1
    raise SystemExit("could not balance editor grid")


def nav(label: str, key: str) -> str:
    return f"""                  <Button Command="{{Binding SetModelEditorSectionCommand}}" CommandParameter="{key}"
                          Classes="nav" HorizontalAlignment="Stretch" HorizontalContentAlignment="Left"
                          Margin="6,2" Padding="10,9">
                    <DockPanel>
                      <Border Classes="nav-indicator" DockPanel.Dock="Left" VerticalAlignment="Center"
                              IsVisible="{{Binding ModelEditorSection, Converter={{x:Static conv:StringEqualsConverter.Instance}}, ConverterParameter={key}}}"/>
                      <TextBlock Text="{label}" Classes="nav-label" VerticalAlignment="Center"/>
                    </DockPanel>
                  </Button>"""


def page(key: str, title: str, subtitle: str, body: str) -> str:
    return f"""                <ScrollViewer IsVisible="{{Binding ModelEditorSection, Converter={{x:Static conv:StringEqualsConverter.Instance}}, ConverterParameter={key}}}"
                              VerticalScrollBarVisibility="Auto" HorizontalScrollBarVisibility="Disabled">
                  <Border Padding="28,22">
                    <StackPanel Spacing="12" MaxWidth="720" HorizontalAlignment="Center">
                      <StackPanel Spacing="4">
                        <TextBlock Text="{title}" FontSize="18" FontWeight="SemiBold" Foreground="#CDD6F4"/>
                        <TextBlock Text="{subtitle}" FontSize="12" Foreground="#6C7086" TextWrapping="Wrap"/>
                      </StackPanel>
                      <Border Classes="card" Padding="16">
{body}
                      </Border>
                    </StackPanel>
                  </Border>
                </ScrollViewer>"""


def field(label: str, control_xml: str) -> str:
    return f"""                        <StackPanel Spacing="6">
                          <TextBlock Text="{label}" FontSize="12" Foreground="#A6ADC8"/>
{control_xml}
                        </StackPanel>"""


def two_col(left_label: str, left_ctrl: str, right_label: str, right_ctrl: str) -> str:
    return f"""                      <Grid ColumnDefinitions="*,16,*">
                        <StackPanel Spacing="6">
                          <TextBlock Text="{left_label}" FontSize="12" Foreground="#A6ADC8"/>
{left_ctrl}
                        </StackPanel>
                        <StackPanel Grid.Column="2" Spacing="6">
                          <TextBlock Text="{right_label}" FontSize="12" Foreground="#A6ADC8"/>
{right_ctrl}
                        </StackPanel>
                      </Grid>"""


def tb(bind: str, ph: str = "") -> str:
    return (
        f'                          <TextBox Text="{{Binding {bind}}}" PlaceholderText="{ph}" '
        f'MinHeight="34" HorizontalAlignment="Stretch"/>'
    )


def cb(items: str, bind: str) -> str:
    return (
        f'                          <ComboBox ItemsSource="{{Binding {items}}}" SelectedItem="{{Binding {bind}}}" '
        f'MinHeight="34" HorizontalAlignment="Stretch"/>'
    )


def cbe(items: str, bind: str, ph: str = "") -> str:
    return (
        f'                          <ComboBox ItemsSource="{{Binding {items}}}" SelectedItem="{{Binding {bind}}}" '
        f'IsEditable="True" PlaceholderText="{ph}" MinHeight="34" HorizontalAlignment="Stretch"/>'
    )


SOURCE_CTRL = """                          <Grid ColumnDefinitions="*,Auto" ColumnSpacing="8">
                            <TextBox Text="{Binding SelectedModelSourceLabel}" IsReadOnly="True" MinHeight="34"
                                     TextTrimming="CharacterEllipsis"/>
                            <Button Grid.Column="1" Content="Choose" Click="OnChooseModelClick" Classes="ghost"
                                    Padding="14,6" MinHeight="34" VerticalAlignment="Center"/>
                          </Grid>"""

API_KEY_CTRL = (
    '                          <TextBox Text="{Binding SelectedModel.ApiKey}" PasswordChar="*" '
    'MinHeight="34" HorizontalAlignment="Stretch"/>'
)


def main() -> None:
    text = AXAML.read_text()
    start, end = find_editor_span(text)

    essentials = f"""
                      <StackPanel Spacing="14">
{two_col('Model ID', tb('SelectedModel.ModelId', 'qwen2.5-14b'), 'Display name', tb('SelectedModel.Name', 'Friendly label'))}
{two_col('Context', tb('SelectedModel.ContextSize', '0 = default'), 'GPU layers', cbe('GpuLayersOptions', 'SelectedModel.GpuLayers', 'auto | all | N'))}
{two_col('Reasoning', cb('ReasoningOptions', 'SelectedModel.Reasoning'), 'TTL (s)', tb('SelectedModel.Ttl', '0 = never'))}
{field('Model source', SOURCE_CTRL)}
{field('Server path', tb('SelectedModel.LlamaServerPath', '/path/to/llama-server'))}
                        <StackPanel Orientation="Horizontal" Spacing="16" Margin="0,2,0,0">
                          <CheckBox IsChecked="{{Binding SelectedModel.UseJinja}}" Content="jinja" Foreground="#CDD6F4"/>
                          <CheckBox IsChecked="{{Binding SelectedModel.FitOn}}" Content="fit on" Foreground="#CDD6F4"/>
                          <CheckBox IsChecked="{{Binding SelectedModel.NoMmap}}" Content="no-mmap" Foreground="#CDD6F4"/>
                        </StackPanel>
                      </StackPanel>
"""

    identity = f"""
                      <StackPanel Spacing="14">
{field('Description', tb('SelectedModel.Description', 'Optional'))}
{field('Aliases', tb('SelectedModel.AliasesText', 'comma-separated'))}
{two_col('Host', tb('SelectedModel.Host', '127.0.0.1'), 'Port', tb('SelectedModel.Port', '${{PORT}}'))}
                      </StackPanel>
"""

    runtime = f"""
                      <StackPanel Spacing="14">
{two_col('Predict', tb('SelectedModel.Predict', '-1'), 'Threads', tb('SelectedModel.Threads', '-1'))}
{two_col('Threads batch', tb('SelectedModel.ThreadsBatch', ''), 'Batch size', tb('SelectedModel.BatchSize', '2048'))}
{field('uBatch', tb('SelectedModel.UBatchSize', '512'))}
                      </StackPanel>
"""

    gpu = f"""
                      <StackPanel Spacing="14">
{two_col('GPU layers', cbe('GpuLayersOptions', 'SelectedModel.GpuLayers', 'auto | all | N'), 'Flash attn', cb('AutoOnOffOptions', 'SelectedModel.FlashAttention'))}
{two_col('Device', tb('SelectedModel.Device', 'CUDA0,Metal'), 'Split mode', cb('SplitModeOptions', 'SelectedModel.SplitMode'))}
{two_col('Tensor split', tb('SelectedModel.TensorSplit', '3,1'), 'Main GPU', tb('SelectedModel.MainGpu', '0'))}
                        <StackPanel Orientation="Horizontal" Spacing="16">
                          <CheckBox IsChecked="{{Binding SelectedModel.Mlock}}" Content="mlock" Foreground="#CDD6F4"/>
                          <CheckBox IsChecked="{{Binding SelectedModel.FitOn}}" Content="fit on" Foreground="#CDD6F4"/>
                          <CheckBox IsChecked="{{Binding SelectedModel.NoMmap}}" Content="no-mmap" Foreground="#CDD6F4"/>
                        </StackPanel>
                      </StackPanel>
"""

    kv = f"""
                      <StackPanel Spacing="14">
{two_col('Cache K', cb('CacheTypeOptions', 'SelectedModel.CacheTypeK'), 'Cache V', cb('CacheTypeOptions', 'SelectedModel.CacheTypeV'))}
                      </StackPanel>
"""

    sampling = f"""
                      <StackPanel Spacing="14">
{two_col('Temperature', tb('SelectedModel.Temperature', ''), 'Top K', tb('SelectedModel.TopK', ''))}
{two_col('Top P', tb('SelectedModel.TopP', ''), 'Min P', tb('SelectedModel.MinP', ''))}
{two_col('Repeat penalty', tb('SelectedModel.RepeatPenalty', ''), 'Seed', tb('SelectedModel.Seed', ''))}
{field('Samplers', cbe('CommonSamplersOptions', 'SelectedModel.Samplers', ''))}
                      </StackPanel>
"""

    server = f"""
                      <StackPanel Spacing="14">
{two_col('Parallel', tb('SelectedModel.Parallel', ''), 'Timeout', tb('SelectedModel.Timeout', ''))}
{two_col('HTTP threads', tb('SelectedModel.ThreadsHttp', ''), 'API key', API_KEY_CTRL)}
                        <StackPanel Orientation="Horizontal" Spacing="14">
                          <CheckBox IsChecked="{{Binding SelectedModel.ContBatching}}" Content="continuous batching" Foreground="#CDD6F4"/>
                          <CheckBox IsChecked="{{Binding SelectedModel.Slots}}" Content="slots" Foreground="#CDD6F4"/>
                          <CheckBox IsChecked="{{Binding SelectedModel.Embeddings}}" Content="embeddings" Foreground="#CDD6F4"/>
                          <CheckBox IsChecked="{{Binding SelectedModel.Metrics}}" Content="metrics" Foreground="#CDD6F4"/>
                          <CheckBox IsChecked="{{Binding SelectedModel.Reranking}}" Content="reranking" Foreground="#CDD6F4"/>
                        </StackPanel>
                      </StackPanel>
"""

    chat = f"""
                      <StackPanel Spacing="14">
{field('Chat template', cbe('ChatTemplatePresets', 'SelectedModel.ChatTemplate', 'chatml | path.jinja'))}
{two_col('Reasoning', cb('ReasoningOptions', 'SelectedModel.Reasoning'), 'Reasoning format', cb('ReasoningFormatOptions', 'SelectedModel.ReasoningFormat'))}
{field('Reasoning budget', tb('SelectedModel.ReasoningBudget', '-1 / 0 / N'))}
                        <CheckBox IsChecked="{{Binding SelectedModel.UseJinja}}" Content="Enable jinja" Foreground="#CDD6F4"/>
                        <TextBlock Text="Template: built-in name, path to .jinja, or raw Jinja. Empty uses the model default."
                                   FontSize="11" Foreground="#6C7086" TextWrapping="Wrap"/>
                      </StackPanel>
"""

    raw = """
                      <StackPanel Spacing="12">
                        <Grid ColumnDefinitions="*,Auto,Auto" ColumnSpacing="8">
                          <TextBlock Text="Extra raw llama-server flags" FontSize="12" Foreground="#A6ADC8" VerticalAlignment="Center"/>
                          <Button Grid.Column="1" Content="Copy" Click="OnCopyExtraArgsClick" Classes="ghost" Padding="10,5"/>
                          <Button Grid.Column="2" Content="Paste" Click="OnPasteExtraArgsClick" Classes="ghost" Padding="10,5"/>
                        </Grid>
                        <TextBox Name="ExtraArgsTextBox" Text="{Binding SelectedModel.ExtraArgs}" AcceptsReturn="True"
                                 MinHeight="200" TextWrapping="Wrap"
                                 KeyDown="OnExtraArgsKeyDown" GotFocus="OnExtraArgsGotFocus"
                                 PlaceholderText="--rope-scaling yarn --mmproj /path/mmproj.gguf"/>
                      </StackPanel>
"""

    editor = f"""
    <!-- FULL-WINDOW MODEL EDITOR: centered form column, proportional * fields, label-above -->
    <Grid Grid.Row="0" Grid.Column="0" Grid.ColumnSpan="2" Grid.RowSpan="2"
          IsVisible="{{Binding HasSelectedModel}}" ZIndex="200">
      <Border Background="#C0000000" PointerPressed="OnModelEditorBackdropPressed"/>
      <Border Margin="40,32"
              HorizontalAlignment="Stretch"
              VerticalAlignment="Stretch"
              Background="#0F0F18"
              BorderBrush="#2A2A3D"
              BorderThickness="1"
              CornerRadius="16">
        <Grid RowDefinitions="Auto,*,Auto">
          <Border Grid.Row="0" Padding="20,14" BorderBrush="#1F1F2E" BorderThickness="0,0,0,1">
            <DockPanel LastChildFill="True">
              <Button DockPanel.Dock="Right" Content="Close" Command="{{Binding CloseModelEditorCommand}}"
                      Classes="ghost" Padding="14,7" VerticalAlignment="Center" Margin="12,0,0,0"/>
              <StackPanel Spacing="2" VerticalAlignment="Center">
                <TextBlock Text="EDIT MODEL" FontSize="10" FontWeight="Bold" Foreground="#585B70"/>
                <TextBlock Text="{{Binding SelectedModel.ModelId}}" FontSize="17" FontWeight="SemiBold" Foreground="#CDD6F4"/>
              </StackPanel>
            </DockPanel>
          </Border>

          <Grid Grid.Row="1" ColumnDefinitions="220,*">
            <Border Grid.Column="0" Background="#0C0C14" BorderBrush="#1A1A28" BorderThickness="0,0,1,0">
              <ScrollViewer VerticalScrollBarVisibility="Auto">
                <StackPanel Margin="8,14" Spacing="2">
                  <TextBlock Text="SECTIONS" Classes="nav-section"/>
{nav('Essentials', 'essentials')}
{nav('Identity', 'identity')}
{nav('Runtime', 'runtime')}
{nav('GPU and memory', 'gpu')}
{nav('KV cache', 'kv')}
{nav('Sampling', 'sampling')}
{nav('Server and API', 'server')}
{nav('Chat', 'chat')}
{nav('Raw flags', 'raw')}
                </StackPanel>
              </ScrollViewer>
            </Border>
            <Panel Grid.Column="1" Background="#101018">
{page('essentials', 'Essentials', 'Common knobs for every model profile.', essentials)}
{page('identity', 'Identity', 'Names, aliases and network bind.', identity)}
{page('runtime', 'Runtime', 'Prediction length and CPU batching.', runtime)}
{page('gpu', 'GPU and memory', 'Device placement and memory behavior.', gpu)}
{page('kv', 'KV cache', 'Key/value cache datatypes.', kv)}
{page('sampling', 'Sampling', 'Generation style controls.', sampling)}
{page('server', 'Server and API', 'Slots, auth and endpoint toggles.', server)}
{page('chat', 'Chat', 'Templates and reasoning behavior.', chat)}
{page('raw', 'Raw flags', 'Escape hatch for unsupported flags.', raw)}
            </Panel>
          </Grid>

          <Border Grid.Row="2" Padding="18,14" Background="#0C0C14" BorderBrush="#1A1A28" BorderThickness="0,1,0,0">
            <Grid ColumnDefinitions="*,Auto,Auto,Auto" ColumnSpacing="10">
              <TextBlock Text="Centered form column (max 720). Label-above fields with proportional width — no fixed-px overflow."
                         FontSize="11" Foreground="#6C7086" VerticalAlignment="Center" TextWrapping="Wrap"/>
              <Button Grid.Column="1" Content="Save" Command="{{Binding SaveModelCommand}}" Classes="success" Padding="18,9" MinWidth="120"/>
              <Button Grid.Column="2" Content="Cancel" Command="{{Binding CancelModelCommand}}" Classes="ghost" Padding="18,9" MinWidth="110" IsVisible="{{Binding IsNewModel}}"/>
              <Button Grid.Column="3" Content="Delete" Click="OnDeleteModelClick" Classes="danger" Padding="18,9" MinWidth="110"/>
            </Grid>
          </Border>
        </Grid>
      </Border>
    </Grid>
"""

    AXAML.write_text(text[:start] + editor + text[end:])
    print("ok", AXAML.stat().st_size)
    print("max720", (text[:start] + editor + text[end:]).count('MaxWidth="720"'))
    print("star_two_col", (text[:start] + editor + text[end:]).count('ColumnDefinitions="*,16,*"'))


if __name__ == "__main__":
    main()
