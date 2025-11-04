# C++ Worker 编译步骤（完整版）

## ✅ 你已完成的准备工作

- ✅ 模型文件已放置：
  - `models/granite-3.3-2b-npu/`
  - `models/bge-m3-npu/`
- ✅ OpenVINO GenAI 已放到 `third_party/openvino_genai/`
- ✅ RAG 源代码已创建：
  - `src/granite/granite_genai.cpp`
  - `src/embedding/embedding_genai.cpp`

---

## 📋 接下来需要做的（3 步）

### 第 1 步：备份并替换 CMakeLists.txt

```powershell
cd C:\VisualStudio\MeetingAISolution\MeetingAI.Worker

# 备份原文件
Copy-Item CMakeLists.txt CMakeLists.txt.backup

# 使用新的集成版本
Copy-Item CMakeLists_INTEGRATED.txt CMakeLists.txt -Force
```

---

### 第 2 步：在 Visual Studio 中打开并配置

1. **打开** Visual Studio 2022
2. **文件** → **打开** → **CMake...**
3. 选择 `C:\VisualStudio\MeetingAISolution\MeetingAI.Worker\CMakeLists.txt`
4. Visual Studio 会自动配置 CMake

**等待配置完成**（约 1-2 分钟）

---

### 第 3 步：编译

在 Visual Studio 中：

1. **生成** → **全部重新生成**
2. 或者点击工具栏的 **▶ 本地 Windows 调试器**

**预期输出位置**：
```
MeetingAI.Worker\out\build\x64-Debug\bin\Debug\MeetingAIWorker.exe
或
MeetingAI.Worker\out\build\x64-Release\bin\Release\MeetingAIWorker.exe
```

---

## ⚡ 快速命令行方式（可选）

如果你想用命令行编译：

```powershell
# 1. 打开 Developer Command Prompt for VS 2022

# 2. 进入目录
cd C:\VisualStudio\MeetingAISolution\MeetingAI.Worker

# 3. 备份并替换 CMakeLists.txt
Copy-Item CMakeLists.txt CMakeLists.txt.backup
Copy-Item CMakeLists_INTEGRATED.txt CMakeLists.txt -Force

# 4. 创建 build 目录
mkdir build -Force
cd build

# 5. 配置 CMake
cmake .. -G "Visual Studio 17 2022" -A x64

# 6. 编译 (Release)
cmake --build . --config Release

# 7. 或编译 (Debug)
cmake --build . --config Debug
```

**编译完成后，可执行文件在**：
```
build\bin\Release\MeetingAIWorker.exe
或
build\bin\Debug\MeetingAIWorker.exe
```

---

## 🔍 验证编译成功

运行生成的程序：

```powershell
# Release 版本
cd build\bin\Release
.\MeetingAIWorker.exe

# 应该看到：
# [Granite GenAI] ✅ Initialized on NPU
# [Embedding GenAI] ✅ Initialized on NPU, dim=1024
# [Main] 等待客户端连接...
```

---

## ❓ 如果遇到错误

### 错误 1: 找不到 openvino/genai/llm_pipeline.hpp

**解决**：检查 `third_party/openvino_genai/include` 是否存在

### 错误 2: 链接错误 LNK2019

**解决**：检查 `.lib` 文件路径是否正确

### 错误 3: 运行时找不到 DLL

**解决**：CMakeLists 会自动复制 DLL，但你也可以手动复制：

```powershell
# 复制 OpenVINO DLL
Copy-Item "third_party\openvino_genai\bin\intel64\Release\*.dll" "build\bin\Release\"

# 或 Debug 版本
Copy-Item "third_party\openvino_genai\bin\intel64\Debug\*.dll" "build\bin\Debug\"
```

---

## 🎯 推荐方式

**最简单**：用 Visual Studio 打开 CMakeLists.txt 并编译

**优点**：
- ✅ 自动配置
- ✅ 智能提示
- ✅ 调试方便

**只需 3 步**：
1. 替换 CMakeLists.txt
2. 用 VS 打开
3. 点击编译

**就这么简单！** 🚀
