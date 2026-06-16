@echo off
echo Building Pixel Animator...
dotnet publish -c Release -r win-x64 --self-contained false -o publish
echo.
echo Done! Run: publish\PixelAnimator.exe
pause
