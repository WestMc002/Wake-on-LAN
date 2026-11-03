@ECHO OFF
SETLOCAL EnableExtensions
ECHO #######################################
ECHO #  Wake on LAN erstellen              #
ECHO #                                     #
ECHO #  Autor: Guenther Schoder            #
ECHO #         www.westmc002.de            #
ECHO #                                     #
ECHO #  Datum: 01.11.2026 / 02.11.2026     #
ECHO #######################################
ECHO.

CD /D "%~dp0"
SET "RESULT=0"

REM Einstellungen (projektspezifisch anpassen, Platzhalter in [ ] ersetzen)
REM MAJOR     = Hauptversion
REM NAME      = Programmname = Name der EXE ohne .exe (auch Prozessname und Backup-Ordner)
REM NAMESPACE = C#-Namespace, in dem die Klasse AppInfo liegt
REM SRCDIR    = Ordner mit allen .cs-Dateien (VersionInfo.cs wird dort erzeugt)
REM RESDIR    = Ordner mit Icon und GS-Logo.png
REM ICON      = Dateiname des Programm-Icons (.ico) im Ordner RESDIR
REM EXTRAREFS = zusaetzliche Verweise (z. B. /r:System.Web.Extensions.dll), sonst leer lassen
SET "MAJOR=1"
SET "NAME=Wake on LAN"
SET "NAMESPACE=WakeOnLan"
SET "SRCDIR=Quellcode"
SET "RESDIR=Resources"
SET "ICON=WakeOnLan.ico"
SET "EXTRAREFS=/win32manifest:Quellcode\app.manifest /resource:Resources\Demo.xml,Demo.xml /resource:Resources\Web\index.html,Web.index.html /resource:Resources\Web\app.css,Web.app.css /resource:Resources\Web\app.js,Web.app.js /r:System.Xml.dll /r:System.Xml.Linq.dll /r:System.Security.dll /r:Microsoft.CSharp.dll /lib:%WINDIR%\Microsoft.NET\Framework\v4.0.30319\WPF /r:PresentationFramework.dll /r:PresentationCore.dll /r:WindowsBase.dll /r:System.Xaml.dll"
SET "EXE=%NAME%.exe"
SET "NEWEXE=%NAME%.neu.exe"

REM C#-Compiler aus dem .NET Framework 4 suchen
SET "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
IF NOT EXIST "%CSC%" SET "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
IF NOT EXIST "%CSC%" GOTO :ERR_CSC

REM Das Programm muss beendet sein
:WAITEXE
powershell -NoProfile -Command "if(Get-Process -Name '%NAME%' -ErrorAction SilentlyContinue){exit 1}"
IF NOT ERRORLEVEL 1 GOTO :VERSION
ECHO "%EXE%" laeuft noch. Bitte beenden und dann eine Taste druecken.
PAUSE >NUL
GOTO :WAITEXE

:VERSION
REM Versionsnummer: Haupt.Neben.Jahr.MonatTag
REM Nebenversion = hoechste vorhandene (aktuelle EXE und Backup) + 1
SET "MINOR="
FOR /F "usebackq delims=" %%M IN (`powershell -NoProfile -Command "$m=0; if(Test-Path -LiteralPath '%EXE%'){ $v=(Get-Item -LiteralPath '%EXE%').VersionInfo.FileVersion; if($v -match '^(\d+)\.(\d+)' -and [int]$Matches[1] -eq %MAJOR%){ $m=[int]$Matches[2]+1 } }; if(Test-Path -LiteralPath 'Backup'){ foreach($f in Get-ChildItem -LiteralPath 'Backup'){ if($f.Name -match ' v(\d+)\.(\d+)\.' -and [int]$Matches[1] -eq %MAJOR% -and [int]$Matches[2] -ge $m){ $m=[int]$Matches[2]+1 } } }; $m"`) DO SET "MINOR=%%M"
IF "%MINOR%"=="" GOTO :ERR_VERSION
FOR /F "usebackq delims=" %%V IN (`powershell -NoProfile -Command "Get-Date -Format 'yyyy.MMdd'"`) DO SET "DATEVER=%%V"
SET "VER=%MAJOR%.%MINOR%.%DATEVER%"
ECHO Neue Version: %VER%
ECHO.

REM Versionsdatei fuer den Compiler erzeugen
> "%SRCDIR%\VersionInfo.cs" ECHO [assembly: System.Reflection.AssemblyVersion("%VER%")]
>> "%SRCDIR%\VersionInfo.cs" ECHO [assembly: System.Reflection.AssemblyFileVersion("%VER%")]
>> "%SRCDIR%\VersionInfo.cs" ECHO [assembly: System.Reflection.AssemblyInformationalVersion("%VER%")]
>> "%SRCDIR%\VersionInfo.cs" ECHO namespace %NAMESPACE% { public static partial class AppInfo { public const string Version = "%VER%"; } }

