@echo off
setlocal
set "ROOT=%~dp0..\.."
set "CAMERA=%ROOT%\Insta360SDK_Source\CameraSDK-20250812_192505-2.1.1-win64"
set "MEDIA=%ROOT%\Insta360SDK_Source\MediaSDK-3.1.3-20260128-win64\MediaSDK"
set "OUTPUT=%ROOT%\Assets\Plugins\x86_64"

if not exist "%OUTPUT%" mkdir "%OUTPUT%"

call "C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat"
cl /nologo /std:c++17 /EHsc /O2 /LD ^
  /I"%CAMERA%\include" ^
  /I"%MEDIA%\include\stitcher" ^
  "%~dp0Insta360UnityBridge.cpp" ^
  /link /OUT:"%OUTPUT%\Insta360UnityBridge.dll" ^
  /LIBPATH:"%CAMERA%\lib" CameraSDK.lib ^
  /LIBPATH:"%MEDIA%\lib" MediaSDK.lib

exit /b %errorlevel%
