using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Text;
using System.Xml.Linq;

namespace VcsTextureCompare.Svn;

/// <summary>
/// 调用本机 svn.exe（PATH 或 TortoiseSVN 安装目录），不经过 cmd.exe。
/// </summary>
public static class SvnClient
{
    private static string? _svnExe;
    private static bool _resolved;
    private static readonly List<string> TempFiles = new();
    private static readonly object TempLock = new();

    public static string? SvnExePath
    {
        get
        {
            EnsureResolved();
            return _svnExe;
        }
    }

    public static bool IsAvailable => SvnExePath != null;

    /// <summary>解析 svn.exe 路径，优先 PATH，再试 TortoiseSVN 默认安装位置。</summary>
    public static void EnsureResolved()
    {
        if (_resolved)
            return;
        _resolved = true;
        foreach (var candidate in EnumerateCandidates())
        {
            if (TryVersion(candidate))
            {
                _svnExe = candidate;
                return;
            }
        }
    }

    public static async Task<IReadOnlyList<SvnLogEntry>> GetLogAsync(string localPath, int limit = 80, CancellationToken ct = default)
    {
        var xml = await RunTextAsync(new[] { "log", "--xml", "-l", limit.ToString(CultureInfo.InvariantCulture), "--", localPath }, Path.GetDirectoryName(localPath), ct);
        return ParseLogXml(xml);
    }

    /// <summary>把指定修订导出到临时文件，返回路径。revision 可为 BASE 或数字。</summary>
    public static async Task<string> CatToTempAsync(string localPath, string revision, CancellationToken ct = default)
    {
        var ext = Path.GetExtension(localPath);
        if (string.IsNullOrEmpty(ext))
            ext = ".bin";
        var tempDir = Path.Combine(Path.GetTempPath(), "VcsTextureCompare");
        Directory.CreateDirectory(tempDir);
        var tempPath = Path.Combine(tempDir, $"{Path.GetFileNameWithoutExtension(localPath)}_{revision}_{Guid.NewGuid():N}{ext}");
        await RunBinaryToFileAsync(new[] { "cat", "-r", revision, "--", localPath }, Path.GetDirectoryName(localPath), tempPath, ct);
        TrackTemp(tempPath);
        return tempPath;
    }

    public static async Task<SvnInfo?> GetInfoAsync(string localPath, CancellationToken ct = default)
    {
        var xml = await RunTextAsync(new[] { "info", "--xml", "--", localPath }, Path.GetDirectoryName(localPath), ct);
        return ParseInfoXml(xml);
    }

    public static void CleanupTempFiles()
    {
        lock (TempLock)
        {
            foreach (var path in TempFiles)
            {
                try
                {
                    if (File.Exists(path))
                        File.Delete(path);
                }
                catch
                {
                    // 临时文件可能仍被占用
                }
            }
            TempFiles.Clear();
        }
    }

    private static void TrackTemp(string path)
    {
        lock (TempLock)
            TempFiles.Add(path);
    }

    private static IEnumerable<string> EnumerateCandidates()
    {
        yield return "svn.exe";
        yield return "svn";
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        yield return Path.Combine(pf, "TortoiseSVN", "bin", "svn.exe");
        yield return Path.Combine(pf86, "TortoiseSVN", "bin", "svn.exe");
    }

