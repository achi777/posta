Unicode true
!ifdef INSTALL_ALL_USERS
  RequestExecutionLevel admin
  !define INSTALL_ROOT HKLM
  !define SHELL_CONTEXT all
!else
  RequestExecutionLevel user
  !define INSTALL_ROOT HKCU
  !define SHELL_CONTEXT current
!endif
SetCompressor /SOLID zlib

!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "WinVer.nsh"
!include "x64.nsh"

!define UNINSTALL_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\Posta"
!define WEBVIEW_KEY "Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}"

Name "Posta (ფოსტა)"
OutFile "${SETUP_OUTPUT}"
!ifdef INSTALL_ALL_USERS
  InstallDir "$PROGRAMFILES64\Posta"
!else
  InstallDir "$LOCALAPPDATA\Programs\Posta"
  InstallDirRegKey HKCU "${UNINSTALL_KEY}" "InstallLocation"
!endif
VIProductVersion "${APP_VERSION}"
VIAddVersionKey /LANG=1033 "ProductName" "Posta"
VIAddVersionKey /LANG=1033 "FileDescription" "Posta Setup for Windows x64"
VIAddVersionKey /LANG=1033 "CompanyName" "Archil Odishelidze"
VIAddVersionKey /LANG=1033 "LegalCopyright" "© 2026 Archil Odishelidze"
VIAddVersionKey /LANG=1033 "FileVersion" "${APP_VERSION}"
VIAddVersionKey /LANG=1033 "ProductVersion" "${APP_VERSION}"

!define MUI_ICON "..\src\Posta.App\Assets\posta.ico"
!define MUI_UNICON "..\src\Posta.App\Assets\posta.ico"
!ifndef INSTALL_ALL_USERS
  !define MUI_FINISHPAGE_RUN "$INSTDIR\Posta.App.exe"
  !define MUI_FINISHPAGE_RUN_NOTCHECKED
!endif
!define MUI_LANGDLL_ALLLANGUAGES
!define MUI_LANGDLL_WINDOWTITLE "Posta Setup"
!define MUI_LANGDLL_INFO "აირჩიეთ ენა / Select a language"
!define MUI_WELCOMEPAGE_TEXT "$(WelcomeText)"

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_COMPONENTS
!ifndef INSTALL_ALL_USERS
  !insertmacro MUI_PAGE_DIRECTORY
!endif
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES

!insertmacro MUI_LANGUAGE "Georgian"
!insertmacro MUI_LANGUAGE "English"

LangString MainSection ${LANG_GEORGIAN} "ფოსტა (Posta)"
LangString MainSection ${LANG_ENGLISH} "Posta"
!ifdef INSTALL_ALL_USERS
  LangString WelcomeText ${LANG_GEORGIAN} "Posta დაყენდება Program Files-ში ამ კომპიუტერის ყველა მომხმარებლისთვის."
  LangString WelcomeText ${LANG_ENGLISH} "Install Posta in Program Files for everyone who uses this computer."
!else
  LangString WelcomeText ${LANG_GEORGIAN} "დააყენეთ ფოსტა (Posta) თქვენი Windows ანგარიშისთვის. დააჭირეთ შემდეგს გასაგრძელებლად."
  LangString WelcomeText ${LANG_ENGLISH} "Install Posta for your Windows account. Select Next to continue."
!endif
LangString DesktopSection ${LANG_GEORGIAN} "დესკტოპის მალსახმობი"
LangString DesktopSection ${LANG_ENGLISH} "Desktop shortcut"
LangString UnsupportedSystem ${LANG_GEORGIAN} "Posta-ს სჭირდება Windows 10 ან 11 (x64)."
LangString UnsupportedSystem ${LANG_ENGLISH} "Posta requires Windows 10 or 11 (x64)."
LangString WebViewInstalling ${LANG_GEORGIAN} "მიმდინარეობს Microsoft WebView2-ის ინსტალაცია…"
LangString WebViewInstalling ${LANG_ENGLISH} "Installing Microsoft WebView2 Runtime…"
LangString WebViewFailed ${LANG_GEORGIAN} "Microsoft WebView2 ვერ დაყენდა. შეამოწმეთ ინტერნეტთან კავშირი და ხელახლა გაუშვით ინსტალაცია."
LangString WebViewFailed ${LANG_ENGLISH} "Microsoft WebView2 could not be installed. Check your internet connection and run Setup again."

