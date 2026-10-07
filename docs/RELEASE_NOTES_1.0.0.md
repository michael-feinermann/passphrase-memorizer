# Passphrase Memorizer 1.0.0

## English

The first standalone macOS release creates private memory aids for an existing sequence of 1 to 128 BIP39 or EFF words. Choose a rhyme, ballad, poem, short story or rap in English or German. English is the initial language; the last selected language and window size are retained.

The interface displays “Passphrase Memorizer 1.0.0”. Required words are shown literally in square brackets, highlighted blue and bold. Confirmed reveal lasts up to ten minutes; switching away still conceals the text immediately.

The app uses its own local native inference worker with Gemma 4 E4B or a compatible GGUF. Metal GPU acceleration, including supported M5 GPU tensor kernels, is selected after a public startup computation passes. A CPU path handles orderly unavailability; fatal driver or inference errors stop the request safely. This build does not run on the separate Apple Neural Engine. Each generation starts a fresh context. Kernel-enforced network and filesystem-write restrictions and the identity of the running worker are checked before it receives the phrase. Input and output have no saved history. Clearing, closing and orderly quitting invalidate pending results, terminate inference and wipe controlled secret buffers. A new book-and-lock icon accompanies the app.

Three CPU/Metal test pairs on the tested M5 gave median complete worker-process durations of 12.665 and 11.762 seconds with a public twelve-word fixture. Metal took 7.1% less time in this test; output lengths differed, so this is not a general performance or energy claim. The audit records the method and remaining acceleration limits.

The source and release archive contain no model weights. Obtain a trusted model separately using the [model setup guide](https://github.com/michael-feinermann/passphrase-memorizer/blob/main/docs/LOCAL_AI.md). The [internal security audit](https://github.com/michael-feinermann/passphrase-memorizer/blob/main/docs/SECURITY_AUDIT.md) documents executed tests, the distribution checks and remaining operating-system, memory and custom-sandbox limitations. Complete physical erasure of all OS, framework, GPU or external copies cannot be guaranteed. This is an internal review, not third-party certification.

The release targets Apple Silicon Macs running macOS 14 or later. Download the ZIP and its SHA-256 sidecar together. Model licenses apply separately.

## Deutsch

Die erste eigenständige macOS-Version erzeugt Merkhilfen für vorhandene Folgen aus 1 bis 128 BIP39- oder EFF-Wörtern. Zur Auswahl stehen Reim, Ballade, Gedicht, Kurzgeschichte und Rap auf Englisch oder Deutsch. Englisch ist die anfängliche Sprache. Die zuletzt gewählte Sprache und die Fenstergröße werden gespeichert.

Die Oberfläche zeigt „Passphrase Memorizer 1.0.0“. Die erforderlichen Wörter bleiben wörtlich in eckigen Klammern und werden blau und fett hervorgehoben. Nach bestätigtem Einblenden bleiben sie bis zu zehn Minuten sichtbar. Beim Wechsel zu einer anderen App wird der Text weiterhin sofort verdeckt.

Die App verwendet einen eigenen lokalen KI-Prozess mit Gemma 4 E4B oder einem kompatiblen GGUF. Die Metal-GPU-Beschleunigung einschließlich unterstützter M5-GPU-Tensorkernel wird nach erfolgreicher öffentlicher Startberechnung gewählt. Für kontrollierte Nichtverfügbarkeit gibt es einen CPU-Pfad. Fatale Treiber- oder Inferenzfehler brechen die Anfrage sicher ab. Diese Version verwendet nicht die separate Apple Neural Engine. Jede Generierung beginnt mit einem leeren Kontext. Netzwerk- und Dateischreibsperren sowie die Identität des laufenden KI-Prozesses werden geprüft, bevor er die Wörter erhält. Es gibt keinen gespeicherten Eingabe-, Ausgabe- oder Chatverlauf. Leeren, Schließen und reguläres Beenden verwerfen ausstehende Ergebnisse, beenden die KI-Ausführung und überschreiben kontrollierte geheime Puffer. Die App hat ein neues Icon mit Buch und Schloss.

Drei CPU-/Metal-Testpaare auf dem getesteten M5 ergaben mit einer öffentlichen zwölf Wörter langen Beispielsequenz mediane Gesamtprozessdauern von 12,665 und 11,762 Sekunden. Metal brauchte in diesem Test 7,1 % weniger Zeit. Die Ausgabelängen unterschieden sich; daraus folgt keine allgemeine Leistungs- oder Energieaussage. Der Audit beschreibt Methode und verbleibende Grenzen der Beschleunigung.

Quellcode und Download enthalten keine Modellgewichte. Die [deutsche Modellanleitung](https://github.com/michael-feinermann/passphrase-memorizer/blob/main/docs/LOCAL_AI.de.md) erklärt die getrennte Einrichtung. Der [interne Sicherheits-Audit](https://github.com/michael-feinermann/passphrase-memorizer/blob/main/docs/SECURITY_AUDIT.md) nennt ausgeführte Tests, Freigabeprüfungen und verbleibende Grenzen des Betriebssystems, der Speicherbereinigung und der eigenen Sandbox. Vollständige physische Löschung aller Betriebssystem-, Framework-, GPU- oder externen Kopien kann nicht garantiert werden. Der interne Audit ist keine unabhängige Zertifizierung.

Der Download ist für Apple-Silicon-Macs ab macOS 14 vorgesehen. Lade die ZIP-Datei zusammen mit der SHA-256-Prüfsumme herunter. Modelllizenzen gelten separat.
