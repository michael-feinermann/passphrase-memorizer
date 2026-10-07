# Passphrase Memorizer

[English](README.md) | Deutsch

Passphrase Memorizer ist eine eigenständige macOS-App. Sie erstellt aus einer vorhandenen BIP39- oder EFF-Wortfolge einen Reim, eine Ballade, ein Gedicht, eine Kurzgeschichte oder einen Rap. Gemma 4 E4B oder ein kompatibles lokales GGUF-Modell läuft dabei in einem eigenen isolierten nativen Prozess mit Metal-GPU-Beschleunigung und CPU-Ausweichpfad. Die App ist vom Password Generator und anderen KI-Anwendungen unabhängig. Die [Modellanleitung](docs/LOCAL_AI.de.md) erklärt den Ausweichpfad beim Start und die Grenzen der Beschleunigung.

Oberfläche und Ausgabe starten auf Englisch. Deutsch ist ebenfalls verfügbar. Nur die zuletzt gewählte Sprache und die Fenstergröße werden als Einstellungen gespeichert. Modellgewichte wählst du lokal aus. Sie sind weder in Git noch im App-Archiv enthalten.

Die App-Version steht in der Oberfläche hinter dem Namen. Die erforderlichen Wörter erscheinen in eckigen Klammern, blau und fett. Eine eingeblendete Merkhilfe bleibt bis zu zehn Minuten sichtbar. Bei Deaktivierung wird der Text weiterhin sofort verdeckt.

## Download und Installation

