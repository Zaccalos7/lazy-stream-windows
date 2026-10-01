; Orbis Stream - Windows setup
;
; Compiles a self-contained installer that does not require the .NET runtime on
; the target machine: the payload is the single-file publish produced by
; build-installer.sh / build-installer.ps1 (artifacts/publish).
;
;   makensis Installer/installer.nsi
;
; build-installer.sh / build-installer.ps1 pass -DAPPVERSION and -DPUBLISHDIR
; read from Directory.Build.props and the publish output.
Unicode true

!include "MUI2.nsh"
!include "FileFunc.nsh"
!include "LogicLib.nsh"
!include "x64.nsh"

!ifndef APPVERSION
  !define APPVERSION "2.0.0"
!endif
!ifndef PUBLISHDIR
  !define PUBLISHDIR "..\artifacts\publish"
!endif
!ifdef OUTPUTDIR
  !define SETUPFILE "${OUTPUTDIR}\OrbisStream-${APPVERSION}-win-x64-setup.exe"
!else
  !define SETUPFILE "OrbisStream-${APPVERSION}-win-x64-setup.exe"
!endif

!define APPNAME      "Orbis Stream"
!define APPEXE       "OrbisStream.exe"
!define APPPUBLISHER "Orbis"
!define REGKEY       "Software\Orbis\OrbisStream"
!define UNINSTKEY    "Software\Microsoft\Windows\CurrentVersion\Uninstall\OrbisStream"
!define WEBVIEW2KEY  "SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}"
!define WEBVIEW2KEY64 "SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}"

Name "${APPNAME}"
OutFile "${SETUPFILE}"
InstallDir "$LOCALAPPDATA\Programs\OrbisStream"
InstallDirRegKey HKCU "${REGKEY}" "InstallDir"
RequestExecutionLevel user
ShowInstDetails show
ShowUninstDetails show
SetCompressor /SOLID lzma
BrandingText "${APPNAME} ${APPVERSION}"

VIProductVersion "${APPVERSION}.0"
VIAddVersionKey "ProductName"     "${APPNAME}"
VIAddVersionKey "ProductVersion"  "${APPVERSION}"
VIAddVersionKey "CompanyName"     "${APPPUBLISHER}"
VIAddVersionKey "FileDescription" "${APPNAME} setup"
VIAddVersionKey "FileVersion"     "${APPVERSION}.0"
VIAddVersionKey "LegalCopyright"  "${APPPUBLISHER}"

!define MUI_ABORTWARNING
!define MUI_ICON "..\src\Orbis.Stream.App\wwwroot\favicon.ico"
!define MUI_UNICON "..\src\Orbis.Stream.App\wwwroot\favicon.ico"
!define MUI_FINISHPAGE_RUN "$INSTDIR\${APPEXE}"
!define MUI_FINISHPAGE_RUN_TEXT "Start ${APPNAME}"
!define MUI_FINISHPAGE_RUN_CHECKED
!define MUI_UNPAGE_CONFIRM_TEXT "Remove ${APPNAME} and its files?$\r$\n$\r$\nThe local database, logs and uploaded images stored in the installation folder are removed as well. Videos themselves are never touched."

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_LICENSE "..\LICENSE"
!insertmacro MUI_PAGE_COMPONENTS
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES

!insertmacro MUI_LANGUAGE "English"

!macro Require64Bit
  ${IfNot} ${RunningX64}
    MessageBox MB_ICONSTOP|MB_OK "${APPNAME} is a 64-bit application and cannot run on this edition of Windows."
    Abort
  ${EndIf}
!macroend

!macro CheckWebView2
  ; The Evergreen WebView2 Runtime is preinstalled on Windows 11; warn instead of
  ; failing so the installer never blocks a machine that only needs an update.
  ReadRegStr $0 HKLM "${WEBVIEW2KEY64}" "pv"
  ${If} $0 == ""
    ReadRegStr $0 HKLM "${WEBVIEW2KEY}" "pv"
  ${EndIf}
  ${If} $0 == ""
    MessageBox MB_ICONEXCLAMATION|MB_OK \
      "The Microsoft Edge WebView2 Runtime was not found.$\r$\n$\r$\n${APPNAME} needs it to display its interface.$\r$\nInstall the 'Microsoft Edge WebView2 Evergreen Runtime' from Microsoft, then run this installer again."
  ${EndIf}
!macroend

!macro CheckFfmpegInstalled
  ; Used when the FFmpeg component is not selected: the user must already have it.
  nsExec::ExecToStack '"$SYSDIR\where.exe" ffmpeg'
  Pop $0
  ${If} $0 != 0
    MessageBox MB_ICONEXCLAMATION|MB_OK \
      "FFmpeg will not be installed.$\r$\n$\r$\n${APPNAME} needs ffmpeg and ffprobe to stream: install them with 'winget install Gyan.FFmpeg', or copy the two executables into the 'ffmpeg' folder of the installation directory."
  ${EndIf}
!macroend

Function .onInit
  !insertmacro Require64Bit
  !insertmacro CheckWebView2
FunctionEnd

