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

/// <summary>永続化する設定。</summary>
public sealed class ViewerSettings
{
    public string Spread { get; set; } = nameof(SpreadMode.Off);

    /// <summary>見開きを右から左に並べるか（既定はオン）。</summary>
    public bool RightToLeft { get; set; } = true;
}
