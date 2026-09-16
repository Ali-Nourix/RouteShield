@echo off
setlocal
rem Removes every build output, including the cached sing-box download.

for %%D in (artifacts dist .tools third_party) do (
    if exist "%~dp0%%D" rmdir /s /q "%~dp0%%D"
)

for /d /r "%~dp0src" %%D in (bin obj) do if exist "%%D" rmdir /s /q "%%D"
for /d /r "%~dp0tests" %%D in (bin obj) do if exist "%%D" rmdir /s /q "%%D"

echo Clean.
