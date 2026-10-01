using ClrDiag.Core;

namespace ClrDiag.Tests;

/// <summary>NoticeWriter 依互動模式決定提示訊息的輸出位置。</summary>
public sealed class NoticeWriterTests
{
    [Fact]
    public void 非互動模式以純文字寫到標準錯誤且不經過主控台()
    {
        var error = new StringWriter();
        var console = new List<string>();

        new NoticeWriter(false, error, console.Add).Write("[專案] 掃描已達上限");

        Assert.Equal("[專案] 掃描已達上限" + Environment.NewLine, error.ToString());
        Assert.Empty(console);
    }

    [Fact]
    public void 互動模式交給主控台且不寫標準錯誤()
    {
        var error = new StringWriter();
        var console = new List<string>();

        new NoticeWriter(true, error, console.Add).Write("已取消");

        Assert.Equal(new[] { "已取消" }, console);
        Assert.Equal("", error.ToString());
    }
}
