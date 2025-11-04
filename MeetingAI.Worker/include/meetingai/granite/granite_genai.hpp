#pragma once
#include <openvino/genai/llm_pipeline.hpp>
#include <string>
#include <functional>
#include <memory>

namespace meetingai::granite {

class GraniteGenAI {
public:
    GraniteGenAI(const std::string& model_path, const std::string& device = "NPU");
    
    // 普通生成
    std::string generate(const std::string& prompt, 
                        int max_tokens = 128, 
                        float temperature = 0.7f);
    
    // 流式生成
    void generateStream(const std::string& prompt,
                       std::function<void(std::string)> callback,
                       int max_tokens = 128,
                       float temperature = 0.7f);
    
private:
    std::unique_ptr<ov::genai::LLMPipeline> pipeline_;
};

} // namespace meetingai::granite
