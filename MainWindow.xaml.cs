using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace PixelAnimator
{
    // ══════════════════════════════════════════════════════
    //  LAYER DATA
    // ══════════════════════════════════════════════════════

    internal class SpriteLayer
    {
        public string FilePath { get; set; } = "";
        public string Name     { get; set; } = "";

        public BitmapSource? Sheet { get; set; }

        // Each layer has its own Image element in LayerCanvas
        public Image ImageElement { get; } = CreateImage();

        private static Image CreateImage()
        {
            var img = new Image { SnapsToDevicePixels = true };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.NearestNeighbor);
            RenderOptions.SetEdgeMode(img, EdgeMode.Aliased);
            return img;
        }

        public DateTime LastWriteTime { get; set; } = DateTime.MinValue;
    }

    // ══════════════════════════════════════════════════════
    //  MAIN WINDOW
    // ══════════════════════════════════════════════════════

    public partial class MainWindow : Window
    {
        // ── State ──────────────────────────────────────────
        private readonly List<SpriteLayer> _layers = new();
        private int _selectedIndex = -1;

        private int _frameWidth  = 64;
        private int _frameHeight = 64;
        private int _targetRow   = 0;
        private int _fps         = 12;
        private double _scale    = 4;

        private int  _currentFrame = 0;
        private int  _totalFrames  = 0;
        private bool _isPlaying    = false;

        // ── Timers ─────────────────────────────────────────
        private readonly DispatcherTimer _watchTimer = new();

        private readonly System.Diagnostics.Stopwatch _stopwatch = new();
        private double _accumulatedMs = 0;
        private double _lastRenderMs  = 0;

        private bool _suppressTextChange = false;

        // ── Constructor ────────────────────────────────────
        public MainWindow()
        {
            InitializeComponent();

            CompositionTarget.Rendering += OnRendering;

            _watchTimer.Interval = TimeSpan.FromSeconds(1);
            _watchTimer.Tick    += WatchTimer_Tick;
            _watchTimer.Start();

            AllowDrop = true;
            Drop     += MainWindow_Drop;

            UpdateControlStates();
        }

        // ══════════════════════════════════════════════════════
        //  ADD LAYER
        // ══════════════════════════════════════════════════════

        private void AddLayerBtn_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title      = "Add Layer — Sprite Sheet",
                Filter     = "Image Files|*.png;*.bmp;*.gif;*.jpg;*.jpeg;*.tiff|All Files|*.*",
                Multiselect = true
            };
            if (dlg.ShowDialog() != true) return;

            foreach (var path in dlg.FileNames)
                AddLayer(path);
        }

        private void MainWindow_Drop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            var files = (string[])e.Data.GetData(DataFormats.FileDrop)!;
            foreach (var f in files) AddLayer(f);
        }

        private void AddLayer(string path)
        {
            var layer = new SpriteLayer
            {
                FilePath      = path,
                Name          = Path.GetFileNameWithoutExtension(path),
                LastWriteTime = GetLastWrite(path)
            };

            if (!LoadSheet(layer, silent: false)) return;

            _layers.Add(layer);
            LayerCanvas.Children.Add(layer.ImageElement);
            ApplyZOrder();

            RebuildLayerListUI();
            SelectLayer(_layers.Count - 1);

            RecalcFrameCount();
            RenderAllFrames();
            UpdateControlStates();
            ShowDropHint(false);
        }

        // ══════════════════════════════════════════════════════
        //  LOAD / RELOAD SHEET
        // ══════════════════════════════════════════════════════

        private bool LoadSheet(SpriteLayer layer, bool silent)
        {
            try
            {
                byte[] bytes = File.ReadAllBytes(layer.FilePath);
                using var ms = new MemoryStream(bytes);

                var decoder = BitmapDecoder.Create(
                    ms,
                    BitmapCreateOptions.PreservePixelFormat,
                    BitmapCacheOption.OnLoad);

                layer.Sheet = decoder.Frames[0];
                return true;
            }
            catch (Exception ex)
            {
                StatusLabel.Text = $"error loading {layer.Name}: {ex.Message}";
                return false;
            }
        }

        // ══════════════════════════════════════════════════════
        //  RECALC FRAME COUNT
        // ══════════════════════════════════════════════════════

        private void RecalcFrameCount()
        {
            // Total frames = minimum cols across all layers (they must be in sync)
            _totalFrames = int.MaxValue;
            foreach (var l in _layers)
            {
                if (l.Sheet == null) continue;
                int cols = Math.Max(1, (int)(l.Sheet.PixelWidth / _frameWidth));
                if (cols < _totalFrames) _totalFrames = cols;
            }
            if (_totalFrames == int.MaxValue) _totalFrames = 0;

            if (_currentFrame >= _totalFrames && _totalFrames > 0)
                _currentFrame = _totalFrames - 1;
            if (_totalFrames == 0)
                _currentFrame = 0;

            FrameCounterLabel.Text = _totalFrames > 0
                ? $"frame {_currentFrame + 1} / {_totalFrames}"
                : "—";
        }

        // ══════════════════════════════════════════════════════
        //  RENDER
        // ══════════════════════════════════════════════════════

        private void RenderAllFrames()
        {
            double canvasW = 0, canvasH = 0;

            for (int i = 0; i < _layers.Count; i++)
            {
                var layer = _layers[i];
                RenderLayerFrame(layer);

                double w = _frameWidth  * _scale;
                double h = _frameHeight * _scale;
                if (w > canvasW) canvasW = w;
                if (h > canvasH) canvasH = h;
            }

            // Size the canvas so it centers correctly
            LayerCanvas.Width  = canvasW;
            LayerCanvas.Height = canvasH;

            FrameCounterLabel.Text = _totalFrames > 0
                ? $"frame {_currentFrame + 1} / {_totalFrames}"
                : "—";
        }

        private void RenderLayerFrame(SpriteLayer layer)
        {
            if (layer.Sheet == null || _totalFrames == 0)
            {
                layer.ImageElement.Source = null;
                return;
            }

            int x = _currentFrame * _frameWidth;
            int y = _targetRow    * _frameHeight;

            if (x + _frameWidth  > layer.Sheet.PixelWidth)  return;
            if (y + _frameHeight > layer.Sheet.PixelHeight) return;

            var cropped = new CroppedBitmap(layer.Sheet,
                new Int32Rect(x, y, _frameWidth, _frameHeight));

            layer.ImageElement.Source = cropped;
            layer.ImageElement.Width  = _frameWidth  * _scale;
            layer.ImageElement.Height = _frameHeight * _scale;

            // All layers sit at 0,0 — they composite on top of each other
            Canvas.SetLeft(layer.ImageElement, 0);
            Canvas.SetTop(layer.ImageElement,  0);
        }

        // Z-order: index 0 = bottom, last = top
        private void ApplyZOrder()
        {
            for (int i = 0; i < _layers.Count; i++)
                Panel.SetZIndex(_layers[i].ImageElement, i);
        }

        // ══════════════════════════════════════════════════════
        //  ANIMATION TIMER
        // ══════════════════════════════════════════════════════

        private void OnRendering(object? sender, EventArgs e)
        {
            if (!_isPlaying || _totalFrames == 0) return;

            double nowMs   = _stopwatch.Elapsed.TotalMilliseconds;
            double deltaMs = nowMs - _lastRenderMs;
            _lastRenderMs  = nowMs;

            if (deltaMs > 200) deltaMs = 200;

            _accumulatedMs += deltaMs;

            double msPerFrame = 1000.0 / Math.Max(1, _fps);

            if (_accumulatedMs >= msPerFrame)
            {
                _accumulatedMs -= msPerFrame;
                if (_accumulatedMs > msPerFrame) _accumulatedMs = 0;

                _currentFrame++;
                if (_currentFrame >= _totalFrames)
                {
                    if (LoopCheck.IsChecked == true)
                        _currentFrame = 0;
                    else
                    {
                        _currentFrame = _totalFrames - 1;
                        StopAnimation();
                        return;
                    }
                }
                RenderAllFrames();
            }
        }

        private void StartAnimation()
        {
            if (_totalFrames == 0) return;
            _isPlaying     = true;
            _accumulatedMs = 0;
            _stopwatch.Restart();
            _lastRenderMs = 0;
            UpdateControlStates();
        }

        private void StopAnimation()
        {
            _isPlaying = false;
            _stopwatch.Stop();
            UpdateControlStates();
        }

        // ══════════════════════════════════════════════════════
        //  FILE WATCHER
        // ══════════════════════════════════════════════════════

        private void WatchTimer_Tick(object? sender, EventArgs e)
        {
            bool anyReloaded = false;
            foreach (var layer in _layers)
            {
                if (string.IsNullOrEmpty(layer.FilePath)) continue;
                try
                {
                    var wt = GetLastWrite(layer.FilePath);
                    if (wt <= layer.LastWriteTime) continue;

                    // Aseprite may still hold a write lock — retry a few times
                    bool ok = false;
                    for (int attempt = 0; attempt < 3; attempt++)
                    {
                        try
                        {
                            if (LoadSheet(layer, silent: true))
                            {
                                ok = true;
                                break;
                            }
                        }
                        catch { }
                        System.Threading.Thread.Sleep(80);
                    }

                    if (ok)
                    {
                        layer.LastWriteTime = wt; // only stamp on success
                        anyReloaded = true;
                    }
                }
                catch { }
            }

            if (anyReloaded)
            {
                int saved = _currentFrame;
                RecalcFrameCount();
                _currentFrame = _totalFrames > 0 ? Math.Min(saved, _totalFrames - 1) : 0;
                RenderAllFrames();
                LastReloadLabel.Text = $"reloaded {DateTime.Now:HH:mm:ss}";
            }
        }

        private static DateTime GetLastWrite(string path)
        {
            try   { return File.GetLastWriteTime(path); }
            catch { return DateTime.MinValue; }
        }

        // ══════════════════════════════════════════════════════
        //  LAYER PANEL UI
        // ══════════════════════════════════════════════════════

        private void RebuildLayerListUI()
        {
            LayerListPanel.Children.Clear();

            // Display in reverse order: top layer first visually (like Photoshop)
            for (int i = _layers.Count - 1; i >= 0; i--)
            {
                int capturedIndex = i;
                var layer = _layers[i];

                bool isSelected = (i == _selectedIndex);

                var row = new Border
                {
                    Style       = isSelected
                                    ? (Style)Resources["LayerRowSelected"]
                                    : (Style)Resources["LayerRow"],
                    Cursor      = System.Windows.Input.Cursors.Hand,
                    Tag         = capturedIndex
                };

                row.MouseLeftButtonDown += (_, _) => SelectLayer(capturedIndex);

                var inner = new Grid();
                inner.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                inner.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                // Layer index badge + name
                var namePanel = new StackPanel { Orientation = Orientation.Horizontal };
                namePanel.Children.Add(new TextBlock
                {
                    Text       = $"[{i}] ",
                    Foreground = new SolidColorBrush(isSelected ? Color.FromRgb(0x89, 0xB4, 0xFA) : Color.FromRgb(0x58, 0x5B, 0x70)),
                    FontFamily = new FontFamily("Consolas"),
                    FontSize   = 10,
                    VerticalAlignment = VerticalAlignment.Center
                });
                namePanel.Children.Add(new TextBlock
                {
                    Text              = layer.Name,
                    Foreground        = new SolidColorBrush(isSelected ? Colors.White : Color.FromRgb(0xCD, 0xD6, 0xF4)),
                    FontFamily        = new FontFamily("Consolas"),
                    FontSize          = 11,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming      = TextTrimming.CharacterEllipsis,
                    MaxWidth          = 140
                });

                Grid.SetColumn(namePanel, 0);
                inner.Children.Add(namePanel);

                row.Child = inner;
                LayerListPanel.Children.Add(row);
            }
        }

        private void SelectLayer(int index)
        {
            _selectedIndex = (index >= 0 && index < _layers.Count) ? index : -1;
            RebuildLayerListUI();
            UpdateStatusForSelected();
            UpdateControlStates();
        }

        private void UpdateStatusForSelected()
        {
            if (_selectedIndex < 0 || _selectedIndex >= _layers.Count)
            {
                StatusLabel.Text = _layers.Count == 0 ? "No layers loaded" : $"{_layers.Count} layer(s)";
                return;
            }
            var l = _layers[_selectedIndex];
            StatusLabel.Text = $"[{_selectedIndex}] {l.Name}  ({_layers.Count} total)";
        }

        // ══════════════════════════════════════════════════════
        //  LAYER MANAGEMENT BUTTONS
        // ══════════════════════════════════════════════════════

        private void RemoveLayerBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedIndex < 0 || _selectedIndex >= _layers.Count) return;

            LayerCanvas.Children.Remove(_layers[_selectedIndex].ImageElement);
            _layers.RemoveAt(_selectedIndex);

            _selectedIndex = Math.Min(_selectedIndex, _layers.Count - 1);

            RebuildLayerListUI();
            RecalcFrameCount();
            RenderAllFrames();
            UpdateStatusForSelected();
            UpdateControlStates();

            if (_layers.Count == 0) ShowDropHint(true);
        }

        private void MoveUpBtn_Click(object sender, RoutedEventArgs e)
        {
            // "Up" in list = higher z-order = higher index in _layers
            if (_selectedIndex < 0 || _selectedIndex >= _layers.Count - 1) return;

            (_layers[_selectedIndex], _layers[_selectedIndex + 1])
                = (_layers[_selectedIndex + 1], _layers[_selectedIndex]);

            _selectedIndex++;
            ApplyZOrder();
            RebuildLayerListUI();
            UpdateStatusForSelected();
            UpdateControlStates();
        }

        private void MoveDownBtn_Click(object sender, RoutedEventArgs e)
        {
            // "Down" in list = lower z-order = lower index in _layers
            if (_selectedIndex <= 0) return;

            (_layers[_selectedIndex], _layers[_selectedIndex - 1])
                = (_layers[_selectedIndex - 1], _layers[_selectedIndex]);

            _selectedIndex--;
            ApplyZOrder();
            RebuildLayerListUI();
            UpdateStatusForSelected();
            UpdateControlStates();
        }

        private void ClearAllBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_layers.Count == 0) return;

            var result = MessageBox.Show(
                "Remove all layers?",
                "Clear Scene",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes) return;

            StopAnimation();
            LayerCanvas.Children.Clear();
            _layers.Clear();
            _selectedIndex = -1;
            _currentFrame  = 0;
            _totalFrames   = 0;

            LayerListPanel.Children.Clear();
            FrameCounterLabel.Text = "—";
            StatusLabel.Text       = "No layers loaded";
            LastReloadLabel.Text   = "";

            ShowDropHint(true);
            UpdateControlStates();
        }

        // ══════════════════════════════════════════════════════
        //  PLAYBACK BUTTONS
        // ══════════════════════════════════════════════════════

        private void PlayBtn_Click(object sender, RoutedEventArgs e)  => StartAnimation();
        private void PauseBtn_Click(object sender, RoutedEventArgs e) => StopAnimation();

        private void PrevFrameBtn_Click(object sender, RoutedEventArgs e)
        {
            StopAnimation();
            if (_totalFrames == 0) return;
            _currentFrame = (_currentFrame - 1 + _totalFrames) % _totalFrames;
            RenderAllFrames();
        }

        private void NextFrameBtn_Click(object sender, RoutedEventArgs e)
        {
            StopAnimation();
            if (_totalFrames == 0) return;
            _currentFrame = (_currentFrame + 1) % _totalFrames;
            RenderAllFrames();
        }

        private void UpdateControlStates()
        {
            bool hasLayers   = _layers.Count > 0;
            bool hasSelected = _selectedIndex >= 0 && _selectedIndex < _layers.Count;

            PlayBtn.IsEnabled        = hasLayers && !_isPlaying;
            PauseBtn.IsEnabled       = hasLayers &&  _isPlaying;
            PrevFrameBtn.IsEnabled   = hasLayers;
            NextFrameBtn.IsEnabled   = hasLayers;
            MoveUpBtn.IsEnabled      = hasSelected && _selectedIndex < _layers.Count - 1;
            MoveDownBtn.IsEnabled    = hasSelected && _selectedIndex > 0;
            RemoveLayerBtn.IsEnabled = hasSelected;
            ClearAllBtn.IsEnabled    = hasLayers;
        }

        // ══════════════════════════════════════════════════════
        //  INPUT HANDLERS
        // ══════════════════════════════════════════════════════

        private void SizeInput_Changed(object sender, TextChangedEventArgs e)
        {
            if (_suppressTextChange) return;
            if (int.TryParse(WidthInput?.Text,  out int w) && w > 0) _frameWidth  = w;
            if (int.TryParse(HeightInput?.Text, out int h) && h > 0) _frameHeight = h;
            RecalcFrameCount();
            RenderAllFrames();
        }

        private void ColumnInput_Changed(object sender, TextChangedEventArgs e)
        {
            if (_suppressTextChange) return;
            if (int.TryParse(ColumnInput?.Text, out int col) && col >= 0)
            {
                _targetRow    = col;
                _currentFrame = 0;
                RecalcFrameCount();
                RenderAllFrames();
            }
        }

        private void FpsInput_Changed(object sender, TextChangedEventArgs e)
        {
            if (_suppressTextChange) return;
            if (int.TryParse(FpsInput?.Text, out int fps) && fps > 0)
            {
                _fps = Math.Clamp(fps, 1, 120);
                if (FpsSlider != null)
                {
                    _suppressTextChange = true;
                    FpsSlider.Value     = Math.Min(_fps, 60);
                    _suppressTextChange = false;
                }
            }
        }

        private void FpsSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressTextChange) return;
            _fps = (int)FpsSlider.Value;
            _suppressTextChange = true;
            if (FpsInput != null) FpsInput.Text = _fps.ToString();
            _suppressTextChange = false;
        }

        private void ScaleSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            _scale = ScaleSlider.Value;
            if (ScaleLabel != null)
                ScaleLabel.Text = $"{_scale:0}x";
            RenderAllFrames();
        }

        // ══════════════════════════════════════════════════════
        //  HELPERS
        // ══════════════════════════════════════════════════════

        private void ShowDropHint(bool show)
        {
            DropHint.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        }

        protected override void OnClosed(EventArgs e)
        {
            CompositionTarget.Rendering -= OnRendering;
            _watchTimer.Stop();
            base.OnClosed(e);
        }
    }
}
