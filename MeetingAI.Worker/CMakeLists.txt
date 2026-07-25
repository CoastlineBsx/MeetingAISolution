



# ============================================================================
# Project Configuration
# ============================================================================
cmake_minimum_required(VERSION 3.20)
project(MeetingAIWorker LANGUAGES C CXX)

# C++ Standard Configuration
set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

# ============================================================================
# Source Files
# ============================================================================
add_executable(MeetingAIWorker
    # Core application sources
    src/main/main.cpp
    src/pch.cpp
    
    # IPC (Inter-Process Communication) module
    src/ipc/pipe_security.cpp
    
    # Protocol and command handling
    src/proto/command_parser.cpp
    
    # Data storage module
    src/storage/sqlite_store.cpp
    
    # Speech transcription module
    src/transcribe/whisper_transcriber.cpp
    src/transcribe/transcript_text_normalizer.cpp
    
    # Utility modules
    src/util/logging.cpp
    src/util/paths.cpp
    
    # Third-party source files
    third_party/sqlite/src/sqlite3.c
    
    # RAG (Retrieval-Augmented Generation) modules
    src/granite/granite_genai.cpp      # LLM inference with Granite model
    src/embedding/embedding_genai.cpp  # Text embedding generation for semantic search
)

# ============================================================================
# Include Directories
# ============================================================================
target_include_directories(MeetingAIWorker PRIVATE
    # Source directories
    ${CMAKE_SOURCE_DIR}/src
    ${CMAKE_SOURCE_DIR}/src/util
    
    # Project headers - Core modules
    ${CMAKE_SOURCE_DIR}/include/meetingai
    ${CMAKE_SOURCE_DIR}/include/meetingai/app
    ${CMAKE_SOURCE_DIR}/include/meetingai/ipc
    ${CMAKE_SOURCE_DIR}/include/meetingai/transcribe
    ${CMAKE_SOURCE_DIR}/include/meetingai/storage
    ${CMAKE_SOURCE_DIR}/include/meetingai/proto
    ${CMAKE_SOURCE_DIR}/include/meetingai/util
    
    # Third-party headers - SQLite database
    ${CMAKE_SOURCE_DIR}/third_party/sqlite/include
    
    # Third-party headers - Whisper speech recognition
    ${CMAKE_SOURCE_DIR}/third_party/whisper/include
    
    # Third-party headers - OpenVINO GenAI for NPU acceleration
    ${CMAKE_SOURCE_DIR}/third_party/openvino_genai/include
)

# ============================================================================
# Preprocessor Definitions
# ============================================================================
target_compile_definitions(MeetingAIWorker PRIVATE
    # SQLite configuration
    SQLITE_OMIT_LOAD_EXTENSION         # Disable dynamic extension loading for security
    SQLITE_THREADSAFE=1                 # Enable multi-threading support
    SQLITE_ENABLE_FTS5                  # Enable full-text search version 5
    SQLITE_ENABLE_JSON1                 # Enable JSON support
    SQLITE_DEFAULT_WAL_SYNCHRONOUS=1    # WAL mode synchronization setting
    
    # Build configuration macros
    $<$<CONFIG:Debug>:_DEBUG>           # Define _DEBUG in Debug builds
    $<$<CONFIG:Release>:NDEBUG>        # Define NDEBUG in Release builds
)

# ============================================================================
# Precompiled Headers (PCH) for faster compilation
# ============================================================================
if (CMAKE_VERSION VERSION_GREATER_EQUAL "3.16")
    target_precompile_headers(MeetingAIWorker PRIVATE src/pch.h)
endif()

# ============================================================================
# Library Paths Configuration
# ============================================================================

# Whisper/GGML library paths for speech recognition
set(WHISPER_LIB_DIR_DEBUG ${CMAKE_SOURCE_DIR}/third_party/whisper/lib/x64/Debug)
set(WHISPER_LIB_DIR_RELEASE ${CMAKE_SOURCE_DIR}/third_party/whisper/lib/x64/Release)

# OpenVINO GenAI library paths for NPU acceleration
set(OV_GENAI_LIB_DIR_DEBUG ${CMAKE_SOURCE_DIR}/third_party/openvino_genai/lib/intel64/Debug)
set(OV_GENAI_LIB_DIR_RELEASE ${CMAKE_SOURCE_DIR}/third_party/openvino_genai/lib/intel64/Release)

