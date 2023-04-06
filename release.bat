@echo off

set VERSION=%1

if "%VERSION%"=="" (
    echo Please provide a version parameter.
    exit /B 1
)

for /F "tokens=1,2 delims=-" %%a in ("%VERSION%") do (
    set FILE_VERSION=%%a
    set SUFFIX=%%b
)

set ASSEMBLY_VERSION=%FILE_VERSION%
if not "%SUFFIX%"=="" (
    set SUFFIX=-%SUFFIX%
)

echo Updating project file...
powershell -Command "(Get-Content .\gpt.csproj) -replace '<FileVersion>[\d\.]+<\/FileVersion>', '<FileVersion>%FILE_VERSION%<\/FileVersion>' -replace '<AssemblyVersion>[\d\.]+<\/AssemblyVersion>', '<AssemblyVersion>%ASSEMBLY_VERSION%<\/AssemblyVersion>' -replace '<Version>[\d\.\-a-zA-Z]+<\/Version>', '<Version>%FILE_VERSION%%SUFFIX%<\/Version>' | Set-Content .\gpt.csproj"

echo Tagging the repository...
echo git tag v%FILE_VERSION%%SUFFIX%
git tag v%FILE_VERSION%%SUFFIX%

echo Pushing the tag to the remote repository...
echo git push origin v%FILE_VERSION%%SUFFIX%
git push origin v%FILE_VERSION%%SUFFIX%

echo Done.
