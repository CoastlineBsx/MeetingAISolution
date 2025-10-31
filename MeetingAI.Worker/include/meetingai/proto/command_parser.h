#pragma once
#include <string>

namespace meetingai::proto {

	std::string trim(std::string s);

	// 将字符串做 JSON 转义（仅最基本的控制字符与引号/反斜杠）
	std::string jsonEscape(const std::string& s);

	// 判断是否是 {"type":"quit"}（宽松匹配）
	bool isQuit(const std::string& s);

	// 判断是否是 {"type":"transcribe_file"}（宽松匹配）
	bool isTranscribe(const std::string& s);

	// 从 {"path":"..."} 提取 path；失败返回空
	std::string extractPath(const std::string& json);

	// 从 {"mode":"..."} 提取 mode；失败返回 "auto"
	std::string extractMode(const std::string& json);

	// 从 {"language":"..."} 提取 language；失败返回 "auto"
	std::string extractLanguage(const std::string& json);

	// ==================== 流式转录相关 ====================

	// 判断是否是 {"type":"start_stream"}（宽松匹配）
	bool isStartStream(const std::string& s);

	// 判断是否是 {"type":"stream_chunk"}（宽松匹配）
	bool isStreamChunk(const std::string& s);

	// 判断是否是 {"type":"stop_stream"}（宽松匹配）
	bool isStopStream(const std::string& s);

	// 从 {"data":"..."} 提取 base64 编码的音频数据；失败返回空
	std::string extractData(const std::string& json);

	// 从 {"sample_rate":16000} 提取采样率；失败返回 16000
	int extractSampleRate(const std::string& json);

} // namespace meetingai::proto
