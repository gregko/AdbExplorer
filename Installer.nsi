; AdbExplorer NSIS Installer Script (Fixed Uninstaller)
; For NSIS 3.x

!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "x64.nsh"
!include "FileFunc.nsh"

;--------------------------------
; General

Name "ADB Explorer"
OutFile "installer_output\AdbExplorer_Setup.exe"
InstallDir "$PROGRAMFILES64\AdbExplorer"
InstallDirRegKey HKLM "Software\AdbExplorer" "InstallPath"
RequestExecutionLevel admin
ShowInstDetails show
ShowUninstDetails show

;--------------------------------
; Version Information

!define PRODUCT_NAME "ADB Explorer"
!define PRODUCT_VERSION "1.2.7"
!define PRODUCT_PUBLISHER "AdbExplorer Team"
!define PRODUCT_UNINST_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\${PRODUCT_NAME}"
!define PRODUCT_UNINST_ROOT_KEY "HKLM"

VIProductVersion "1.2.7.0"
VIAddVersionKey "ProductName" "${PRODUCT_NAME}"
VIAddVersionKey "CompanyName" "${PRODUCT_PUBLISHER}"
VIAddVersionKey "FileDescription" "ADB Explorer Installer"
VIAddVersionKey "FileVersion" "${PRODUCT_VERSION}"

;--------------------------------
; Interface Settings

!define MUI_ABORTWARNING
!define MUI_ICON "Resources\icon.ico"
!define MUI_UNICON "Resources\icon.ico"

;--------------------------------
; Welcome Page Customization

!define MUI_WELCOMEPAGE_TITLE "Welcome to ADB Explorer ${PRODUCT_VERSION} Setup"
!define MUI_WELCOMEPAGE_TEXT "This wizard will guide you through the installation of ADB Explorer version ${PRODUCT_VERSION}.$\r$\n$\r$\nADB Explorer is a Windows file manager for Android devices that provides a familiar Explorer-like interface for browsing and managing files via ADB.$\r$\n$\r$\nClick Next to continue."

;--------------------------------
; Pages

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_LICENSE "LICENSE.txt"
!insertmacro MUI_PAGE_COMPONENTS
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_WELCOME
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_UNPAGE_FINISH

;--------------------------------
; Languages

!insertmacro MUI_LANGUAGE "English"

;--------------------------------
; Installer Sections

Section "!ADB Explorer Core Files" SEC_CORE
  SectionIn RO
  
  ; IMPORTANT: Set registry view to 64-bit for 64-bit installer
  SetRegView 64
  
  SetOutPath "$INSTDIR"
  
  ; Main application files
  File "bin\Release\net8.0-windows\AdbExplorer.exe"
  File "bin\Release\net8.0-windows\AdbExplorer.dll"
  File "bin\Release\net8.0-windows\AdbExplorer.deps.json"
  File "bin\Release\net8.0-windows\AdbExplorer.runtimeconfig.json"
  File /r "bin\Release\net8.0-windows\*.dll"
  
  ; Resources
  SetOutPath "$INSTDIR\Resources"
  File /r "Resources\*.*"
  
  ; Documentation
  SetOutPath "$INSTDIR"
  File "LICENSE.txt"
  File "README.md"
  
  ; Write registry keys (64-bit registry)
  WriteRegStr HKLM "Software\AdbExplorer" "InstallPath" "$INSTDIR"
  WriteRegStr HKLM "${PRODUCT_UNINST_KEY}" "DisplayName" "${PRODUCT_NAME}"
  WriteRegStr HKLM "${PRODUCT_UNINST_KEY}" "UninstallString" '"$INSTDIR\Uninstall.exe"'
  WriteRegStr HKLM "${PRODUCT_UNINST_KEY}" "QuietUninstallString" '"$INSTDIR\Uninstall.exe" /S'
  WriteRegStr HKLM "${PRODUCT_UNINST_KEY}" "DisplayIcon" "$INSTDIR\AdbExplorer.exe"
  WriteRegStr HKLM "${PRODUCT_UNINST_KEY}" "DisplayVersion" "${PRODUCT_VERSION}"
  WriteRegStr HKLM "${PRODUCT_UNINST_KEY}" "Publisher" "${PRODUCT_PUBLISHER}"
  WriteRegStr HKLM "${PRODUCT_UNINST_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegDWORD HKLM "${PRODUCT_UNINST_KEY}" "NoModify" 1
  WriteRegDWORD HKLM "${PRODUCT_UNINST_KEY}" "NoRepair" 1
  WriteRegDWORD HKLM "${PRODUCT_UNINST_KEY}" "EstimatedSize" 10240
  
  ; Create uninstaller
  WriteUninstaller "$INSTDIR\Uninstall.exe"
  
