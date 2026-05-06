# ADB Explorer - Project Information for Codex

## Project Overview
ADB Explorer is a Windows file manager for Android devices that provides a familiar Explorer-like interface for browsing and managing files via ADB (Android Debug Bridge).

## Build Commands

### Building the Application
```bash
dotnet build -c Release
```

### Creating the Installer
NSIS is installed at: `C:\Program Files (x86)\NSIS`

To build the installer:
```bash
"C:\Program Files (x86)\NSIS\makensis.exe" Installer.nsi
```

The installer will be created at: `installer_output\AdbExplorer_Setup.exe`

## Project Structure
- Main application: WPF app using .NET 8.0
- Installer: NSIS script (Installer.nsi)
- Version info: Updated in both AdbExplorer.csproj and Installer.nsi

## GitHub Repository
https://github.com/gregko/AdbExplorer

## Development Notes
- When updating version numbers, update in:
  - AdbExplorer.csproj (AssemblyVersion, FileVersion, ProductVersion)
  - Installer.nsi (PRODUCT_VERSION and VIProductVersion)
  - History.txt (add new version entry). Do NOT update dates for previous version changes lists, only set the current date for the newest entry added on top.

## Testing Commands
- Lint/format checks: (to be determined)
- Type checking: Built into dotnet build