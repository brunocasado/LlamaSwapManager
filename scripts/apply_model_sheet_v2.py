#!/usr/bin/env python3
"""Replace the model editor region with a clean 90% bottom sheet + multi-section sidebar."""
from __future__ import annotations

import re
from pathlib import Path

ROOT = Path("/Users/brunocasado/projects/LlamaSwapManager")
AXAML = ROOT / "LlamaSwapManager.Desktop/Views/MainWindow.axaml"


def nav(label: str, key: str) -> str:
    return f"""                      <Button Command="{{Binding SetModelEditorSectionCommand}}" CommandParameter="{key}"
                              Classes="nav" HorizontalAlignment="Stretch" HorizontalContentAlignment="Left"
                              Margin="6,2" Padding="10,9">
                        <DockPanel>
                          <Border Classes="nav-indicator" DockPanel.Dock="Left" VerticalAlignment="Center"
                                  IsVisible="{{Binding ModelEditorSection, Converter={{x:Static conv:StringEqualsConverter.Instance}}, ConverterParameter={key}}}"/>
                          <TextBlock Text="{label}" Classes="nav-label" VerticalAlignment="Center"/>
                        </DockPanel>
                      </Button>"""


def page(key: str, title: str, subtitle: str, body: str) -> str:
    return f"""                    <ScrollViewer IsVisible="{{Binding ModelEditorSection, Converter={{x:Static conv:StringEqualsConverter.Instance}}, ConverterParameter={key}}}"
                                  VerticalScrollBarVisibility="Auto" Padding="24,18">
                      <StackPanel Spacing="14" Margin="0,0,0,28" MaxWidth="920" HorizontalAlignment="Left">
                        <StackPanel Spacing="2">
                          <TextBlock Text="{title}" FontSize="18" FontWeight="SemiBold" Foreground="#CDD6F4"/>
                          <TextBlock Text="{subtitle}" FontSize="12" Foreground="#6C7086" TextWrapping="Wrap"/>
                        </StackPanel>
{body}
                      </StackPanel>
                    </ScrollViewer>"""


def card(inner: str) -> str:
    return f"""                        <Border Classes="card" Padding="16">
{inner}
                        </Border>"""


