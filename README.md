# ADB Explorer


A modern Windows file manager for Android devices that provides a familiar Explorer-like interface for browsing and managing files on your Android emulator or device via ADB (Android Debug Bridge).

In developing and testing my Android app, I needed to access files and folders in the emulators. Got tired with the clunky file explorer that Android Studio provides, so I asked Claude.ai (Opus 4.1) to create this app. Took about 3 days of prompting and manually entering the patches, but it was a fun learning experience, and I find this app useful. Starting from ver. 1.1 of this app I switched to using Claude Code, which is simply ingenious.

## Features



### 🚀 Core Features

- **Familiar Interface** - Windows Explorer-like dual-pane interface with tree view and file list

- **Drag & Drop** - Seamlessly drag files between your PC and Android device

- **Multiple Windows** - Open multiple windows to work with different folders or devices simultaneously

- **Fast File Operations** - Copy, move, delete, rename files and folders with ease

- **Real-time Sync** - Auto-sync edited files back to device when opened in Windows

- **Non-intrusive Notifications** - Status bar notifications with color coding (blue for success, red for errors)



### 📁 File Management

- Browse entire Android filesystem including system directories

- Create new folders

- Batch operations on multiple files

- File properties viewer with permissions info

- Context menu integration - "Open in ADB Explorer" for folders

- **Favorites/Bookmarks** - Save frequently accessed folders for quick navigation

- **Rename Support** - Press F2 or use context menu to rename files and folders



### ⌨️ Keyboard Shortcuts

- **F1** - Help

- **F2** - Rename selected item

- **F5** - Refresh current folder

- **Delete** - Delete selected items

- **Ctrl+C/V** - Copy and paste files

- **Ctrl+A** - Select all

- **Ctrl+N** - New folder

- **Ctrl+L** - Sync tree with current location

- **Alt+←/→** - Navigate back/forward

- **Alt+↑** - Go to parent folder

- **Backspace** - Navigate up one level

- **Enter** - Open file/folder



## Requirements



- **Windows 10/11** (64-bit)

- **.NET 8.0 Desktop Runtime** - [Download](https://dotnet.microsoft.com/download/dotnet/8.0)

- **ADB (Android Debug Bridge)** - Usually included with Android SDK Platform Tools

- **USB Debugging** enabled on your Android device



## Installation



1. Download the latest installer from [Releases](https://github.com/gregko/AdbExplorer/releases)

2. Run `AdbExplorer_Setup.exe`

3. Follow the installation wizard

4. Launch ADB Explorer from Start Menu or Desktop



## Setup Your Android Device



1. **Enable Developer Options**:

&nbsp;  - Go to Settings → About Phone

&nbsp;  - Tap "Build Number" 7 times



2. **Enable USB Debugging**:

&nbsp;  - Go to Settings → Developer Options

&nbsp;  - Enable "USB Debugging"

&nbsp;  - Connect your device via USB

&nbsp;  - Accept the debugging authorization prompt on your device



## Usage



1. Launch ADB Explorer

2. Connect your Android device via USB

3. Select your device from the dropdown

4. Browse and manage files using familiar Windows Explorer controls

5. Drag files to/from Windows Explorer for easy transfers



### Tips

- Double-click files to open them in Windows (changes auto-sync back)

- Right-click for context menus

- Use the tree view for quick navigation

- Open multiple windows with "New Window" button for different folders



## Building from Source



```bash

# Clone the repository

git clone https://github.com/gregko/AdbExplorer.git

cd adbexplorer



# Build the project

dotnet build -c Release



# Run the application

dotnet run

```



## Troubleshooting



### Device Not Detected

- Ensure USB Debugging is enabled

- Try different USB cable or port

- Install device-specific USB drivers

- Check if `adb devices` shows your device in command prompt



### Access Denied Errors

- Some system directories require root access

- Regular user directories (/sdcard, /storage) should be accessible



### ADB Not Found

- Install Android SDK Platform Tools

- Add ADB to your system PATH

- Or place adb.exe in the application directory



## License



MIT License - See [LICENSE.txt](LICENSE.txt) for details



## Contributing



Contributions are welcome! Please feel free to submit pull requests or open issues for bugs and feature requests.



## Acknowledgments

- Thank you, Claude.ai, and all who contributed to it's training (willingly or not...)

- Built with .NET 8.0 and WPF

- Uses Android Debug Bridge (ADB) for device communication

- Icons from Windows Emoji set



---



**Note**: This tool requires USB Debugging to be enabled on your Android emulator or device. It provides direct filesystem access, so use with caution when modifying system files.

