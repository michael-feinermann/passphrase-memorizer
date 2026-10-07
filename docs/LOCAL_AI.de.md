# Lokale Modelle und Offline-Ausführung

[English](LOCAL_AI.md) | Deutsch

Passphrase Memorizer ist eine eigenständige macOS-App. Trage deine vorhandene BIP39- oder EFF-Wortfolge ein und wähle Reim, Ballade, Gedicht, Kurzgeschichte oder Rap. Die App generiert keine Passwörter und verändert den Password Generator nicht. Beim ersten Start ist Englisch voreingestellt. Die zuletzt gewählte Sprache und die Fenstergröße werden lokal gespeichert. Die Oberfläche zeigt die App-Version hinter dem Namen. Eine eingeblendete Merkhilfe bleibt zehn Minuten sichtbar. Die genauen Passphrase-Wörter erscheinen blau und fett als `[Wort]`. Beim Deaktivieren der App wird der Text früher verdeckt. Blende ihn nur erneut ein, wenn deine Umgebung geschützt ist.

Die App verwendet ihren eigenen nativen `LocalMnemonicRunner` mit einer festgelegten llama.cpp-Version. Jede Generierung startet einen neuen Prozess. Es gibt keinen HTTP-Dienst, keine lokale API, keine Anbindung anderer KI-Apps, keine Recherche, keinen Browser und keine Werkzeugausführung. Der KI-Prozess aktiviert seine macOS-Seatbelt-Beschränkungen vor der Annahme vertraulicher Eingaben. Netzwerkoperationen und Dateischreibzugriffe sind gesperrt. Beim Versuch einer Beschleunigung läuft vor dem Bereitschaftssignal eine begrenzte öffentliche Metal-Testberechnung; danach werden Netzwerk-, Schreib- und Lesesperren erneut geprüft. Die Oberfläche attestiert anschließend den tatsächlich gestarteten Prozess, bevor sie den Prompt übermittelt.

## Native Beschleunigung

Die Laufzeit wählt das native Metal-GPU-Backend, wenn seine öffentliche Startberechnung erfolgreich ist. Ist die geordnete Initialisierung nicht verfügbar oder schlägt sie kontrolliert fehl, wird die CPU gewählt. Ein fataler Apple-Treiberfehler, eine fehlgeschlagene Sicherheitsprüfung oder ein späterer Inferenzfehler beendet die Anfrage. Es gibt keine allgemeine Garantie für einen automatischen erneuten Versuch. CPU und Metal behalten dieselbe Netzwerk- und Dateischreibsperre. Die Wahl bleibt flüchtig und wird weder gespeichert noch für jede Anfrage durch CPU-/GPU-Zeitmessungen kalibriert.

Der signierte KI-Prozess enthält vorkompilierte Metal-Shaderbibliotheken und feste Tensor-Fähigkeitsproben. Eine geprüfte Build-Anpassung entfernt die Shader-Quelltextkompilierung und den dateibasierten Shader-Ausweichpfad zur Laufzeit. Andere GPU-Backends und dynamisch geladene Plug-ins sind deaktiviert. Für die GPU werden nur `AGXDeviceUserClient`, Apples `com.apple.MTLCompilerService` zur Pipeline-Spezialisierung und eng festgelegte Verzeichnismetadaten freigegeben. Das umfasst den tatsächlichen direkten Elternordner unter seinem kanonischen Pfad und der geprüften dyld-Schreibweise. Bei einem tatsächlichen Start unter `/var/` oder `/tmp/` und dem passenden geprüften kanonischen `/private/`-Pfad kommen ausschließlich die Metadaten dieses öffentlichen Systemalias selbst hinzu. Verzeichnisauflistung, weitere Dateiinhalte und Dateischreibzugriffe werden dadurch nicht erlaubt. Der Compiler erhält festen Shadercode und Spezialisierungskonstanten statt Wortfolge oder Tensorinhalten. Diese Konstanten können Tensorformen und Arbeitsgrößen beschreiben. Der Apple-Dienst kann solche Metadaten in eigenen Shader-Caches und Protokollen außerhalb der KI-Prozess-Sandbox behalten. Der GPU-Modus vertraut deshalb zusätzlich Apples Compiler und GPU-Treiber.