Section "Application" SecMain
  ; The web root holds only application files: drop the previous one so an upgrade does not
  ; keep stale assets (e.g. the React build of the versions before the Razor pages).
  RMDir /r "$INSTDIR\wwwroot"
  SetOutPath "$INSTDIR"
  File /r "${PUBLISHDIR}\*"

  CreateDirectory "$SMPROGRAMS\${APPNAME}"
  CreateShortCut "$SMPROGRAMS\${APPNAME}\${APPNAME}.lnk" "$INSTDIR\${APPEXE}"
  CreateShortCut "$SMPROGRAMS\${APPNAME}\Uninstall.lnk" "$INSTDIR\uninstall.exe"

  WriteUninstaller "$INSTDIR\uninstall.exe"

  WriteRegStr HKCU "${REGKEY}" "InstallDir" "$INSTDIR"
  WriteRegStr HKCU "${REGKEY}" "Version"   "${APPVERSION}"

  WriteRegStr   HKCU "${UNINSTKEY}" "DisplayName"          "${APPNAME}"
  WriteRegStr   HKCU "${UNINSTKEY}" "DisplayVersion"       "${APPVERSION}"
  WriteRegStr   HKCU "${UNINSTKEY}" "Publisher"            "${APPPUBLISHER}"
  WriteRegStr   HKCU "${UNINSTKEY}" "DisplayIcon"          "$INSTDIR\${APPEXE}"
  WriteRegStr   HKCU "${UNINSTKEY}" "InstallLocation"      "$INSTDIR"
  WriteRegStr   HKCU "${UNINSTKEY}" "UninstallString"      '"$INSTDIR\uninstall.exe"'
  WriteRegStr   HKCU "${UNINSTKEY}" "QuietUninstallString" '"$INSTDIR\uninstall.exe" /S'
  WriteRegDWORD HKCU "${UNINSTKEY}" "NoModify" 1
  WriteRegDWORD HKCU "${UNINSTKEY}" "NoRepair" 1

  ; Size in KB for the Add/Remove Programs entry.
  ${GetSize} "$INSTDIR" "/S=0K" $0 $1 $2
  WriteRegDWORD HKCU "${UNINSTKEY}" "EstimatedSize" "$1"
SectionEnd

Section /o "Desktop shortcut" SecDesktop
  CreateShortCut "$DESKTOP\${APPNAME}.lnk" "$INSTDIR\${APPEXE}"
SectionEnd

!ifdef FFMPEGDIR
Section "FFmpeg (included, works offline)" SecFfmpeg
  ; The shared build keeps ffmpeg.exe, ffprobe.exe and the FFmpeg DLLs together in the
  ; ffmpeg subdirectory: the application looks there before falling back to PATH.
  SetOutPath "$INSTDIR\ffmpeg"
  File "${FFMPEGDIR}\*"
  SetOutPath "$INSTDIR"

  DetailPrint "FFmpeg 8.1.3 (GPL) installed in $INSTDIR\ffmpeg"
  WriteRegStr HKCU "${REGKEY}" "Ffmpeg" "$INSTDIR\ffmpeg\ffmpeg.exe"
SectionEnd
!else
Section "FFmpeg (downloaded with winget)" SecFfmpeg
  ; The setup was built without the FFmpeg payload: let winget install the official
  ; build instead. Requires Windows Package Manager, present on Windows 11.
  nsExec::ExecToStack '"$SYSDIR\winget.exe" --version'
  Pop $0
  ${If} $0 != 0
    MessageBox MB_ICONSTOP|MB_OK "winget is not available on this system. Install FFmpeg with 'winget install Gyan.FFmpeg' and run this installer again without the FFmpeg option."
    Abort
  ${EndIf}

  DetailPrint "Installing FFmpeg with winget, this takes a moment..."
  nsExec::ExecToStack '"$SYSDIR\winget.exe" install --id Gyan.FFmpeg --exact --silent --accept-package-agreements --accept-source-agreements --disable-interactivity'
  Pop $0
  Pop $1
  ${If} $0 != 0
    MessageBox MB_ICONSTOP|MB_OK "winget could not install FFmpeg (exit code $0).$\r$\n$\r$\nInstall it manually with 'winget install Gyan.FFmpeg' and start ${APPNAME} again."
  ${EndIf}
SectionEnd
!endif

Section "Uninstall"
  Delete "$DESKTOP\${APPNAME}.lnk"
  RMDir "$SMPROGRAMS\${APPNAME}"
  Delete "$INSTDIR\uninstall.exe"
  RMDir /r "$INSTDIR"
  DeleteRegKey HKCU "${UNINSTKEY}"
  DeleteRegKey HKCU "${REGKEY}"
SectionEnd

!ifdef FFMPEGDIR
; The install section records the bundled FFmpeg in the registry: when the value is
; missing the user opted out of the component, so warn if nothing else provides it.
Function un.onInit
  !insertmacro Require64Bit
  ReadRegStr $0 HKCU "${REGKEY}" "Ffmpeg"
  ${If} $0 == ""
    !insertmacro CheckFfmpegInstalled
  ${EndIf}
FunctionEnd
!endif

Section "Uninstall"
  Delete "$DESKTOP\${APPNAME}.lnk"
  RMDir "$SMPROGRAMS\${APPNAME}"
  Delete "$INSTDIR\uninstall.exe"
  RMDir /r "$INSTDIR"
  DeleteRegKey HKCU "${UNINSTKEY}"
  DeleteRegKey HKCU "${REGKEY}"
SectionEnd
