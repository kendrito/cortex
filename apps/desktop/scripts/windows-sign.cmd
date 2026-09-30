@echo off
setlocal DisableDelayedExpansion
set "signTool=%CORTEX_DESKTOP_WINDOWS_SIGNTOOL%"
set "certificateFile=%CORTEX_DESKTOP_WINDOWS_CER_FILE%"
set "tokenPin=%CORTEX_DESKTOP_WINDOWS_TOKEN_PIN%"
set "keyContainer=%CORTEX_DESKTOP_WINDOWS_KEY_CONTAINER%"
set "targetFile=%CORTEX_DESKTOP_WINDOWS_SIGN_TARGET%"
set "appendSignature="
if "%CORTEX_DESKTOP_WINDOWS_SIGN_APPEND%"=="1" set "appendSignature=/as"
set "CORTEX_DESKTOP_WINDOWS_SIGNTOOL="
set "CORTEX_DESKTOP_WINDOWS_CER_FILE="
set "CORTEX_DESKTOP_WINDOWS_TOKEN_PIN="
set "CORTEX_DESKTOP_WINDOWS_KEY_CONTAINER="
set "CORTEX_DESKTOP_WINDOWS_SIGN_TARGET="
set "CORTEX_DESKTOP_WINDOWS_SIGN_APPEND="
set "signTool=" & set "certificateFile=" & set "tokenPin=" & set "keyContainer=" & set "targetFile=" & set "appendSignature=" & "%signTool%" sign /v /fd sha256 /f "%certificateFile%" /kc "[{{%tokenPin%}}]=%keyContainer%" /csp "eToken Base Cryptographic Provider" %appendSignature% "%targetFile%"
exit /b %errorlevel%
