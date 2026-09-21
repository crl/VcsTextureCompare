using System.IO;

namespace VcsTextureCompare.Cli;

/// <summary>
/// 启动参数。无参数为独立浏览；两个路径槽视为 Tortoise Diff（%base %mine），
/// 新增文件时左侧常不存在或 0 字节。
/// </summary>
public sealed class StartupArgs
{
    /// <summary>历史 / 左侧图（Tortoise %base）</summary>
    public string? LeftPath { get; init; }

    /// <summary>本地 / 右侧图（Tortoise %mine）</summary>
    public string? RightPath { get; init; }

    public string? LeftTitle { get; init; }
    public string? RightTitle { get; init; }

    /// <summary>命令行给了两个路径槽，按 Diff 成对加载（Tortoise 或手动两图）。</summary>
    public bool FromTortoise { get; init; }

    /// <summary>至少有一侧能打开的图，直接进入对比并隐藏历史栏。</summary>
    public bool DirectCompare => HasUsableLeft || HasUsableRight;

    public bool HasUsableLeft => IsUsable(LeftPath);
    public bool HasUsableRight => IsUsable(RightPath);

    public static bool IsUsable(string? path) =>
        !string.IsNullOrWhiteSpace(path) && File.Exists(path) && new FileInfo(path).Length > 0;

    public static StartupArgs Parse(string[] args)
    {
        if (args == null || args.Length == 0)
            return new StartupArgs();

        if (args.Length == 1)
        {
            var one = NormalizeSlot(args[0]);
            return IsUsable(one) ? new StartupArgs { RightPath = one } : new StartupArgs();
        }

        return new StartupArgs
        {
            LeftPath = NormalizeSlot(args[0]),
            RightPath = NormalizeSlot(args[1]),
            LeftTitle = args.Length > 2 && !string.IsNullOrWhiteSpace(args[2]) ? args[2] : null,
            RightTitle = args.Length > 3 && !string.IsNullOrWhiteSpace(args[3]) ? args[3] : null,
            FromTortoise = true
        };
    }

    private static string? NormalizeSlot(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        try
        {
            return Path.GetFullPath(raw);
        }
        catch (Exception)
        {
            return raw.Trim();
        }
    }
}
