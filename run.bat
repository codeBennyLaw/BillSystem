@echo off
rem 编译（有改动才会重编）后启动。加 --tray 可静默启动到托盘。
pushd "%~dp0src\BillSystem"
dotnet build -c Release -v q --nologo
if %ERRORLEVEL% neq 0 (
    popd
    echo 编译失败。
    pause
    exit /b 1
)
start "" "bin\Release\net8.0-windows\BillSystem.exe" %*
popd