# ============================================================================
# Link Libraries
# ============================================================================
target_link_libraries(MeetingAIWorker PRIVATE
    # Whisper speech recognition libraries
    debug ${WHISPER_LIB_DIR_DEBUG}/whisper.lib           # Main Whisper library
    debug ${WHISPER_LIB_DIR_DEBUG}/ggml.lib             # GGML machine learning library
    debug ${WHISPER_LIB_DIR_DEBUG}/ggml-base.lib        # GGML base components
    debug ${WHISPER_LIB_DIR_DEBUG}/ggml-cpu.lib         # GGML CPU backend
    optimized ${WHISPER_LIB_DIR_RELEASE}/whisper.lib
    optimized ${WHISPER_LIB_DIR_RELEASE}/ggml.lib
    optimized ${WHISPER_LIB_DIR_RELEASE}/ggml-base.lib
    optimized ${WHISPER_LIB_DIR_RELEASE}/ggml-cpu.lib
    
    # OpenVINO GenAI libraries for AI inference
    debug ${OV_GENAI_LIB_DIR_DEBUG}/openvino_genaid.lib  # GenAI wrapper (Debug)
    debug ${OV_GENAI_LIB_DIR_DEBUG}/openvinod.lib        # Core OpenVINO (Debug)
    optimized ${OV_GENAI_LIB_DIR_RELEASE}/openvino_genai.lib  # GenAI wrapper (Release)
    optimized ${OV_GENAI_LIB_DIR_RELEASE}/openvino.lib        # Core OpenVINO (Release)
)

# ============================================================================
# Output Directory Configuration
# ============================================================================
set_target_properties(MeetingAIWorker PROPERTIES
    RUNTIME_OUTPUT_DIRECTORY ${CMAKE_BINARY_DIR}/bin                    # Base output directory
    RUNTIME_OUTPUT_DIRECTORY_DEBUG ${CMAKE_BINARY_DIR}/bin/Debug        # Debug build output
    RUNTIME_OUTPUT_DIRECTORY_RELEASE ${CMAKE_BINARY_DIR}/bin/Release    # Release build output
)

# ============================================================================
# Post-Build Commands - Copy Required DLLs
# ============================================================================

# Copy OpenVINO GenAI DLLs for Debug configuration
if(CMAKE_BUILD_TYPE STREQUAL "Debug" OR CMAKE_CONFIGURATION_TYPES)
    add_custom_command(TARGET MeetingAIWorker POST_BUILD
        COMMAND ${CMAKE_COMMAND} -E copy_if_different
        "${CMAKE_SOURCE_DIR}/third_party/openvino_genai/bin/intel64/Debug/openvino_genaid.dll"
        "$<TARGET_FILE_DIR:MeetingAIWorker>"
        COMMAND ${CMAKE_COMMAND} -E copy_if_different
        "${CMAKE_SOURCE_DIR}/third_party/openvino_genai/bin/intel64/Debug/openvinod.dll"
        "$<TARGET_FILE_DIR:MeetingAIWorker>"
        COMMENT "Copying OpenVINO GenAI Debug DLLs to output directory"
    )
endif()

# Copy OpenVINO GenAI DLLs for Release configuration
if(CMAKE_BUILD_TYPE STREQUAL "Release" OR CMAKE_CONFIGURATION_TYPES)
    add_custom_command(TARGET MeetingAIWorker POST_BUILD
        COMMAND ${CMAKE_COMMAND} -E copy_if_different
        "${CMAKE_SOURCE_DIR}/third_party/openvino_genai/bin/intel64/Release/openvino_genai.dll"
        "$<TARGET_FILE_DIR:MeetingAIWorker>"
        COMMAND ${CMAKE_COMMAND} -E copy_if_different
        "${CMAKE_SOURCE_DIR}/third_party/openvino_genai/bin/intel64/Release/openvino.dll"
        "$<TARGET_FILE_DIR:MeetingAIWorker>"
        COMMENT "Copying OpenVINO GenAI Release DLLs to output directory"
    )
endif()

# ============================================================================
# Optional: Copy Whisper DLLs if they exist
# ============================================================================
# Uncomment if Whisper uses dynamic libraries instead of static
# add_custom_command(TARGET MeetingAIWorker POST_BUILD
#     COMMAND ${CMAKE_COMMAND} -E copy_if_different
#     "${CMAKE_SOURCE_DIR}/third_party/whisper/bin/x64/$<CONFIG>/whisper.dll"
#     "$<TARGET_FILE_DIR:MeetingAIWorker>"
#     COMMENT "Copying Whisper DLL to output directory"
# )

# ============================================================================
# Installation Rules (Optional)
# ============================================================================
# install(TARGETS MeetingAIWorker
#     RUNTIME DESTINATION bin
#     LIBRARY DESTINATION lib
#     ARCHIVE DESTINATION lib
# )

# ============================================================================
# Status Messages
# ============================================================================
message(STATUS "========================================")
message(STATUS "MeetingAI Worker Configuration Summary")
message(STATUS "========================================")
message(STATUS "CMake Version:        ${CMAKE_VERSION}")
message(STATUS "C++ Standard:         C++${CMAKE_CXX_STANDARD}")
message(STATUS "Build Type:           ${CMAKE_BUILD_TYPE}")
message(STATUS "Project Directory:    ${CMAKE_SOURCE_DIR}")
message(STATUS "Binary Directory:     ${CMAKE_BINARY_DIR}")
message(STATUS "")
message(STATUS "Features Enabled:")
message(STATUS "  - Whisper Speech Recognition")
message(STATUS "  - SQLite Database with FTS5")
message(STATUS "  - OpenVINO GenAI for NPU Acceleration")
message(STATUS "  - RAG (Retrieval-Augmented Generation)")
message(STATUS "========================================")
message(STATUS "Configuration complete for MeetingAIWorker with RAG support")
