#include "pch.h"
#include "punctuator.hpp"
#include "transcript_text_normalizer.hpp"
#include <sherpa-onnx/c-api/c-api.h>
#include <cstring>
#include <iostream>
#include <filesystem>

namespace meetingai::transcribe {

// sherpa-onnx 的句柄一律以 const 指针形式返回并传入
struct Punctuator::Impl {
    const SherpaOnnxOfflinePunctuation* punct = nullptr;
};

Punctuator::Punctuator()
    : m_impl(std::make_unique<Impl>())
    , m_initialized(false)
{
}

Punctuator::~Punctuator()
{
    if (m_impl->punct != nullptr) {
        SherpaOnnxDestroyOfflinePunctuation(m_impl->punct);
        m_impl->punct = nullptr;
    }
}

bool Punctuator::Initialize(const std::string& modelDir)
{
    if (m_initialized) {
        m_lastError = "Already initialized";
        return false;
    }

    const std::string modelPath = modelDir + "\\model.int8.onnx";

    // 缺文件时 sherpa 内部可能直接 exit()，先自查给出可读错误
    {
        std::error_code ec;
        if (!std::filesystem::is_directory(modelDir, ec)) {
            m_lastError = "标点模型目录不存在: " + modelDir;
            return false;
        }
        if (!std::filesystem::exists(modelPath, ec)) {
            m_lastError = "标点模型文件不存在: " + modelPath;
            return false;
        }
    }

    SherpaOnnxOfflinePunctuationConfig config;
    memset(&config, 0, sizeof(config));

    config.model.ct_transformer = modelPath.c_str();
    config.model.num_threads = 1;
    config.model.provider = "cpu";
    config.model.debug = 0;

    std::cout << "[Punct] model: " << modelPath << std::endl;

    m_impl->punct = SherpaOnnxCreateOfflinePunctuation(&config);
    if (m_impl->punct == nullptr) {
        m_lastError = "SherpaOnnxCreateOfflinePunctuation 返回空";
        return false;
    }

    m_initialized = true;
    m_lastError.clear();
    return true;
}

std::string Punctuator::AddPunctuation(const std::string& text) const
{
    // 任何异常路径都返回原文：标点是锦上添花，绝不能让它挡住转录结果
    if (!m_initialized || m_impl->punct == nullptr || text.empty()) {
        return text;
    }

    const char* out = SherpaOfflinePunctuationAddPunct(m_impl->punct, text.c_str());
    if (out == nullptr) {
        m_lastError = "SherpaOfflinePunctuationAddPunct 返回空";
        return text;
    }

    std::string result(out);
    SherpaOfflinePunctuationFreeText(out);

    return result.empty() ? text : NormalizeBilingualTranscript(result);
}

} // namespace meetingai::transcribe
