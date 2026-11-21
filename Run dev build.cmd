@echo off
rem Builds and starts the development version of Paneless from this folder.
cd /d "%~dp0"
dotnet build src\Paneless.App\Paneless.App.csproj -nologo -v q || (pause & exit /b 1)
start "" "src\Paneless.App\bin\Debug\net10.0\Paneless.exe"
