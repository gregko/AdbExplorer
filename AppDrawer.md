# App Drawer Feature - Implementation Summary

## Overview

The App Drawer is a new feature for ADB Explorer that provides an Android-style app launcher window. It displays all installed apps with their icons and labels, supports launching apps, creating desktop shortcuts via drag-and-drop, and long-press context menus for app management. Primary use case: Windows Subsystem for Android (WSA).

---

## New Files Created

### 1. `Models/AppInfo.cs`
Simple data model implementing `INotifyPropertyChanged`. Properties: `PackageName`, `Label`, `ApkPath`, `IsSystemApp`, `Icon` (ImageSource). The `Label` and `Icon` properties fire `PropertyChanged` so the WPF UI updates when they are enriched asynchronously.

### 2. `Services/AppService.cs`
Core service wrapping ADB commands for app management. Takes `AdbService` in constructor.

**Key methods:**
- `GetInstalledApps(bool includeSystem)` - Runs `pm list packages -3` (or without `-3`), returns list with package-name-derived labels initially.
- `ExtractAppInfo(packageName, deviceId)` - Main entry point for enrichment. Checks disk cache first, then calls `ExtractFromApk()`. Returns `(ImageSource? icon, string? label)`.
- `ExtractFromApk(...)` - Pulls APK via `adb pull`, parses `AndroidManifest.xml` (via `AndroidManifestParser`) to find label/icon resource references, resolves them via `resources.arsc` (via `ArscResourceParser`), extracts icon PNGs from the ZIP, and caches results to disk.
- `LaunchApp(packageName)` - Uses `monkey -p <pkg> -c android.intent.category.LAUNCHER 1`.
- `GetAppVersionInfo(packageName)` - Parses `dumpsys package` for version info.
- `OpenAppSettings(packageName)` - Runs `am start -a android.settings.APPLICATION_DETAILS_SETTINGS`.
- `ForceStopApp(packageName)` - Runs `am force-stop <pkg>`.
- `CleanupCache(deviceId, installedPackages)` - Removes cached data for uninstalled apps.
- `DeriveLabel(packageName)` - Fallback: extracts readable name from package name (e.g., `com.example.myapp` -> `Myapp`).

**Caching:** Results are cached in `%AppData%/AdbExplorer/AppCache/<sanitized-deviceId>/` with `.png` (icon), `.label.txt` (label), and `.noicon` (marker for apps where no icon was found) files. The `SanitizeDeviceId()` method replaces invalid path characters (e.g., `:` from WSA device IDs like `127.0.0.1:58526`) with `_`.

**Icon search pipeline:** Manifest icon name -> foreground variant (`_foreground`) -> common names (`ic_launcher`, `ic_launcher_foreground`, `ic_launcher_round`) -> broad search across all mipmap/drawable entries. Prefers highest density, mipmap over drawable, PNG over WEBP/JPEG.

**Label resolution pipeline:** Manifest direct string -> ArscParser by resource ID -> ArscParser by key name search (app_name, application_name, app_label) -> `dumpsys package` fallback.

**Diagnostic logging:** Uses `TextWriterTraceListener` to write `extraction.log` in the cache directory. Logs all parsing steps for debugging.

### 3. `Helpers/AndroidManifestParser.cs`
Parses Android's binary XML (AXML) format to extract `<application>` attributes from `AndroidManifest.xml`.

**Outputs:** `AppLabel`, `LabelIsReference`, `LabelResourceId`, `IconResourceName`, `IconResourceId`, `RoundIconResourceName`, `RoundIconResourceId`, `PackageName`.

**How it works:**
1. Reads the string pool (all strings referenced in the binary XML).
2. Reads the resource ID table (maps attribute indices to `android:attr` resource IDs like `android:label = 0x01010001`).
3. Walks XML element chunks looking for `<application>`.
4. Reads attributes to find `label` (attr `0x01010001`), `icon` (attr `0x01010002`), and `roundIcon`.
5. For string values, returns the string directly. For reference values (type `0x01`), returns the resource ID for resolution via `ArscResourceParser`.

