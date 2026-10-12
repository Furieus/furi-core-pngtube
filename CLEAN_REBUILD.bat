@echo off
setlocal

echo.
echo ================================
echo  FURI//CORE Clean Rebuild
echo ================================
echo.

set "ROOT=%~dp0"
cd /d "%ROOT%"

echo Cleaning compiled folders...
echo.

for /d /r "%ROOT%" %%D in (bin,obj) do (
    if exist "%%D" (
        echo Deleting: %%D
        rmdir /s /q "%%D"
    )
)

echo.
echo Restoring packages...
dotnet restore
if errorlevel 1 goto fail

echo.
echo Building Release...
dotnet build -c Release
if errorlevel 1 goto fail

echo.
echo Publishing Windows x64 Release...
dotnet publish -c Release -r win-x64 --self-contained false
if errorlevel 1 goto fail

echo.
echo ================================
echo  BUILD COMPLETE
echo ================================
echo.

echo Your compiled app should be in:
echo bin\Release\net10.0-windows\win-x64\publish\
echo.

pause
exit /b 0

:fail
echo.
echo ================================
echo  BUILD FAILED
echo ================================
echo.
echo Copy the error text above and send it to ChatGPT.
echo.
pause
exit /b 1