REM Kompilieren (zuerst in eine neue Datei, die alte EXE bleibt bei Fehlern erhalten)
IF EXIST "%NEWEXE%" DEL /F /Q "%NEWEXE%"
ECHO Kompiliere %EXE% ...
"%CSC%" /nologo /target:winexe /optimize+ /codepage:65001 "/out:%NEWEXE%" "/win32icon:%RESDIR%\%ICON%" "/resource:%RESDIR%\%ICON%,%ICON%" "/resource:%RESDIR%\GS-Logo.png,GS-Logo.png" /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll %EXTRAREFS% "%SRCDIR%\*.cs" > Erstellen.log 2>&1
SET "RESULT=%ERRORLEVEL%"
TYPE Erstellen.log
IF NOT "%RESULT%"=="0" GOTO :ERR_BUILD

REM Bisherige EXE sichern, falls ihre Version noch keinen Ordner in Backup hat
IF NOT EXIST "%EXE%" GOTO :INSTALL
SET "OLDVER="
FOR /F "usebackq delims=" %%O IN (`powershell -NoProfile -Command "(Get-Item -LiteralPath '%EXE%').VersionInfo.FileVersion"`) DO SET "OLDVER=%%O"
IF "%OLDVER%"=="" SET "OLDVER=unbekannt"
SET "OLDDIR=Backup\%NAME% v%OLDVER%"
IF EXIST "%OLDDIR%\" GOTO :INSTALL
MKDIR "%OLDDIR%"
MOVE /Y "%EXE%" "%OLDDIR%\%EXE%" >NUL
IF ERRORLEVEL 1 GOTO :ERR_BACKUP
ECHO Backup der bisherigen EXE: %OLDDIR%

:INSTALL
MOVE /Y "%NEWEXE%" "%EXE%" >NUL
IF ERRORLEVEL 1 GOTO :ERR_INSTALL
COLOR A0
ECHO Erfolgreich erstellt: %EXE% (Version %VER%)

REM Optionale projektspezifische Zusatzschritte hier einfuegen
REM Screenshots fuer GitHub aus Beispieldaten (hell und dunkel) nach Screenshots,
REM nur auf Nachfrage: ohne Antwort nach 10 Sekunden Nein, bei /nopause nie
IF /I "%~1"=="/nopause" GOTO :NOSHOTS
ECHO.
CHOICE /C JN /T 10 /D N /M "Screenshots fuer GitHub erstellen (nach 10 s automatisch Nein)"
IF ERRORLEVEL 2 GOTO :NOSHOTS
ECHO Erstelle Screenshots (bitte Maus und Tastatur kurz nicht benutzen) ...
START "" /WAIT "%EXE%" --screenshots "%CD%\Screenshots"
IF ERRORLEVEL 1 (ECHO Hinweis: Screenshots fehlgeschlagen - siehe Screenshots\Fehler.txt) ELSE (ECHO Screenshots erstellt.)
:NOSHOTS

REM Sicherung: komplettes Projekt nach Backup\<Projekt> v<Version>
SET "BAKDIR=Backup\%NAME% v%VER%"
ECHO.
ECHO Sichere nach %BAKDIR% ...
ROBOCOPY "%CD%" "%CD%\%BAKDIR%" /E /XD "%CD%\Backup" /XF "%NEWEXE%" "Erstellen.log" /R:1 /W:1 /NFL /NDL /NJH /NJS /NP >NUL
IF ERRORLEVEL 8 GOTO :ERR_SNAPSHOT
ECHO Gesichert.
GOTO :Ende

:ERR_CSC
COLOR C0
SET "RESULT=1"
ECHO Fehler: csc.exe aus .NET Framework 4 wurde nicht gefunden.
GOTO :Ende

:ERR_VERSION
COLOR C0
SET "RESULT=1"
ECHO Fehler: Die neue Versionsnummer konnte nicht ermittelt werden.
GOTO :Ende

:ERR_BUILD
COLOR C0
ECHO.
ECHO Fehler beim Kompilieren - siehe Erstellen.log
GOTO :Ende

:ERR_BACKUP
COLOR C0
SET "RESULT=1"
ECHO Fehler: Die alte EXE konnte nicht nach Backup verschoben werden.
IF EXIST "%NEWEXE%" DEL /F /Q "%NEWEXE%"
GOTO :Ende

:ERR_INSTALL
COLOR C0
SET "RESULT=1"
ECHO Fehler: Die neue EXE konnte nicht an ihren Platz verschoben werden.
ECHO Sie liegt als "%NEWEXE%" im Projektordner.
GOTO :Ende

:ERR_SNAPSHOT
COLOR C0
SET "RESULT=1"
ECHO Sicherung fehlgeschlagen.

:Ende
ECHO.
IF /I NOT "%~1"=="/nopause" PAUSE
ENDLOCAL & EXIT /B %RESULT%