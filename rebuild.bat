@echo off
rem ---------------------------------------------------------------------------
rem  One-click rebuild: stop the running pill, compile, start it again.
rem  NetPill.exe locks its own file while running, so csc cannot overwrite it
rem  in place - hence the taskkill. Settings and usage history live in
rem  %APPDATA%\NetPill and are untouched by a rebuild.
rem ---------------------------------------------------------------------------
cd /d "%~dp0"

echo Stopping NetPill...
taskkill /im NetPill.exe /f >nul 2>&1
rem Give Windows a moment to release the image lock.
ping -n 2 127.0.0.1 >nul

echo Compiling...
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /optimize+ /win32manifest:app.manifest /out:NetPill.exe NetPill.cs

if errorlevel 1 goto failed

echo Starting NetPill...
start "" NetPill.exe
echo.
echo Build OK.
timeout /t 3 >nul
exit /b 0

:failed
echo.
echo BUILD FAILED - the compiler errors are above. NetPill was not restarted.
pause
exit /b 1
