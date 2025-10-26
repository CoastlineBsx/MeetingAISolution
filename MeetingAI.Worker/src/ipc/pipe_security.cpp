#include "pch.h"
#include "pipe_security.h"
#include "logging.h"
#include <sddl.h>

namespace meetingai::ipc {

    bool createPipeSecurity(SECURITY_ATTRIBUTES& sa, PSECURITY_DESCRIPTOR& pSD) {
        LPCWSTR sddl = L"D:(A;;GA;;;AC)(A;;GA;;;WD)";
        if (!::ConvertStringSecurityDescriptorToSecurityDescriptorW(
            sddl, SDDL_REVISION_1, &pSD, nullptr)) {
            meetingai::util::logLastError(L"[IPC] SDDL parse failed");
            return false;
        }
        sa.nLength = sizeof(sa);
        sa.bInheritHandle = FALSE;
        sa.lpSecurityDescriptor = pSD;
        return true;
    }

} // namespace meetingai::ipc

