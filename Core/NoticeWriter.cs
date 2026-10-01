namespace ClrDiag.Core;

/// <summary>
/// 非致命提示訊息（旗標衝突、掃描上限、記錄寫入失敗）的輸出位置。
/// 互動模式交給 console 以主控台樣式顯示；其餘模式以純文字寫到 error（標準錯誤），
/// 標準輸出只留給批次結果，例如 --pipe-name 的管道名稱。
/// </summary>
public sealed class NoticeWriter
{
    private readonly bool _interactive;
    private readonly TextWriter _error;
    private readonly Action<string> _console;

    /// <summary>interactive 表示這次執行是互動模式；error 是非互動時的輸出；console 是互動時的輸出。</summary>
    public NoticeWriter(bool interactive, TextWriter error, Action<string> console)
    {
        _interactive = interactive;
        _error = error;
        _console = console;
    }

    /// <summary>輸出一則提示訊息。</summary>
    public void Write(string message)
    {
        if (_interactive)
            _console(message);
        else
            _error.WriteLine(message);
    }
}