essentials = card(
    """
                          <Grid ColumnDefinitions="150,*,150,*" RowSpacing="12" ColumnSpacing="14">
                            <Grid.RowDefinitions>
                              <RowDefinition Height="Auto"/>
                              <RowDefinition Height="Auto"/>
                              <RowDefinition Height="Auto"/>
                              <RowDefinition Height="Auto"/>
                              <RowDefinition Height="Auto"/>
                              <RowDefinition Height="Auto"/>
                            </Grid.RowDefinitions>
                            <TextBlock Grid.Row="0" Grid.Column="0" Text="Model ID" FontSize="12" Foreground="#A6ADC8" VerticalAlignment="Center"/>
                            <TextBox Grid.Row="0" Grid.Column="1" Text="{Binding SelectedModel.ModelId}" PlaceholderText="qwen2.5-14b"/>
                            <TextBlock Grid.Row="0" Grid.Column="2" Text="Display name" FontSize="12" Foreground="#A6ADC8" VerticalAlignment="Center"/>
                            <TextBox Grid.Row="0" Grid.Column="3" Text="{Binding SelectedModel.Name}" PlaceholderText="Friendly label"/>
                            <TextBlock Grid.Row="1" Grid.Column="0" Text="Model source" FontSize="12" Foreground="#A6ADC8" VerticalAlignment="Center"/>
                            <Grid Grid.Row="1" Grid.Column="1" Grid.ColumnSpan="3" ColumnDefinitions="*,Auto" ColumnSpacing="8">
                              <TextBox Grid.Column="0" Text="{Binding SelectedModelSourceLabel}" IsReadOnly="True"/>
                              <Button Grid.Column="1" Content="Choose" Click="OnChooseModelClick" Classes="ghost" Padding="12,6"/>
                            </Grid>
                            <TextBlock Grid.Row="2" Grid.Column="0" Text="Server path" FontSize="12" Foreground="#A6ADC8" VerticalAlignment="Center"/>
                            <TextBox Grid.Row="2" Grid.Column="1" Grid.ColumnSpan="3" Text="{Binding SelectedModel.LlamaServerPath}" PlaceholderText="/path/to/llama-server"/>
                            <TextBlock Grid.Row="3" Grid.Column="0" Text="Context" FontSize="12" Foreground="#A6ADC8" VerticalAlignment="Center"/>
                            <TextBox Grid.Row="3" Grid.Column="1" Text="{Binding SelectedModel.ContextSize}" PlaceholderText="0 = model default"/>
                            <TextBlock Grid.Row="3" Grid.Column="2" Text="GPU layers" FontSize="12" Foreground="#A6ADC8" VerticalAlignment="Center"/>
                            <ComboBox Grid.Row="3" Grid.Column="3" ItemsSource="{Binding GpuLayersOptions}" SelectedItem="{Binding SelectedModel.GpuLayers}" IsEditable="True" PlaceholderText="auto | all | N"/>
                            <TextBlock Grid.Row="4" Grid.Column="0" Text="Reasoning" FontSize="12" Foreground="#A6ADC8" VerticalAlignment="Center"/>
                            <ComboBox Grid.Row="4" Grid.Column="1" ItemsSource="{Binding ReasoningOptions}" SelectedItem="{Binding SelectedModel.Reasoning}"/>
                            <TextBlock Grid.Row="4" Grid.Column="2" Text="TTL (s)" FontSize="12" Foreground="#A6ADC8" VerticalAlignment="Center"/>
                            <TextBox Grid.Row="4" Grid.Column="3" Text="{Binding SelectedModel.Ttl}" PlaceholderText="0 = never"/>
                            <StackPanel Grid.Row="5" Grid.Column="0" Grid.ColumnSpan="4" Orientation="Horizontal" Spacing="16">
                              <CheckBox IsChecked="{Binding SelectedModel.UseJinja}" Content="jinja" Foreground="#CDD6F4"/>
                              <CheckBox IsChecked="{Binding SelectedModel.FitOn}" Content="fit on" Foreground="#CDD6F4"/>
                              <CheckBox IsChecked="{Binding SelectedModel.NoMmap}" Content="no-mmap" Foreground="#CDD6F4"/>
                            </StackPanel>
                          </Grid>
"""
)

identity = card(
    """
                          <Grid ColumnDefinitions="150,*" RowSpacing="10">
                            <TextBlock Grid.Row="0" Grid.Column="0" Text="Description" FontSize="12" Foreground="#A6ADC8" VerticalAlignment="Center"/>
                            <TextBox Grid.Row="0" Grid.Column="1" Text="{Binding SelectedModel.Description}" PlaceholderText="Optional"/>
                            <TextBlock Grid.Row="1" Grid.Column="0" Text="Aliases" FontSize="12" Foreground="#A6ADC8" VerticalAlignment="Center"/>
                            <TextBox Grid.Row="1" Grid.Column="1" Text="{Binding SelectedModel.AliasesText}" PlaceholderText="comma-separated"/>
                            <TextBlock Grid.Row="2" Grid.Column="0" Text="Host" FontSize="12" Foreground="#A6ADC8" VerticalAlignment="Center"/>
                            <TextBox Grid.Row="2" Grid.Column="1" Text="{Binding SelectedModel.Host}" PlaceholderText="127.0.0.1"/>
                            <TextBlock Grid.Row="3" Grid.Column="0" Text="Port" FontSize="12" Foreground="#A6ADC8" VerticalAlignment="Center"/>
                            <TextBox Grid.Row="3" Grid.Column="1" Text="{Binding SelectedModel.Port}" PlaceholderText="${PORT}"/>
                          </Grid>
"""
)

