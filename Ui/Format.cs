using System.Text;
using Spectre.Console;

namespace ClrDiag.Ui;

/// <summary>UI 顯示用的格式化工具。所有動態文字都必須先 Esc()，型別名稱含 [] 會被 Spectre 當成標記。</summary>
public static class Format
{
    private static readonly string[] StripPrefixes =
    {
        "System.Collections.Generic.",
        "System.Collections.Concurrent.",
        "System.Collections.",
        "System.Runtime.CompilerServices.",
        "System.Reflection.",
        "System.Threading.Tasks.",
        "System.Threading.",
        "System.Web.",
        "System.Data.",
        "System.",
    };

    /// <summary>
    /// 次要文字（欄位標題、時間戳、無變化的差異值）唯一可用的低調色。
    /// 禁止使用 Spectre 的 grey（#808080）：在深色終端機背景下讀不到。
    /// 這裡是全 UI 唯一的調節點，覺得還是太暗就把它換成 white。
    /// </summary>
    public const string Muted = "silver";

    public static string Esc(string? text) => Markup.Escape(text ?? string.Empty);

    public static string Mb(double? value) => value is null ? "n/a" : $"{value.Value:N1} MB";

    public static string MbBytes(ulong bytes) => $"{bytes / 1024.0 / 1024.0:N1}";

    public static string Signed(long deltaBytes)
    {
        double mb = deltaBytes / 1024.0 / 1024.0;
        return deltaBytes switch
        {
            0 => "-",
            > 0 => $"+{mb:N1}",
            _ => $"{mb:N1}",
        };
    }

    public static string SignedCount(long delta) =>
        delta switch
        {
            0 => "-",
            > 0 => $"+{delta:N0}",
            _ => $"{delta:N0}",
        };

    public static string Number(long? value) => value is null ? "n/a" : $"{value.Value:N0}";

    public static string Percent(double? value) => value is null ? "n/a" : $"{value.Value:N1}%";

    public static string Rate(double? value) => value is null ? "n/a" : $"{value.Value:N1}/s";

    public static string Duration(TimeSpan span) =>
        span.TotalSeconds < 60
            ? $"{span.TotalSeconds:N1}s"
            : $"{(int)span.TotalMinutes}m{span.Seconds:00}s";

    /// <summary>縮短型別名稱：拿掉常見命名空間前綴，必要時截斷中段。</summary>
    public static string ShortType(string typeName, int maxLength)
    {
        string shortened = typeName;
        foreach (string prefix in StripPrefixes)
        {
            shortened = shortened.Replace(prefix, string.Empty, StringComparison.Ordinal);
        }

        if (shortened.Length <= maxLength)
        {
            return shortened;
        }

        if (maxLength <= 5)
        {
            return shortened[..Math.Max(1, maxLength)];
        }

        int head = (maxLength - 3) * 2 / 3;
        int tail = maxLength - 3 - head;
        return string.Concat(
            shortened.AsSpan(0, head),
            "...",
            shortened.AsSpan(shortened.Length - tail)
        );
    }

    /// <summary>
    /// 縮短方法框架名稱時保留尾段：方法名稱在最後面，
    /// 命名空間開頭對辨識沒有幫助（Hangfire.Server.Worker.Execute → Worker.Execute）。
    /// </summary>
    public static string TailFrame(string frame, int maxLength)
    {
        if (frame.Length <= maxLength)
        {
            return frame;
        }

        string[] segments = frame.Split('.');
        var kept = new List<string>();
        int length = 0;

        for (int i = segments.Length - 1; i >= 0; i--)
        {
            int extra = segments[i].Length + (kept.Count > 0 ? 1 : 0);
            if (length + extra > maxLength - 1)
            {
                break;
            }

            kept.Insert(0, segments[i]);
            length += extra;
        }

        if (kept.Count == 0)
        {
            return "…" + frame[^Math.Max(1, maxLength - 1)..];
        }

        return (kept.Count < segments.Length ? "…" : string.Empty) + string.Join('.', kept);
    }

    /// <summary>走勢圖與其實際值域；值域必須一起顯示，否則相對縮放的圖形會被誤讀。</summary>
    public readonly record struct SparkBand(string Chart, double Min, double Max);

    /// <summary>
    /// 以區塊字元畫出走勢圖。刻意採用「視窗內 min–max」相對縮放而非固定從 0 起算：
    /// 記憶體通常是幾百 MB 的基底加上幾 MB 的變化，從 0 起算會讓所有格子都變成滿格而看不出形狀。
    /// 變化幅度小於 2% 時視為持平，畫成一整條中線，避免把雜訊放大成山峰。
    /// </summary>
    public static SparkBand Sparkline(IReadOnlyList<double> values, int width)
    {
        const string blocks = "▁▂▃▄▅▆▇█";

        if (values.Count == 0 || width <= 0)
        {
            return new SparkBand(string.Empty, 0, 0);
        }

        double[] window = values.Count <= width ? values.ToArray() : Downsample(values, width);

        double max = window.Max();
        double min = window.Min();
        double range = max - min;

        if (max <= 0)
        {
            return new SparkBand(new string(blocks[0], window.Length), min, max);
        }

        if (range < max * 0.02)
        {
            return new SparkBand(new string(blocks[3], window.Length), min, max);
        }

        var builder = new StringBuilder(window.Length);
        foreach (double value in window)
        {
            int index = (int)Math.Round((value - min) / range * (blocks.Length - 1));
            builder.Append(blocks[Math.Clamp(index, 0, blocks.Length - 1)]);
        }

        return new SparkBand(builder.ToString(), min, max);
    }

    /// <summary>中日韓字元在終端機占兩格，用字元數補空白會讓欄位對不齊。</summary>
    public static int DisplayWidth(string text)
    {
        int width = 0;
        foreach (char c in text)
        {
            width += IsWide(c) ? 2 : 1;
        }

        return width;
    }

    public static string PadRightDisplay(string text, int width) =>
        text + new string(' ', Math.Max(0, width - DisplayWidth(text)));

    private static bool IsWide(char c) =>
        c >= 0x1100
        && (
            c <= 0x115F
            || (c >= 0x2E80 && c <= 0xA4CF)
            || (c >= 0xAC00 && c <= 0xD7A3)
            || (c >= 0xF900 && c <= 0xFAFF)
            || (c >= 0xFE30 && c <= 0xFE6F)
            || (c >= 0xFF00 && c <= 0xFF60)
            || (c >= 0xFFE0 && c <= 0xFFE6)
        );

    /// <summary>把過多的取樣點壓縮到指定寬度，每格取區間最大值（保留尖峰）。</summary>
    private static double[] Downsample(IReadOnlyList<double> values, int width)
    {
        var result = new double[width];
        double bucket = (double)values.Count / width;

        for (int i = 0; i < width; i++)
        {
            int start = (int)(i * bucket);
            int end = Math.Min(values.Count, Math.Max(start + 1, (int)((i + 1) * bucket)));
            double max = values[start];
            for (int j = start + 1; j < end; j++)
            {
                max = Math.Max(max, values[j]);
            }

            result[i] = max;
        }

        return result;
    }
}
