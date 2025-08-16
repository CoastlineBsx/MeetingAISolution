#pragma once
#include <string>
#include <string_view>

namespace meetingai::util {

	// 将 Windows 宽路径与相对路径拼接，并转成 UTF-8 字符串返回。
	// 调试期可固定到本地目录；发布期可切换到 ProgramData 之类的公共目录。
	std::string resolveModelFileUtf8(const wchar_t* filename);

} // namespace meetingai::util
#pragma once
