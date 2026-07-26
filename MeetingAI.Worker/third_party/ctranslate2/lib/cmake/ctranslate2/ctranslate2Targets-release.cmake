#----------------------------------------------------------------
# Generated CMake target import file for configuration "Release".
#----------------------------------------------------------------

# Commands may need to know the format version.
set(CMAKE_IMPORT_FILE_VERSION 1)

# Import target "CTranslate2::ctranslate2" for configuration "Release"
set_property(TARGET CTranslate2::ctranslate2 APPEND PROPERTY IMPORTED_CONFIGURATIONS RELEASE)
set_target_properties(CTranslate2::ctranslate2 PROPERTIES
  IMPORTED_IMPLIB_RELEASE "${_IMPORT_PREFIX}/lib/ctranslate2.lib"
  IMPORTED_LOCATION_RELEASE "${_IMPORT_PREFIX}/bin/ctranslate2.dll"
  )

list(APPEND _cmake_import_check_targets CTranslate2::ctranslate2 )
list(APPEND _cmake_import_check_files_for_CTranslate2::ctranslate2 "${_IMPORT_PREFIX}/lib/ctranslate2.lib" "${_IMPORT_PREFIX}/bin/ctranslate2.dll" )

# Commands beyond this point should not need to know the version.
set(CMAKE_IMPORT_FILE_VERSION)
