$array = @("win-x64","linux-x64","osx-x64","osx-arm64")

for ($i=0; $i -lt $array.length; $i++){
    $enviroment = $array[$i]
	dotnet publish StudIPDownloader/StudIPDownloader.csproj -c Release -r $enviroment -p:PublishSingleFile=true -p:PublishTrimmed=true --self-contained true -p:IncludeNativeLibrariesForSelfExtract=true
	if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
	Compress-Archive -Path ($PSScriptRoot + "/StudIPDownloader/bin/Release/net8.0/" + $enviroment + "/publish/*") -Force -DestinationPath ("StudIP-Downloader_" + $enviroment + ".zip")
}
