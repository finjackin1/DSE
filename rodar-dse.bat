@echo off
setlocal
title DSE - desenvolvimento

rem Vai para a pasta deste .bat (raiz do DSE), qualquer que seja o caminho.
pushd "%~dp0"

where dotnet >nul 2>nul
if errorlevel 1 (
  echo [ERRO] .NET SDK nao encontrado no PATH.
  echo        Instale o .NET 8 SDK e abra o terminal de novo:
  echo        https://dotnet.microsoft.com/download
  goto :fim
)

if not exist "src\DSE.App\DSE.App.csproj" (
  echo [ERRO] Nao encontrei src\DSE.App\DSE.App.csproj
  echo        Este .bat precisa estar na RAIZ do projeto DSE.
  echo        Pasta atual: %CD%
  goto :fim
)

echo Rodando o DSE em modo desenvolvimento...
echo.
dotnet run --project "src\DSE.App"

if errorlevel 1 (
  echo.
  echo [ERRO] A execucao falhou. Veja as mensagens acima.
  echo        Dica: se for erro de cache, apague as pastas bin e obj e tente de novo.
)

:fim
popd
rem Nao pausa quando chamado por uma task do VS Code (que passa "nopause").
if /i not "%~1"=="nopause" (
  echo.
  pause
)
endlocal