**Critical bugs fixed (details below):** String pool offset calculation, attribute data positioning, chunk header reading.

### 4. `Helpers/ArscResourceParser.cs`
Parses Android's `resources.arsc` file to resolve resource IDs to string values.

**Key methods:**
- `FindAppLabel(byte[] arscData, int resourceId)` - Resolves label resource ID to string. Falls back to searching key names (`app_name`, `application_name`, `app_label`).
- `ResolveResourceString(byte[] arscData, int resourceId)` - Resolves any resource ID to its string value (used for icon path resolution).

**How it works:**
1. Reads the global string pool (all resource values).
2. Reads the package header to find type and key string pools (using offset fields, not sequential reading).
3. Walks all type chunks, building maps from `(typeId, entryIndex)` to global string pool indices.
4. For a given resource ID `0xPPTTEEEE`, looks up type `TT` (1-based) and entry `EEEE` to find the value.

**Critical bug fixed:** Package header size mismatch on newer Android (288 bytes vs expected 284).

### 5. `Helpers/ShortcutHelper.cs`
Creates Windows `.lnk` shortcut files using COM interop (`IShellLink` + `IPersistFile`).

`CreateAppLaunchShortcut(shortcutPath, packageName, appLabel, deviceName, iconPath)`:
- Target: `AdbExplorer.exe`
- Arguments: `-launch <packageName> [deviceName]`
- Icon: Custom icon if available, otherwise AdbExplorer.exe icon

### 6. `AppDrawerWindow.xaml` + `AppDrawerWindow.xaml.cs`
New WPF Window with:
- **Header row:** Device name label, "Show system apps" checkbox, filter TextBox (with watermark), Refresh button.
- **App grid:** `ScrollViewer` > `ItemsControl` with `WrapPanel`. Each tile is 90x100px with 48x48 icon + label (centered, trimmed). Hover effect via style triggers.
- **Default icon:** Segoe MDL2 Assets glyph E71D (gray) shown via DataTrigger when `Icon` is null.
- **Status bar:** Shows app count, loading progress.
- **Loading overlay:** Semi-transparent overlay with "Loading apps..." text.

**Behavior:**
- **Single click** launches app via `AppService.LaunchApp()`.
- **Long press** (500ms DispatcherTimer): Shows context menu with Launch, App Info, App Settings.
- **Drag** (mouse move past threshold): Creates temp `.lnk` shortcut, uses `DataObject.SetFileDropList()` + `DragDrop.DoDragDrop()`. Temp files cleaned up on window close.
- **Filter:** `ICollectionView` filter on label and package name.
- **Async enrichment:** Apps shown immediately with derived labels and default icons. Labels and icons enriched in background with `SemaphoreSlim(3)` concurrency limit. Progress shown in status bar. Sort by label updated periodically and on completion.

---

## Files Modified

### 7. `App.xaml.cs`
Added command-line argument parsing in `OnStartup`:
- **`-apps [deviceName]`**: Creates `AdbService`, finds device (by model name if given, else first available), shows `AppDrawerWindow` only (no MainWindow).
- **`-launch <packageName> [deviceName]`**: Creates `AdbService`, finds device, calls `AppService.LaunchApp()`, then `Shutdown()` - fire-and-forget mode for desktop shortcuts.
- **No args**: Existing behavior (show `MainWindow`).

Device matching: Iterates `GetDevices()`, matches `device.Model` case-insensitively.

### 8. `MainWindow.xaml`
Added App Drawer toolbar button (green Segoe MDL2 Assets glyph E71D) before the Help button, with a separator.

### 9. `MainWindow.xaml.cs`
Added `AppDrawerButton_Click` handler: Gets active device from `DeviceTabControl`, creates `AppDrawerWindow(adbService, activeDeviceId, model)`, sets `Owner = this`, calls `.Show()`.

---

## Critical Bugs Found and Fixed

