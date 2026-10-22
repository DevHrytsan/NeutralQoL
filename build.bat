@echo off
rem Builds the mod and puts NeutralQoL.dll (and its .pdb) in Build\ inside the
rem repo. A thin wrapper: dotnet build does the same, without the Build\ copy.
rem   build.bat           Release build
rem   build.bat debug     Debug build (includes the Dev sample feature)
rem   build.bat deploy    also copy it into the game's BepInEx\plugins
rem   build.bat debug deploy
rem   build.bat game "E:\SteamLibrary\steamapps\common\Nuclear Option"
rem Without game, the path comes from NO_GAME_PATH, GamePath.user.props
rem (python tools\setup.py) or the default Steam library, in that order.
setlocal

set "ROOT=%~dp0"
set "CONFIG=Release"
set "EXTRA="

:args
if "%~1"=="" goto build
if /i "%~1"=="debug" set "CONFIG=Debug"
if /i "%~1"=="release" set "CONFIG=Release"
if /i "%~1"=="deploy" set "EXTRA=%EXTRA% -p:DeployToGame=true"
if /i "%~1"=="game" (
    set "NO_GAME_PATH=%~2"
    shift
)
shift
goto args

:build
where dotnet >nul 2>nul
if errorlevel 1 (
    echo dotnet not found. Install the .NET SDK 9.0.200 or newer from https://dotnet.microsoft.com/download
    exit /b 1
)
echo Building NeutralQoL (%CONFIG%)
dotnet build "%ROOT%src\NeutralQoL\NeutralQoL.csproj" -c %CONFIG% %EXTRA% -nologo -v:minimal
if errorlevel 1 (
    echo Build failed.
    exit /b 1
)

set "OUT=%ROOT%src\NeutralQoL\bin\%CONFIG%"
if exist "%ROOT%Build" rmdir /s /q "%ROOT%Build"
mkdir "%ROOT%Build"
copy /y "%OUT%\NeutralQoL.dll" "%ROOT%Build\" >nul || exit /b 1
copy /y "%OUT%\NeutralQoL.pdb" "%ROOT%Build\" >nul

echo.
echo Done: %ROOT%Build\NeutralQoL.dll
endlocal
