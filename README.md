# SVN 贴图对比

Windows 上看 SVN / TortoiseSVN 贴图差异的小工具。左边历史、右边工作副本，默认中间拉杆擦除对照。

支持 **PNG / JPG / TGA / BMP / WEBP / GIF**。解码走系统 WIC，TGA 自研读取，不依赖额外图像库。需要本机有 `svn.exe`（TortoiseSVN 安装时勾选 command line tools）才能拉历史；没有 SVN 时仍可对比两张本地图。

## 下载

到 [Releases](https://github.com/crl/VcsTextureCompare/releases) 下载最新 `VcsTextureCompare-win-x64.zip`，解压后运行 `VcsTextureCompare.exe`。自包含发布，不必先装 .NET。

## 用法

### 独立打开

1. 运行程序，拖入或点「打开」选择工作副本里的贴图。
2. 左侧列出 SVN 历史，默认对比 **BASE**。点某条修订会取出该版本。
3. 主视口默认拉杆：左历史 / 右本地。拖动手柄或竖线切换露出范围。
4. 滚轮缩放，中键或空白处拖动画布平移。按 **F** 居中适配窗口。

### TortoiseSVN Diff

1. 在程序里点「注册 TortoiseSVN Diff」（写入当前用户注册表）。
2. 在资源管理器里对 png / jpg / tga 等使用 **TortoiseSVN → Diff**。
3. 由 Tortoise 拉起时只显示对比视口，不再出现左侧历史栏。

命令行也可以直接比两张图：

```text
VcsTextureCompare.exe 历史图.png 本地图.png
```

Tortoise 外部 Diff 命令等价于：

```text
VcsTextureCompare.exe %base %mine %bname %yname
```

## 对比模式

| 模式 | 作用 |
|------|------|
| 拉杆 | 中间分割，左历史右本地 |
| 热力 | 像素差热力图 |
| 叠加 | 半透明叠两张图 |
| 闪烁 | 左右交替显示，找细微差异 |

拉杆快捷键：左右方向键微调分割线。放手后带惯性，碰到左右边界会反弹。

## 从源码编译

需要 [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)（含 Windows Desktop）。

```text
dotnet publish VcsTextureCompare.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish/win-x64
```

## 许可

仅用于项目内工具分发。未单独指定开源许可证。