runtime = card(
    """
                          <Grid ColumnDefinitions="150,*,150,*" RowSpacing="10" ColumnSpacing="12">
                            <TextBlock Grid.Row="0" Grid.Column="0" Text="Predict" FontSize="12" Foreground="#A6ADC8" VerticalAlignment="Center"/>
                            <TextBox Grid.Row="0" Grid.Column="1" Text="{Binding SelectedModel.Predict}" PlaceholderText="-1"/>
                            <TextBlock Grid.Row="0" Grid.Column="2" Text="Threads" FontSize="12" Foreground="#A6ADC8" VerticalAlignment="Center"/>
                            <TextBox Grid.Row="0" Grid.Column="3" Text="{Binding SelectedModel.Threads}" PlaceholderText="-1"/>
                            <TextBlock Grid.Row="1" Grid.Column="0" Text="Threads batch" FontSize="12" Foreground="#A6ADC8" VerticalAlignment="Center"/>
                            <TextBox Grid.Row="1" Grid.Column="1" Text="{Binding SelectedModel.ThreadsBatch}"/>
                            <TextBlock Grid.Row="1" Grid.Column="2" Text="Batch size" FontSize="12" Foreground="#A6ADC8" VerticalAlignment="Center"/>
                            <TextBox Grid.Row="1" Grid.Column="3" Text="{Binding SelectedModel.BatchSize}" PlaceholderText="2048"/>
                            <TextBlock Grid.Row="2" Grid.Column="0" Text="uBatch" FontSize="12" Foreground="#A6ADC8" VerticalAlignment="Center"/>
                            <TextBox Grid.Row="2" Grid.Column="1" Text="{Binding SelectedModel.UBatchSize}" PlaceholderText="512"/>
                          </Grid>
"""
)

gpu = card(
    """
                          <StackPanel Spacing="12">
                            <Grid ColumnDefinitions="150,*,150,*" RowSpacing="10" ColumnSpacing="12">
                              <TextBlock Grid.Row="0" Grid.Column="0" Text="GPU layers" FontSize="12" Foreground="#A6ADC8" VerticalAlignment="Center"/>
                              <ComboBox Grid.Row="0" Grid.Column="1" ItemsSource="{Binding GpuLayersOptions}" SelectedItem="{Binding SelectedModel.GpuLayers}" IsEditable="True"/>
                              <TextBlock Grid.Row="0" Grid.Column="2" Text="Flash attn" FontSize="12" Foreground="#A6ADC8" VerticalAlignment="Center"/>
                              <ComboBox Grid.Row="0" Grid.Column="3" ItemsSource="{Binding AutoOnOffOptions}" SelectedItem="{Binding SelectedModel.FlashAttention}"/>
                              <TextBlock Grid.Row="1" Grid.Column="0" Text="Device" FontSize="12" Foreground="#A6ADC8" VerticalAlignment="Center"/>
                              <TextBox Grid.Row="1" Grid.Column="1" Text="{Binding SelectedModel.Device}" PlaceholderText="CUDA0,Metal"/>
                              <TextBlock Grid.Row="1" Grid.Column="2" Text="Split mode" FontSize="12" Foreground="#A6ADC8" VerticalAlignment="Center"/>
                              <ComboBox Grid.Row="1" Grid.Column="3" ItemsSource="{Binding SplitModeOptions}" SelectedItem="{Binding SelectedModel.SplitMode}"/>
                              <TextBlock Grid.Row="2" Grid.Column="0" Text="Tensor split" FontSize="12" Foreground="#A6ADC8" VerticalAlignment="Center"/>
                              <TextBox Grid.Row="2" Grid.Column="1" Text="{Binding SelectedModel.TensorSplit}" PlaceholderText="3,1"/>
                              <TextBlock Grid.Row="2" Grid.Column="2" Text="Main GPU" FontSize="12" Foreground="#A6ADC8" VerticalAlignment="Center"/>
                              <TextBox Grid.Row="2" Grid.Column="3" Text="{Binding SelectedModel.MainGpu}" PlaceholderText="0"/>
                            </Grid>
                            <StackPanel Orientation="Horizontal" Spacing="16">
                              <CheckBox IsChecked="{Binding SelectedModel.Mlock}" Content="mlock" Foreground="#CDD6F4"/>
                              <CheckBox IsChecked="{Binding SelectedModel.FitOn}" Content="fit on" Foreground="#CDD6F4"/>
                              <CheckBox IsChecked="{Binding SelectedModel.NoMmap}" Content="no-mmap" Foreground="#CDD6F4"/>
                            </StackPanel>
                          </StackPanel>
"""
)

