using System.IO;
using Microsoft.Win32;

namespace VcsTextureCompare.Tortoise;

/// <summary>
/// 把本工具注册为 TortoiseSVN 按扩展名的外部 Diff Viewer（写入当前用户注册表）。
/// </summary>
public static class DiffToolRegistrar
{
    public static readonly string[] Extensions = { ".png", ".jpg", ".jpeg", ".tga", ".bmp", ".webp", ".gif" };

    private const string KeyPath = @"Software\TortoiseSVN\DiffTools";

    public static bool IsTortoiseInstalled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\TortoiseSVN");
        if (key != null)
            return true;
        using var lm = Registry.LocalMachine.OpenSubKey(@"Software\TortoiseSVN");
        return lm != null;
    }

    /// <summary>注册成功返回说明文字；失败抛异常。</summary>
    public static string Register(string exePath)
    {
        if (!File.Exists(exePath))
            throw new FileNotFoundException("找不到当前程序，无法注册。", exePath);
        if (!IsTortoiseInstalled())
            throw new InvalidOperationException("未检测到 TortoiseSVN，请先安装后再注册 Diff 工具。");

        var cmd = $"\"{exePath}\" %base %mine %bname %yname";
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
        if (key == null)
            throw new InvalidOperationException("无法写入注册表 HKCU\\Software\\TortoiseSVN\\DiffTools。");
        foreach (var ext in Extensions)
            key.SetValue(ext, cmd);
        return "已注册为 TortoiseSVN 图片 Diff 工具（" + string.Join(" ", Extensions) + "）。在资源管理器中对图片使用 TortoiseSVN → Diff 即可打开本工具。";
    }
}
