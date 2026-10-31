# Latency Master

A small Windows network latency and packet-loss monitor built with C# and WinUI 3.

<img width="1917" height="1007" alt="Screenshot 2026-10-31 183249" src="https://github.com/user-attachments/assets/f1744d5a-43ff-4ec6-af03-d2849a6bc53e" />

## Run it in Visual Studio

1. Open Visual Studio Community.
2. Choose **Open a project or solution** and select `LatencyMaster.csproj` in this folder.
3. Select **x64** if Visual Studio asks for a platform, then press **F5** to build and run.

The app supports ICMP ping, TCP connections to a chosen port, and HTTPS requests. Add or remove targets in the app; the target list and manual order are saved locally under your Windows user profile. The detected default gateway is pinned at the top and cannot be removed. New launches check every second by default.

## Build a release version

Open PowerShell in this folder and run:

```powershell
dotnet restore .\LatencyMaster.csproj -r win-x64
dotnet publish .\LatencyMaster.csproj -c Release -r win-x64 --self-contained true -p:Platform=x64 -p:PublishDir='.\artifacts\release-v2\'
```

The runnable app is `artifacts\release-v2\LatencyMaster.exe`. Keep the other files in that folder alongside it when copying or sharing the app. The self-contained output includes the .NET and Windows App SDK runtimes.

## Notes

- ICMP may be blocked by a host or network even while its website is available.
- TCP results measure connection setup to the selected port; HTTPS results measure time to response headers.
- This is an early prototype. The target list is persistent, while latency history is kept for the current session.
- It saves the configuration in a .json file under appdata folder. Example: "C:\Users\username\AppData\Local\LatencyMaster"
