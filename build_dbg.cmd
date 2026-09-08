@echo off
cd /d C:\Users\Maxi\Documents\WHPO
dotnet build src\WHPO.UI\WHPO.UI.csproj -c Debug -p:Platform=x64 --nologo -v q > build_out2.txt 2>&1
