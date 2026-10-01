namespace ClrDiag.Tests;

/// <summary>測試用的暫存資料夾樹；Dispose 時整個刪除。</summary>
public sealed class TempTree : IDisposable
{
    /// <summary>暫存資料夾的完整路徑。</summary>
    public string Root { get; } =
        Path.Combine(Path.GetTempPath(), "clrdiag-tests", Guid.NewGuid().ToString("N"));

    public TempTree()
    {
        Directory.CreateDirectory(Root);
    }

    /// <summary>以 / 分隔的相對路徑建立空檔案（含中間資料夾），回傳完整路徑。</summary>
    public string File(string relativePath)
    {
        var fullPath = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        System.IO.File.WriteAllText(fullPath, "");
        return fullPath;
    }

    /// <summary>暫存資料夾內某個相對路徑的完整路徑，不建立任何東西。</summary>
    public string PathOf(string relativePath) =>
        Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException) { }
    }
}
