@echo off
rem 打包正式发布版：自带 .NET 8 运行时，目标机器什么都不用装，解压双击就能跑。
rem 产物在 publish\BillSystem\，整个文件夹压成 zip 就是 GitHub Release 里的附件。
setlocal
pushd "%~dp0"

if exist "publish\BillSystem" rd /s /q "publish\BillSystem"

dotnet publish src\BillSystem\BillSystem.csproj -c Release -r win-x64 ^
    --self-contained true -p:PublishSingleFile=false -p:DebugType=none ^
    -o publish\BillSystem
if %ERRORLEVEL% neq 0 (
    popd
    echo.
    echo 打包失败。
    pause
    exit /b 1
)

echo.
echo 打包完成：%~dp0publish\BillSystem
echo 把这个文件夹整个压成 zip 上传到 Release 即可（用户解压后双击 BillSystem.exe）。
echo 历史数据存在 exe 旁边的 data\ 里，升级时把旧的 data\ 拷进新文件夹就接着用。
popd
pause
