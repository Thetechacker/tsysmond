@echo off

set CURDIR=%~dp0

IF NOT EXIST "%CURDIR%\build\" (
    mkdir "%CURDIR%\build\"
    copy "%CURDIR%\redist\OpenHardwareMonitorLib.dll" "%CURDIR%\build\"
)

csc -out:"%CURDIR%\build\tsysmond.exe" -win32icon:"%CURDIR%\..\Assets\win32icon.ico" -r:"%CURDIR%\redist\OpenHardwareMonitorLib.dll" "%CURDIR%\..\AssemblyInfo.cs" "%CURDIR%\..\PowerManagement.cs" "%CURDIR%\..\TexSystemMonitor.cs" && mt.exe -manifest "%CURDIR%\TexSystemMonitor.manifest" -outputresource:"%CURDIR%\build\tsysmond.exe";1