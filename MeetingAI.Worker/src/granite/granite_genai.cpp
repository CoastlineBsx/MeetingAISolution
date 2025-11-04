#include "pch.h"
#include "granite_genai.hpp"
#include <iostream>

namespace meetingai::granite {

GraniteGenAI::GraniteGenAI(const std::string& model_path, const std::string& device) {
    try {
        pipeline_ = std::make_unique<ov::genai::LLMPipeline>(model_path, device);
        std::cout << "[Granite GenAI] ✅ Initialized on " << device << std::endl;
    } catch (const std::exception& e) {
        std::cerr << "[Granite GenAI] ❌ Failed: " << e.what() << std::endl;
        throw;
    }
}

std::string GraniteGenAI::generate(const std::string& prompt, 
                                   int max_tokens, 
                                   float temperature) {
    ov::genai::GenerationConfig config;
    config.max_new_tokens = max_tokens;
    config.temperature = temperature;
    config.do_sample = (temperature > 0.0f);
    
    return pipeline_->generate(prompt, config);
}

void GraniteGenAI::generateStream(const std::string& prompt,
                                  std::function<void(std::string)> callback,
                                  int max_tokens,
                                  float temperature) {
    ov::genai::GenerationConfig config;
    config.max_new_tokens = max_tokens;
    config.temperature = temperature;
    config.do_sample = (temperature > 0.0f);
    
    // 流式回调
    auto streamer = [callback](std::string token) -> bool {
        callback(token);
        return false; // continue generation
    };
    
    pipeline_->generate(prompt, config, streamer);
}

} // namespace meetingai::granite
