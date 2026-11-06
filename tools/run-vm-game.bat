@echo off
setlocal
set "PATCH_SOURCE=\\VBOXSVR\Imperialism2_Patch\obj\vm-deploy"
set "GAME_DIR=C:\Games\Imperialism2"

if not exist "%GAME_DIR%\Data\" (
    echo Game assets were not found in "%GAME_DIR%".
    goto :failed
)

%SystemRoot%\System32\tasklist.exe /FI "IMAGENAME eq Imperialism II.exe" /NH | %SystemRoot%\System32\find.exe /I "Imperialism II.exe" >nul
if not errorlevel 1 (
    echo Close Imperialism II before updating its files, then run this launcher again.
    goto :failed
)

for %%F in ("Imperialism II.exe" "dplayx.dll" "dplayx.ini") do (
    if not exist "%PATCH_SOURCE%\%%~F" (
        echo Missing patch file: "%PATCH_SOURCE%\%%~F"
        echo Build the patch on the host computer before running this launcher.
        goto :failed
    )
)

for %%F in ("Imperialism II.exe" "dplayx.dll" "dplayx.ini") do (
    if exist "%GAME_DIR%\%%~F" if not exist "%GAME_DIR%\%%~F.before-worldsync.bak" (
        copy /Y "%GAME_DIR%\%%~F" "%GAME_DIR%\%%~F.before-worldsync.bak" >nul
        if errorlevel 1 goto :failed
    )
    copy /Y "%PATCH_SOURCE%\%%~F" "%GAME_DIR%\%%~F" >nul
    if errorlevel 1 goto :failed
    %SystemRoot%\System32\fc.exe /B "%PATCH_SOURCE%\%%~F" "%GAME_DIR%\%%~F" >nul
    if errorlevel 1 goto :failed
)

echo Updated and verified the game EXE, dplayx.dll, and dplayx.ini.
if /I "%~1"=="/deploy-only" exit /b 0
cd /D "%GAME_DIR%"
if errorlevel 1 goto :failed
start "" /D "%GAME_DIR%" "%GAME_DIR%\Imperialism II.exe"
if errorlevel 1 goto :failed
exit /b 0

:failed
echo The game was not started. Resolve the error above and try again.
if /I not "%~1"=="/deploy-only" pause
exit /b 1
