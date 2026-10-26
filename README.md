# 拾光 · 桌面相册

拾光是一款完全本地运行的 Windows 桌面照片晾衣绳。鼠标停在屏幕顶部入口即可展开照片，离开后自动收起。

## 主要功能

- 顶部入口可自由拖动并记住位置。
- 晾衣绳可连续左右拖动，照片整体跟随。
- 屏幕左右边缘停留或滚动鼠标可自动浏览。
- 单击、双击或长按照片打开大图，离开大图自动关闭。
- 大图支持 `Ctrl + 滚轮` 在 100%–300% 之间缩放。
- 放大后按住照片拖动，可查看上下左右细节；缩回 100% 自动复位。
- 照片可添加备注；相册还支持独立的回忆便签。
- 回忆便签最多 100 张。
- 照片右上角 `×` 只从相册移除，不会删除原始照片。
- 最多记录 300 张照片，达到上限后停止导入，不自动删除旧照片。
- 完全本地保存，不联网、不上传。
- 开机启动默认关闭，可从托盘菜单主动启用。

## 性能策略

- 40 张以内预加载小缩略图。
- 超过 40 张后，只创建和加载屏幕附近的照片卡片。
- 大图最多缓存 2 张，相册收起时清空。
- 大量照片时不缓存整条超长绳，避免移动时产生超宽位图。
- 原始高清照片只在查看时按需读取。

## 使用

运行 `album-dist/MemoryGallery.exe`。目标系统为 Windows 10/11，需要 .NET Framework 4.8。

当前便携构建未进行代码签名，首次下载运行时 Windows SmartScreen 可能显示提醒。请从项目的正式 Release 页面下载并核对发布包 SHA-256。

| 操作 | 效果 |
| --- | --- |
| 停在顶部蓝线 | 展开照片晾衣绳 |
| 拖动顶部蓝线 | 调整入口位置 |
| 拖动绳子或照片 | 连续左右浏览 |
| 停在屏幕左右边缘 | 自动向对应方向浏览 |
| 鼠标滚轮 | 快速左右浏览 |
| 单击、双击或长按照片 | 打开大图 |
| 大图中 `Ctrl + 滚轮` | 100%–300% 缩放 |
| 放大后按住照片拖动 | 查看不同区域的细节 |
| 鼠标离开大图 | 自动关闭并播放烟花效果 |
| 照片右上角 `×` | 仅从相册移除，保留原始文件 |
| `Ctrl + Alt + T` | 固定展开 / 收起 |

更完整的说明见 [使用说明.txt](使用说明.txt)。

## 构建

运行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
```

构建脚本调用 Windows 自带的 .NET Framework C# 编译器，输出 `MemoryGallery.exe`。

运行完整自检：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -OutputDirectory .\test-build -IncludeTests
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\verify.ps1 -BinaryPath .\test-build\MemoryGallery.exe
```

`-IncludeTests` 只用于本地验证和持续集成；正式构建与发布包不会包含测试入口。

## 来源与许可

这是受 Tendedero 和 Snapline 交互启发的独立 Windows 实现，使用自行设计的名称与图标。源码许可与第三方来源说明见 `LICENSE.txt` 和 `THIRD_PARTY_NOTICES.md`。