kv = card(
    """
                          <Grid ColumnDefinitions="150,*,150,*" ColumnSpacing="12">
                            <TextBlock Grid.Column="0" Text="Cache K" FontSize="12" Foreground="#A6ADC8" VerticalAlignment="Center"/>
                            <ComboBox Grid.Column="1" ItemsSource="{Binding CacheTypeOptions}" SelectedItem="{Binding SelectedModel.CacheTypeK}"/>
                            <TextBlock Grid.Column="2" Text="Cache V" FontSize="12" Foreground="#A6ADC8" VerticalAlignment="Center"/>
                            <ComboBox Grid.Column="3" ItemsSource="{Binding CacheTypeOptions}" SelectedItem="{Binding SelectedModel.CacheTypeV}"/>
                          </Grid>
"""
)

sampling = card(
    """
                          <Grid ColumnDefinitions="150,*,150,*" RowSpacing="10" ColumnSpacing="12">
                            <Grid.RowDefinitions>
                              <RowDefinition Height="Auto"/><RowDefinition Height="Auto"/><RowDefinition Height="Auto"/><RowDefinition Height="Auto"/>
                            </Grid.RowDefinitions>
                            <TextBlock Grid.Row="0" Grid.Column="0" Text="Temperature" FontSize="12" Foreground="#A6ADC8" VerticalAlignment="Center"/>
                            <TextBox Grid.Row="0" Grid.Column="1" Text="{Binding SelectedModel.Temperature}"/>
                            <TextBlock Grid.Row="0" Grid.Column="2" Text="Top K" FontSize="12" Foreground="#A6ADC8" VerticalAlignment="Center"/>
                            <TextBox Grid.Row="0" Grid.Column="3" Text="{Binding SelectedModel.TopK}"/>
                            <TextBlock Grid.Row="1" Grid.Column="0" Text="Top P" FontSize="12" Foreground="#A6ADC8" VerticalAlignment="Center"/>
                            <TextBox Grid.Row="1" Grid.Column="1" Text="{Binding SelectedModel.TopP}"/>
                            <TextBlock Grid.Row="1" Grid.Column="2" Text="Min P" FontSize="12" Foreground="#A6ADC8" VerticalAlignment="Center"/>
                            <TextBox Grid.Row="1" Grid.Column="3" Text="{Binding SelectedModel.MinP}"/>
                            <TextBlock Grid.Row="2" Grid.Column="0" Text="Repeat penalty" FontSize="12" Foreground="#A6ADC8" VerticalAlignment="Center"/>
                            <TextBox Grid.Row="2" Grid.Column="1" Text="{Binding SelectedModel.RepeatPenalty}"/>
                            <TextBlock Grid.Row="2" Grid.Column="2" Text="Seed" FontSize="12" Foreground="#A6ADC8" VerticalAlignment="Center"/>
                            <TextBox Grid.Row="2" Grid.Column="3" Text="{Binding SelectedModel.Seed}"/>
                            <TextBlock Grid.Row="3" Grid.Column="0" Text="Samplers" FontSize="12" Foreground="#A6ADC8" VerticalAlignment="Center"/>
                            <ComboBox Grid.Row="3" Grid.Column="1" Grid.ColumnSpan="3" ItemsSource="{Binding CommonSamplersOptions}" SelectedItem="{Binding SelectedModel.Samplers}" IsEditable="True"/>
                          </Grid>
"""
)