Metal berechnet auf der GPU. Dieser Build enthält kein separates Core-ML-/Apple-Neural-Engine-Backend. Auf kompatiblen M5-Systemen können GPU-Tensoroperationen die Neural Accelerators der GPU nutzen. Sie sind von der separaten Neural Engine zu unterscheiden. Die Verfügbarkeit hängt von Hardware, Betriebssystem und Modell ab. [Apples M5-Architektur](https://www.apple.com/newsroom/2025/10/apple-unleashes-m5-the-next-big-leap-in-ai-performance-for-apple-silicon/).

Bei drei frischen CPU-/Metal-Messpaaren auf Apple M5 mit dem endgültigen Text-Prompt und der Grammatik dauerte der gesamte KI-Prozess median 12,665 s auf der CPU und 11,762 s mit Metal, einschließlich Start und Bereinigung. Das sind für diese öffentliche zwölf Wörter lange Fixture 7,1 % weniger Zeit. Die Ausgabelängen unterschieden sich aufgrund numerischer Backend-Unterschiede. Die Messung belegt keine allgemeine Beschleunigung und keinen geringeren Energieverbrauch. Methode und aktuellen Prüfstand findest du im [Audit](SECURITY_AUDIT.md).

## Gemma 4 E4B verwenden

Beschaffe das Modell, bevor du ein Geheimnis eingibst. Die App lädt keine Modelle herunter. Die Beschaffung und das Installieren von Entwicklungsabhängigkeiten sind getrennte Einrichtungsschritte mit Internetzugriff. Die Generierung erfolgt offline.

1. Öffne [Googles offizielles Repository für Gemma 4 E4B QAT GGUF](https://huggingface.co/google/gemma-4-E4B-it-qat-q4_0-gguf/tree/main).
2. Lade `gemma-4-E4B_q4_0-it.gguf` in einen vollständig lokalen Ordner außerhalb dieses Git-Checkouts und außerhalb synchronisierter Cloudordner. Die `mmproj`-Datei wird für multimodale Eingaben verwendet und ist hier nicht erforderlich. Eingebundene Netzlaufwerke, bekannte Cloudanbieter-Verzeichnisse und Platzhalter ohne lokale Dateidaten werden abgelehnt.
3. Prüfe Herausgeber, Lizenz und veröffentlichte Prüfsumme. Notiere Modellrevision und Prüfsumme, wenn du die Auswahl reproduzieren möchtest. Die Dateiendung `.gguf` belegt keine vertrauenswürdige Herkunft.
4. Starte die signierte App, wähle die lokale `.gguf`-Datei, trage die Wortfolge ein, wähle Wortliste und Textform und starte die Generierung.

Nach dem nächsten App-Start wählst du das Modell erneut aus. Die App speichert weder seinen Pfad noch ein Zugriffs-Lesezeichen. Die bewusst installierte Modelldatei bleibt auf deiner Festplatte; das Leeren einer Sitzung löscht keine Modellgewichte.

[Google](https://ai.google.dev/gemma/docs/core) und die [offizielle Modellkarte](https://huggingface.co/google/gemma-4-E4B-it) beschreiben die Modellfamilie und den E4B-Checkpoint. Die Fähigkeiten des Modells sind von den in dieser App erlaubten Aktionen zu unterscheiden: Generierter Text kann keine Webseiten öffnen und keine vorgeschlagenen Befehle ausführen.

## Native Laufzeit bauen

Verwendet wird der festgelegte llama.cpp-Commit [`8e1642198dcd4e408f8776222d6ae31b74d01187`](https://github.com/ggml-org/llama.cpp/tree/8e1642198dcd4e408f8776222d6ae31b74d01187). Du benötigst CMake, eine vollständige Xcode-Installation mit macOS-SDK ab Version 26 und den Metal-Compiler samt Toolchain. Installiere diese Werkzeuge vor dem Offline-Build. Das macOS-Deployment-Ziel bleibt Version 14; Tensorbibliotheken werden nur auf unterstützten Geräten und Systemen geladen. Führe im Repository aus:

```sh
./Scripts/build-local-ai.sh --fetch
```

Der ausdrückliche Einrichtungsschritt lädt den festgelegten Quellcode in das ignorierte Build-Verzeichnis und baut `.build/local-ai/LocalMnemonicRunner`. Anschließend ist ein erneuter Build ohne Netz möglich:

```sh
./Scripts/build-local-ai.sh
```

Das Paketierungsskript fügt die Laufzeit unter `Passphrase Memorizer.app/Contents/Helpers/LocalMnemonicRunner` ein. Der signierte KI-Prozess gehört zum Release. Die Modellgewichte bindest du lokal ein. Ein selbst gebauter Ad-hoc-KI-Prozess schaltet die Generierung in der produktiven Oberfläche nicht frei. Die [Build- und Signieranleitung](../README.de.md) beschreibt das erwartete Signierteam und die bewusst zu prüfenden Änderungen für einen separat signierten Fork. Ersetze den Prozess nicht durch `llama-server`, Ollama, LM Studio oder ein beliebiges Programm; Protokoll und Schutzprüfung sind für diesen Runner ausgelegt.

## Einen anderen Checkpoint konvertieren

Bevorzuge eine fertige GGUF-Datei eines vertrauenswürdigen Herausgebers. Für eine eigene Konvertierung benötigst du den vollständigen [offiziellen E4B-Instruction-Checkpoint](https://huggingface.co/google/gemma-4-E4B-it), einschließlich Tokenizer und Konfiguration. Installiere die Konvertierungsabhängigkeiten in einer getrennten Python-Umgebung und verwende den festgelegten llama.cpp-Checkout. Die Konvertierung verarbeitet Modelldateien. Verwende keine geheimen Wortfolgen in Befehlen oder Dateinamen.

Die folgenden Beispielpfade musst du durch deine lokalen Verzeichnisse ersetzen:

```sh
python3 -m venv /path/to/conversion-env
/path/to/conversion-env/bin/python -m pip install -r /path/to/llama.cpp/requirements/requirements-convert_hf_to_gguf.txt
HF_HUB_OFFLINE=1 TRANSFORMERS_OFFLINE=1 /path/to/conversion-env/bin/python /path/to/llama.cpp/convert_hf_to_gguf.py /path/to/gemma-4-E4B-it --outfile /path/to/models/gemma-4-E4B-it-f16.gguf --outtype f16
```

Installiere alle Abhängigkeiten und beschaffe sämtliche Checkpoint-Dateien vor dem Offline-Konvertierungsbefehl. Je nach Checkpoint werden erhebliche Mengen an freiem RAM und Speicherplatz benötigt. Beachte `--help` des festgelegten Konverters und die [llama.cpp-Dokumentation zu Modellen](https://github.com/ggml-org/llama.cpp/blob/8e1642198dcd4e408f8776222d6ae31b74d01187/docs/models.md). Eine optionale Quantisierung erfolgt getrennt und lokal mit `llama-quantize` aus derselben Revision.

## Andere Modelle anbinden

Wähle eine lokale, für Text geeignete Instruction-GGUF-Datei, deren Architektur von der festgelegten llama.cpp-Revision unterstützt wird. Die GGUF-Metadaten müssen Tokenizer und eine unterstützte Chatvorlage enthalten. Gemma 4 verwendet das ausdrücklich festgelegte Textformat des Runners. Bei anderen Modellen verarbeitet llama.cpp seine unterstützten einfachen Vorlagen; beliebiger Jinja-Code wird nicht ausgeführt. `.safetensors`-, ONNX-, MLX-, Adapter- und Projektor-Dateien ersetzen das Hauptmodell nicht. Neue Architekturen oder nicht unterstützte Vorlagen können eine geprüfte Aktualisierung der Laufzeit und neue Isolationstests erfordern.

Bei der Generierung werden keine Konten, API-Schlüssel, entfernten Modellkennungen, `-hf`-Downloadoptionen, Internet-Ausweichwege oder frei konfigurierbaren Endpunkte angenommen. Eine Ausgabegrammatik erzwingt alle `[Wort]`-Markierungen in der richtigen Reihenfolge einschließlich Wiederholungen und der eingegebenen Großschreibung. Die Oberfläche prüft die Folge erneut. Reine Markerlisten werden abgelehnt: Die Grammatik verlangt vor der ersten Markierung einen unmarkierten englischen oder deutschen Buchstaben, die Oberfläche prüft einen unmarkierten Unicode-Buchstaben. Diese Mindestprüfung belegt weder literarische Qualität noch sachliche Richtigkeit. Scheitern Beschränkungen, Grenzen oder Prüfung, wird keine unbeschränkte Ersatzausgabe verwendet. Teste neue Modelle zunächst mit öffentlichen Beispielwörtern. Eine Merkhilfe erhöht die Sicherheit des Passworts nicht und kann die Wortfolge ebenso deutlich verraten.

## Sitzung leeren und Grenzen

Leere die Sitzung, bevor du sie unbeaufsichtigt lässt. Ein Abbruch fordert zunächst die reguläre Bereinigung des KI-Prozesses an und erzwingt bei Bedarf nach zwei Sekunden sein Ende. Beim regulären Beenden werden neue Prozessstarts gesperrt. Die Oberfläche bleibt aktiv, bis laufende und bereits abbrechende KI-Prozesse beendet und abgeholt sind. Ein Abbruch löscht den kontrollierten Prompt auch dann, wenn die eingeplante Generierungsaufgabe noch nicht gestartet wurde. Bereits eingereichte GPU-Kommandos lassen sich nicht garantiert unterbrechen; erzwungenes Beenden kann die Bereinigung durch Destruktoren verhindern. Leeren, Schließen und reguläres Beenden brechen die Generierung ab, beenden den KI-Prozess und entfernen die flüchtigen Eingaben und Ausgaben der App. Es gibt keine Verlaufsdatei, keinen Prompt-Cache, keine Datenbank, keine Telemetrie, keinen automatischen Export und keine gespeicherte Modellauswahl. Die eigenen App-Einstellungen speichern bewusst nur Sprache und Fenstergröße. Eine Positivliste entfernt `NSNav`-/`NSOSP`-Lesezeichen und automatische Fensterrahmen aus dem Einstellungsbereich der App beim Start, nach dem Dateidialog und beim regulären Beenden. Globale Finder- und Betriebssystemmetadaten sind davon nicht erfasst.

Dies löscht die App-Sitzung und überschreibt kontrollierte Puffer ausdrücklich. Eine unwiderrufliche physische Löschung sämtlicher Swift-, AppKit-, llama.cpp-, GPU-/Treiber-, Register-, Auslagerungs-, Absturzbericht-, Bildschirmfoto- oder Betriebssystemkopien lässt sich nicht garantieren. Die reguläre Bereinigung synchronisiert GPU-Arbeit und leert den Modell-Speichercache vor dem Abbau des Kontextes. Nicht jede Metal-Allokation, jeder Treibercache oder jede Kopie eines Betriebssystemdienstes lässt sich dadurch zuverlässig überschreiben. Erzwungenes Beenden kann die Bereinigung verhindern. Die benutzte macOS-Sandbox-API ist veraltet; ihre Funktion muss auf zukünftigen Systemen erneut geprüft werden. Kann der KI-Prozess seine Beschränkungen nicht aktivieren, wird die Generierung gesperrt. Der [interne Sicherheits-Audit](SECURITY_AUDIT.md) nennt die belegten Schutzmaßnahmen und verbleibenden Grenzen.

Maßgeblich bleibt die ursprüngliche Wortfolge. Die App prüft bei 1 bis 128 Wörtern die Zugehörigkeit zur englischen Wortliste, keine Wallet-Gültigkeit oder BIP39-Prüfsumme. Die Großschreibung bleibt erhalten. Leerraum und EFF-Bindestriche trennen die Wörter und gehören nicht zur Merkhilfe; verwende bei der Anmeldung weiterhin die Trennzeichen deines ursprünglichen Passworts. Standardkonforme BIP39-Mnemonics haben 12, 15, 18, 21 oder 24 Wörter und eine gültige Prüfsumme. Andere Längen aus dieser Wortliste sind allgemeine Wortfolgen und keine gültigen BIP39-Wallet-Mnemonics. [BIP39-Spezifikation](https://github.com/bitcoin/bips/blob/master/bip-0039.mediawiki). Die Zugehörigkeit zur EFF-Wortliste beweist keine zufällige Auswahl. [EFF-Methode für Passphrasen](https://www.eff.org/dice).
