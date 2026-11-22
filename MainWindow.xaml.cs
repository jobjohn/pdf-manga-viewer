using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI.Core;
using WinRT.Interop;

namespace PdfMangaViewer;

public sealed partial class MainWindow : Window
{
    /// <summary>キャッシュに保持するページ数の上限。</summary>
    private const int MaxCachedPages = 12;

    /// <summary>レンダリングする画像の長辺の上限（px）。メモリ使用量の抑制用。</summary>
    private const double MaxRenderEdge = 2600;

    /// <summary>最近開いたファイルの保存上限。</summary>
    private const int MaxRecentBooks = 30;

    /// <summary>連続でページ送りするときの最短間隔。</summary>
    private static readonly TimeSpan MinStepInterval = TimeSpan.FromMilliseconds(80);

    private static readonly string SettingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PdfMangaViewer");

    private static readonly string SettingsPath = Path.Combine(SettingsDirectory, "settings.json");

    private sealed record CachedPage(BitmapImage Image, int PixelWidth, int PixelHeight);

    /// <summary>見開き（2ページ表示）1組分のレイアウト。</summary>
    private readonly record struct SpreadLayout(
        PixelSize FirstRenderSize,
        PixelSize SecondRenderSize,
        double DisplayHeight,
        double FirstDisplayWidth,
        double SecondDisplayWidth);

    private readonly Dictionary<uint, CachedPage> _cache = new();
    private readonly LinkedList<uint> _lru = new();
    private readonly SemaphoreSlim _renderGate = new(1, 1);

    private readonly DispatcherTimer _hideControlsTimer = new();
    private readonly DispatcherTimer _resizeTimer = new();
    private readonly DispatcherTimer _saveProgressTimer = new();
    private readonly DispatcherTimer _hideNextVolumeTimer = new();

    private readonly ObservableCollection<ThumbnailItem> _thumbnailItems = new();
    private readonly Queue<ThumbnailItem> _thumbnailQueue = new();
    private bool _thumbnailWorkerRunning;
    private int _thumbnailGeneration;

    private List<RecentBook> _recentBooks = new();
    private RectInt32? _lastNormalBounds;
    private bool _windowMaximized;

    private IMangaDocument? _document;
    private uint _pageIndex;
    private bool _rightToLeft = true;
    private SpreadMode _spreadMode = SpreadMode.Off;
    private int _spreadOffset;
    private bool _autoSingleLandscape = true;
    private bool _reverseKeys = true;
    private bool _showPageNumber;
    private bool _clickNavigation;
    private int _rotationDegrees;

    private bool _controlsVisible;
    private bool _isFullScreen;
    private bool _dialogOpen;
    private bool _flyoutOpen;
    private string? _pendingOpenPath;
    private string? _pendingNextVolumePath;

    private int _documentVersion;
    private int _renderVersion;
    private int _prefetchGeneration;
    private DateTime _lastWheelTurn = DateTime.MinValue;

    private bool _isStepping;
    private int _queuedStep;
    private int _travelDirection = 1;
    private DateTime _lastStepTime = DateTime.MinValue;
    private bool _isSeeking;

    private double _zoomScale = 1.0;
    private double _panX;
    private double _panY;
    private bool _isPanning;
    private Point _panStartPointer;
    private double _panStartX;
    private double _panStartY;

    public MainWindow()
    {
        InitializeComponent();

        LoadSettings();
        ConfigureInitialWindow();
        ConfigureTitleBar();
        RegisterExtraAccelerators();

        ThumbnailGridView.ItemsSource = _thumbnailItems;

        _hideControlsTimer.Interval = TimeSpan.FromSeconds(1.0);
        _hideControlsTimer.Tick += (_, _) => HideControls();

        _resizeTimer.Interval = TimeSpan.FromMilliseconds(250);
        _resizeTimer.Tick += (_, _) => OnResizeSettled();

        _saveProgressTimer.Interval = TimeSpan.FromSeconds(1.5);
        _saveProgressTimer.Tick += (_, _) =>
        {
            _saveProgressTimer.Stop();
            SaveSettings();
        };

        _hideNextVolumeTimer.Interval = TimeSpan.FromSeconds(6.0);
        _hideNextVolumeTimer.Tick += (_, _) => HideNextVolumeBanner();

        AppWindow.Changed += AppWindow_Changed;
        Closed += MainWindow_Closed;

        UpdateSpreadUi();
        UpdateReadingDirectionUi();
        UpdateNavigationUi();
        UpdatePageNumberUi();
        UpdateClickNavigationUi();
        RefreshRecentFilesUi();
    }

    private void RegisterExtraAccelerators()
    {
        // OEM Plus (= / +) と OEM Minus (-) の Ctrl 組み合わせをズームに割り当て
        var oemPlus = new KeyboardAccelerator { Key = (VirtualKey)187, Modifiers = VirtualKeyModifiers.Control };
        oemPlus.Invoked += ZoomInKey_Invoked;
        RootHost.KeyboardAccelerators.Add(oemPlus);

        var oemMinus = new KeyboardAccelerator { Key = (VirtualKey)189, Modifiers = VirtualKeyModifiers.Control };
        oemMinus.Invoked += ZoomOutKey_Invoked;
        RootHost.KeyboardAccelerators.Add(oemMinus);
    }

    // ------------------------------------------------------------------
    // 起動・ファイルを開く
    // ------------------------------------------------------------------

    /// <summary>起動直後に開くファイル／フォルダを指定する（コマンドライン引数用）。</summary>
    public void ScheduleOpen(string path)
    {
        if (RootGrid.IsLoaded)
        {
            _ = OpenPathAsync(path);
        }
        else
        {
            _pendingOpenPath = path;
        }
    }

    private async void RootGrid_Loaded(object sender, RoutedEventArgs e)
    {
        RootHost.Focus(FocusState.Programmatic);

        if (_pendingOpenPath is { } path)
        {
            _pendingOpenPath = null;
            await OpenPathAsync(path);
        }
    }

    private async Task OpenPathAsync(string path)
    {
        if (Directory.Exists(path))
        {
            await OpenFolderPathAsync(path);
            return;
        }

        if (!File.Exists(path))
        {
            await ShowErrorAsync("ファイルが見つかりません", path);
            return;
        }

        if (MangaDocumentFactory.IsImageExtension(path))
        {
            string? parent = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(parent))
            {
                await OpenFolderPathAsync(parent, path);
            }

            return;
        }

        if (MangaDocumentFactory.IsArchiveExtension(path))
        {
            await OpenArchiveAsync(path);
            return;
        }

        if (MangaDocumentFactory.IsPdfExtension(path))
        {
            try
            {
                StorageFile file = await StorageFile.GetFileFromPathAsync(path);
                await OpenPdfFileAsync(file);
            }
            catch (Exception ex)
            {
                await ShowErrorAsync("ファイルを開けません", ex.Message);
            }

            return;
        }