server = card(
    """
                          <StackPanel Spacing="12">
                            <Grid ColumnDefinitions="150,*,150,*" RowSpacing="10" ColumnSpacing="12">
                              <TextBlock Grid.Row="0" Grid.Column="0" Text="Parallel" FontSize="12" Foreground="#A6ADC8" VerticalAlignment="Center"/>
                              <TextBox Grid.Row="0" Grid.Column="1" Text="{Binding SelectedModel.Parallel}"/>
                              <TextBlock Grid.Row="0" Grid.Column="2" Text="Timeout" FontSize="12" Foreground="#A6ADC8" VerticalAlignment="Center"/>
                              <TextBox Grid.Row="0" Grid.Column="3" Text="{Binding SelectedModel.Timeout}"/>
                              <TextBlock Grid.Row="1" Grid.Column="0" Text="HTTP threads" FontSize="12" Foreground="#A6ADC8" VerticalAlignment="Center"/>
                              <TextBox Grid.Row="1" Grid.Column="1" Text="{Binding SelectedModel.ThreadsHttp}"/>
                              <TextBlock Grid.Row="1" Grid.Column="2" Text="API key" FontSize="12" Foreground="#A6ADC8" VerticalAlignment="Center"/>
                              <TextBox Grid.Row="1" Grid.Column="3" Text="{Binding SelectedModel.ApiKey}" PasswordChar="*"/>
                            </Grid>
                            <StackPanel Orientation="Horizontal" Spacing="14">
                              <CheckBox IsChecked="{Binding SelectedModel.ContBatching}" Content="continuous batching" Foreground="#CDD6F4"/>
                              <CheckBox IsChecked="{Binding SelectedModel.Slots}" Content="slots" Foreground="#CDD6F4"/>
                              <CheckBox IsChecked="{Binding SelectedModel.Embeddings}" Content="embeddings" Foreground="#CDD6F4"/>
                              <CheckBox IsChecked="{Binding SelectedModel.Metrics}" Content="metrics" Foreground="#CDD6F4"/>
                              <CheckBox IsChecked="{Binding SelectedModel.Reranking}" Content="reranking" Foreground="#CDD6F4"/>
                              <CheckBox IsChecked="{Binding SelectedModel.PropsEndpoint}" Content="props" Foreground="#CDD6F4"/>
                            </StackPanel>
                          </StackPanel>
"""
)

chat = card(
    """
                          <StackPanel Spacing="12">
                            <Grid ColumnDefinitions="150,*" RowSpacing="10">
                              <TextBlock Grid.Row="0" Grid.Column="0" Text="Chat template" FontSize="12" Foreground="#A6ADC8" VerticalAlignment="Center"/>
                              <ComboBox Grid.Row="0" Grid.Column="1" ItemsSource="{Binding ChatTemplatePresets}" SelectedItem="{Binding SelectedModel.ChatTemplate}" IsEditable="True" PlaceholderText="chatml | path.jinja | raw"/>
                              <TextBlock Grid.Row="1" Grid.Column="0" Text="Reasoning" FontSize="12" Foreground="#A6ADC8" VerticalAlignment="Center"/>
                              <ComboBox Grid.Row="1" Grid.Column="1" ItemsSource="{Binding ReasoningOptions}" SelectedItem="{Binding SelectedModel.Reasoning}"/>
                              <TextBlock Grid.Row="2" Grid.Column="0" Text="Reasoning format" FontSize="12" Foreground="#A6ADC8" VerticalAlignment="Center"/>
                              <ComboBox Grid.Row="2" Grid.Column="1" ItemsSource="{Binding ReasoningFormatOptions}" SelectedItem="{Binding SelectedModel.ReasoningFormat}"/>
                              <TextBlock Grid.Row="3" Grid.Column="0" Text="Reasoning budget" FontSize="12" Foreground="#A6ADC8" VerticalAlignment="Center"/>
                              <TextBox Grid.Row="3" Grid.Column="1" Text="{Binding SelectedModel.ReasoningBudget}" PlaceholderText="-1 / 0 / N"/>
                            </Grid>
                            <CheckBox IsChecked="{Binding SelectedModel.UseJinja}" Content="Enable jinja" Foreground="#CDD6F4"/>
                            <TextBlock Text="Template: built-in name, path to .jinja, or raw Jinja. Empty uses model default."
                                       FontSize="11" Foreground="#6C7086" TextWrapping="Wrap"/>
                          </StackPanel>
"""
)

