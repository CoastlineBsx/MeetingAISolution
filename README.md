# MeetingAI

MeetingAI 是一个 Windows 本地会议助手。Host 使用 .NET 8 和 WinUI 3，Worker 使用 C++17、OpenVINO GenAI 和 Sherpa ONNX。

## 当前支持范围

- Windows 10/11 x64
- Visual Studio 2022，MSVC v143
- .NET 8 SDK（更高版本 SDK 可通过 `global.json` roll-forward 使用）
- Windows 11 SDK 10.0.26100

当前正式构建入口只有 `MeetingAISolution.sln`。根目录 CMake 配置已暂停维护，不能用于正式构建。x86、ARM64、Linux 和 macOS 当前不受支持。

## 依赖分层

- .NET：NuGet、`Directory.Packages.props` 和 `packages.lock.json`
- 常规 C++：根目录唯一的 `vcpkg.json`
- 原生 AI SDK：`dependencies/native-win-x64.json` 描述的校验 bundle
- AI 模型：`dependencies/models.json` 描述的按功能 bundle
- 仓库内源码依赖：SQLite 和 libfvad

生成目录、`vcpkg_installed`、原生 SDK 和模型均不进入 Git。

## 第一次准备

1. 在 Visual Studio Installer 中选择 **Import configuration**，导入 `.vsconfig`。
2. 获取 `meetingai-native-win-x64-2025.3-1.zip` 及其 SHA256。
3. 执行：

```powershell
.\scripts\setup.ps1 `
  -Configuration Release `
  -NativeBundle D:\MeetingAICache\meetingai-native-win-x64-2025.3-1.zip `
  -NativeBundleSha256 <sha256>
```

也可以设置环境变量：

```powershell
$env:MEETINGAI_NATIVE_BUNDLE_URI = 'https://.../meetingai-native-win-x64-2025.3-1.zip'
$env:MEETINGAI_NATIVE_BUNDLE_SHA256 = '<sha256>'
.\scripts\setup.ps1
```

私有 GitHub Release 可以通过 `MEETINGAI_DOWNLOAD_TOKEN` 提供下载令牌。GitHub Actions 会自动使用 `GITHUB_TOKEN`。

## 构建

```powershell
.\scripts\build.ps1 -Configuration Debug
.\scripts\build.ps1 -Configuration Release
```

脚本自动定位 Visual Studio，不依赖 Community/Professional/Enterprise 的固定安装路径。

## 模型

模型不在源码仓库中。可用功能包：

- `streaming`：Sherpa 实时识别和标点
- `offline-transcription`：OpenVINO Whisper
- `translation`：中英离线翻译
- `rag`：OpenVINO BGE-M3
- `assistant`：OpenVINO Granite
- `vision`：OpenVINO LLaVA
- `image-generation`：OpenVINO Stable Diffusion

示例：

```powershell
$env:MEETINGAI_MODEL_STREAMING_URI = 'https://.../meetingai-model-streaming.zip'
$env:MEETINGAI_MODEL_STREAMING_SHA256 = '<sha256>'
.\scripts\Restore-Models.ps1 -Feature streaming
```

原开发电脑可以生成可上传到私有 Release 的包：

```powershell
.\scripts\New-NativeBundle.ps1
.\scripts\New-ModelBundle.ps1 -Feature streaming
.\scripts\New-ModelBundle.ps1 -Feature offline-transcription
```

输出位于 `artifacts/bundles`，并附带 `.sha256` 文件。

## 生成用户包

```powershell
.\scripts\package.ps1 -Configuration Release
```

输出为 `artifacts/publish/MeetingAI-Release-win-x64.zip`。用户包自带 .NET 运行时和 Windows App SDK，不要求目标电脑安装 Visual Studio 或 .NET SDK；默认不包含大模型。便携包自动包含 `portable.flag`，因此放在 C、D、E 等可写磁盘时，数据和模型适配缓存都会保存在程序目录下的 `data` 文件夹中。

## CI 配置

在 GitHub 仓库中配置 Actions secrets：

- `MEETINGAI_NATIVE_BUNDLE_URI`
- `MEETINGAI_NATIVE_BUNDLE_SHA256`

`windows-x64` workflow 会在干净的 Windows runner 上恢复依赖、构建 Release x64，并生成不含模型的可运行包。

## 已移除的旧路线

- whisper.cpp / GGML legacy Whisper
- Python Flask / sentence-transformers BGE-M3 服务

文件转录现在统一使用 OpenVINO Whisper；实时会议转录使用 Sherpa ONNX。
