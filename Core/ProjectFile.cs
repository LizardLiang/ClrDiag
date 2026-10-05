using System.Xml.Linq;

namespace ClrDiag.Core;

/// <summary>專案檔判斷的共用邏輯（副檔名、XML 讀取、SDK 樣式），避免建置與啟動推斷各寫一份。</summary>
internal static class ProjectFile
{
    /// <summary>Microsoft.NET.Sdk.Web 的 Sdk 名稱。</summary>
    public const string SdkWebName = "Microsoft.NET.Sdk.Web";

    /// <summary>.sln 或 .slnx 方案檔。</summary>
    public static bool IsSolution(string path)
    {
        string extension = Path.GetExtension(path);
        return extension.Equals(".sln", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>讀取 XML 的根元素；檔案不存在或格式錯誤回傳 null。</summary>
    public static XElement? LoadXml(string path)
    {
        try
        {
            return XDocument.Load(path).Root;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>專案根元素宣告的 Sdk 名稱：&lt;Project Sdk="..."&gt; 屬性，或 &lt;Sdk Name="..."/&gt; 子元素。</summary>
    public static IEnumerable<string> SdkNames(XElement root)
    {
        if ((string?)root.Attribute("Sdk") is { Length: > 0 } attribute)
        {
            yield return attribute;
        }

        foreach (XElement element in root.Elements().Where(e => e.Name.LocalName == "Sdk"))
        {
            if ((string?)element.Attribute("Name") is { Length: > 0 } name)
            {
                yield return name;
            }
        }
    }

    /// <summary>是 SDK 樣式專案（宣告了任何 Sdk）；方案檔、讀不到或格式錯誤當成舊式專案。</summary>
    public static bool IsSdkStyle(string path)
    {
        if (IsSolution(path))
        {
            return false;
        }

        XElement? root = LoadXml(path);
        return root is not null && SdkNames(root).Any();
    }

    /// <summary>是 Microsoft.NET.Sdk.Web 專案。</summary>
    public static bool IsSdkWeb(XElement root) =>
        SdkNames(root).Any(name => string.Equals(name, SdkWebName, StringComparison.OrdinalIgnoreCase));
}
