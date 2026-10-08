# Release-Prüfsummen und hybride Signaturen

[English](HYBRID_SIGNING.md) | Deutsch

Passphrase Memorizer ergänzt Apples Developer-ID-Signatur, Notarisierung und Gatekeeper-Prüfung um getrennte RSA-4096-PSS/SHA-512- und ML-DSA-87-Signaturen. Beide Verfahren müssen erfolgreich geprüft werden. Der eigenständige Hybrid Signer ist ein Release-Werkzeug. Er hat keine Anbindung an die Merkhilfe-App, das Modell, den KI-Prozess oder deine Passphrase.

Das [öffentliche Release v1.0.0](https://github.com/michael-feinermann/passphrase-memorizer/releases/tag/v1.0.0) enthält alle 28 Assets einschließlich des notarisierten Signers und zwölf hybrider Signaturdateien. Jedes Asset wurde ohne Anmeldung heruntergeladen, mit dem geprüften Original verglichen und nach der Installation erneut geprüft. Der [interne Audit](SECURITY_AUDIT.md) dokumentiert Quellcode-, Signatur-, Manipulations-, Notarisierungs- und Downloadnachweise getrennt. Der Signing-Quellcode trägt den Tag [v1.0.0-hybrid.1](https://github.com/michael-feinermann/passphrase-memorizer/tree/v1.0.0-hybrid.1). Der ursprüngliche App-Tag und das App-ZIP bleiben unverändert.

Der aktuelle App-Release ist 1.0.1, Build 2. Der unveränderte notarisierte Signer bleibt 1.0.0, Build 1. Die aktuellen Prüfskripte verwenden diese Versionen unabhängig als Voreinstellung. Mit `--app-version 1.0.0 --app-build 1` prüfst du den älteren Release 1.0.0. Die historischen Nachweise oben gelten weiterhin nur für 1.0.0.

## Erforderliche Release-Dateien

Die vollständige Prüfung verlangt diese vier Ziele. Fehlt der Signer, wird die Prüfung abgelehnt.

| Ziel | Abgesicherter Inhalt |
| --- | --- |
| `Passphrase-Memorizer-1.0.1.zip` | Vollständiges Archiv der endgültigen notarisierten App |
| `Passphrase-Memorizer-1.0.1.integrity.txt` | Produkt und Version sowie alle drei Prüfsummen von App- und Signer-Archiv |
| `Passphrase-Memorizer-1.0.1.bundle-inventory.json` | Jede App-Datei, Länge, Berechtigung und jedes Verzeichnis |
| `Passphrase-Memorizer-HybridSigner-1.0.0.zip` | Eigenständige Signer.app, Laufzeit, Referenzbibliothek, Lizenzen und kanonisches `signer-inventory.json` |

Zu jedem Ziel gehören die Dateien `.khsig`, `.sha3`, `.sha3.khsig`, `.skein` und `.skein.khsig`. Damit sind zwölf hybride Signaturdateien verpflichtend. Jede enthält beide Signaturen. Für die beiden ZIPs gibt es außerdem herkömmliche `.sha256`-Dateien. SHA-256, SHA3-512 und Skein-1024-1024 sind öffentliche Integritätsprüfsummen. Eine Prüfsumme allein belegt keinen Herausgeber. Das signierte Manifest und Archiv binden die erwarteten Werte; der Verifier berechnet alle drei erneut.

Das Signer-Inventar liegt innerhalb des signierten ZIPs neben dem versiegelten `.app`-Bundle. Die getrennten Signaturen liegen neben dem ZIP. Dadurch verändern sie das signierte Archiv nicht und müssen sich nicht selbst enthalten.

`Passphrase-Memorizer-1.0.1.hybrid-signatures.zip` bündelt die Signaturdateien, öffentlichen Vertrauensdaten, Prüfskripte und C-Skein-Referenzen. Es besitzt eine eigene SHA-256-Datei. Es ist kein fünftes Ziel der hybriden Signierung. Seine Prüfsumme authentifiziert weder den enthaltenen Verifier noch die Schlüssel. Stelle das unten beschriebene unabhängige Vertrauen her, bevor du heruntergeladenen Prüfcode ausführst.

## Öffentliche Schlüssel zuerst prüfen

Der öffentliche [Signing-Ordner](../Signing/) enthält `trust.json`, `rsa4096-spki.der`, `rsa4096-certificate.cer`, `mldsa87-public.bin` und `PassphraseMemorizerPolicy.props`. Die App verwendet neue unabhängige RSA- und ML-DSA-Schlüssel. Der Signer enthält weder einen voreingestellten öffentlichen Schlüssel noch Ersatz-Pins aus Keep Vault oder Password Generator. `sign` und `verify` verlangen eine ausdrücklich angegebene Richtlinie mit allen drei Fingerabdrücken jedes öffentlichen Schlüssels.

Beschaffe oder bestätige `trust.json` und die sechs öffentlichen Fingerabdrücke über einen unabhängig vertrauenswürdigen Kanal, bevor du ein Release akzeptierst. Ein Archiv, ausgetauschte Schlüssel und passende Pins aus demselben kompromittierten Konto können die Identität des Herausgebers nicht belegen. Das selbst ausgestellte Zertifikat wird aufgrund seines genau festgelegten öffentlichen Schlüssels akzeptiert. Es bildet keine Vertrauenskette zu einer Zertifizierungsstelle. Diese Pins sind von Apples Developer-ID-Identität getrennt.

Die unabhängige Python-Prüfung benötigt Python 3, OpenSSL ab Version 3.5 und den vertrauenswürdigen C-Skein-Referenzwrapper. Der Wrapper kompiliert die mitgelieferte Referenz mit dem C-Compiler aus Xcode. Prüfe die Herkunft des Verifiers und seiner Abhängigkeiten vor der Ausführung. Er prüft das Signer-ZIP, ohne den Signer zu entpacken oder auszuführen.

Lege sämtliche Release-Assets in einem lokalen Ordner ab. Führe aus einer unabhängig vertrauenswürdigen Kopie dieses Repositories aus:

```sh
python3 -I Scripts/verify-hybrid-signatures.py \
  --release-dir '/path/to/downloaded-release' \
  --trust '/path/to/trusted/Signing/trust.json' \
  --checksum-tool '/path/to/trusted/repository/Scripts/skein-reference-checksum.sh' \
  --openssl '/path/to/trusted/openssl' \
  --app '/Applications/Passphrase Memorizer.app'
```

`--app` vergleicht zusätzlich die installierte App mit dem signierten Inventar. Lass diese Option weg, um nur das heruntergeladene Release zu prüfen. Eine vollständige erfolgreiche Prüfung bestätigt zwölf hybride Signaturdateien und prüft beide Verfahren, öffentliche Fingerabdrücke, Zertifikatsrichtlinie, alle Prüfsummen, Produkt und Version, sichere ZIP-Inhalte und vollständige Inventare. Apples Signatur-, Notarisierungs- und Gatekeeper-Prüfungen bleiben zusätzlich erforderlich. Der [Release-Verifier](../Scripts/verify-release.sh) führt sie aus.

In der geprüften Installation liegen öffentliche Assets und Werkzeuge unter `/Applications/Passphrase Memorizer 1.0.1.signatures`. Der eigenständige Signer liegt unter `/Applications/Passphrase Memorizer Hybrid Signer.app`, die Merkhilfe-App weiterhin unter `/Applications/Passphrase Memorizer.app`. Die öffentlichen Prüfwerkzeuge benötigen weder das private Schlüsselvolume noch das KI-Modell.

## Prüfsummen berechnen oder den Signer verwenden

Entpacke nach unabhängiger Prüfung des Archivs das vollständige Signer.app mit einem macOS-Archivwerkzeug, das Apples Bundle-Metadaten erhält. Halte das gesamte `.app` zusammen. Der Produktionswrapper lehnt einzelne Executables, beschädigte Ressourcenversiegelungen, falsche Teams oder Versionen, zusätzliche Entitlements und externe Referenzbibliotheken ab.

Beispiel für eine öffentliche Datei, ausgeführt im Repository:

```sh
memorizer_signer_dir='/path/to/Passphrase Memorizer Hybrid Signer.app/Contents/MacOS'
Scripts/run-hybrid-signer.sh --signer-dir "$memorizer_signer_dir" \
  hash --target '/path/to/public-file'
```

`hash` gibt SHA-256, SHA3-512 und Skein-1024-1024 als JSON aus und greift auf keinen privaten Schlüssel zu. Dieselben Prüfsummen werden beim Signieren ausgegeben. Eine SHA-256-Datei neben einem ZIP kannst du außerdem im zugehörigen Ordner mit `shasum -a 256 -c FILE.sha256` prüfen. Das prüft die Integrität und ersetzt die vollständige Signaturprüfung nicht.

Eine öffentliche Signer-Prüfung einer gewöhnlichen Datei mit ihren benachbarten Signaturdateien erfolgt so:

```sh
Scripts/run-hybrid-signer.sh --signer-dir "$memorizer_signer_dir" \
  verify --target '/path/to/public-file' \
  --mldsa-public-key '/path/to/trusted/Signing/mldsa87-public.bin' \
  --policy '/path/to/trusted/Signing/PassphraseMemorizerPolicy.props'
```

Dieser Befehl prüft die Datei und ihre SHA3-/Skein-Signaturen. Die Python-Release-Prüfung verlangt zusätzlich alle vier Release-Ziele und die vollständigen App-/Signer-Inventare.

## Geschützte lokale Release-Schlüssel

Der angeforderte Schlüsselcontainer liegt unter `/Volumes/NO NAME/Passphrase Memorizer Keys/ReleaseKeys.sparsebundle`. Das äußere Volume ist ein VeraCrypt-gestütztes FAT-Volume ohne durchgesetzte Eigentümerrechte. FAT-Berechtigungen allein liefern nicht die erforderlichen macOS-Prüfungen privater Dateien. Ein eingebundenes, unverschlüsseltes APFS-Abbild mit 128 MB stellt durchgesetzte Eigentümerrechte bereit. Es fügt keine weitere Verschlüsselungsschicht hinzu.

Das Release-Schlüsselverzeichnis im Abbild heißt `/Volumes/Passphrase Memorizer Release Keys/MemorizerRelease-v1`. Es gehört dem Release-Nutzer und hat Modus 0700. Private Dateien haben Modus 0600, genau einen Hardlink und keine erweiterten ACLs. Die an offene Dateideskriptoren gebundene Implementierung lehnt symbolische Links, ausgetauschte Pfade oder Objekte, deaktivierte Eigentümerrechte und bestehende Schlüsselsets ab. Private Inhalte bleiben außerhalb von Git und GitHub.

RSA liegt in einer verschlüsselten PKCS#12-Datei. Deren Passwort und der private ML-DSA-Schlüssel verwenden getrennte AES-256-GCM-Hüllen und unabhängige RSA-/ML-DSA-Wrapping-Schlüssel. Die Wrapping-Schlüssel befinden sich auf demselben geschützten Volume. Die Vertraulichkeit gespeicherter Schlüssel hängt deshalb vom äußeren VeraCrypt-Schutz ab. Passwörter und private Schlüssel werden weder als Argumente, Umgebungswerte oder Protokollausgaben noch als Release-Assets übergeben.

Die explizite Operation `release-keygen` benötigt ein neues leeres Verzeichnis mit Modus 0700 außerhalb eines Repositories. Sie überschreibt kein bestehendes Schlüsselset. Private Produktionsoperationen müssen `Scripts/run-hybrid-signer.sh` verwenden. Der Wrapper prüft vor jedem Schlüsselzugriff das gesamte Apple-versiegelte Signer.app und akzeptiert nur dessen eigenen genauen Pfad `Contents/MacOS/libmldsa87_ref.dylib`. Er verwendet eine bereinigte Umgebung, deaktiviert Diagnose und Core-Dumps und stellt ein leeres privates TMPDIR bereit. Anschließend werden temporäre macOS-PFX-Keychain-Objekte und die Keychain-Liste des Nutzers geprüft. Unerwartete Reste bleiben zur Prüfung erhalten und führen zum Fehlschlag.

## Release bauen und veröffentlichen

`Scripts/build-hybrid-signer.sh` beschafft das offizielle Microsoft-SDK 10.0.400, prüft dessen festgelegte SHA-512-Prüfsumme und Microsoft Developer ID und verwendet ein neues privates SDK-/Build-Verzeichnis. NuGet verwendet ausschließlich die konfigurierte Quelle und festgelegte Paketdaten. Der Signer enthält die .NET-10.0.11-Laufzeit. Für die Ausführung des veröffentlichten Werkzeugs sind weder .NET-SDK noch Restore erforderlich. Build und Abhängigkeitsbeschaffung sind Einrichtungsschritte außerhalb der lokalen KI-Ausführung.

```sh
Scripts/build-hybrid-signer.sh --output '/absolute/path/to/new-signer-output'
```

Das Release-Layout enthält den verwalteten Code und die vollständige CLR-Laufzeit im nativen Apphost und benötigt keine Selbstentpackung zur Laufzeit. Die eigene native ML-DSA-Referenz bleibt eine separate signierte Mach-O-Bibliothek. Readme und Lizenzen gehören nach Resources. Dadurch hängt die Paketprüfung nicht von generischen Signaturen verwalteter DLLs in erweiterten Dateiattributen eines Codeverzeichnisses ab. Nur die eigenständige Signer.app erhält `com.apple.security.cs.allow-jit`. Die Merkhilfe-App und ihr KI-Prozess behalten ihre bestehenden strengeren Rechte.

Paketiere einen neuen Kandidaten mit der ausdrücklich angegebenen SHA-1-Kennung des Developer-ID-Zertifikats. Ersetze die Beispielpfade und den Zertifikatsplatzhalter:

```sh
python3 -I Scripts/package-hybrid-signer.py \
  --publish-dir '/absolute/path/to/new-signer-output' \
  --output-app '/absolute/path/to/new-staging/Passphrase Memorizer Hybrid Signer.app' \
  --identity 'YOUR_40_HEX_CERTIFICATE_SHA1' \
  --archive '/absolute/path/to/new-archive/Passphrase-Memorizer-HybridSigner-1.0.0.xcarchive'
```

Die ausgegebene App und das optionale `.xcarchive` dürfen noch nicht existieren. Der Schritt erzeugt einen signierten Kandidaten und belegt keine Notarisierung. Prüfe nach Apples endgültigem notarisierten Export und dem Anheften des Tickets das tatsächlich exportierte Bundle mit der Standard-Veröffentlichungsrichtlinie:

```sh
python3 -I Scripts/verify-hybrid-signer-app.py \
  --app '/absolute/path/to/final-export/Passphrase Memorizer Hybrid Signer.app'
```

`--signed-candidate` überspringt ausdrücklich nur die Notarisierungsprüfungen und belegt kein endgültiges Release. Die Standardprüfung kontrolliert die vollständige Versiegelung, genau zwei Code-Dateien, arm64, Team und Identität, Hardened Runtime, minimale Entitlements, ausschließlich Systemabhängigkeiten, fehlende Laufzeitsuchpfade, Ticket und Gatekeeper.

Lege das unveränderte endgültige App-ZIP und optional dessen vorhandene SHA-256-Datei in einen neuen Release-Ordner. Erzeuge das öffentliche Signer-Archiv, Inventare, Prüfsummen und Integritätsmanifest mit:

```sh
python3 -I Scripts/create-hybrid-release.py \
  --release-dir '/absolute/path/to/new-release-directory' \
  --app '/absolute/path/to/final-export/Passphrase Memorizer.app' \
  --signer-app '/absolute/path/to/final-export/Passphrase Memorizer Hybrid Signer.app'
```

Dieses Skript liest keinen privaten Schlüssel. Es verlangt ein neues Integritätsmanifest und entweder ein neues Signer-ZIP oder `--reuse-signer-zip /absoluter/pfad/zum/unveränderten-signer.zip`. Die Wiederverwendung prüft das vollständige signierte Inventar gegen die angegebene notarisierte Signer.app und behält die Archivbytes bei. Das Skript prüft den notarisierten Signer vor und nach dem ZIP-Rundtrip und vergleicht dessen drei Prüfsummen mit Python-SHA-256/SHA3 und der unabhängigen C-Skein-Referenz. Die zwölf hybriden Signaturdateien erzeugt es noch nicht.

Der Herausgeber signiert zunächst beide endgültigen App-Bundles mit Developer ID, notarisiert sie und heftet Apples Ticket an. Erst nach dem endgültigen Export entstehen kanonische Inventare außerhalb der versiegelten Bundles und die ZIPs. Das Signer-ZIP enthält sein eigenes Inventar. Das Integritätsmanifest erhält die endgültigen App- und Signer-Prüfsummen. Anschließend werden die vier unveränderten Ziele mit der ausdrücklich angegebenen Produktrichtlinie signiert, ohne danach ein Bundle neu zu bauen oder zu signieren. Die unabhängige Prüfung und die Negativtests mit ausschließlich öffentlichen Daten müssen bestehen. Abschließend werden die ohne Anmeldung von GitHub heruntergeladenen Assets geprüft, einschließlich der Apple-Versiegelung des entpackten Signer-Bundles.

Zum Signieren gewöhnlicher Dateien verwendest du den Wrapper-Befehl `sign`, jeweils `--target FILE` und Pfade für `--pfx`, `--pfx-password-encrypted`, `--pfx-wrapping-key-file`, `--mldsa-private-key-encrypted`, `--mldsa-wrapping-key-file`, `--mldsa-public-key`, `--reference-library`, `--policy` und `--launcher-pins`. Die letzte Option schreibt eine externe öffentliche Pin-Datei. Geheime Dateiinhalte werden ausschließlich im Signer geladen. Die Zielbytes bleiben unverändert; getrennte Signaturdateien entstehen neben ihnen.

## Beibehaltenes Keep-Vault-Format

Die Herkunft ist in [SOURCE_PROVENANCE.json](../Signing/HybridSigner/SOURCE_PROVENANCE.json) dokumentiert. Der angepasste Signer behält die `KZVHSIG1`-Hülle, Version 1, deren Little-Endian-Längen-/SHA-512-Bindung, die Payload-Domäne `KalynaZpaqVault/HybridArtifactSignature/SHA-512/v1\0` und den reinen ML-DSA-Kontext `KalynaZpaqVault/HybridArtifactSignature/v1`. Der historische Namensraum ist eine bewusste Formatentscheidung. Er bedeutet keine Übernahme fremder Produktschlüssel. Produktidentität und vollständiger Release-Umfang werden zusätzlich über signierte Manifeste und Inventare sowie die ausdrücklich angegebene neue Vertrauensrichtlinie geprüft.

RSA-PSS verwendet SHA-512, MGF1/SHA-512 und 64 Byte Salz. Die zweite Signatur ist reines ML-DSA-87 mit festgelegtem Kontext und Message-Encoding, kein HashML-DSA. Signieren und Schlüsselgenerierung prüfen Bouncy Castle und die auf `pq-crystals/dilithium@d35ba3fe5449bee3e6d43e1f296c3ca818bd36be` festgelegte native Referenz in beide Richtungen gegeneinander. Die öffentliche Prüfung verwendet OpenSSL unabhängig davon. Quellen für Verfahren und Optionen: [NIST FIPS 204](https://csrc.nist.gov/pubs/fips/204/final) und [OpenSSL pkeyutl](https://docs.openssl.org/3.5/man1/openssl-pkeyutl/).
