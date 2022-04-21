dotnet publish ./Douze/Douze.csproj -c Release --self-contained -r win-x64 -p:PublishReadyToRun=true -p:PublishTrimmed=true -p:PublishSingleFile=true -o ./artifacts/win-x64
