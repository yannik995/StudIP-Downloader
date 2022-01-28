$array = @("linux-x64","win-x64","osx-x64")

for ($i=0; $i -lt $array.length; $i++){
    $enviroment = $array[$i]   
	dotnet publish -r $enviroment -p:PublishSingleFile=true --self-contained false
	Compress-Archive -Path ($PSScriptRoot + "\StudIPDownloader\bin\Debug\netcoreapp3.1\" + $enviroment + "\publish\*") -Force -DestinationPath ("StudIP-Downloader_" + $enviroment + ".zip")
}
