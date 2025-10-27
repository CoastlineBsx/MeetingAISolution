#pragma once
#include <windows.h>

namespace meetingai::ipc {

	// 允许 AppContainer(AC) 与 Everyone(WD) 访问的安全属性
	bool createPipeSecurity(SECURITY_ATTRIBUTES& sa, PSECURITY_DESCRIPTOR& pSD);

} // namespace meetingai::ipc
