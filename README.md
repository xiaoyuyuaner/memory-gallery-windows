<p align="center">
  <img src="memory-gallery-icon.png" width="112" alt="拾光图标">
</p>

<h1 align="center">拾光 · 桌面相册</h1>

<p align="center">
  把照片挂在 Windows 桌面顶部，像晾衣绳一样随时展开、浏览与收起。
</p>

<p align="center">
  <a href="https://github.com/xiaoyuyuaner/memory-gallery-windows/releases/latest">下载最新版</a>
  ·
  <a href="使用说明.txt">使用说明</a>
  ·
  <a href="LICENSE.txt">MIT License</a>
</p>

![Windows](https://img.shields.io/badge/Windows-10%20%2F%2011-2478CC?logo=windows)
![.NET Framework](https://img.shields.io/badge/.NET%20Framework-4.8-512BD4)
![License](https://img.shields.io/badge/license-MIT-green)

拾光是一款完全本地运行的 Windows 桌面照片相册。鼠标停在屏幕顶部的小入口，照片晾衣绳便会滑下来；离开后自动收起，不打断原本的桌面使用。

## 核心体验

- 顶部入口可以自由拖动，并自动记住位置。
- 晾衣绳支持连续左右拖动、滚轮浏览和屏幕边缘自动移动。
- 单击、双击或长按照片，以弹出动效打开大图。
- 大图支持 `Ctrl + 滚轮` 在 100%–300% 之间缩放，放大后可拖动查看细节。
- 鼠标离开大图后自动关闭，并播放轻量彩色粒子动效。
- 照片可以添加备注，也可以创建独立的回忆便签。
- 照片右上角 `×` 只会从相册移除，不会删除电脑里的原始文件。
- 所有数据只保存在本机，不联网、不上传。

## 下载与安装

1. 打开 [Releases](https://github.com/xiaoyuyuaner/memory-gallery-windows/releases) 页面。
2. 下载最新的 `MemoryGallery-*-windows-portable.zip`。
3. 解压到一个固定目录，运行 `MemoryGallery.exe`。
4. 从托盘菜单选择“导入照片”，或者直接把照片拖到晾衣绳上。

支持 Windows 10/11，需要 .NET Framework 4.8。程序为便携版，不需要安装。

当前版本尚未进行代码签名，首次运行时 Windows SmartScreen 可能显示提醒。请只从本仓库的正式 Release 下载，并核对 Release 中提供的 SHA-256。

## 操作方式

| 操作 | 效果 |
| --- | --- |
| 停在顶部蓝线 | 展开照片晾衣绳 |
| 拖动顶部蓝线 | 调整入口位置 |
| 拖动绳子或照片 | 连续左右浏览 |
| 停在屏幕左右边缘 | 自动向对应方向浏览 |
| 鼠标滚轮 | 快速左右浏览 |
| 单击、双击或长按照片 | 打开大图 |
| 大图中 `Ctrl + 滚轮` | 在 100%–300% 之间缩放 |
| 放大后按住照片拖动 | 查看不同区域的细节 |
| 鼠标离开大图 | 自动关闭并播放粒子动效 |
| 照片右上角 `×` | 仅从相册移除，保留原始文件 |
| `Ctrl + Alt + T` | 固定展开 / 收起 |

完整说明见 [使用说明.txt](使用说明.txt)。

## 本地数据与容量

- 新导入的照片保留在原位置；相册只记录文件路径并生成小型缩略图。
- 最多记录 300 张照片，达到上限后停止导入，不会自动淘汰旧照片。
- 最多保存 100 张回忆便签。
- 40 张以内预加载小缩略图；更多照片采用可视区域虚拟化和按需读取。
- 大图最多缓存 2 张，相册收起时释放缓存。
- 开机启动默认关闭，可在托盘菜单中主动启用。

本地数据默认保存在：

```text
%LOCALAPPDATA%\MemoryGallery
```

## macOS 版本与贡献

拾光目前仅支持 Windows。现有版本基于 WPF、WinForms 和部分 Win32 接口，无法直接编译为 macOS 应用，因此 Mac 版本需要使用原生技术重新实现。

我们非常欢迎熟悉 Swift、SwiftUI 或 AppKit 的开发者参与 macOS 版本。目标不是逐行翻译 Windows 代码，而是在保留“桌面顶部展开照片晾衣绳”这一核心体验的同时，做出符合 macOS 使用习惯的原生应用。

可以优先参与的方向包括：

- 桌面顶部触发区域与无边框悬浮窗口。
- 照片晾衣绳布局、连续拖动和边缘自动浏览。
- 大图预览、缩放、平移和自动关闭。
- 多显示器、全屏应用与 Mission Control 场景适配。
- 菜单栏入口、登录时启动和本地照片权限处理。
- 与 Windows 版兼容的照片备注、回忆便签和设置数据格式。
- Developer ID 签名、notarization 及 GitHub Actions 自动构建。

## 从源码构建

在 Windows PowerShell 中运行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
```

构建脚本调用 Windows 自带的 .NET Framework C# 编译器，生成 `dist\MemoryGallery.exe`。

运行完整自检：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -OutputDirectory .\test-build -IncludeTests
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\verify.ps1 -BinaryPath .\test-build\MemoryGallery.exe
```

`-IncludeTests` 只用于本地验证和持续集成；正式构建与发布包不包含测试入口。仓库也配置了 GitHub Actions，在推送和 Pull Request 时自动构建并运行核心自检。

## 来源与许可

本项目基于 MIT 许可的 [TE1233/snapline-windows](https://github.com/TE1233/snapline-windows) 修改，并借鉴了 [Tendedero](https://github.com/alejandrobujan/tendedero) 的桌面交互构想，现已改造成以本地照片为核心的 Windows 桌面相册。

项目采用 MIT License。详细版权、来源及第三方说明见 [LICENSE.txt](LICENSE.txt) 和 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。
