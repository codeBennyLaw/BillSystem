@echo off
rem 编译 Release 版，产物在 src\BillSystem\bin\Release\net8.0-windows\BillSystem.exe
pushd "%~dp0src\BillSystem"
dotnet build -c Release
set ERR=%ERRORLEVEL%
popd
if %ERR% neq 0 (
    echo.
    echo 编译失败。
    pause
    exit /b %ERR%
)
echo.
echo 编译完成：%~dp0src\BillSystem\bin\Release\net8.0-windows\BillSystem.exe
pause