Der macOS-Release unterstützt Apple Silicon (arm64) und macOS ab Version 14. Lade das App-ZIP aus den [GitHub-Releases](https://github.com/michael-feinermann/passphrase-memorizer/releases/latest), entpacke es und verschiebe `Passphrase Memorizer.app` nach `/Applications`. Das lokale Modell wird separat installiert. Die [Modellanleitung](docs/LOCAL_AI.de.md) erklärt die Einrichtung.

Das passende Icon liegt in [Assets/AppIcon-1024.png](Assets/AppIcon-1024.png). Die Gestaltungsbeschreibung steht in [ICON_PROMPT.md](Assets/ICON_PROMPT.md).

Der Release enthält SHA-256-, SHA3-512- und Skein-1024-1024-Prüfsummen, abgetrennte Signaturen mit RSA-4096-PSS/SHA-512 und ML-DSA-87 sowie einen separaten lokalen Hybrid-Signer. App-ZIP, Integritätsmanifest, Bundle-Inventar und Signer-ZIP müssen jeweils beide Signaturen bestehen. Die [Anleitung zur Hybridprüfung und Signierung](docs/HYBRID_SIGNING.de.md) erklärt die erforderliche Vertrauensprüfung der öffentlichen Schlüssel. Das unveränderliche [Integritätsmanifest für v1.0.0](Signing/Releases/v1.0.0/Passphrase-Memorizer-1.0.0.integrity.txt) und die abgetrennten Signaturen sind auch im Repository enthalten. Der Signer ist von der KI-Ausführung getrennt und erhält keine Passphrasen.

## Verwendung

1. Besorge ein vertrauenswürdiges lokales GGUF-Modell, bevor du ein Geheimnis eingibst. Die [Anleitung für Gemma 4 E4B und andere Modelle](docs/LOCAL_AI.de.md) beschreibt die Einrichtung.
2. Öffne Passphrase Memorizer, wähle das lokale Modell und trage deine vorhandenen Wörter ein.
3. Wähle BIP39- oder EFF-Wortliste, Sprache und literarische Form. Erzeuge anschließend die Merkhilfe.
4. Zeige sie nur bei geschütztem Bildschirm an. Leere die Sitzung, wenn du fertig bist.

Die App nimmt 1 bis 128 Wörter aus der gewählten englischen Wortliste an. Sie prüft die Zugehörigkeit zur Wortliste sowie die markierte Wortfolge in der Ausgabe, einschließlich Wiederholungen. BIP39-Wallet-Prüfsummen werden nicht geprüft. Standardkonforme BIP39-Mnemonics bestehen aus 12, 15, 18, 21 oder 24 Wörtern und haben die vorgeschriebene Prüfsumme. Andere angenommene Längen sind allgemeine Wortfolgen. Siehe die [BIP39-Spezifikation](https://github.com/bitcoin/bips/blob/master/bip-0039.mediawiki).

Maßgeblich bleibt die ursprüngliche Wortfolge. Die Merkhilfe erhöht deren Entropie nicht und ersetzt kein sicheres Backup. Allein die Zugehörigkeit zur EFF-Wortliste belegt keine zufällige Auswahl. Siehe die [EFF-Methode für Passphrasen](https://www.eff.org/dice).

## Datenschutz und Sicherheit

Jede Generierung startet einen neuen nativen Prozess. Bevor er die Wörter annimmt, aktiviert er eine macOS-Seatbelt-Sandbox und prüft die Sperre von IPv4-/IPv6-Datenverkehr, Dateischreiben und fremden Dateilesezugriffen. Der Prozess enthält keinen HTTP-Server, Modelldownload, Browser oder Werkzeuge. Die Oberfläche kommuniziert über begrenzte Pipes, ohne Netzwerk-Endpunkt. App und KI-Prozess benötigen Hardened Runtime. Die produktive Generierung verlangt außerdem die vorgesehenen Signaturen.

Chatverlauf, Prompt-Cache, Geschichte, Telemetrie, Modellpfad und Modell-Lesezeichen werden nicht absichtlich gespeichert. Leeren, Schließen und reguläres Beenden stoppen die Generierung und überschreiben kontrollierte geheime Puffer. Bei Deaktivierung wird die Anzeige verdeckt. Die Merkhilfe wird zusätzlich nach etwa 10 Minuten wieder verborgen. Kopieren und Export der Geschichte werden nicht angeboten.

Eine vollständige unwiderrufliche Löschung sämtlicher Kopien in Swift, AppKit, KI-Laufzeit, GPU oder Treiber, Bildschirm, Auslagerungsdateien oder Betriebssystem lässt sich nicht garantieren. Erzwungenes Beenden kann die Bereinigung verhindern. Die eigene Sandbox verwendet eine veraltete API und muss auf dem jeweiligen macOS geprüft werden. Die Oberfläche selbst läuft ohne App Sandbox, damit der neue KI-Prozess seine strengere eigene Sandbox aktivieren kann. Der [interne Sicherheits-Audit](docs/SECURITY_AUDIT.md) beschreibt Belege und verbleibende Grenzen. Er ist keine externe Zertifizierung.

## Selbst bauen

Voraussetzungen: macOS ab Version 14, eine vollständige Xcode-Installation mit Swift 6, macOS SDK ab Version 26 und dessen Metal-Compiler/Toolchain, CMake und Python 3 für die Release-Prüfung. Installiere Entwicklungsabhängigkeiten und lade Modelle vor der Arbeit mit vertraulichen Wörtern. Die Beschaffung ist ein eigener Online-Einrichtungsschritt. Die Generierung benötigt kein Internet.

```sh
./Scripts/build-local-ai.sh --fetch
swift test
./Scripts/package-app.sh --dev
```

Die nativen Transport-Integrationstests benötigen `RUNTIME_TEST_SIGN_IDENTITY` mit der SHA-1-Kennung eines verfügbaren Developer-ID-Zertifikats des Teams `2T6K9PGS55`. Sie signieren öffentliche Testprogramme und prüfen den tatsächlich gestarteten Prozess vor jedem Prompt-Byte. Ohne diese ausdrücklich gesetzte Testkennung werden die signierten Integrationsfälle übersprungen. Die Ablehnung unsignierter Prozesse und die übrigen Tests laufen weiterhin. Nur die Tests lesen diese Einstellung. Sie kann keine produktive Signaturprüfung umgehen.

Der erste Befehl lädt ausdrücklich die festgelegte llama.cpp-Version. Spätere Builds verwenden `./Scripts/build-local-ai.sh` ohne Download. Das Paketierungsskript baut Swift im Release-Modus mit Warnungen als Fehlern, baut den nativen Prozess, entfernt externe Build-Pfade, kopiert Wortlisten und Lizenzhinweise und signiert zuerst den KI-Prozess und dann die App. Die Ergebnisse sind:

- `build/Passphrase Memorizer.app`
- `build/Passphrase-Memorizer-1.0.0.zip`
- `build/Passphrase-Memorizer-1.0.0.zip.sha256`

Ein Entwicklungsbuild wird standardmäßig ausdrücklich ad hoc signiert. Die nativen Sandbox-Prüfungen lassen sich damit ausführen. Die produktive Generierung in der Oberfläche lehnt diese Signatur absichtlich ab. Für einen Produktionsbuild ist das konfigurierte Developer-ID-Team `2T6K9PGS55` erforderlich. Ein Fork mit anderem Team muss beide Signaturrichtlinien bewusst prüfen und anpassen.

## Release signieren und prüfen

Verwende ein Developer-ID-Application-Zertifikat und ein vorhandenes Notarisierungsprofil im Schlüsselbund. Passwörter, private Schlüssel, Signierungsexporte und Modellgewichte gehören nicht ins Repository.

```sh
SIGN_IDENTITY='Developer ID Application: Dein Name (2T6K9PGS55)' \
NOTARY_PROFILE='Dein-Schluesselbund-Profil' ./Scripts/package-app.sh
./Scripts/verify-release.sh
```

Apples Werkzeuge verwenden die Zugangsdaten direkt aus dem Schlüsselbundprofil. Ohne `NOTARY_PROFILE` entsteht ein signierter Kandidat, der vor der Veröffentlichung noch notarisiert werden muss. Die Prüfung verlangt das vorgesehene Team und die Signierungskennungen, Hardened Runtime ohne gelockerte Entitlements, gültige verschachtelte Signaturen, ausschließlich Systemabhängigkeiten, unveränderte Wortlisten, Lizenzhinweise, keine Modellgewichte, erfolgreiche Sandbox-Tests des enthaltenen KI-Prozesses, ein gültiges angeheftetes Notarisierungsticket und die Annahme durch Gatekeeper. Auch das Archiv und die daraus entpackte App werden geprüft.

Nur für ein ausdrücklich als Entwicklungspaket bezeichnetes Artefakt:

```sh
ALLOW_UNNOTARIZED_DEVELOPMENT=1 ./Scripts/verify-release.sh
```

Diese Ausnahme überspringt Notarisierung und Gatekeeper und erlaubt zusammenpassende Ad-hoc-Signaturen. Sie macht das Artefakt weder zu einem geprüften öffentlichen Release noch schaltet sie die produktive Generierung frei.

Der Anwendungscode steht unter der [MIT-Lizenz](LICENSE). Wortlisten und native Abhängigkeiten haben eigene [Lizenzhinweise](THIRD_PARTY_NOTICES.md). Für lokal installierte Modellgewichte gelten deren separate Lizenzen.