raw = card(
    """
                          <StackPanel Spacing="10">
                            <Grid ColumnDefinitions="*,Auto,Auto" ColumnSpacing="8">
                              <TextBlock Text="Extra raw llama-server flags" FontSize="12" Foreground="#A6ADC8" VerticalAlignment="Center"/>
                              <Button Grid.Column="1" Content="Copy" Click="OnCopyExtraArgsClick" Classes="ghost" Padding="10,5"/>
                              <Button Grid.Column="2" Content="Paste" Click="OnPasteExtraArgsClick" Classes="ghost" Padding="10,5"/>
                            </Grid>
                            <TextBox Name="ExtraArgsTextBox"
                                     Text="{Binding SelectedModel.ExtraArgs}"
                                     AcceptsReturn="True"
                                     MinHeight="160"
                                     TextWrapping="Wrap"
                                     KeyDown="OnExtraArgsKeyDown"
                                     GotFocus="OnExtraArgsGotFocus"
                                     PlaceholderText="--rope-scaling yarn --mmproj /path/mmproj.gguf"/>
                          </StackPanel>
"""
)

sheet = f"""
          <!-- EDITOR SHEET: ~90% height + multi-section sidebar (no expanders) -->
          <Grid IsVisible="{{Binding HasSelectedModel}}" ZIndex="40">
            <Border Background="#B8000000" PointerPressed="OnModelEditorBackdropPressed"/>
            <Grid RowDefinitions="*,9*">
              <Border Grid.Row="0" Background="Transparent" PointerPressed="OnModelEditorBackdropPressed"/>
              <Border Grid.Row="1" Background="#0F0F18" BorderBrush="#2A2A3D"
                      BorderThickness="0,1,0,0" CornerRadius="18,18,0,0">
                <Grid RowDefinitions="Auto,*,Auto">
                  <Border Grid.Row="0" Padding="18,10" BorderBrush="#1F1F2E" BorderThickness="0,0,0,1">
                    <DockPanel>
                      <Border Width="42" Height="4" CornerRadius="2" Background="#3A3A50"
                              HorizontalAlignment="Center" DockPanel.Dock="Top" Margin="0,2,0,10"/>
                      <StackPanel Spacing="2" VerticalAlignment="Center">
                        <TextBlock Text="EDIT MODEL" FontSize="10" FontWeight="Bold" Foreground="#585B70"/>
                        <TextBlock Text="{{Binding SelectedModel.ModelId}}" FontSize="17" FontWeight="SemiBold" Foreground="#CDD6F4"/>
                      </StackPanel>
                      <Button DockPanel.Dock="Right" Content="Close" Command="{{Binding CloseModelEditorCommand}}"
                              Classes="ghost" Padding="12,6" VerticalAlignment="Center"/>
                    </DockPanel>
                  </Border>

                  <Grid Grid.Row="1" ColumnDefinitions="220,*">
                    <Border Grid.Column="0" Background="#0C0C14" BorderBrush="#1A1A28" BorderThickness="0,0,1,0">
                      <ScrollViewer VerticalScrollBarVisibility="Auto">
                        <StackPanel Margin="6,14" Spacing="2">
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

                  <Border Grid.Row="2" Padding="16,12" Background="#0C0C14" BorderBrush="#1A1A28" BorderThickness="0,1,0,0">
                    <Grid ColumnDefinitions="*,Auto,Auto,Auto" ColumnSpacing="10">
                      <TextBlock Text="Sheet ~90% height. Sections swap on the left — no nested expanders."
                                 FontSize="11" Foreground="#6C7086" VerticalAlignment="Center"/>
                      <Button Grid.Column="1" Content="Save" Command="{{Binding SaveModelCommand}}" Classes="success" Padding="18,9" MinWidth="120"/>
                      <Button Grid.Column="2" Content="Cancel" Command="{{Binding CancelModelCommand}}" Classes="ghost" Padding="18,9" MinWidth="110" IsVisible="{{Binding IsNewModel}}"/>
                      <Button Grid.Column="3" Content="Delete" Click="OnDeleteModelClick" Classes="danger" Padding="18,9" MinWidth="110"/>
                    </Grid>
                  </Border>
                </Grid>
              </Border>
            </Grid>
          </Grid>
"""