Function .onInit
  SetShellVarContext ${SHELL_CONTEXT}
  SetRegView 64
  !ifdef INSTALL_ALL_USERS
    StrCpy $INSTDIR "$PROGRAMFILES64\Posta"
  !endif
  !insertmacro MUI_LANGDLL_DISPLAY
  ${IfNot} ${IsNativeAMD64}
  ${OrIfNot} ${AtLeastWin10}
    MessageBox MB_OK|MB_ICONSTOP "$(UnsupportedSystem)" /SD IDOK
    SetErrorLevel 1
    Abort
  ${EndIf}
FunctionEnd

Function GetWebView2Version
  SetRegView 32
  ReadRegStr $0 HKLM "${WEBVIEW_KEY}" "pv"
  !ifndef INSTALL_ALL_USERS
  ${If} $0 == ""
  ${OrIf} $0 == "0.0.0.0"
    ReadRegStr $0 HKCU "${WEBVIEW_KEY}" "pv"
  ${EndIf}
  !endif
  SetRegView 64
FunctionEnd

Section "$(MainSection)"
  SectionIn RO
  Call GetWebView2Version
  ${If} $0 == ""
  ${OrIf} $0 == "0.0.0.0"
    DetailPrint "$(WebViewInstalling)"
    InitPluginsDir
    SetOutPath "$PLUGINSDIR"
    File /oname=WebView2Setup.exe "${WEBVIEW_SETUP}"
    ExecWait '"$PLUGINSDIR\WebView2Setup.exe" /silent /install'
    Call GetWebView2Version
    ${If} $0 == ""
    ${OrIf} $0 == "0.0.0.0"
      MessageBox MB_OK|MB_ICONSTOP "$(WebViewFailed)" /SD IDOK
      SetErrorLevel 2
      Abort
    ${EndIf}
  ${EndIf}

  SetOutPath "$INSTDIR"
  File /r "${PAYLOAD}\*.*"
  WriteUninstaller "$INSTDIR\Uninstall.exe"
  CreateShortcut "$SMPROGRAMS\Posta.lnk" "$INSTDIR\Posta.App.exe"
  WriteRegStr ${INSTALL_ROOT} "${UNINSTALL_KEY}" "DisplayName" "Posta (ფოსტა)"
  WriteRegStr ${INSTALL_ROOT} "${UNINSTALL_KEY}" "DisplayVersion" "${APP_VERSION}"
  WriteRegStr ${INSTALL_ROOT} "${UNINSTALL_KEY}" "Publisher" "Archil Odishelidze"
  WriteRegStr ${INSTALL_ROOT} "${UNINSTALL_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr ${INSTALL_ROOT} "${UNINSTALL_KEY}" "DisplayIcon" "$INSTDIR\Posta.App.exe,0"
  WriteRegStr ${INSTALL_ROOT} "${UNINSTALL_KEY}" "UninstallString" '"$INSTDIR\Uninstall.exe"'
  WriteRegStr ${INSTALL_ROOT} "${UNINSTALL_KEY}" "QuietUninstallString" '"$INSTDIR\Uninstall.exe" /S'
  WriteRegDWORD ${INSTALL_ROOT} "${UNINSTALL_KEY}" "NoModify" 1
  WriteRegDWORD ${INSTALL_ROOT} "${UNINSTALL_KEY}" "NoRepair" 1
  WriteRegDWORD ${INSTALL_ROOT} "${UNINSTALL_KEY}" "Language" $LANGUAGE
SectionEnd

Section /o "$(DesktopSection)"
  SetOutPath "$INSTDIR"
  CreateShortcut "$DESKTOP\Posta.lnk" "$INSTDIR\Posta.App.exe"
  WriteRegDWORD ${INSTALL_ROOT} "${UNINSTALL_KEY}" "DesktopShortcut" 1
SectionEnd

Function un.onInit
  SetShellVarContext ${SHELL_CONTEXT}
  SetRegView 64
  !ifdef INSTALL_ALL_USERS
    StrCpy $INSTDIR "$PROGRAMFILES64\Posta"
  !endif
  ReadRegDWORD $0 ${INSTALL_ROOT} "${UNINSTALL_KEY}" "Language"
  ${If} $0 != ""
    StrCpy $LANGUAGE $0
  ${EndIf}
FunctionEnd

Section "Uninstall"
  Delete "$SMPROGRAMS\Posta.lnk"
  ReadRegDWORD $0 ${INSTALL_ROOT} "${UNINSTALL_KEY}" "DesktopShortcut"
  ${If} $0 == 1
    Delete "$DESKTOP\Posta.lnk"
  ${EndIf}
  DeleteRegKey ${INSTALL_ROOT} "${UNINSTALL_KEY}"
  !include "${UNINSTALL_MANIFEST}"
  Delete /REBOOTOK "$INSTDIR\Uninstall.exe"
  RMDir "$INSTDIR"
SectionEnd
