:: make sure we have a clean release build
msbuild /t:Rebuild /p:Configuration=Release /p:Platform="Any CPU" NVorbis.sln

:: remove any existing nupkg files
del *.nupkg

:: build the nuget packages
tools\nuget pack NVorbis.nuspec
tools\nuget pack NVorbis.NAudioSupport.nuspec

:: upload the nuget packages
tools\nuget push *.nupkg