### Bug 1: String Pool Offset (AndroidManifestParser)
**Symptom:** All strings from the manifest were garbled/empty. No labels found.
**Cause:** `stringsDataStart` was calculated as `chunkStart + 8 + stringsOffset`, but `stringsOffset` is already relative to the chunk start. The extra `+8` shifted all string reads by 8 bytes.
**Fix:** Changed to `stringsDataStart = chunkStart + stringsStart`.

### Bug 2: Chunk Header Reading (AndroidManifestParser)
**Symptom:** Inconsistent chunk parsing.
**Cause:** Chunk type and headerSize were read as a single `ReadInt32()` with combined constants like `0x001C0001`. This was fragile and error-prone.
**Fix:** Changed to separate `ReadUInt16()` for type and `ReadUInt16()` for headerSize.

### Bug 3: Attribute Positioning Off by 20 Bytes (AndroidManifestParser)
**Symptom:** Manifest parser found `<application>` element but read garbage for all attribute values (wrong label, wrong icon).
**Cause:** Used `chunkStart + headerSize` (= chunkStart + 16) to position to attribute data, but attributes actually start at `chunkStart + 16 (node header) + attributeStart (usually 20)` = chunkStart + 36. The parser was reading the element's namespace/name/attrExt fields as attribute data.
**Fix:** Changed to `attrsStart = chunkStart + nodeHeaderSize + attributeStart` where nodeHeaderSize = 16.

### Bug 4: Package Header Size Mismatch (ArscResourceParser)
**Symptom:** `stringTypeIdx = -1` and `keyStringPool: 0 keys` in trace output. ArscParser couldn't find type or key string pools.
**Cause:** Package header is 288 bytes on newer Android (extra `typeIdOffset` field), but parser read only 284 bytes of fixed fields. This left the reader 4 bytes misaligned, and the type/key string pools were read from wrong positions.
**Fix:** Changed to use `typeStringsOffset` and `keyStringsOffset` fields from the package header to seek directly to the correct pool positions, instead of assuming pools immediately follow the fixed header.

### Bug 5: `noIconMarkerPath` Variable Scope
**Symptom:** Build error - variable defined in `ExtractAppInfo` but used in `ExtractFromApk`.
**Fix:** Passed as parameter to `ExtractFromApk`.

### Bug 6: Emoji Icon Not Rendering in WPF
**Symptom:** The original phone emoji (U+1F4F1) showed as a garbled grid in WPF.
**Fix:** Replaced with Segoe MDL2 Assets font glyph E71D, which renders reliably in WPF.

### Bug 7: WSA Device ID Contains Invalid Path Characters
**Symptom:** Cache directory creation failed for WSA devices (IDs like `127.0.0.1:58526`).
**Fix:** `SanitizeDeviceId()` replaces `:` and other invalid path chars with `_`.

---

## Current Status (What Works)

- App Drawer window opens from toolbar button or via `-apps` command line
- Apps listed with derived labels from package names (immediate)
- Single click launches apps on device
- Long-press context menu with App Info, Settings
- Drag-to-desktop creates `.lnk` shortcuts
- Filter/search by label or package name
- `-launch <package>` command-line mode for shortcut execution
- Async enrichment of labels and icons from APK files
- Caching to disk (labels, icons, no-icon markers)
- Cache cleanup for uninstalled apps

**Label resolution:** Works for most apps. Successfully resolves resource references via resources.arsc for apps like Chrome ("Chrome"), @Voice Aloud Reader ("@Voice Aloud Reader"), Firefox, Gmail, Bookshop.org, Google Play Services for AR, KernelSU.

**Icon extraction:** Works for several apps (Firefox, Bookshop.org, AR Core, @Voice). Icons cached as PNG files.

---

## Known Issues / TODO

