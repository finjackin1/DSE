@echo off
setlocal
title DSE - publicar portable

rem Tudo relativo a pasta deste .bat (raiz do DSE).
pushd "%~dp0"

rem Pasta de saida. Para mandar para outro lugar, mude so esta linha
rem (ex: set "SAIDA=A:\Documentos\Claude-Projects\_builds").
set "SAIDA=%~dp0dist"
set "PORTABLE=%SAIDA%\DSE-Portable"
set "ZIP=%SAIDA%\DSE-Portable.zip"

where dotnet >nul 2>nul
if errorlevel 1 (
  echo [ERRO] .NET SDK nao encontrado no PATH.
  echo        Instale o .NET 8 SDK: https://dotnet.microsoft.com/download
  goto :fim
)

if not exist "src\DSE.App\DSE.App.csproj" (
  echo [ERRO] Nao encontrei src\DSE.App\DSE.App.csproj
  echo        Este .bat precisa estar na RAIZ do projeto DSE.
  echo        Pasta atual: %CD%
  goto :fim
)

echo [1/3] Publicando ^(framework-dependent, precisa do .NET 8 Desktop Runtime^)...
if exist "%PORTABLE%" rmdir /s /q "%PORTABLE%"
dotnet publish "src\DSE.App\DSE.App.csproj" -c Release -r win-x64 --self-contained false -o "%PORTABLE%"
if errorlevel 1 (
  echo.
  echo [ERRO] O publish falhou. Veja as mensagens acima.
  goto :fim
)

echo.
rem A GPLv3 exige que a licenca acompanhe o programa, nao so o repositorio.
if exist "%~dp0LICENSE" copy /y "%~dp0LICENSE" "%PORTABLE%\LICENSE.txt" >nul
if exist "%~dp0THIRD-PARTY-NOTICES.txt" copy /y "%~dp0THIRD-PARTY-NOTICES.txt" "%PORTABLE%\" >nul

echo [2/3] Compactando...
if exist "%ZIP%" del /q "%ZIP%"
powershell -NoProfile -ExecutionPolicy Bypass -Command "Compress-Archive -Path '%PORTABLE%\*' -DestinationPath '%ZIP%' -Force"
if errorlevel 1 (
  echo.
  echo [ERRO] Nao consegui compactar. O portable continua disponivel em:
  echo        %PORTABLE%
  goto :fim
)

echo.
echo [3/3] Pronto.
echo        Zip:      %ZIP%
echo        Portable: %PORTABLE%
echo        Executavel: DSE.App.exe

:fim
popd
if /i not "%~1"=="nopause" (
  echo.
  pause
)
endlocal