        await ShowErrorAsync("対応していない形式です", "PDF・ZIP・CBZ・画像ファイルまたはフォルダを選択してください。");
    }

    private async Task OpenPdfFileAsync(StorageFile file)
    {
        int version = ++_documentVersion;
        _renderVersion++;
        _prefetchGeneration++;
        HideNextVolumeBanner();
        CloseThumbnails();
        SetBusy(true);

        try
        {
            PdfMangaDocument? document = await LoadPdfWithPasswordPromptAsync(file, version);
            if (document is null || version != _documentVersion)
            {
                return;
            }

            await ActivateDocumentAsync(document, initialPageOverride: null);
        }
        catch (Exception ex)
        {
            if (version != _documentVersion)
            {
                return;
            }

            await ShowErrorAsync("PDFを開けませんでした", $"{file.Name}\n\n{ex.Message}");
        }
        finally
        {
            if (version == _documentVersion)
            {
                SetBusy(false);
            }
        }
    }

    private async Task<PdfMangaDocument?> LoadPdfWithPasswordPromptAsync(StorageFile file, int version)
    {
        try
        {
            return await PdfMangaDocument.OpenAsync(file);
        }
        catch (Exception ex) when (IsPasswordRequiredException(ex))
        {
            // パスワード保護された PDF の場合はダイアログで入力を求める
            bool isRetry = false;
            while (version == _documentVersion)
            {
                SetBusy(false);
                string? password = await PromptForPasswordAsync(file.Name, isRetry);
                if (password is null || version != _documentVersion)
                {
                    return null;
                }

                SetBusy(true);
                try
                {
                    return await PdfMangaDocument.OpenAsync(file, password);
                }
                catch (Exception retryEx) when (IsPasswordRequiredException(retryEx))
                {
                    isRetry = true;
                }
            }

            return null;
        }
    }

    private static bool IsPasswordRequiredException(Exception ex)
    {
        // ERROR_INVALID_PASSWORD (0x80070056) / ERROR_WRONG_PASSWORD (0x8007052B)
        return ex.HResult == unchecked((int)0x80070056)
            || ex.HResult == unchecked((int)0x8007052B)
            || ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("パスワード", StringComparison.Ordinal);
    }

    private async Task<string?> PromptForPasswordAsync(string fileName, bool isRetry)
    {
        if (_dialogOpen || RootGrid.XamlRoot is null)
        {
            return null;
        }

        _dialogOpen = true;
        try
        {
            var passwordBox = new PasswordBox
            {
                PlaceholderText = "パスワードを入力",
            };

            var panel = new StackPanel { Spacing = 8 };
            panel.Children.Add(new TextBlock
            {
                Text = isRetry
                    ? $"{fileName}\nパスワードが正しくありません。もう一度入力してください。"
                    : $"{fileName}\nこの PDF はパスワードで保護されています。",
                TextWrapping = TextWrapping.Wrap,
            });
            panel.Children.Add(passwordBox);

            var dialog = new ContentDialog
            {
                Title = "パスワードの入力",
                Content = panel,
                PrimaryButtonText = "開く",
                CloseButtonText = "キャンセル",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = RootGrid.XamlRoot,
            };

            bool submittedByEnter = false;
            passwordBox.KeyDown += (_, args) =>
            {
                if (args.Key == VirtualKey.Enter)
                {
                    args.Handled = true;
                    submittedByEnter = true;
                    dialog.Hide();
                }
            };

            ContentDialogResult result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary || submittedByEnter)
            {
                return passwordBox.Password;
            }

            return null;
        }
        finally
        {
            _dialogOpen = false;
            RootHost.Focus(FocusState.Programmatic);
        }
    }

    private async Task OpenArchiveAsync(string path)
    {
        int version = ++_documentVersion;
        _renderVersion++;
        _prefetchGeneration++;
        HideNextVolumeBanner();
        CloseThumbnails();
        SetBusy(true);

        try
        {
            ZipMangaDocument document = await Task.Run(() => ZipMangaDocument.Open(path));
            if (version != _documentVersion)
            {
                document.Dispose();
                return;
            }

            await ActivateDocumentAsync(document, initialPageOverride: null);
        }
        catch (Exception ex)
        {
            if (version != _documentVersion)
            {
                return;
            }

            await ShowErrorAsync("アーカイブを開けませんでした", $"{Path.GetFileName(path)}\n\n{ex.Message}");
        }
        finally
        {
            if (version == _documentVersion)
            {
                SetBusy(false);
            }
        }
    }

    private async Task OpenFolderPathAsync(string folderPath, string? initialImagePath = null)
    {
        int version = ++_documentVersion;
        _renderVersion++;
        _prefetchGeneration++;
        HideNextVolumeBanner();
        CloseThumbnails();
        SetBusy(true);

        try
        {
            uint? initialPage = null;
            FolderMangaDocument document = await Task.Run(() =>
                FolderMangaDocument.Open(folderPath, out initialPage, initialImagePath));

            if (version != _documentVersion)
            {
                document.Dispose();
                return;
            }

            await ActivateDocumentAsync(document, initialPage);
        }
        catch (Exception ex)
        {
            if (version != _documentVersion)
            {
                return;
            }

            await ShowErrorAsync("フォルダを開けませんでした", $"{folderPath}\n\n{ex.Message}");
        }
        finally
        {
            if (version == _documentVersion)
            {
                SetBusy(false);
            }
        }
    }

    private async Task ActivateDocumentAsync(IMangaDocument document, uint? initialPageOverride)
    {
        // 前の本の読書位置を保存して解放
        if (_document is not null)
        {
            UpdateCurrentBookProgress();
            SaveSettings();
            _document.Dispose();
        }

        _document = document;
        _cache.Clear();
        _lru.Clear();
        _travelDirection = 1;
        ResetZoom(updateBadge: true);

        RecentBook? saved = FindRecentBook(document.SourcePath);
        uint startPage = 0;
        _spreadOffset = 0;
        _rotationDegrees = 0;

        if (saved is not null)
        {
            if (saved.PageIndex < document.PageCount)
            {
                startPage = saved.PageIndex;
            }

            _spreadOffset = saved.SpreadOffset is 1 ? 1 : 0;
            _rotationDegrees = NormalizeRotation(saved.RotationDegrees);
        }

        if (initialPageOverride is { } overridePage && overridePage < document.PageCount)
        {
            startPage = overridePage;
        }

        _pageIndex = startPage;
        ApplyRotationTransform();

        PageImage.Source = null;
        SpreadLeftImage.Source = null;
        SpreadRightImage.Source = null;
        SpreadPanel.Visibility = Visibility.Collapsed;
        PageImage.Visibility = Visibility.Visible;
        EmptyState.Visibility = Visibility.Collapsed;
        FileNameText.Text = document.FileName;

        UpdateCurrentBookProgress();
        SaveSettings();
        RefreshRecentFilesUi();

        await ShowViewAsync(startPage);
        ShowControls();
    }

    private async Task PickAndOpenFileAsync()
    {
        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            ViewMode = PickerViewMode.Thumbnail,
        };
        picker.FileTypeFilter.Add(".pdf");
        picker.FileTypeFilter.Add(".zip");
        picker.FileTypeFilter.Add(".cbz");

        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));

        StorageFile? file = await picker.PickSingleFileAsync();
        if (file is not null)
        {
            await OpenPathAsync(file.Path);
        }
    }

    private async Task PickAndOpenFolderAsync()
    {
        var picker = new FolderPicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
        };
        picker.FileTypeFilter.Add("*");

        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));

        StorageFolder? folder = await picker.PickSingleFolderAsync();
        if (folder is not null)
        {
            await OpenFolderPathAsync(folder.Path);
        }
    }

    private async Task OpenSiblingVolumeAsync(int direction)
    {
        if (_document is null)
        {
            return;
        }

        string? sibling = MangaDocumentFactory.FindSiblingBook(_document.SourcePath, direction);
        if (sibling is null)
        {
            return;
        }

        await OpenPathAsync(sibling);
    }

    // ------------------------------------------------------------------
    // ページ表示
    // ------------------------------------------------------------------

    /// <summary>現在の表示モード（単ページ／見開き）に応じてページを表示する。</summary>
    private async Task ShowViewAsync(uint start, bool normalize = false)
    {
        if (_document is null)
        {
            return;
        }

        if (start >= _document.PageCount)
        {
            start = _document.PageCount - 1;
        }

        if (normalize)
        {
            start = await NormalizeToViewAsync(start);
        }

        _pageIndex = start;
        _prefetchGeneration++;
        HideNextVolumeBanner();
        ResetZoom(updateBadge: true);

        bool twoPage = await IsTwoPageViewAsync(start);
        UpdatePageLabel(twoPage);
        ScheduleSaveProgress();

        PixelSize viewport = GetEffectiveViewportPixels();
        int version = ++_renderVersion;

        try
        {
            if (!twoPage)
            {
                PixelSize desired = await GetRenderSizeAsync(start, viewport);
                CachedPage? page = await GetPageForDisplayAsync(start, desired, version);
                if (page is null)
                {
                    return;
                }

                ApplySinglePageLayout(desired);
                PageImage.Source = page.Image;
                PageImage.Visibility = Visibility.Visible;
                SpreadPanel.Visibility = Visibility.Collapsed;
                StartPrefetch(start, viewport);
                return;
            }

            SpreadLayout layout = await GetSpreadLayoutAsync(start, viewport);

            CachedPage? first = await GetPageForDisplayAsync(start, layout.FirstRenderSize, version);
            if (first is null)
            {
                return;
            }

            CachedPage? second = await GetPageForDisplayAsync(start + 1, layout.SecondRenderSize, version);
            if (second is null)
            {
                return;
            }

            // 右綴じでは若いページを右、後のページを左に置く
            SpreadLeftImage.Width = _rightToLeft ? layout.SecondDisplayWidth : layout.FirstDisplayWidth;
            SpreadLeftImage.Height = layout.DisplayHeight;
            SpreadLeftImage.Source = _rightToLeft ? second.Image : first.Image;

            SpreadRightImage.Width = _rightToLeft ? layout.FirstDisplayWidth : layout.SecondDisplayWidth;
            SpreadRightImage.Height = layout.DisplayHeight;
            SpreadRightImage.Source = _rightToLeft ? first.Image : second.Image;

            SpreadPanel.Visibility = Visibility.Visible;
            PageImage.Visibility = Visibility.Collapsed;
            StartPrefetch(start, viewport);
        }
        catch (Exception ex)
        {
            if (version != _renderVersion)
            {
                return;
            }

            SetBusy(false);
            await ShowErrorAsync("ページを表示できません", $"ページ {start + 1}\n\n{ex.Message}");
        }
    }

    private void ApplySinglePageLayout(PixelSize renderSize)
    {
        if (_rotationDegrees % 180 != 0)
        {
            double raster = RootGrid.XamlRoot?.RasterizationScale ?? 1.0;
            if (raster <= 0)
            {
                raster = 1.0;
            }

            PageImage.Width = renderSize.Width / raster;
            PageImage.Height = renderSize.Height / raster;
        }
        else
        {
            PageImage.Width = double.NaN;
            PageImage.Height = double.NaN;
        }
    }

    private async Task<CachedPage?> GetPageForDisplayAsync(uint index, PixelSize desired, int version)
    {
        if (TryGetCached(index, desired, out CachedPage? cached))
        {
            return cached;
        }

        CachedPage? rendered = await RenderPageAsync(index, desired, () => version != _renderVersion);
        if (rendered is null || version != _renderVersion)
        {
            return null;
        }

        return rendered;
    }

    private async Task<CachedPage?> RenderPageAsync(uint index, PixelSize desired, Func<bool> isStale)
    {
        await _renderGate.WaitAsync();
        try
        {
            if (isStale())
            {
                return null;
            }

            if (TryGetCached(index, desired, out CachedPage? cached))
            {
                return cached;
            }

            IMangaDocument? document = _document;
            if (document is null)
            {
                throw new InvalidOperationException("ファイルが読み込まれていません。");
            }

            BitmapImage bitmap = await document.RenderPageAsync(index, desired);
            var rendered = new CachedPage(bitmap, desired.Width, desired.Height);

            if (ReferenceEquals(_document, document))
            {
                CachePage(index, rendered);
            }

            return rendered;
        }
        finally
        {
            _renderGate.Release();
        }
    }

    /// <summary>現在のウィンドウサイズ（物理ピクセル）を返す。90°/270°回転時は縦横を入れ替える。</summary>
    private PixelSize GetEffectiveViewportPixels()
    {
        double scale = RootGrid.XamlRoot?.RasterizationScale ?? 1.0;
        int width = (int)Math.Round(RootGrid.ActualWidth * scale);
        int height = (int)Math.Round(RootGrid.ActualHeight * scale);

        if (width < 64 || height < 64)
        {
            SizeInt32 client = AppWindow.ClientSize;
            width = Math.Max(1, client.Width);
            height = Math.Max(1, client.Height);
        }

        return _rotationDegrees % 180 != 0
            ? new PixelSize(height, width)
            : new PixelSize(width, height);
    }

    private async Task<PageSize> GetPageSizeAsync(uint index)
    {
        if (_document is null || index >= _document.PageCount)
        {
            return new PageSize(1, 1);
        }

        return await _document.GetPageSizeAsync(index);
    }

    private async Task<PixelSize> GetRenderSizeAsync(uint index, PixelSize viewport)
    {
        PageSize page = await GetPageSizeAsync(index);

        double scale = Math.Min(viewport.Width / page.Width, viewport.Height / page.Height);
        if (double.IsNaN(scale) || double.IsInfinity(scale) || scale <= 0)
        {
            scale = 1.0;
        }

        double longEdge = Math.Max(page.Width, page.Height) * scale;
        if (longEdge > MaxRenderEdge)
        {
            scale *= MaxRenderEdge / longEdge;
        }

        return new PixelSize(
            (int)Math.Max(1, Math.Round(page.Width * scale)),
            (int)Math.Max(1, Math.Round(page.Height * scale)));
    }

    private static PixelSize ScaleToRenderSize(PageSize page, double targetHeightPx)
    {
        double height = Math.Max(1.0, targetHeightPx);
        double width = height * (page.Width / page.Height);

        double longEdge = Math.Max(width, height);
        if (longEdge > MaxRenderEdge)
        {
            double factor = MaxRenderEdge / longEdge;
            width *= factor;
            height *= factor;
        }

        return new PixelSize(
            (int)Math.Max(1, Math.Round(width)),
            (int)Math.Max(1, Math.Round(height)));
    }

    private async Task<SpreadLayout> GetSpreadLayoutAsync(uint start, PixelSize viewport)
    {
        PageSize first = await GetPageSizeAsync(start);
        PageSize second = await GetPageSizeAsync(start + 1);

        double firstRatio = first.Width / first.Height;
        double secondRatio = second.Width / second.Height;

        double displayHeightPx = Math.Min(
            viewport.Height,
            viewport.Width / (firstRatio + secondRatio));

        if (double.IsNaN(displayHeightPx) || double.IsInfinity(displayHeightPx) || displayHeightPx <= 0)
        {
            displayHeightPx = viewport.Height;
        }

        double raster = RootGrid.XamlRoot?.RasterizationScale ?? 1.0;
        if (raster <= 0)
        {
            raster = 1.0;
        }

        return new SpreadLayout(
            ScaleToRenderSize(first, displayHeightPx),
            ScaleToRenderSize(second, displayHeightPx),
            displayHeightPx / raster,
            displayHeightPx * firstRatio / raster,
            displayHeightPx * secondRatio / raster);
    }

    /// <summary>表紙などのルールにより単独表示が強制されるページか。</summary>
    private bool IsForcedSingle(uint index)
    {
        return _spreadMode switch
        {
            SpreadMode.Off => true,
            SpreadMode.CoverSingle => _spreadOffset == 0 && index == 0,
            SpreadMode.PairFromStart => _spreadOffset == 1 && index == 0,
            SpreadMode.FirstTwoSingle => (_spreadOffset == 0 && index <= 1) || (_spreadOffset == 1 && index == 0),
            _ => true,
        };
    }

    private async Task<bool> IsSingleOnlyPageAsync(uint index)
    {
        if (IsForcedSingle(index))
        {
            return true;
        }

        if (_autoSingleLandscape)
        {
            PageSize size = await GetPageSizeAsync(index);
            if (size.IsLandscape)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>指定開始ページが2ページ見開きになるかを返す。</summary>
    private async Task<bool> IsTwoPageViewAsync(uint start)
    {
        if (_spreadMode == SpreadMode.Off ||
            _document is null ||
            start + 1 >= _document.PageCount)
        {
            return false;
        }

        if (await IsSingleOnlyPageAsync(start))
        {
            return false;
        }

        if (await IsSingleOnlyPageAsync(start + 1))
        {
            return false;
        }

        return true;
    }

    /// <summary>ジャンプ先のページを見開きモードの境界に揃える。</summary>
    private async Task<uint> NormalizeToViewAsync(uint index)
    {
        if (_document is null)
        {
            return 0;
        }

        if (index >= _document.PageCount)
        {
            index = _document.PageCount - 1;
        }

        if (_spreadMode == SpreadMode.Off || await IsSingleOnlyPageAsync(index))
        {
            return index;
        }

        bool expectsOddStart = _spreadMode switch
        {
            SpreadMode.CoverSingle => _spreadOffset == 0,
            SpreadMode.PairFromStart => _spreadOffset == 1,
            SpreadMode.FirstTwoSingle => _spreadOffset == 1,
            _ => false,
        };

        bool isOdd = index % 2 == 1;
        if (isOdd == expectsOddStart || index == 0)
        {
            return index;
        }

        uint candidate = index - 1;
        return await IsSingleOnlyPageAsync(candidate) ? index : candidate;
    }

    private async Task<uint> GetNextViewStartAsync(uint start)
    {
        bool twoPage = await IsTwoPageViewAsync(start);
        return twoPage ? start + 2 : start + 1;
    }

    private async Task<uint> GetPreviousViewStartAsync(uint start)
    {
        if (start == 0)
        {
            return 0;
        }

        if (_spreadMode == SpreadMode.Off)
        {
            return start - 1;
        }

        uint prev1 = start - 1;
        if (await IsSingleOnlyPageAsync(prev1) || prev1 == 0)
        {
            return prev1;
        }

        uint prev2 = start - 2;
        if (await IsSingleOnlyPageAsync(prev2))
        {
            return prev1;
        }

        return prev2;
    }

    private async Task<uint> GetAdjacentViewStartAsync(uint start, int direction)
    {
        if (_document is null)
        {
            return start;
        }

        if (direction > 0)
        {
            uint next = await GetNextViewStartAsync(start);
            return next < _document.PageCount ? next : start;
        }

        return await GetPreviousViewStartAsync(start);
    }

    private async Task<PixelSize> GetPageDesiredSizeAsync(uint viewStart, uint pageIndex, PixelSize viewport)
    {
        if (!await IsTwoPageViewAsync(viewStart))
        {
            return await GetRenderSizeAsync(pageIndex, viewport);
        }

        SpreadLayout layout = await GetSpreadLayoutAsync(viewStart, viewport);
        return pageIndex == viewStart ? layout.FirstRenderSize : layout.SecondRenderSize;
    }

    // ------------------------------------------------------------------
    // キャッシュ・先読み
    // ------------------------------------------------------------------

    private bool TryGetCached(uint index, PixelSize desired, [NotNullWhen(true)] out CachedPage? page)
    {
        if (_cache.TryGetValue(index, out CachedPage? cached) &&
            cached.PixelWidth >= desired.Width * 0.92 &&
            cached.PixelHeight >= desired.Height * 0.92)
        {
            Touch(index);
            page = cached;
            return true;
        }

        if (_cache.Remove(index))
        {
            _lru.Remove(index);
        }

        page = null!;
        return false;
    }

    private void Touch(uint index)
    {
        if (_lru.Remove(index))
        {
            _lru.AddLast(index);
        }
    }

    private void CachePage(uint index, CachedPage page)
    {
        if (_cache.Remove(index))
        {
            _lru.Remove(index);
        }

        _cache[index] = page;
        _lru.AddLast(index);

        while (_cache.Count > MaxCachedPages && _lru.First is { } oldest)
        {
            _lru.RemoveFirst();
            _cache.Remove(oldest.Value);
        }
    }

    private void StartPrefetch(uint viewStart, PixelSize viewport)
    {
        int generation = ++_prefetchGeneration;
        _ = PrefetchAsync(viewStart, viewport, generation);
    }

    private async Task PrefetchAsync(uint viewStart, PixelSize viewport, int generation)
    {
        try
        {
            await Task.Delay(30);
            if (generation != _prefetchGeneration || _document is null)
            {
                return;
            }

            int direction = _travelDirection;
            var targets = new List<(uint ViewStart, uint PageIndex)>(6);

            uint ahead = await GetAdjacentViewStartAsync(viewStart, direction);
            if (ahead != viewStart)
            {
                await AddViewPagesAsync(targets, ahead);

                uint ahead2 = await GetAdjacentViewStartAsync(ahead, direction);
                if (ahead2 != ahead)
                {
                    await AddViewPagesAsync(targets, ahead2);
                }
            }

            uint behind = await GetAdjacentViewStartAsync(viewStart, -direction);
            if (behind != viewStart)
            {
                await AddViewPagesAsync(targets, behind);
            }

            foreach ((uint targetViewStart, uint pageIndex) in targets)
            {
                if (generation != _prefetchGeneration || _document is null)
                {
                    return;
                }

                if (pageIndex >= _document.PageCount)
                {
                    continue;
                }

                PixelSize desired = await GetPageDesiredSizeAsync(targetViewStart, pageIndex, viewport);
                if (TryGetCached(pageIndex, desired, out _))
                {
                    continue;
                }

                CachedPage? rendered = await RenderPageAsync(
                    pageIndex,
                    desired,
                    () => generation != _prefetchGeneration);

                if (rendered is null || generation != _prefetchGeneration)
                {
                    return;
                }
            }
        }
        catch
        {
            // 先読みは失敗しても無視する
        }
    }

    private async Task AddViewPagesAsync(List<(uint ViewStart, uint PageIndex)> list, uint start)
    {
        if (_document is null || start >= _document.PageCount)
        {
            return;
        }

        if (list.All(x => x.PageIndex != start))
        {
            list.Add((start, start));
        }

        if (await IsTwoPageViewAsync(start) && list.All(x => x.PageIndex != start + 1))
        {
            list.Add((start, start + 1));
        }
    }

    // ------------------------------------------------------------------
    // ナビゲーション
    // ------------------------------------------------------------------

    private void GoNext() => RequestStep(1);

    private void GoPrevious() => RequestStep(-1);

    private void RequestStep(int direction)
    {
        if (_document is null || IsThumbnailOverlayOpen)
        {
            return;
        }

        _travelDirection = direction;

        if (_isStepping)
        {
            _queuedStep = direction;
            return;
        }

        _ = StepAsync(direction);
    }

    private async Task StepAsync(int direction)
    {
        _isStepping = true;
        try
        {
            while (_document is not null)
            {
                TimeSpan sinceLast = DateTime.UtcNow - _lastStepTime;
                if (sinceLast < MinStepInterval)
                {
                    await Task.Delay(MinStepInterval - sinceLast);
                }

                uint target = await GetAdjacentViewStartAsync(_pageIndex, direction);
                if (target == _pageIndex)
                {
                    if (direction > 0)
                    {
                        TryShowNextVolumeBanner();
                    }

                    break;
                }

                _lastStepTime = DateTime.UtcNow;
                await ShowViewAsync(target);

                if (_queuedStep == 0)
                {
                    break;
                }

                direction = _queuedStep;
                _queuedStep = 0;
            }
        }
        finally
        {
            _isStepping = false;
            _queuedStep = 0;
        }
    }

    private void TryShowNextVolumeBanner()
    {
        if (_document is null)
        {
            return;
        }

        string? nextVolume = MangaDocumentFactory.FindSiblingBook(_document.SourcePath, 1);
        if (nextVolume is null)
        {
            return;
        }

        _pendingNextVolumePath = nextVolume;
        NextVolumeText.Text = $"次の巻があります: {Path.GetFileName(nextVolume)}";
        NextVolumeBanner.Visibility = Visibility.Visible;

        _hideNextVolumeTimer.Stop();
        _hideNextVolumeTimer.Start();
    }

    private void HideNextVolumeBanner()
    {
        _hideNextVolumeTimer.Stop();
        _pendingNextVolumePath = null;
        NextVolumeBanner.Visibility = Visibility.Collapsed;
    }

    private async void OpenPendingNextVolume_Click(object sender, RoutedEventArgs e)
    {
        string? path = _pendingNextVolumePath;
        HideNextVolumeBanner();
        if (path is not null)
        {
            await OpenPathAsync(path);
        }
    }

    private void DismissNextVolumeBanner_Click(object sender, RoutedEventArgs e)
    {
        HideNextVolumeBanner();
        RootHost.Focus(FocusState.Programmatic);
    }

    private void JumpTo(uint index)
    {
        if (_document is null)
        {
            return;
        }

        _ = ShowViewAsync(index, normalize: true);
    }

    private void UpdatePageLabel(bool isTwoPage)
    {
        if (_document is null)
        {
            PageLabel.Text = "0 / 0";
            CornerPageNumber.Text = string.Empty;
            SetProgress(0);
            Title = "PDF Manga Viewer";
            return;
        }

        uint end = isTwoPage ? _pageIndex + 1 : _pageIndex;
        uint first = Math.Min(_pageIndex + 1, _document.PageCount);
        uint last = Math.Min(end + 1, _document.PageCount);
        string range = first == last ? $"{first}" : $"{first}-{last}";

        PageLabel.Text = $"{range} / {_document.PageCount}";
        CornerPageNumber.Text = PageLabel.Text;
        UpdatePageNumberUi();
        SetProgress((double)last / _document.PageCount);

        Title = $"{_document.DisplayName} ({range}/{_document.PageCount}) - PDF Manga Viewer";
    }

    private void SetProgress(double fraction)
    {
        ReadingProgress.Value = fraction;
        VerticalProgressDoneRow.Height = new GridLength(fraction, GridUnitType.Star);
        VerticalProgressRestRow.Height = new GridLength(1.0 - fraction, GridUnitType.Star);
    }

    private void UpdateNavigationUi()
    {
        ReverseKeysMenuItem.IsChecked = _reverseKeys;
        ToolTipService.SetToolTip(PrevButton, _reverseKeys ? "次のページ (←)" : "前のページ (←)");
        ToolTipService.SetToolTip(NextButton, _reverseKeys ? "前のページ (→)" : "次のページ (→)");
    }

    // ------------------------------------------------------------------
    // ズーム・パン・回転
    // ------------------------------------------------------------------

    private void AdjustZoom(double factor, Point? focusPoint = null)
    {
        if (_document is null || IsThumbnailOverlayOpen)
        {
            return;
        }

        double oldScale = _zoomScale;
        double newScale = Math.Clamp(oldScale * factor, 1.0, 4.0);
        if (Math.Abs(newScale - 1.0) < 0.03)
        {
            ResetZoom(updateBadge: true);
            return;
        }

        if (focusPoint is { } pt && RootGrid.ActualWidth > 0 && RootGrid.ActualHeight > 0)
        {
            double cx = RootGrid.ActualWidth / 2.0;
            double cy = RootGrid.ActualHeight / 2.0;
            double dx = pt.X - cx;
            double dy = pt.Y - cy;
            double ratio = newScale / oldScale;
            _panX = _panX * ratio - dx * (ratio - 1.0);
            _panY = _panY * ratio - dy * (ratio - 1.0);
        }

        _zoomScale = newScale;
        ClampPan();
        ApplyZoomTransform();
    }

    private void ResetZoom(bool updateBadge)
    {
        _zoomScale = 1.0;
        _panX = 0;
        _panY = 0;
        _isPanning = false;
        ApplyZoomTransform(updateBadge);
    }

    private void ClampPan()
    {
        double maxPanX = Math.Max(0, RootGrid.ActualWidth * (_zoomScale - 1.0) / 2.0);
        double maxPanY = Math.Max(0, RootGrid.ActualHeight * (_zoomScale - 1.0) / 2.0);
        _panX = Math.Clamp(_panX, -maxPanX, maxPanX);
        _panY = Math.Clamp(_panY, -maxPanY, maxPanY);
    }

    private void ApplyZoomTransform(bool updateBadge = true)
    {
        ZoomTransform.ScaleX = _zoomScale;
        ZoomTransform.ScaleY = _zoomScale;
        ZoomTransform.TranslateX = _panX;
        ZoomTransform.TranslateY = _panY;

        if (updateBadge)
        {
            if (_zoomScale > 1.01)
            {
                ZoomBadgeText.Text = $"{Math.Round(_zoomScale * 100):0}%";
                ZoomBadge.Visibility = Visibility.Visible;
            }
            else
            {
                ZoomBadge.Visibility = Visibility.Collapsed;
            }
        }
    }

    private void RotateBy(int deltaDegrees)
    {
        if (_document is null || IsThumbnailOverlayOpen)
        {
            return;
        }

        _rotationDegrees = NormalizeRotation(_rotationDegrees + deltaDegrees);
        ApplyRotationTransform();
        ScheduleSaveProgress();
        _ = ShowViewAsync(_pageIndex);
    }

    private static int NormalizeRotation(int degrees)
    {
        int mod = degrees % 360;
        return mod < 0 ? mod + 360 : mod;
    }

    private void ApplyRotationTransform()
    {
        ZoomTransform.Rotation = _rotationDegrees;
    }

    // ------------------------------------------------------------------
    // サムネイル一覧
    // ------------------------------------------------------------------

    private bool IsThumbnailOverlayOpen => ThumbnailOverlay.Visibility == Visibility.Visible;

    private void ToggleThumbnails()
    {
        if (_document is null)
        {
            return;
        }

        if (IsThumbnailOverlayOpen)
        {
            CloseThumbnails();
        }
        else
        {
            OpenThumbnails();
        }
    }

    private void OpenThumbnails()
    {
        if (_document is null)
        {
            return;
        }

        HideControls();
        HideNextVolumeBanner();
        _thumbnailGeneration++;
        _thumbnailQueue.Clear();
        _thumbnailItems.Clear();

        for (uint i = 0; i < _document.PageCount; i++)
        {
            _thumbnailItems.Add(new ThumbnailItem(i, i == _pageIndex));
        }

        ThumbnailSummaryText.Text = $"{_document.DisplayName}（全 {_document.PageCount} ページ）";
        ThumbnailOverlay.Visibility = Visibility.Visible;

        if (_pageIndex < _thumbnailItems.Count)
        {
            ThumbnailGridView.ScrollIntoView(_thumbnailItems[(int)_pageIndex]);
        }
    }

    private void CloseThumbnails()
    {
        _thumbnailGeneration++;
        _thumbnailQueue.Clear();
        ThumbnailOverlay.Visibility = Visibility.Collapsed;
        RootHost.Focus(FocusState.Programmatic);
    }

    private void ThumbnailGridView_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue || args.Phase != 0 || args.Item is not ThumbnailItem item)
        {
            return;
        }

        if (item.Image is not null || item.IsRequested)
        {
            return;
        }

        item.IsRequested = true;
        _thumbnailQueue.Enqueue(item);
        if (!_thumbnailWorkerRunning)
        {
            _ = ProcessThumbnailQueueAsync(_thumbnailGeneration);
        }
    }

    private async Task ProcessThumbnailQueueAsync(int generation)
    {
        _thumbnailWorkerRunning = true;
        try
        {
            while (_thumbnailQueue.Count > 0 && generation == _thumbnailGeneration && _document is not null)
            {
                ThumbnailItem item = _thumbnailQueue.Dequeue();
                if (item.Image is not null)
                {
                    continue;
                }

                await _renderGate.WaitAsync();
                try
                {
                    if (generation != _thumbnailGeneration || _document is null)
                    {
                        return;
                    }

                    PageSize size = await _document.GetPageSizeAsync(item.PageIndex);
                    double scale = Math.Min(160.0 / size.Width, 210.0 / size.Height);
                    var desired = new PixelSize(
                        (int)Math.Max(1, Math.Round(size.Width * scale)),
                        (int)Math.Max(1, Math.Round(size.Height * scale)));

                    BitmapImage bmp = await _document.RenderPageAsync(item.PageIndex, desired);
                    if (generation == _thumbnailGeneration)
                    {
                        item.Image = bmp;
                    }
                }
                catch
                {
                    // サムネイル1枚の失敗は無視して続行
                }
                finally
                {
                    _renderGate.Release();
                }
            }
        }
        finally
        {
            _thumbnailWorkerRunning = false;
        }
    }

    private void ThumbnailGridView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ThumbnailItem item)
        {
            CloseThumbnails();
            JumpTo(item.PageIndex);
        }
    }

    private void CloseThumbnailsButton_Click(object sender, RoutedEventArgs e)
    {
        CloseThumbnails();
    }

    // ------------------------------------------------------------------
    // ページ番号直接ジャンプ
    // ------------------------------------------------------------------

    private async Task ShowGoToPageDialogAsync()
    {
        if (_document is null || _dialogOpen || RootGrid.XamlRoot is null)
        {
            return;
        }

        _dialogOpen = true;
        try
        {
            var numberBox = new NumberBox
            {
                Minimum = 1,
                Maximum = _document.PageCount,
                Value = _pageIndex + 1,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
                Header = $"ページ番号を入力（1 〜 {_document.PageCount}）",
            };

            var dialog = new ContentDialog
            {
                Title = "ページへ移動",
                Content = numberBox,
                PrimaryButtonText = "移動",
                CloseButtonText = "キャンセル",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = RootGrid.XamlRoot,
            };

            ContentDialogResult result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary && !double.IsNaN(numberBox.Value))
            {
                uint target = (uint)Math.Clamp(Math.Round(numberBox.Value), 1, _document.PageCount) - 1;
                JumpTo(target);
            }
        }
        finally
        {
            _dialogOpen = false;
            RootHost.Focus(FocusState.Programmatic);
        }
    }

    // ------------------------------------------------------------------
    // キーボード・マウス
    // ------------------------------------------------------------------

    private void LeftKey_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (IsThumbnailOverlayOpen || _dialogOpen)
        {
            return;
        }

        args.Handled = true;
        if (_reverseKeys)
        {
            GoNext();
        }
        else
        {
            GoPrevious();
        }
    }

    private void RightKey_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (IsThumbnailOverlayOpen || _dialogOpen)
        {
            return;
        }

        args.Handled = true;
        if (_reverseKeys)
        {
            GoPrevious();
        }
        else
        {
            GoNext();
        }
    }

    private void UpKey_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (IsThumbnailOverlayOpen || _dialogOpen)
        {
            return;
        }

        args.Handled = true;
        GoPrevious();
    }

    private void DownKey_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (IsThumbnailOverlayOpen || _dialogOpen)
        {
            return;
        }

        args.Handled = true;
        GoNext();
    }

    private void NextKey_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (IsThumbnailOverlayOpen || _dialogOpen)
        {
            return;
        }

        args.Handled = true;
        GoNext();
    }

    private void PreviousKey_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (IsThumbnailOverlayOpen || _dialogOpen)
        {
            return;
        }

        args.Handled = true;
        GoPrevious();
    }

    private void FirstPageKey_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (IsThumbnailOverlayOpen || _dialogOpen)
        {
            return;
        }

        args.Handled = true;
        JumpTo(0);
    }

    private void LastPageKey_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (IsThumbnailOverlayOpen || _dialogOpen || _document is null)
        {
            return;
        }

        args.Handled = true;
        JumpTo(_document.PageCount - 1);
    }

    private async void OpenFileKey_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        await PickAndOpenFileAsync();
    }

    private async void OpenFolderKey_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        await PickAndOpenFolderAsync();
    }

    private async void GoToPageKey_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        await ShowGoToPageDialogAsync();
    }

    private void ShiftSpreadKey_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (IsThumbnailOverlayOpen || _dialogOpen)
        {
            return;
        }

        args.Handled = true;
        ShiftSpreadOffset();
    }

    private void RotateCwKey_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (IsThumbnailOverlayOpen || _dialogOpen)
        {
            return;
        }

        args.Handled = true;
        RotateBy(90);
    }

    private void RotateCcwKey_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (IsThumbnailOverlayOpen || _dialogOpen)
        {
            return;
        }

        args.Handled = true;
        RotateBy(-90);
    }

    private void ThumbnailKey_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (_dialogOpen)
        {
            return;
        }

        args.Handled = true;
        ToggleThumbnails();
    }

    private async void PrevVolumeKey_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (_dialogOpen)
        {
            return;
        }

        args.Handled = true;
        await OpenSiblingVolumeAsync(-1);
    }

    private async void NextVolumeKey_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (_dialogOpen)
        {
            return;
        }

        args.Handled = true;
        await OpenSiblingVolumeAsync(1);
    }

    private void ZoomInKey_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (IsThumbnailOverlayOpen || _dialogOpen)
        {
            return;
        }

        args.Handled = true;
        AdjustZoom(1.25);
    }

    private void ZoomOutKey_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (IsThumbnailOverlayOpen || _dialogOpen)
        {
            return;
        }

        args.Handled = true;
        AdjustZoom(1.0 / 1.25);
    }

    private void ZoomResetKey_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (IsThumbnailOverlayOpen || _dialogOpen)
        {
            return;
        }

        args.Handled = true;
        ResetZoom(updateBadge: true);
    }

    private void FullScreenKey_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        ToggleFullScreen();
    }

    private void EscapeKey_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (_dialogOpen)
        {
            return;
        }

        if (IsThumbnailOverlayOpen)
        {
            args.Handled = true;
            CloseThumbnails();
            return;
        }

        if (_zoomScale > 1.01)
        {
            args.Handled = true;
            ResetZoom(updateBadge: true);
            return;
        }

        if (_isFullScreen)
        {
            args.Handled = true;
            ToggleFullScreen();
        }
    }

    private void RootGrid_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_document is null || IsThumbnailOverlayOpen)
        {
            return;
        }

        if (_isPanning)
        {
            Point current = e.GetCurrentPoint(RootGrid).Position;
            _panX = _panStartX + (current.X - _panStartPointer.X);
            _panY = _panStartY + (current.Y - _panStartPointer.Y);
            ClampPan();
            ApplyZoomTransform(updateBadge: false);
            return;
        }

        if (_controlsVisible)
        {
            ShowControls();
            return;
        }

        Point position = e.GetCurrentPoint(RootGrid).Position;
        if (IsPointNearControlsBar(position) || IsPointNearVerticalProgress(position))
        {
            ShowControls();
        }
    }

    private void RootGrid_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        RootHost.Focus(FocusState.Pointer);

        if (_document is null || IsThumbnailOverlayOpen)
        {
            return;
        }

        PointerPoint pointerPoint = e.GetCurrentPoint(RootGrid);
        Point position = pointerPoint.Position;

        if (IsPointInsideControlsBar(position))
        {
            return;
        }

        // ズーム中は左ドラッグでパン移動
        if (_zoomScale > 1.01 && pointerPoint.Properties.IsLeftButtonPressed)
        {
            _isPanning = RootGrid.CapturePointer(e.Pointer);
            _panStartPointer = position;
            _panStartX = _panX;
            _panStartY = _panY;
            return;
        }

        // メニュー表示中にメニュー以外の場所をクリックしたら閉じる
        if (_controlsVisible)
        {
            HideControls();
            return;
        }

        // 画面クリックでのページ送りモード
        if (_clickNavigation && pointerPoint.Properties.IsLeftButtonPressed && RootGrid.ActualWidth > 0)
        {
            double ratio = position.X / RootGrid.ActualWidth;
            if (ratio < 0.35)
            {
                if (_reverseKeys)
                {
                    GoNext();
                }
                else
                {
                    GoPrevious();
                }

                return;
            }

            if (ratio > 0.65)
            {
                if (_reverseKeys)
                {
                    GoPrevious();
                }
                else
                {
                    GoNext();
                }

                return;
            }
        }

        ShowControls();
    }

    private void RootGrid_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_isPanning)
        {
            return;
        }

        _isPanning = false;
        RootGrid.ReleasePointerCapture(e.Pointer);
    }

    private void RootGrid_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (_document is null || IsThumbnailOverlayOpen)
        {
            return;
        }

        Point position = e.GetPosition(RootGrid);
        if (IsPointInsideControlsBar(position))
        {
            return;
        }

        if (_zoomScale > 1.01)
        {
            ResetZoom(updateBadge: true);
        }
        else
        {
            AdjustZoom(2.0, position);
        }
    }

    private void RootGrid_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (_document is null || IsThumbnailOverlayOpen)
        {
            return;
        }

        if (_zoomScale > 1.01)
        {
            ResetZoom(updateBadge: true);
            e.Handled = true;
        }
    }

    private bool IsPointInsideControlsBar(Point position)
    {
        if (!TryGetControlsBarBounds(out Rect bounds))
        {
            return false;
        }

        return position.X >= bounds.X
            && position.X <= bounds.X + bounds.Width
            && position.Y >= bounds.Y
            && position.Y <= bounds.Y + bounds.Height;
    }

    private bool IsPointNearControlsBar(Point position)
    {
        if (!TryGetControlsBarBounds(out Rect bounds))
        {
            return false;
        }

        const double margin = 32;

        return position.X >= bounds.X - margin
            && position.X <= bounds.X + bounds.Width + margin
            && position.Y >= bounds.Y - margin
            && position.Y <= bounds.Y + bounds.Height + margin;
    }

    private bool IsPointNearVerticalProgress(Point position)
    {
        if (VerticalProgressHitArea.Visibility != Visibility.Visible)
        {
            return false;
        }

        const double margin = 24;
        double left = RootGrid.ActualWidth
            - VerticalProgressHitArea.Margin.Right
            - VerticalProgressHitArea.ActualWidth
            - margin;

        return position.X >= left
            && position.Y >= VerticalProgressHitArea.Margin.Top
            && position.Y <= RootGrid.ActualHeight - VerticalProgressHitArea.Margin.Bottom;
    }

    private bool TryGetControlsBarBounds(out Rect bounds)
    {
        bounds = default;

        if (ControlsBar.ActualWidth <= 0 || ControlsBar.ActualHeight <= 0)
        {
            return false;
        }

        var transform = ControlsBar.TransformToVisual(RootGrid);
        Point topLeft = transform.TransformPoint(new Point(0, 0));
        Point bottomRight = transform.TransformPoint(
            new Point(ControlsBar.ActualWidth, ControlsBar.ActualHeight));

        bounds = new Rect(
            topLeft.X,
            topLeft.Y,
            bottomRight.X - topLeft.X,
            bottomRight.Y - topLeft.Y);
        return true;
    }

    private void ProgressHitArea_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        e.Handled = true;
        RootHost.Focus(FocusState.Pointer);

        if (_document is null)
        {
            return;
        }

        var area = (UIElement)sender;
        _isSeeking = area.CapturePointer(e.Pointer);
        ShowControls();
        SeekToPointer(area, e);
    }

    private void ProgressHitArea_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isSeeking)
        {
            return;
        }

        e.Handled = true;
        ShowControls();
        SeekToPointer((UIElement)sender, e);
    }

    private void ProgressHitArea_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_isSeeking)
        {
            return;
        }

        _isSeeking = false;
        SeekPreviewBadge.Visibility = Visibility.Collapsed;
        ((UIElement)sender).ReleasePointerCapture(e.Pointer);
    }

    private void SeekToPointer(UIElement area, PointerRoutedEventArgs e)
    {
        if (_document is null)
        {
            return;
        }

        Point position = e.GetCurrentPoint(area).Position;
        double fraction;

        if (area == VerticalProgressHitArea)
        {
            if (VerticalProgressHitArea.ActualHeight <= 0)
            {
                return;
            }

            fraction = position.Y / VerticalProgressHitArea.ActualHeight;
        }
        else
        {
            if (ProgressHitArea.ActualWidth <= 0)
            {
                return;
            }

            fraction = position.X / ProgressHitArea.ActualWidth;
            if (_rightToLeft)
            {
                fraction = 1.0 - fraction;
            }
        }

        fraction = Math.Clamp(fraction, 0.0, 1.0);
        uint page = (uint)Math.Min(_document.PageCount - 1, Math.Floor(fraction * _document.PageCount));

        UpdateSeekPreview(area, e.GetCurrentPoint(RootGrid).Position, page);
        JumpTo(page);
    }

    private void UpdateSeekPreview(UIElement area, Point rootPosition, uint page)
    {
        if (_document is null)
        {
            return;
        }

        SeekPreviewText.Text = $"P. {page + 1} / {_document.PageCount}";
        SeekPreviewBadge.Visibility = Visibility.Visible;

        double badgeWidth = SeekPreviewBadge.ActualWidth > 0 ? SeekPreviewBadge.ActualWidth : 108;
        double badgeHeight = SeekPreviewBadge.ActualHeight > 0 ? SeekPreviewBadge.ActualHeight : 30;

        if (area == VerticalProgressHitArea)
        {
            double x = Math.Max(12, RootGrid.ActualWidth - badgeWidth - 46);
            double y = Math.Clamp(rootPosition.Y - badgeHeight / 2.0, 12, Math.Max(12, RootGrid.ActualHeight - badgeHeight - 12));
            SeekPreviewTransform.X = x;
            SeekPreviewTransform.Y = y;
        }
        else
        {
            double x = Math.Clamp(rootPosition.X - badgeWidth / 2.0, 12, Math.Max(12, RootGrid.ActualWidth - badgeWidth - 12));
            double y = Math.Max(12, rootPosition.Y - badgeHeight - 24);
            SeekPreviewTransform.X = x;
            SeekPreviewTransform.Y = y;
        }
    }

    private void RootGrid_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (_document is not null && !_flyoutOpen)
        {
            HideControls();
        }
    }

    private void RootGrid_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (_document is null || IsThumbnailOverlayOpen)
        {
            return;
        }

        PointerPoint point = e.GetCurrentPoint(RootGrid);
        int delta = point.Properties.MouseWheelDelta;
        if (delta == 0)
        {
            return;
        }

        // Ctrl + ホイールでズーム
        bool ctrlPressed = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
            .HasFlag(CoreVirtualKeyStates.Down);
        if (ctrlPressed)
        {
            e.Handled = true;
            AdjustZoom(delta > 0 ? 1.2 : 1.0 / 1.2, point.Position);
            return;
        }

        DateTime now = DateTime.UtcNow;
        if ((now - _lastWheelTurn).TotalMilliseconds < 180)
        {
            e.Handled = true;
            return;
        }

        _lastWheelTurn = now;
        e.Handled = true;

        if (delta < 0)
        {
            GoNext();
        }
        else
        {
            GoPrevious();
        }
    }

    // ------------------------------------------------------------------
    // 操作バー・メニュー
    // ------------------------------------------------------------------

    private void ShowControls()
    {
        if (_document is null || IsThumbnailOverlayOpen)
        {
            return;
        }

        _hideControlsTimer.Stop();
        if (!_flyoutOpen)
        {
            _hideControlsTimer.Start();
        }

        if (_controlsVisible)
        {
            return;
        }

        _controlsVisible = true;
        ControlsBar.IsHitTestVisible = true;
        VerticalProgressHitArea.IsHitTestVisible = true;
        AnimateOpacity(ControlsBar, 1, 140);
        AnimateOpacity(FileChip, 1, 140);
        AnimateOpacity(VerticalProgressHitArea, 1, 140);
    }

    private void HideControls()
    {
        _hideControlsTimer.Stop();

        if (!_controlsVisible)
        {
            return;
        }

        _controlsVisible = false;
        ControlsBar.IsHitTestVisible = false;
        VerticalProgressHitArea.IsHitTestVisible = false;
        AnimateOpacity(ControlsBar, 0, 260);
        AnimateOpacity(FileChip, 0, 260);
        AnimateOpacity(VerticalProgressHitArea, 0, 260);
    }

    private static void AnimateOpacity(UIElement element, double to, int milliseconds)
    {
        var animation = new DoubleAnimation
        {
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(milliseconds)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };

        Storyboard.SetTarget(animation, element);
        Storyboard.SetTargetProperty(animation, "Opacity");

        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        storyboard.Begin();
    }

    private async void OpenButton_Click(object sender, RoutedEventArgs e)
    {
        RootHost.Focus(FocusState.Programmatic);
        await PickAndOpenFileAsync();
    }

    private async void OpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        RootHost.Focus(FocusState.Programmatic);
        await PickAndOpenFolderAsync();
    }

    private void PrevButton_Click(object sender, RoutedEventArgs e)
    {
        RootHost.Focus(FocusState.Programmatic);
        if (_reverseKeys)
        {
            GoNext();
        }
        else
        {
            GoPrevious();
        }
    }

    private void NextButton_Click(object sender, RoutedEventArgs e)
    {
        RootHost.Focus(FocusState.Programmatic);
        if (_reverseKeys)
        {
            GoPrevious();
        }
        else
        {
            GoNext();
        }
    }

    private async void PageLabelButton_Click(object sender, RoutedEventArgs e)
    {
        await ShowGoToPageDialogAsync();
    }

    private void ThumbnailButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleThumbnails();
    }

    private void RotateButton_Click(object sender, RoutedEventArgs e)
    {
        RootHost.Focus(FocusState.Programmatic);
        RotateBy(90);
    }

    private void RtlToggle_Click(object sender, RoutedEventArgs e)
    {
        _rightToLeft = RtlToggle.IsChecked == true;
        UpdateReadingDirectionUi();
        SaveSettings();

        RootHost.Focus(FocusState.Programmatic);
        ShowControls();
        _ = ShowViewAsync(_pageIndex);
    }

    private void UpdateReadingDirectionUi()
    {
        RtlToggle.IsChecked = _rightToLeft;
        ReadingProgress.FlowDirection = _rightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        ToolTipService.SetToolTip(
            RtlToggle,
            _rightToLeft
                ? "右から左に表示中（クリックで左から右へ）"
                : "左から右に表示中（クリックで右から左へ）");
    }

    private void ReverseKeysMenuItem_Click(object sender, RoutedEventArgs e)
    {
        _reverseKeys = ReverseKeysMenuItem.IsChecked;
        UpdateNavigationUi();
        SaveSettings();
        RootHost.Focus(FocusState.Programmatic);
    }

    private void PageNumberMenuItem_Click(object sender, RoutedEventArgs e)
    {
        _showPageNumber = PageNumberMenuItem.IsChecked;
        UpdatePageNumberUi();
        SaveSettings();
        RootHost.Focus(FocusState.Programmatic);
    }

    private void UpdatePageNumberUi()
    {
        PageNumberMenuItem.IsChecked = _showPageNumber;
        CornerPageNumber.Visibility = _showPageNumber && _document is not null
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void ClickNavMenuItem_Click(object sender, RoutedEventArgs e)
    {
        _clickNavigation = ClickNavMenuItem.IsChecked;
        UpdateClickNavigationUi();
        SaveSettings();
        RootHost.Focus(FocusState.Programmatic);
    }

    private void UpdateClickNavigationUi()
    {
        ClickNavMenuItem.IsChecked = _clickNavigation;
    }

    private void ZoomInMenuItem_Click(object sender, RoutedEventArgs e) => AdjustZoom(1.25);

    private void ZoomOutMenuItem_Click(object sender, RoutedEventArgs e) => AdjustZoom(1.0 / 1.25);

    private void ZoomResetMenuItem_Click(object sender, RoutedEventArgs e) => ResetZoom(updateBadge: true);

    private void SpreadModeItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioMenuFlyoutItem item ||
            item.Tag is not string tag ||
            !Enum.TryParse(tag, ignoreCase: true, out SpreadMode mode))
        {
            return;
        }

        _spreadMode = mode;
        _spreadOffset = 0;
        UpdateSpreadUi();
        SaveSettings();

        RootHost.Focus(FocusState.Programmatic);
        ShowControls();
        _ = ShowViewAsync(_pageIndex, normalize: true);
    }

    private void ShiftSpreadMenuItem_Click(object sender, RoutedEventArgs e)
    {
        ShiftSpreadOffset();
        RootHost.Focus(FocusState.Programmatic);
    }

    private void ShiftSpreadOffset()
    {
        if (_document is null || _spreadMode == SpreadMode.Off)
        {
            return;
        }

        _spreadOffset = _spreadOffset == 0 ? 1 : 0;

        uint target = _pageIndex;
        if (_pageIndex + 1 < _document.PageCount)
        {
            target = _spreadOffset == 1 ? _pageIndex + 1 : (_pageIndex > 0 ? _pageIndex - 1 : 0);
        }
        else if (_pageIndex > 0)
        {
            target = _pageIndex - 1;
        }

        ScheduleSaveProgress();
        _ = ShowViewAsync(target);
    }

    private void AutoLandscapeMenuItem_Click(object sender, RoutedEventArgs e)
    {
        _autoSingleLandscape = AutoLandscapeMenuItem.IsChecked;
        SaveSettings();
        RootHost.Focus(FocusState.Programmatic);
        _ = ShowViewAsync(_pageIndex);
    }

    private void ControlsFlyout_Opening(object sender, object e)
    {
        _flyoutOpen = true;
        _hideControlsTimer.Stop();
    }

    private void OpenMenuFlyout_Opening(object sender, object e)
    {
        _flyoutOpen = true;
        _hideControlsTimer.Stop();

        string? prev = _document is not null ? MangaDocumentFactory.FindSiblingBook(_document.SourcePath, -1) : null;
        string? next = _document is not null ? MangaDocumentFactory.FindSiblingBook(_document.SourcePath, 1) : null;
        PrevVolumeMenuItem.IsEnabled = prev is not null;
        NextVolumeMenuItem.IsEnabled = next is not null;

        RefreshRecentFilesSubMenu();
    }

    private void ControlsFlyout_Closed(object sender, object e)
    {
        _flyoutOpen = false;
        if (_controlsVisible)
        {
            ShowControls();
        }
    }

    private async void PrevVolumeMenuItem_Click(object sender, RoutedEventArgs e)
    {
        await OpenSiblingVolumeAsync(-1);
    }

    private async void NextVolumeMenuItem_Click(object sender, RoutedEventArgs e)
    {
        await OpenSiblingVolumeAsync(1);
    }

    private void UpdateSpreadUi()
    {
        SpreadButton.Content = _spreadMode switch
        {
            SpreadMode.CoverSingle => "見開き: 表紙のみ",
            SpreadMode.PairFromStart => "見開き: 2枚組",
            SpreadMode.FirstTwoSingle => "見開き: 2枚単独",
            _ => "見開き: なし",
        };

        SpreadOffItem.IsChecked = _spreadMode == SpreadMode.Off;
        SpreadCoverItem.IsChecked = _spreadMode == SpreadMode.CoverSingle;
        SpreadPairItem.IsChecked = _spreadMode == SpreadMode.PairFromStart;
        SpreadFirstTwoItem.IsChecked = _spreadMode == SpreadMode.FirstTwoSingle;
        AutoLandscapeMenuItem.IsChecked = _autoSingleLandscape;
        ShiftSpreadMenuItem.IsEnabled = _spreadMode != SpreadMode.Off;

        bool single = _spreadMode == SpreadMode.Off;
        VerticalProgressHitArea.Visibility = single ? Visibility.Visible : Visibility.Collapsed;
        ProgressHitArea.Visibility = single ? Visibility.Collapsed : Visibility.Visible;
    }

    private void FullScreenButton_Click(object sender, RoutedEventArgs e)
    {
        RootHost.Focus(FocusState.Programmatic);
        ToggleFullScreen();
    }

    private void ToggleFullScreen()
    {
        _isFullScreen = !_isFullScreen;

        AppWindow.SetPresenter(_isFullScreen
            ? AppWindowPresenterKind.FullScreen
            : AppWindowPresenterKind.Default);

        FullScreenButton.Content = _isFullScreen ? "\uE73F" : "\uE740";
        ToolTipService.SetToolTip(
            FullScreenButton,
            _isFullScreen ? "全画面を終了 (F11 / Esc)" : "全画面 (F11)");

        if (_document is not null)
        {
            ShowControls();
        }
    }

    // ------------------------------------------------------------------
    // 最近開いたファイル・設定の保存
    // ------------------------------------------------------------------

    private RecentBook? FindRecentBook(string path)
    {
        string normalized = Path.GetFullPath(path);
        return _recentBooks.FirstOrDefault(b =>
            string.Equals(Path.GetFullPath(b.Path), normalized, StringComparison.OrdinalIgnoreCase));
    }

    private void UpdateCurrentBookProgress()
    {
        if (_document is null || string.IsNullOrEmpty(_document.SourcePath))
        {
            return;
        }

        string fullPath = Path.GetFullPath(_document.SourcePath);
        _recentBooks.RemoveAll(b =>
            string.Equals(Path.GetFullPath(b.Path), fullPath, StringComparison.OrdinalIgnoreCase));

        _recentBooks.Insert(0, new RecentBook
        {
            Path = fullPath,
            DisplayName = _document.FileName,
            PageIndex = _pageIndex,
            PageCount = _document.PageCount,
            SpreadOffset = _spreadOffset,
            RotationDegrees = _rotationDegrees,
            LastOpenedUtc = DateTime.UtcNow,
        });

        if (_recentBooks.Count > MaxRecentBooks)
        {
            _recentBooks.RemoveRange(MaxRecentBooks, _recentBooks.Count - MaxRecentBooks);
        }
    }

    private void ScheduleSaveProgress()
    {
        UpdateCurrentBookProgress();
        _saveProgressTimer.Stop();
        _saveProgressTimer.Start();
    }

    private void RefreshRecentFilesUi()
    {
        RecentFilesList.Children.Clear();

        List<RecentBook> existing = _recentBooks
            .Where(b => !string.IsNullOrEmpty(b.Path) && (File.Exists(b.Path) || Directory.Exists(b.Path)))
            .Take(6)
            .ToList();

        RecentFilesContainer.Visibility = existing.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (RecentBook book in existing)
        {
            var grid = new Grid { ColumnSpacing = 12 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var title = new TextBlock
            {
                Text = book.DisplayName,
                FontSize = 13,
                Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(235, 255, 255, 255)),
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            };

            var progress = new TextBlock
            {
                Text = book.PageCount > 0 ? $"P.{book.PageIndex + 1} / {book.PageCount}" : string.Empty,
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                FontSize = 11,
                Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(140, 255, 255, 255)),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(progress, 1);

            grid.Children.Add(title);
            grid.Children.Add(progress);

            var button = new Button
            {
                Content = grid,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(10, 6, 10, 6),
                CornerRadius = new CornerRadius(8),
                Tag = book.Path,
            };
            ToolTipService.SetToolTip(button, book.Path);
            button.Click += async (s, _) =>
            {
                if (s is Button btn && btn.Tag is string path)
                {
                    await OpenPathAsync(path);
                }
            };

            RecentFilesList.Children.Add(button);
        }
    }

    private void RefreshRecentFilesSubMenu()
    {
        RecentFilesSubMenu.Items.Clear();

        List<RecentBook> existing = _recentBooks
            .Where(b => !string.IsNullOrEmpty(b.Path) && (File.Exists(b.Path) || Directory.Exists(b.Path)))
            .Take(10)
            .ToList();

        if (existing.Count == 0)
        {
            RecentFilesSubMenu.Items.Add(new MenuFlyoutItem { Text = "履歴はありません", IsEnabled = false });
            return;
        }

        foreach (RecentBook book in existing)
        {
            string label = book.PageCount > 0
                ? $"{book.DisplayName}  (P.{book.PageIndex + 1}/{book.PageCount})"
                : book.DisplayName;

            var item = new MenuFlyoutItem { Text = label, Tag = book.Path };
            item.Click += async (s, _) =>
            {
                if (s is MenuFlyoutItem menu && menu.Tag is string path)
                {
                    await OpenPathAsync(path);
                }
            };
            RecentFilesSubMenu.Items.Add(item);
        }
    }

    private void LoadSettings()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return;
            }

            string json = File.ReadAllText(SettingsPath);
            ViewerSettings? settings = JsonSerializer.Deserialize<ViewerSettings>(json);

            if (settings is not null)
            {
                if (settings.Spread is { } spread &&
                    Enum.TryParse(spread, ignoreCase: true, out SpreadMode mode))
                {
                    _spreadMode = mode;
                }

                _rightToLeft = settings.RightToLeft;
                _reverseKeys = settings.ReverseKeys;
                _showPageNumber = settings.ShowPageNumber;
                _autoSingleLandscape = settings.AutoSingleLandscape;
                _clickNavigation = settings.ClickNavigation;
                _windowMaximized = settings.WindowMaximized;

                if (settings.WindowX is { } wx &&
                    settings.WindowY is { } wy &&
                    settings.WindowWidth is { } ww and >= 320 &&
                    settings.WindowHeight is { } wh and >= 240)
                {
                    _lastNormalBounds = new RectInt32(wx, wy, ww, wh);
                }

                if (settings.RecentBooks is not null)
                {
                    _recentBooks = settings.RecentBooks;
                }
            }
        }
        catch
        {
            // 設定ファイルが読めない場合は既定値のまま
        }
    }

    private void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(SettingsDirectory);

            var settings = new ViewerSettings
            {
                Spread = _spreadMode.ToString(),
                RightToLeft = _rightToLeft,
                ReverseKeys = _reverseKeys,
                ShowPageNumber = _showPageNumber,
                AutoSingleLandscape = _autoSingleLandscape,
                ClickNavigation = _clickNavigation,
                WindowX = _lastNormalBounds?.X,
                WindowY = _lastNormalBounds?.Y,
                WindowWidth = _lastNormalBounds?.Width,
                WindowHeight = _lastNormalBounds?.Height,
                WindowMaximized = _windowMaximized,
                RecentBooks = _recentBooks,
            };

            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings));
        }
        catch
        {
            // 設定の保存に失敗しても動作は継続する
        }
    }

    // ------------------------------------------------------------------
    // ウィンドウサイズ変更・終了
    // ------------------------------------------------------------------

    private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (_isFullScreen)
        {
            return;
        }

        if (sender.Presenter is OverlappedPresenter presenter)
        {
            if (presenter.State == OverlappedPresenterState.Maximized)
            {
                _windowMaximized = true;
            }
            else if (presenter.State == OverlappedPresenterState.Restored)
            {
                _windowMaximized = false;
                if (args.DidPositionChange || args.DidSizeChange)
                {
                    _lastNormalBounds = new RectInt32(
                        sender.Position.X,
                        sender.Position.Y,
                        sender.Size.Width,
                        sender.Size.Height);
                }
            }
        }
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        _saveProgressTimer.Stop();
        UpdateCurrentBookProgress();
        SaveSettings();
        _document?.Dispose();
    }

    private void RootGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_document is null)
        {
            return;
        }

        if (_zoomScale > 1.01)
        {
            ClampPan();
            ApplyZoomTransform(updateBadge: false);
        }

        _resizeTimer.Stop();
        _resizeTimer.Start();
    }

    private void OnResizeSettled()
    {
        _resizeTimer.Stop();

        if (_document is null)
        {
            return;
        }

        _ = ShowViewAsync(_pageIndex);
    }

    // ------------------------------------------------------------------
    // ドラッグ＆ドロップ
    // ------------------------------------------------------------------

    private void RootGrid_DragEnter(object sender, DragEventArgs e) => UpdateDragOverlay(e);

    private void RootGrid_DragOver(object sender, DragEventArgs e) => UpdateDragOverlay(e);

    private void RootGrid_DragLeave(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
    }

    private void UpdateDragOverlay(DragEventArgs e)
    {
        bool hasFiles = e.DataView.Contains(StandardDataFormats.StorageItems);
        DropOverlay.Visibility = hasFiles ? Visibility.Visible : Visibility.Collapsed;

        if (hasFiles)
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "開く";
        }
    }

    private async void RootGrid_Drop(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;

        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }

        var items = await e.DataView.GetStorageItemsAsync();
        foreach (IStorageItem item in items)
        {
            if (item is StorageFolder folder)
            {
                await OpenFolderPathAsync(folder.Path);
                return;
            }

            if (item is StorageFile file && MangaDocumentFactory.IsSupportedPath(file.Path))
            {
                await OpenPathAsync(file.Path);
                return;
            }
        }

        await ShowErrorAsync("対応していないファイルです", "PDF・ZIP・CBZ・画像ファイルまたはフォルダをドロップしてください。");
    }

    // ------------------------------------------------------------------
    // ユーティリティ
    // ------------------------------------------------------------------

    private void SetBusy(bool busy)
    {
        LoadingRing.IsActive = busy;
        LoadingRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task ShowErrorAsync(string title, string message)
    {
        if (_dialogOpen || RootGrid.XamlRoot is null)
        {
            return;
        }

        _dialogOpen = true;
        try
        {
            var dialog = new ContentDialog
            {
                Title = title,
                Content = message,
                CloseButtonText = "閉じる",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = RootGrid.XamlRoot,
            };

            await dialog.ShowAsync();
        }
        finally
        {
            _dialogOpen = false;
            RootHost.Focus(FocusState.Programmatic);
        }
    }

    private void ConfigureInitialWindow()
    {
        DisplayArea display = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        RectInt32 work = display.WorkArea;

        if (_lastNormalBounds is { } saved)
        {
            DisplayArea targetDisplay = DisplayArea.GetFromRect(saved, DisplayAreaFallback.Primary);
            RectInt32 targetWork = targetDisplay.WorkArea;

            int width = Math.Clamp(saved.Width, 480, targetWork.Width);
            int height = Math.Clamp(saved.Height, 360, targetWork.Height);
            int x = Math.Clamp(saved.X, targetWork.X, targetWork.X + targetWork.Width - width);
            int y = Math.Clamp(saved.Y, targetWork.Y, targetWork.Y + targetWork.Height - height);

            AppWindow.MoveAndResize(new RectInt32(x, y, width, height));

            if (_windowMaximized && AppWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.Maximize();
            }

            return;
        }

        int defaultWidth = Math.Clamp((int)(work.Width * 0.70), 960, 1400);
        int defaultHeight = Math.Clamp((int)(work.Height * 0.75), 640, 1000);
        defaultWidth = Math.Min(defaultWidth, work.Width);
        defaultHeight = Math.Min(defaultHeight, work.Height);

        int defaultX = work.X + (work.Width - defaultWidth) / 2;
        int defaultY = work.Y + (work.Height - defaultHeight) / 2;

        AppWindow.MoveAndResize(new RectInt32(defaultX, defaultY, defaultWidth, defaultHeight));
    }

    private void ConfigureTitleBar()
    {
        string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (File.Exists(iconPath))
        {
            AppWindow.SetIcon(iconPath);
        }

        if (!AppWindowTitleBar.IsCustomizationSupported())
        {
            return;
        }

        var background = Windows.UI.Color.FromArgb(255, 10, 10, 12);
        var foreground = Windows.UI.Color.FromArgb(255, 240, 240, 245);
        var inactiveForeground = Windows.UI.Color.FromArgb(255, 150, 150, 155);
        var hoverBackground = Windows.UI.Color.FromArgb(255, 40, 40, 46);
        var pressedBackground = Windows.UI.Color.FromArgb(255, 60, 60, 66);

        AppWindowTitleBar titleBar = AppWindow.TitleBar;
        titleBar.BackgroundColor = background;
        titleBar.InactiveBackgroundColor = background;
        titleBar.ForegroundColor = foreground;
        titleBar.InactiveForegroundColor = inactiveForeground;
        titleBar.ButtonBackgroundColor = background;
        titleBar.ButtonInactiveBackgroundColor = background;
        titleBar.ButtonForegroundColor = foreground;
        titleBar.ButtonInactiveForegroundColor = inactiveForeground;
        titleBar.ButtonHoverBackgroundColor = hoverBackground;
        titleBar.ButtonHoverForegroundColor = Colors.White;
        titleBar.ButtonPressedBackgroundColor = pressedBackground;
        titleBar.ButtonPressedForegroundColor = Colors.White;
    }
}
