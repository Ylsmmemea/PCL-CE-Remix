@echo off
chcp 65001 >nul
setlocal

rem ============================================================
rem  以「全新用户」环境启动 PCL，用于验证首次启动相关的改动。
rem
rem  PCL 的状态存在两处：
rem    %APPDATA%\PCLCE        —— 全局配置（config.v1.json），删便携目录不会重置的就是它
rem    %LOCALAPPDATA%\PCLCE   —— 壁纸缓存等
rem
rem  本脚本会先把它们改名备份，启动 PCL 测试，退出后自动还原。
rem  即使中途强制结束脚本，备份仍在 *_testbak 目录下，手动改回名字即可。
rem ============================================================

set "EXE=%~dp0publish\Plain Craft Launcher 2.exe"
set "CFG=%APPDATA%\PCLCE"
set "CFG_BAK=%APPDATA%\PCLCE_testbak"
set "LOC=%LOCALAPPDATA%\PCLCE"
set "LOC_BAK=%LOCALAPPDATA%\PCLCE_testbak"
rem 旧命名目录，存的是账号档案 profiles.json。
rem 不备份它的话，账号会被原样恢复，测出来会以为「改动没生效」。
set "OLD=%APPDATA%\.pclce"
set "OLD_BAK=%APPDATA%\.pclce_testbak"
set "TMP=%TEMP%\PCLCE"
set "TMP_BAK=%TEMP%\PCLCE_testbak"

if not exist "%EXE%" (
  echo [错误] 找不到 %EXE%
  echo        请先构建，或修改本脚本里的 EXE 路径。
  pause
  exit /b 1
)

echo === 备份现有数据 ===
if exist "%CFG%" (
  if exist "%CFG_BAK%" rmdir /s /q "%CFG_BAK%"
  move "%CFG%" "%CFG_BAK%" >nul && echo   已备份 %CFG%
) else (
  echo   %CFG% 不存在，跳过
)
if exist "%LOC%" (
  if exist "%LOC_BAK%" rmdir /s /q "%LOC_BAK%"
  move "%LOC%" "%LOC_BAK%" >nul && echo   已备份 %LOC%
) else (
  echo   %LOC% 不存在，跳过
)
if exist "%OLD%" (
  if exist "%OLD_BAK%" rmdir /s /q "%OLD_BAK%"
  move "%OLD%" "%OLD_BAK%" >nul && echo   已备份 %OLD%   ^<-- 账号档案
) else (
  echo   %OLD% 不存在，跳过
)
if exist "%TMP%" (
  if exist "%TMP_BAK%" rmdir /s /q "%TMP_BAK%"
  move "%TMP%" "%TMP_BAK%" >nul && echo   已备份 %TMP%
) else (
  echo   %TMP% 不存在，跳过
)

echo.
echo === 启动 PCL（当前是全新环境）===
echo.
echo   请依次验证：
echo     1. 启动后不应弹出「PCL 社区版」提示框
echo     2. 启动页左下不应出现需要输入口令才能关闭的提示面板
echo     3. 账号页 - 新建档案 - 登录方式应直接列出三项
echo        （正版验证 / 第三方验证 / 离线），无需先登录微软账号
echo.
echo   验证完请关闭 PCL，本脚本会自动还原数据。
echo.
pause

start /wait "" "%EXE%"

echo.
echo === 还原原有数据 ===
if exist "%CFG_BAK%" (
  rmdir /s /q "%CFG%" 2>nul
  move "%CFG_BAK%" "%CFG%" >nul && echo   已还原 %CFG%
)
if exist "%LOC_BAK%" (
  rmdir /s /q "%LOC%" 2>nul
  move "%LOC_BAK%" "%LOC%" >nul && echo   已还原 %LOC%
)
if exist "%OLD_BAK%" (
  rmdir /s /q "%OLD%" 2>nul
  move "%OLD_BAK%" "%OLD%" >nul && echo   已还原 %OLD%
)
if exist "%TMP_BAK%" (
  rmdir /s /q "%TMP%" 2>nul
  move "%TMP_BAK%" "%TMP%" >nul && echo   已还原 %TMP%
)

echo.
echo 完成。
pause
