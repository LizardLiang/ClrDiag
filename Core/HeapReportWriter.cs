using System.Globalization;
using System.Text;

namespace ClrDiag.Core;

/// <summary>把堆疊快照（含與基準的差異）寫成 CSV，供互動介面的匯出鍵與 --export 共用。</summary>
public static class HeapReportWriter
{
    /// <param name="directory">輸出目錄（由設定檔的 reportDirectory 決定），不存在時會建立。</param>
    public static string Write(
        string directory,
        DiagSnapshot current,
        DiagSnapshot? baseline,
        IReadOnlyList<HeapTypeDelta> rows,
        int? processId
    )
    {
        Directory.CreateDirectory(directory);

        string file = Path.Combine(directory, $"heap-{current.TakenAt:yyyyMMdd-HHmmss}.csv");
        var builder = new StringBuilder();

        builder.AppendLine($"# 快照,{current.Label},CLR,{current.ClrVersion},PID,{processId}");
        builder.AppendLine(
            $"# 物件數,{current.ObjectCount},總大小MB,{current.TotalSizeMb:N1},耗時秒,{current.Duration.TotalSeconds:N1}"
        );
        builder.AppendLine($"# 基準,{baseline?.Label ?? "無"}");
        builder.AppendLine("型別,物件數,物件數差異,大小Bytes,大小差異Bytes");

        foreach (HeapTypeDelta row in rows)
        {
            string name = row.TypeName.Replace("\"", "\"\"", StringComparison.Ordinal);
            builder
                .Append('"')
                .Append(name)
                .Append("\",")
                .Append(row.Count.ToString(CultureInfo.InvariantCulture))
                .Append(',')
                .Append(row.CountDelta.ToString(CultureInfo.InvariantCulture))
                .Append(',')
                .Append(row.TotalSize.ToString(CultureInfo.InvariantCulture))
                .Append(',')
                .Append(row.SizeDelta.ToString(CultureInfo.InvariantCulture))
                .AppendLine();
        }

        // Excel 開啟 UTF-8 CSV 需要 BOM 才不會把中文型別名稱顯示成亂碼
        File.WriteAllText(
            file,
            builder.ToString(),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)
        );
        return file;
    }
}