    private static bool TryVersion(string exe)
    {
        if (!string.Equals(exe, "svn", StringComparison.OrdinalIgnoreCase) && !File.Exists(exe))
            return false;
        try
        {
            using var process = Start(exe, new[] { "--version" }, null);
            if (!process.Start())
                return false;
            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            if (!process.WaitForExit(4000))
            {
                try { process.Kill(true); } catch { /* ignore */ }
                return false;
            }
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<string> RunTextAsync(string[] arguments, string? workingDir, CancellationToken ct)
    {
        var exe = RequireExe();
        using var process = Start(exe, arguments, workingDir);
        process.StartInfo.StandardOutputEncoding = Encoding.UTF8;
        process.StartInfo.StandardErrorEncoding = Encoding.UTF8;
        if (!process.Start())
            throw new InvalidOperationException("无法启动 svn。");
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        ThrowIfFailed(process.ExitCode, stderr, stdout);
        return stdout;
    }

    private static async Task RunBinaryToFileAsync(string[] arguments, string? workingDir, string outputPath, CancellationToken ct)
    {
        var exe = RequireExe();
        using var process = Start(exe, arguments, workingDir);
        // cat 输出为二进制，不能给 StandardOutput 指定文本编码
        process.StartInfo.StandardErrorEncoding = Encoding.UTF8;
        if (!process.Start())
            throw new InvalidOperationException("无法启动 svn。");
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        await using (var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.Read))
            await process.StandardOutput.BaseStream.CopyToAsync(fs, ct);
        await process.WaitForExitAsync(ct);
        var stderr = await stderrTask;
        if (process.ExitCode != 0)
        {
            try { File.Delete(outputPath); } catch { /* ignore */ }
            ThrowIfFailed(process.ExitCode, stderr, "");
        }
        if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(stderr) ? "svn cat 未写出文件。" : stderr.Trim());
    }

    private static Process Start(string exe, string[] arguments, string? workingDir)
    {
        var process = new Process();
        process.StartInfo.FileName = exe;
        process.StartInfo.ArgumentList.Clear();
        foreach (var arg in arguments)
            process.StartInfo.ArgumentList.Add(arg);
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.CreateNoWindow = true;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        if (!string.IsNullOrEmpty(workingDir))
            process.StartInfo.WorkingDirectory = workingDir;
        return process;
    }

    private static string RequireExe()
    {
        EnsureResolved();
        if (_svnExe == null)
            throw new InvalidOperationException("未找到 svn 命令，请确认已安装 SVN 命令行并加入 PATH（TortoiseSVN 需勾选 command line tools）。");
        return _svnExe;
    }

    private static void ThrowIfFailed(int exitCode, string stderr, string stdout)
    {
        if (exitCode == 0)
            return;
        var msg = (stderr ?? "").Trim();
        if (string.IsNullOrEmpty(msg))
            msg = (stdout ?? "").Trim();
        if (msg.Contains("W155007", StringComparison.Ordinal) || msg.Contains("not a working copy", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("该文件不在 SVN 工作副本中。");
        if (msg.Contains("W155010", StringComparison.Ordinal) || msg.Contains("was not found", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("该文件尚未纳入 SVN 版本控制。");
        throw new InvalidOperationException(string.IsNullOrEmpty(msg) ? $"svn 失败，退出码 {exitCode}。" : msg);
    }

    private static List<SvnLogEntry> ParseLogXml(string xml)
    {
        var list = new List<SvnLogEntry>();
        if (string.IsNullOrWhiteSpace(xml))
            return list;
        var doc = XDocument.Parse(xml);
        foreach (var entry in doc.Descendants("logentry"))
        {
            var revAttr = entry.Attribute("revision")?.Value;
            long.TryParse(revAttr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var rev);
            DateTime.TryParse(entry.Element("date")?.Value, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date);
            list.Add(new SvnLogEntry
            {
                Revision = rev,
                Author = entry.Element("author")?.Value ?? "",
                Date = date,
                Message = entry.Element("msg")?.Value ?? ""
            });
        }
        return list;
    }

    private static SvnInfo? ParseInfoXml(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
            return null;
        var doc = XDocument.Parse(xml);
        var entry = doc.Descendants("entry").FirstOrDefault();
        if (entry == null)
            return null;
        long.TryParse(entry.Attribute("revision")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var rev);
        var commit = entry.Element("commit");
        long.TryParse(commit?.Attribute("revision")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var commitRev);
        return new SvnInfo
        {
            Revision = rev,
            CommitRevision = commitRev,
            Url = entry.Element("url")?.Value ?? ""
        };
    }
}

/// <summary>svn info 中与展示相关的字段。</summary>
public sealed class SvnInfo
{
    public long Revision { get; init; }
    public long CommitRevision { get; init; }
    public string Url { get; init; } = "";
}
