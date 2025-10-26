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

} // namespace meetingai::proto
