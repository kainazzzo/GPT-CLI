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
powershell -Command "(Get-Content .\gpt.csproj) -replace '(<FileVersion>)[\d\.]+(</FileVersion>)', '`${1}%FILE_VERSION%`${2}' -replace '(<AssemblyVersion>)[\d\.]+(</AssemblyVersion>)', '`${1}%ASSEMBLY_VERSION%`${2}' -replace '(<Version>)[\d\.\-a-zA-Z]+(</Version>)', '`${1}%FILE_VERSION%%SUFFIX%`${2}' | Set-Content .\gpt.csproj"

echo Committing the changes...
echo git add .\gpt.csproj
git add .\gpt.csproj

echo git commit -m "Releasing v%FILE_VERSION%%SUFFIX%"
git commit -m "Releasing v%FILE_VERSION%%SUFFIX%"

echo Tagging the repository...
echo git tag v%FILE_VERSION%%SUFFIX%
git tag v%FILE_VERSION%%SUFFIX%

echo Pushing the commit and tag to the remote repository...
echo git push origin
git push origin

echo git push origin v%FILE_VERSION%%SUFFIX%
git push origin v%FILE_VERSION%%SUFFIX%

echo Done.
