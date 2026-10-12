@echo off
setlocal
cd /d "%~dp0"

echo.
echo  FURI//CORE 0.7.3 - personal classifier build
echo  ---------------------------------
where dotnet >nul 2>nul
if errorlevel 1 (
  echo.
  echo ERROR: dotnet SDK was not found.
  echo Install the .NET 10 SDK or open FuriXeus.Tuber.Native.csproj in Visual Studio 2026.
  echo.
  pause
  exit /b 1
)

dotnet restore FuriXeus.Tuber.Native.csproj
if errorlevel 1 goto :fail

dotnet publish FuriXeus.Tuber.Native.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false
if errorlevel 1 goto :fail

echo.
echo BUILD COMPLETE
echo Open:
echo bin\Release\net10.0-windows\win-x64\publish\FuriXeus.Tuber.Native.exe
echo.
pause
exit /b 0

:fail
echo.
echo BUILD FAILED. Copy the first error block and send it to ChatGPT.
echo.
pause
exit /b 1
