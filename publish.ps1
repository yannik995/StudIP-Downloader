$array = @("win-x64","linux-x64","osx-x64")

for ($i=0; $i -lt $array.length; $i++){
    $enviroment = $array[$i]   
	dotnet publish -r $enviroment -p:PublishSingleFile=true -p:PublishTrimmed=true --self-contained true -p:IncludeNativeLibrariesForSelfExtract=true
	Compress-Archive -Path ($PSScriptRoot + "\StudIPDownloader\bin\Debug\netcoreapp3.1\" + $enviroment + "\publish\*") -Force -DestinationPath ("StudIP-Downloader_" + $enviroment + ".zip")
}
