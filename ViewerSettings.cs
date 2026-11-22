using System;
using System.Collections.Generic;

namespace PdfMangaViewer;

/// <summary>見開きの組み方。</summary>
public enum SpreadMode
{
    /// <summary>1ページずつ表示。</summary>
    Off,

    /// <summary>表紙（1ページ目）のみ単独。以降は 2-3, 4-5, …</summary>
    CoverSingle,

    /// <summary>最初から 1-2, 3-4, … の2ページ組み。</summary>
    PairFromStart,

    /// <summary>1ページ目と2ページ目のみ単独。以降は 3-4, 5-6, …</summary>
    FirstTwoSingle,
}

/// <summary>最近開いた本と読書位置の記録。</summary>
public sealed class RecentBook
{
    public string Path { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public uint PageIndex { get; set; }

    public uint PageCount { get; set; }

    public int SpreadOffset { get; set; }

    public int RotationDegrees { get; set; }

    public DateTime LastOpenedUtc { get; set; }
}

/// <summary>永続化する設定。</summary>
public sealed class ViewerSettings
{
    public string Spread { get; set; } = nameof(SpreadMode.Off);

    /// <summary>見開きを右から左に並べるか（既定はオン）。</summary>
    public bool RightToLeft { get; set; } = true;

    /// <summary>左右キーと矢印ボタンの向きを反転するか（既定はオン）。</summary>
    public bool ReverseKeys { get; set; } = true;

    /// <summary>右下にページ番号を常に表示するか。</summary>
    public bool ShowPageNumber { get; set; }

    /// <summary>見開き表示時に横長ページを自動で単独表示するか（既定はオン）。</summary>
    public bool AutoSingleLandscape { get; set; } = true;

    /// <summary>画面の左右クリックでページ送りするか。</summary>
    public bool ClickNavigation { get; set; }

    public int? WindowX { get; set; }

    public int? WindowY { get; set; }

    public int? WindowWidth { get; set; }

    public int? WindowHeight { get; set; }

    public bool WindowMaximized { get; set; }

    public List<RecentBook> RecentBooks { get; set; } = new();
}
