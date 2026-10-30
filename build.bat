@cd /d "%~dp0" && %WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe /target:winexe /optimize+ /win32manifest:app.manifest /out:NetPill.exe NetPill.cs