### 1. Missing Icons for Apps with Adaptive XML Icons
**Affected apps:** Chrome, Gmail, and other modern Google apps.
**Problem:** The icon resource resolves to an XML file (e.g., `res/r2e.xml` for Chrome) instead of a PNG. These are Android adaptive icons defined in XML with foreground/background layers. The current parser only looks for raster images (PNG, WEBP, JPEG) and skips XML entries.
**Possible fix:** When the resolved icon path is XML, parse it to find the foreground drawable layer and extract that PNG. Or look for a `_foreground` variant PNG alongside the XML.

### 2. Missing Icons for Some Apps
**Affected apps:** Magisk, Readium, and others.
**Problem:** Some apps don't have PNG icons in their base APK. The icon might be in a split APK (e.g., `split_config.mdpi.apk` for density-specific resources) or might be a vector drawable (XML).
**Possible fix:** Check for split APKs via `pm path <pkg>` (returns multiple lines for split APKs) and search each split for icons.

### 3. @Voice Launches LeakCanary Instead of Main Activity
**Problem:** The `monkey` command launches the wrong activity. Instead of launching the main @Voice Aloud Reader activity, it starts the LeakCanary report activity.
**Possible fix:** Use `am start` with the actual launcher activity instead of `monkey`. Get the launcher activity via `dumpsys package <pkg>` (look for the activity with `android.intent.action.MAIN` + `android.intent.category.LAUNCHER` intent filter), or use `cmd package resolve-activity --brief <pkg>` on newer Android versions.

### 4. Missing Labels for Some Apps
**Affected apps:** Google TTS (`com.google.android.tts`), `com.android.providers.contactkeys`, Google Docs.
**Problem:** The resources.arsc may not contain label entries for the default configuration. The label might be in a locale-specific config or in a split APK.
**Possible fix:** Try matching any config (not just default) when looking up label resources. Fall back to `dumpsys package` more aggressively.

### 5. KernelSU Icon Too Small
**Problem:** KernelSU icon file is only 310 bytes - likely a tiny or corrupt image.
**Possible fix:** Add a minimum file size check and treat very small icon files as invalid.

### 6. Performance
**Problem:** Pulling APK files over ADB is slow, especially for large apps (Chrome ~100MB). Enrichment of ~13 apps takes noticeable time.
**Possible fixes:**
- Use on-device extraction: `unzip -p <apk> <entry> | base64` to extract specific files without pulling the entire APK.
- Increase concurrency beyond 3 for faster devices.
- Show a progress indicator or estimated time.

### 7. Diagnostic Logging
**Current state:** `extraction.log` is written to the cache directory via `TextWriterTraceListener` for debugging.
**TODO:** Make logging optional or remove it for production. Consider a debug mode flag.

### 8. Icon Cache as .ico for Shortcuts
**Current state:** Desktop shortcuts use the AdbExplorer.exe icon for all apps.
**Improvement:** Convert the cached PNG to ICO format and set it as the shortcut icon, so each shortcut shows the actual app icon.

---

## ADB Commands Reference

| Operation | Shell Command |
|-----------|--------------|
| List 3rd-party packages | `pm list packages -3` |
| List all packages | `pm list packages` |
| Get APK path | `pm path <pkg>` |
| Get app version info | `dumpsys package <pkg>` |
| Launch app | `monkey -p <pkg> -c android.intent.category.LAUNCHER 1` |
| App settings screen | `am start -a android.settings.APPLICATION_DETAILS_SETTINGS -d package:<pkg>` |
| Force stop | `am force-stop <pkg>` |

---

## Build and Test

```bash
dotnet build -c Release
```

### Manual test scenarios:
1. Click "App Drawer" button in toolbar with device connected -> window shows apps
2. Click an app -> it launches on device
3. Long-press an app -> context menu appears
4. Drag app to desktop -> `.lnk` shortcut created
5. `AdbExplorer.exe -apps` -> shows drawer standalone
6. `AdbExplorer.exe -apps DeviceName` -> shows drawer for named device
7. `AdbExplorer.exe -launch com.example.app` -> launches app and exits
8. `AdbExplorer.exe -launch com.example.app DeviceName` -> launches on named device
