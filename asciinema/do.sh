. ../../asciinema/demo-magic.sh

alias vpk='../../build/Debug/net8.0/vpk'

clear
pei "dotnet publish -r osx-x64 -c Release --self-contained -o publish"
echo " "
pei "vpk pack -u AvaloniaCrossPlat -v 1.0.1 -p publish -i Velopack.icns"
exit