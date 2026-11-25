@echo off
setlocal
set "vswhere=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
for /f "usebackq delims=" %%v in (`"%vswhere%" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "install=%%v"
if not defined install (
  echo Install the Visual Studio MSVC x64 desktop tools and Windows SDK 10.0.26100.
  exit /b 1
)
call "%install%\Common7\Tools\VsDevCmd.bat" -no_logo -arch=x64 -host_arch=x64 >nul || exit /b 1
pushd "%~1" || exit /b 1
cl /nologo /O2 /MT /EHsc /std:c++20 /LD "%~dp0NativeFeatures.cpp" /link /MACHINE:X64 /IMPLIB:NativeFeatures.lib /OUT:"%~2" windowsapp.lib
exit /b %errorlevel%
