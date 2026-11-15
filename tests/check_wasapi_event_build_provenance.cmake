execute_process(COMMAND git rev-parse HEAD WORKING_DIRECTORY "${SOURCE_DIR}"
  OUTPUT_VARIABLE expected OUTPUT_STRIP_TRAILING_WHITESPACE RESULT_VARIABLE git_result)
string(LENGTH "${expected}" expected_length)
if(NOT git_result EQUAL 0 OR NOT expected MATCHES "^[0-9a-fA-F]+$" OR NOT expected_length EQUAL 40)
  message(FATAL_ERROR "Could not resolve the current source commit for provenance validation.")
endif()
execute_process(COMMAND "${PROGRAM}" --build-info
  OUTPUT_VARIABLE actual OUTPUT_STRIP_TRAILING_WHITESPACE RESULT_VARIABLE program_result)
if(NOT program_result EQUAL 0 OR NOT actual STREQUAL "BUILD_COMMIT=${expected}")
  message(FATAL_ERROR "Built diagnostic provenance does not match current HEAD: expected ${expected}, got '${actual}'.")
endif()
