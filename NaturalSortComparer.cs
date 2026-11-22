using System;
using System.Collections.Generic;

namespace PdfMangaViewer;

/// <summary>
/// 数字の大小を考慮して文字列を並べる自然順コンパレーター（例: 2巻.pdf &lt; 10巻.pdf）。
/// </summary>
public sealed class NaturalSortComparer : IComparer<string?>
{
    public static NaturalSortComparer Instance { get; } = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null)
        {
            return -1;
        }

        if (y is null)
        {
            return 1;
        }

        int ix = 0;
        int iy = 0;

        while (ix < x.Length && iy < y.Length)
        {
            char cx = x[ix];
            char cy = y[iy];

            if (char.IsDigit(cx) && char.IsDigit(cy))
            {
                int startX = ix;
                while (ix < x.Length && char.IsDigit(x[ix]))
                {
                    ix++;
                }

                int startY = iy;
                while (iy < y.Length && char.IsDigit(y[iy]))
                {
                    iy++;
                }

                ReadOnlySpan<char> numX = x.AsSpan(startX, ix - startX).TrimStart('0');
                ReadOnlySpan<char> numY = y.AsSpan(startY, iy - startY).TrimStart('0');

                int cmp = numX.Length.CompareTo(numY.Length);
                if (cmp != 0)
                {
                    return cmp;
                }

                cmp = numX.CompareTo(numY, StringComparison.Ordinal);
                if (cmp != 0)
                {
                    return cmp;
                }

                // 数値が同じなら桁数（ゼロ埋め）が少ない方を先にする
                cmp = (ix - startX).CompareTo(iy - startY);
                if (cmp != 0)
                {
                    return cmp;
                }
            }
            else
            {
                int cmp = char.ToUpperInvariant(cx).CompareTo(char.ToUpperInvariant(cy));
                if (cmp != 0)
                {
                    return cmp;
                }

                ix++;
                iy++;
            }
        }

        return (x.Length - ix).CompareTo(y.Length - iy);
    }
}
