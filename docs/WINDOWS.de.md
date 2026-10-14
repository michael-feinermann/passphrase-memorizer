# Passphrase Memorizer 1.0.1 für Windows

Die Windows-Version übernimmt die Funktionen und Gestaltung von `v1.0.1`: BIP39 und EFF, 1 bis 128 Wörter, Reim, Ballade, Gedicht, Kurzgeschichte und Rap, Englisch und Deutsch, unveränderte blaue, fette `[Wort]`-Markierungen, zehnminütige Anzeige sowie Verdecken beim Deaktivieren. Die Oberfläche verwendet WPF. Die Windows-Laufzeit verwendet einen eigenen nativen CUDA-Prozess in einem weniger privilegierten Windows-AppContainer ohne Netzwerkfähigkeiten. Modellgewichte, KV-Cache und Modellberechnung liegen auf der NVIDIA-GPU. Es gibt keinen CPU-Ausweichbetrieb.

## Installation und Modell

Verwende das CUDA-Paket `Passphrase-Memorizer-1.0.1-Windows-CUDA-x64.zip` und seine Signaturdateien aus dem [Windows-Release](https://github.com/michael-feinermann/passphrase-memorizer/releases/tag/v1.0.1). Entpacke das gesamte Archiv in einen lokalen NTFS-Ordner und öffne `Passphrase Memorizer.exe`; behalte alle Paket- und Signaturdateien zusammen. Für diese direkte Ausführung ist kein .NET SDK nötig. Die unten dokumentierte Installation mit Verknüpfungen und Deinstallationseintrag verwendet dagegen das Repository-Skript und benötigt die Build-Voraussetzungen. Das ältere Paket ohne `CUDA` im Namen ist die frühere CPU-Ausgabe.

Der Build ist für Windows 11 x64, NVIDIA-Ada-GPUs (Compute Capability 8.9) und lokale NTFS-Modelldateien ausgelegt. Für das vorhandene Gemma 4 31B wurde die RTX 4090 mit 24 GiB VRAM geprüft. Das App-Paket enthält die .NET-10-Laufzeit und die erforderlichen CUDA-Bibliotheken; ein kompatibler NVIDIA-Treiber ist nötig. Modellgewichte sind separat und werden nicht heruntergeladen. Die Installation liegt unter `%LOCALAPPDATA%\Programs\Passphrase Memorizer`; Verknüpfungen werden im Startmenü und auf dem Desktop angelegt. Die Deinstallation ist über Windows' installierte Apps verfügbar.

Auf diesem PC wird die vorhandene Gemma-4-31B-GGUF-Datei direkt verwendet:

```text
D:\KI-Modelle\Ollama\blobs\sha256-0374ce7b0124db9ba96fc649e835c531223ee224a497ce88a374baaea10932ec
```

Der SHA-256-Wert wurde über die gesamte 17.650.999.456 Bytes große Datei geprüft und entspricht dem Dateinamen. Ollama meldet Gemma 4, 30,7 Milliarden Parameter und Q4_0. Die App verwendet die Modelldatei direkt mit der festgelegten llama.cpp-Version; Ollamas HTTP-Dienst wird nicht angesprochen. Alle Modell-Tensoren einschließlich der Token-Embeddings werden auf die GPU geladen. Der Kontext bleibt bei 8192 Tokens; Flash Attention und ein Q4_0-KV-Cache sparen Grafikspeicher. Der Abbrechen-Knopf beendet den eigenen KI-Prozess.

Vor jedem CUDA-Start prüft die App ausschließlich für ihren eigenen Runner-Pfad die NVIDIA-Programmeinstellung `Prefer No Sysmem Fallback`. Fehlt genügend freier Grafikspeicher, die erforderliche GPU oder eine überprüfbare Treibereinstellung, wird die Generierung beendet. Es wird kein Modell in den RAM ausgelagert, um die Inferenz dort fortzusetzen. Diese Richtlinie verändert keine globale NVIDIA-Einstellung. Sie verwendet verborgene DRS-Schnittstellen, die mit Treiber 617.42 geprüft wurden; nicht unterstützte Treiber führen zum Abbruch.

Die GPU-Ausführung benötigt weiterhin kleine RAM-Bereiche für Oberfläche, Tokenisierung, Sampling, Verwaltung und vorübergehende Upload-Puffer. Das bedeutet keine dauerhaft im RAM gehaltenen Modellgewichte oder CPU-Modellberechnung. Windows und der Grafiktreiber bleiben Teil der Vertrauensgrenze; unter beliebigen zukünftigen Betriebssystemzuständen lässt sich eine universelle physische Speicherresidenzgarantie nicht geben. [NVIDIAs Beschreibung der Fallback-Einstellung](https://nvidia.custhelp.com/app/answers/detail/a_id/5490).

Beim ersten Start gilt Englisch. Der lokale Modellpfad wird nach Auswahl gespeichert und beim Start erneut geprüft. Auf diesem PC wird das oben genannte vorhandene Modell als Standard vorgeschlagen. Fehlt eine gespeicherte Datei oder ist sie verknüpft, nicht lokal oder cloudbasiert, wird die Generierung gesperrt. Es gibt keinen Online-Ausweichweg.

## Verwendung

1. Wähle BIP39 oder EFF und gib deine vorhandenen englischen Wörter ein. EFF akzeptiert zusätzlich Bindestriche als Trenner. Großschreibung und Wiederholungen bleiben erhalten.
2. Wähle Sprache und Textform und starte die lokale Generierung.
3. Die Ausgabe bleibt zunächst verdeckt. Bestätige die Anzeige nur bei geschütztem Bildschirm. Ein Wechsel zu einer anderen App verdeckt Eingabe und Ausgabe wieder.
4. Leere die Sitzung nach der Verwendung. `Strg+Umschalt+K` leert sie; `Strg+Umschalt+H` verdeckt sie.

Die App prüft Wortlisten-Zugehörigkeit und die genaue markierte Wortfolge. Sie prüft keine BIP39-Wallet-Prüfsumme. Die Merkhilfe erhöht die Entropie nicht und ersetzt kein sicheres Backup. Eingaben, Prompts und Ausgaben werden nicht absichtlich in Dateien, Verlauf, Zwischenablage oder Telemetrie gespeichert. Gespeichert werden ausschließlich Sprache, Fenstergröße und Modellpfad.

## Signaturen und Schlüssel

Jede Paketdatei und das vollständige Inventar verlangen sowohl RSA-4096-PSS/SHA-512 als auch ML-DSA-87. Die App enthält festgelegte öffentliche Schlüsselfingerabdrücke. Die Generierung prüft das Paket und den tatsächlich gestarteten KI-Prozess vor dem ersten Prompt-Byte. Geöffnete Lesesperren verhindern das Ersetzen der geprüften Dateien während der Ausführung. SHA-256, SHA3-512 und Skein-1024-1024 binden das Inventar an die Dateiinhalte.

Die privaten Windows-Schlüssel liegen ausschließlich in `A:\Passphrase Memorizer Keys` als getrennte DPAPI-CurrentUser-verschlüsselte Dateien. Dort gibt es keinen unverschlüsselten Schlüssel zum Entschlüsseln. FAT32 bietet keine NTFS-Dateirechte; der Schutz ruhender Schlüssel erfolgt durch DPAPI. Für spätere Signierung müssen das ursprüngliche Windows-Benutzerprofil und seine DPAPI-Schlüssel verfügbar bleiben. Ein Laufwerksbackup allein garantiert keine Wiederherstellbarkeit. Prozesse mit den Rechten dieses Windows-Benutzers können DPAPI ebenfalls verwenden. [Microsofts DPAPI-Dokumentation](https://learn.microsoft.com/en-us/windows/win32/SecCrypto/example-c-program-using-cryptprotectdata).

Das öffentliche selbstsignierte Zertifikat dient als Identitätsmetadatum für diese Schlüssel. Es wird kein Windows-Stammzertifikat installiert; der Build besitzt keine öffentlich vertrauenswürdige Authenticode-Signatur. Vor einer Installation aus fremder Quelle müssen die öffentlichen Fingerabdrücke über einen unabhängigen vertrauenswürdigen Kanal geprüft werden. Eine App kann durch ihre eigene Prüfung keinen manipulierten erstmaligen Download als vertrauenswürdig beweisen.

## Bauen und Prüfen

Voraussetzungen sind .NET SDK 10.0.401 oder ein kompatibles Patch-Update, Visual Studio 2022 C++ Build Tools mit Windows SDK und CMake, das lokale CUDA Toolkit 13.2 sowie Git. NuGet-Abhängigkeiten sind festgelegt und besitzen Lockdateien. Die native Laufzeit verwendet llama.cpp-Commit `8e1642198dcd4e408f8776222d6ae31b74d01187`. Die erstmalige Quellbeschaffung benötigt Internet; Modelle und private Schlüssel bleiben außerhalb des Repositorys.

```powershell
dotnet run --project Windows/Tests/PassphraseMemorizer.Tests.csproj -c Release
./Windows/Signing/test-signing.ps1
./Scripts/build-windows-runtime.ps1
./Scripts/package-windows.ps1 -SkipNativeBuild
./Scripts/test-windows-app.ps1 -Bundle "<vollständiger Pfad zum signierten Kandidaten>" -Generate
./Scripts/install-windows.ps1 -Bundle "<vollständiger Pfad zum signierten Kandidaten>"
```

Die Schlüssel werden einmalig bereitgestellt; vorhandene Schlüssel werden nie überschrieben. Der Paketierungsschritt benötigt die bereits bereitgestellten Schlüssel auf `A:`. CI verwendet ausschließlich flüchtige Testschlüssel und öffentliche Wortfolgen, keine Produktionsschlüssel oder Modellgewichte. Der [GPU-Audit](WINDOWS_GPU_SECURITY_AUDIT.md) dokumentiert die aktuelle Ausführung; der [ursprüngliche Windows-Audit](WINDOWS_SECURITY_AUDIT.md) bleibt als Nachweis der früheren CPU-Ausgabe erhalten.

Die vollständige Löschung aller CLR-/WPF-/llama.cpp-/Register-/Auslagerungs-/Treiber-/Bildschirmkopien lässt sich nicht garantieren. Eigene veränderbare Puffer werden überschrieben. Das Schließen wartet auf das Ende des KI-Prozesses; erzwungenes Beenden kann reguläre Bereinigung verhindern. Windows-Isolation, Signaturen und Prüfungen sind keine externe Zertifizierung.
