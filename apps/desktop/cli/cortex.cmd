@echo off
setlocal DisableDelayedExpansion
set "ELECTRON_RUN_AS_NODE=1"
"%~dp0..\..\..\..\Cortex.exe" --expose-internals "%~dp0..\..\..\app.asar\cortex\node_modules\@cortex\desktop-host\lib\cli.js" %*
exit /b %errorlevel%
