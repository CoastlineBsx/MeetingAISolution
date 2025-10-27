#pragma once
#include <string>

namespace meetingai::util {
// UTF-8 → UTF-16
std::wstring utf8ToW(const std::string& s);

// exe 所在目录（不含文件名）
std::string getExeDir();

// 数据根目录（优先 MEETINGAI_DATA_DIR；其次便携模式 ./data；否则 %LOCALAPPDATA%\MeetingAI；最后 exe\data）
std::string getDataRoot();

// meeting.db 完整路径（GetDataRoot() + "/meeting.db"）
std::string getDatabasePath();

// 组合模型路径并转 UTF-8
std::string resolveModelFileUtf8(const wchar_t* filename);

} // namespace meetingai::util