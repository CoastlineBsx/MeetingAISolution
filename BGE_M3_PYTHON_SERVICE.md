# bge-m3 使用 sentence-transformers 方案

## 🎯 最佳方案：Python 服务 + C++ Worker

由于 bge-m3 专门为 sentence-transformers 设计，我们使用混合架构：

```
C# → C++ Worker → Python bge-m3 服务
      ├─ Granite (NPU)
      └─ HTTP → bge-m3 (sentence-transformers)
```

---

## 📦 第一步：安装 Python 依赖

```bash
pip install sentence-transformers flask numpy
```

---

## 🚀 第二步：启动 bge-m3 服务

```bash
cd C:\VisualStudio\MeetingAISolution\MeetingAI.Worker
python bge_m3_service.py

# 首次运行会下载模型（约 1.1GB）
# 输出:
# Loading bge-m3 model...
# Model loaded!
# * Running on http://127.0.0.1:8081
```

**保持这个窗口运行！**

---

## 🔧 第三步：编译 C++ Worker

### 3.1 安装 libcurl（HTTP 客户端）

```powershell
# vcpkg 安装
vcpkg install curl:x64-windows
```

### 3.2 更新 CMakeLists.txt

```cmake
# 添加 curl 依赖
find_package(CURL REQUIRED)

target_link_libraries(MeetingAI.Worker PRIVATE
    openvino::runtime
    openvino::genai
    CURL::libcurl
)
```

### 3.3 编译

```powershell
cd build
cmake .. -DCMAKE_TOOLCHAIN_FILE="C:/vcpkg/scripts/buildsystems/vcpkg.cmake"
cmake --build . --config Release
```

---

## ✅ 第四步：测试

### 4.1 测试 Python 服务

```bash
# 新开一个终端
curl -X POST http://127.0.0.1:8081/embed \
  -H "Content-Type: application/json" \
  -d "{\"text\":\"人工智能\"}"

# 应该返回 1024 维向量
```

### 4.2 测试完整流程

```powershell
# 1. 启动 Python 服务（窗口 1）
python bge_m3_service.py

# 2. 启动 C++ Worker（窗口 2）
cd build\bin\Release
.\MeetingAI.Worker.exe

# 应该看到:
# [Granite GenAI] ✅ Initialized on NPU
# [Embedding] Connected to Python bge-m3 service at http://127.0.0.1:8081

# 3. 启动 C# 前端（Visual Studio）
# 测试 RAG 功能
```

---

## 📊 性能对比

| 方案 | 准确度 | 速度 | 复杂度 | 推荐度 |
|------|--------|------|--------|--------|
| OpenVINO 直接转换 | ⭐⭐⭐ | 快 | 简单 | ⭐⭐ |
| C++ Mean Pooling | ⭐⭐⭐⭐ | 快 | 中等 | ⭐⭐⭐ |
| **Python 服务** | ⭐⭐⭐⭐⭐ | 中 | 简单 | ⭐⭐⭐⭐⭐ |

---

## 🔍 架构细节

### 完整调用链

```
用户提问
  ↓
C# RAGService.QueryAsync()
  ↓
Named Pipe → C++ Worker
  ↓
EmbeddingPythonService::encode()
  ↓
HTTP POST → http://127.0.0.1:8081/embed
  ↓
Python Flask
  ↓
sentence_transformers.SentenceTransformer
  ↓
bge-m3 模型推理
  ↓
返回 1024 维向量
  ↓
C++ Worker
  ↓
SQLite 向量检索
  ↓
Granite NPU 生成
  ↓
流式返回给 C#
```

---

## ⚙️ 优化选项

### 1. bge-m3 NPU 加速（可选）

```python
# bge_m3_service.py
from optimum.intel import OVModelForFeatureExtraction

# 使用 OpenVINO 加速
model = OVModelForFeatureExtraction.from_pretrained(
    "BAAI/bge-m3",
    export=True,
    device="NPU"  # 如果 NPU 支持
)
```

### 2. 批处理优化

```python
# 支持批量处理
@app.route('/embed_batch', methods=['POST'])
def embed_batch():
    texts = request.json['texts']
    embeddings = model.encode(texts, batch_size=8)
    return jsonify({'embeddings': embeddings.tolist()})
```

### 3. 缓存优化

```python
from functools import lru_cache

@lru_cache(maxsize=1000)
def cached_encode(text):
    return model.encode(text)
```

---

## 🎉 总结

**最佳方案**：
- ✅ Python 服务 + sentence-transformers
- ✅ 完全兼容 bge-m3 官方实现
- ✅ 简单可靠，易于维护
- ✅ 可以后续优化（NPU、批处理、缓存）

**缺点**：
- ⚠️ 多一个进程（Python 服务）
- ⚠️ HTTP 通信有轻微开销（~1-2ms）

**但这些缺点完全可以接受！**