def main() -> None:
    text = AXAML.read_text()
    start = text.find("<!-- EDITOR")
    if start < 0:
        raise SystemExit("editor marker not found")

    # End at the models root grid close that follows the editor block,
    # identified by Delete button then a short close sequence before matrix panel.
    m = re.search(
        r"<!-- EDITOR[\s\S]*?Click=\"OnDeleteModelClick\"[\s\S]*?</Grid>\s*</Grid>\s*(?=</Grid>\s*</Panel>|\s*</Panel>)",
        text[start:],
    )
    if not m:
        # broader: from EDITOR to just before matrix panel, then trim back to models inner close
        matrix = text.find(
            'ConverterParameter=matrix}">',
            start,
        )
        if matrix < 0:
            raise SystemExit("matrix marker missing")
        # walk back to models panel content end: the pattern </Grid>\n        </Panel> before matrix
        before = text.rfind("</Panel>", start, matrix)
        # editor is inside models grid; find last </Grid> of models content before panel end
        # Use: start .. position of "        </Grid>\n\n        </Panel>" near matrix
        end_rel = text.rfind("        </Grid>", start, before)
        if end_rel < 0:
            raise SystemExit("could not find editor end")
        # include that closing grid? Editor is inside models Grid that also has list.
        # Structure:
        # <Panel models>
        #   <Grid> list + editor
        #   </Grid>
        # </Panel>
        # So editor ends before final </Grid> of models content.
        # Find Delete then find successive closes.
        d = text.find('Click="OnDeleteModelClick"', start)
        # From d, take until we've closed enough to end the HasSelectedModel outer grid.
        # Count grids from editor start.
        slice_end = None
        depth = 0
        i = start
        # find first <Grid after EDITOR for HasSelectedModel
        g0 = text.find("<Grid", start)
        i = g0
        while i < len(text):
            if text.startswith("<Grid", i):
                depth += 1
                i = text.find(">", i) + 1
                continue
            if text.startswith("</Grid>", i):
                depth -= 1
                i2 = i + len("</Grid>")
                if depth == 0:
                    slice_end = i2
                    break
                i = i2
                continue
            i += 1
        if not slice_end:
            raise SystemExit("balance scan failed")
    else:
        slice_end = start + m.end()

    # Prefer balanced scan from EDITOR's outer HasSelectedModel grid.
    # Important: do NOT match <Grid.RowDefinitions / <Grid.ColumnDefinitions.
    g0 = text.find("<Grid", start)
    while g0 != -1 and g0 + 5 < len(text) and text[g0 + 5] in ".\r\n\t ":
        # <Grid followed by space/newline is a real Grid; <Grid. is a property element.
        if text.startswith("<Grid.", g0):
            g0 = text.find("<Grid", g0 + 5)
            continue
        break
    if g0 < 0:
        raise SystemExit("editor grid start missing")

    def is_grid_open(s: str, i: int) -> bool:
        return s.startswith("<Grid", i) and not s.startswith("<Grid.", i)

    depth = 0
    i = g0
    slice_end = None
    while i < len(text):
        if is_grid_open(text, i):
            depth += 1
            i = text.find(">", i) + 1
            continue
        if text.startswith("</Grid>", i):
            depth -= 1
            i2 = i + len("</Grid>")
            if depth == 0:
                slice_end = i2
                break
            i = i2
            continue
        i += 1
    if not slice_end:
        raise SystemExit(f"could not balance editor grid from {g0}")

    new_text = text[:start] + sheet + text[slice_end:]
    AXAML.write_text(new_text)

    for tag in ["Grid", "StackPanel", "Border", "ScrollViewer", "Panel", "Window", "Expander"]:
        o = len(re.findall(rf"<{tag}\b", new_text))
        c = len(re.findall(rf"</{tag}>", new_text))
        print(f"{tag}: {o}/{c} {'OK' if o == c else 'BAD'}")
    print("size", AXAML.stat().st_size)
    print("sections", new_text.count("ModelEditorSection"))
    print("no expanders in sheet", new_text.count("<Expander") == 0 or "Expander" in new_text)


if __name__ == "__main__":
    main()
