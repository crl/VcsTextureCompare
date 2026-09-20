using System.IO;

namespace VcsTextureCompare.Cli;

/// <summary>
/// 启动参数。无参数为独立浏览；两个已存在的路径视为左右对比（TortoiseSVN Diff：%base %mine）。
/// </summary>
public sealed class StartupArgs
{
    /// <summary>历史 / 左侧图（Tortoise %base）</summary>
    public string? LeftPath { get; init; }

    /// <summary>本地 / 右侧图（Tortoise %mine）</summary>
    public string? RightPath { get; init; }

    public string? LeftTitle { get; init; }
    public string? RightTitle { get; init; }

    /// <summary>已带齐两张图，直接进入对比，不必先选文件。</summary>
    public bool DirectCompare =>
        !string.IsNullOrWhiteSpace(LeftPath) && !string.IsNullOrWhiteSpace(RightPath);

    public static StartupArgs Parse(string[] args)
    {
        if (args == null || args.Length == 0)
            return new StartupArgs();

        var files = new List<string>();
        var titles = new List<string>();
        foreach (var raw in args)
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;
            if (File.Exists(raw))
                files.Add(Path.GetFullPath(raw));
            else if (files.Count >= 2)
                titles.Add(raw);
        }

        if (files.Count >= 2)
        {
            return new StartupArgs
            {
                LeftPath = files[0],
                RightPath = files[1],
                LeftTitle = titles.Count > 0 ? titles[0] : null,
                RightTitle = titles.Count > 1 ? titles[1] : null
            };
        }

        if (files.Count == 1)
            return new StartupArgs { RightPath = files[0] };

        return new StartupArgs();
    }
}
