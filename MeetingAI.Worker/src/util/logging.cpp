#include "pch.h"
#include "logging.h"
#include <iostream>

namespace meetingai::util {

    void logLastError(const wchar_t* msg) {
        DWORD err = ::GetLastError();
        std::wcerr << msg << L" (code: " << err << L")\n";
    }

} // namespace meetingai::util
