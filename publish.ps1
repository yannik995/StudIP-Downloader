param([string]$Version = "")

$array = @("win-x64","linux-x64","linux-arm64","osx-x64","osx-arm64")
$versionArgs = @()
if ($Version -ne "") { $versionArgs = @("-p:Version=$Version") }

for ($i=0; $i -lt $array.length; $i++){
    $enviroment = $array[$i]
	dotnet publish StudIPDownloader/StudIPDownloader.csproj -c Release -r $enviroment -p:PublishSingleFile=true -p:PublishTrimmed=true --self-contained true -p:IncludeNativeLibrariesForSelfExtract=true @versionArgs
	if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
	Copy-Item config.ini.sample ($PSScriptRoot + "/StudIPDownloader/bin/Release/net8.0/" + $enviroment + "/publish/")
	Compress-Archive -Path ($PSScriptRoot + "/StudIPDownloader/bin/Release/net8.0/" + $enviroment + "/publish/*") -Force -DestinationPath ("StudIP-Downloader_" + $enviroment + ".zip")
}
