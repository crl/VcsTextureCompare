namespace VcsTextureCompare.Svn;

/// <summary>
/// 一条 SVN 日志。Revision 为 -1 时表示工作副本 BASE，不是仓库修订号。
/// </summary>
public sealed class SvnLogEntry
{
    public const long BaseRevision = -1;

    public long Revision { get; init; }
    public string Author { get; init; } = "";
    public DateTime Date { get; init; }
    public string Message { get; init; } = "";
    public bool IsBase => Revision == BaseRevision;

    public string RevisionText => IsBase ? "BASE" : Revision.ToString();
    public string DateText => IsBase || Date == default ? "" : Date.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    public string AuthorText => Author;
    public string MessageText => string.IsNullOrWhiteSpace(Message) ? (IsBase ? "工作副本基准版本" : "") : OneLine(Message);

    public static SvnLogEntry CreateBase() => new()
    {
        Revision = BaseRevision,
        Message = "工作副本基准版本"
    };

    private static string OneLine(string text)
    {
        return text.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ').Trim();
    }
}