SectionEnd

Section "Start Menu Shortcuts" SEC_STARTMENU
  CreateDirectory "$SMPROGRAMS\${PRODUCT_NAME}"
  CreateShortcut "$SMPROGRAMS\${PRODUCT_NAME}\${PRODUCT_NAME}.lnk" "$INSTDIR\AdbExplorer.exe"
  CreateShortcut "$SMPROGRAMS\${PRODUCT_NAME}\Uninstall.lnk" "$INSTDIR\Uninstall.exe"
SectionEnd

Section "Desktop Shortcut" SEC_DESKTOP
  CreateShortcut "$DESKTOP\${PRODUCT_NAME}.lnk" "$INSTDIR\AdbExplorer.exe"
SectionEnd

Section "Context Menu Integration" SEC_CONTEXT
  SetRegView 64
  WriteRegStr HKCR "Directory\shell\AdbExplorer" "" "Open in ADB Explorer"
  WriteRegStr HKCR "Directory\shell\AdbExplorer" "Icon" "$INSTDIR\AdbExplorer.exe"
  WriteRegStr HKCR "Directory\shell\AdbExplorer\command" "" '"$INSTDIR\AdbExplorer.exe" "%1"'
SectionEnd

;--------------------------------
; Section Descriptions

!insertmacro MUI_FUNCTION_DESCRIPTION_BEGIN
  !insertmacro MUI_DESCRIPTION_TEXT ${SEC_CORE} "Core application files (required)"
  !insertmacro MUI_DESCRIPTION_TEXT ${SEC_STARTMENU} "Create Start Menu shortcuts"
  !insertmacro MUI_DESCRIPTION_TEXT ${SEC_DESKTOP} "Create Desktop shortcut"
  !insertmacro MUI_DESCRIPTION_TEXT ${SEC_CONTEXT} "Add context menu option for folders"
!insertmacro MUI_FUNCTION_DESCRIPTION_END

;--------------------------------
; Uninstaller Section

Section "Uninstall"
  
  ; CRITICAL: Set registry view to 64-bit for uninstaller
  SetRegView 64
  
  ; Ask about settings deletion
  MessageBox MB_YESNO "Do you want to delete all settings and temporary files?" IDYES DeleteSettings IDNO SkipSettings
  
  DeleteSettings:
    ; Delete settings
    RMDir /r "$APPDATA\AdbExplorer"
    
    ; Delete temp files
    RMDir /r "$TEMP\AdbExplorer"
    
  SkipSettings:
  
  ; Delete program files
  Delete "$INSTDIR\AdbExplorer.exe"
  Delete "$INSTDIR\AdbExplorer.dll"
  Delete "$INSTDIR\AdbExplorer.deps.json"
  Delete "$INSTDIR\AdbExplorer.runtimeconfig.json"
  Delete "$INSTDIR\*.dll"
  Delete "$INSTDIR\LICENSE.txt"
  Delete "$INSTDIR\README.md"
  Delete "$INSTDIR\Uninstall.exe"
  
  RMDir /r "$INSTDIR\Resources"
  RMDir "$INSTDIR"
  
  ; Delete shortcuts
  Delete "$DESKTOP\${PRODUCT_NAME}.lnk"
  Delete "$SMPROGRAMS\${PRODUCT_NAME}\*.*"
  RMDir "$SMPROGRAMS\${PRODUCT_NAME}"
  
  ; Delete registry keys - IMPORTANT: Delete in correct order and correct registry view
  
  ; First delete the uninstall key (this removes it from Programs and Features)
  DeleteRegKey HKLM "${PRODUCT_UNINST_KEY}"
  
  ; Then delete application keys
  DeleteRegKey HKLM "Software\AdbExplorer"
  DeleteRegKey HKCU "Software\AdbExplorer"
  
  ; Delete context menu
  DeleteRegKey HKCR "Directory\shell\AdbExplorer"
  
  ; Also try to delete from 32-bit registry view (just in case)
  SetRegView 32
  DeleteRegKey HKLM "${PRODUCT_UNINST_KEY}"
  DeleteRegKey HKLM "Software\AdbExplorer"
  SetRegView 64
  
SectionEnd

;--------------------------------
; Functions

Function .onInit
  ; Check for 64-bit Windows
  ${If} ${RunningX64}
    SetRegView 64
  ${Else}
    MessageBox MB_OK "This application requires 64-bit Windows."
    Abort
  ${EndIf}
FunctionEnd

Function un.onInit
  ; IMPORTANT: Set 64-bit registry view for uninstaller too
  SetRegView 64
FunctionEnd