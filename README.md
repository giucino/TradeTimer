# Trade Timer — ATAS Indikator

Eigenentwickelter ATAS-Indikator (C#) für Futures-Trading. Zeigt als kompaktes
HUD an, **wie lange die aktuelle Position offen ist** — mit Farbwechsel ab einer
Ziel-Haltedauer. **Rein informativ.**

> Hilfsmittel für Prop-Firm-Regeln (z.B. Mindest-/Ziel-Haltedauer): auf einen Blick
> sehen, ob eine Position lange genug offen war.

## Was er macht

- Zeigt die Haltedauer der **offenen Position** im Format `MM:SS`.
- **Rot** = unter der Ziel-Sekundenzahl, **Grün** = darüber.
- Sauberes Tracking von **Entry / Exit / Flip / Pyramiding** über das **signierte**
  `Position.Volume` (Long > 0, Short < 0, Flat = 0): der Timer startet beim ersten
  Entry, bleibt beim Aufstocken/Teilschließen erhalten und wird nur bei Flat
  zurückgesetzt (bzw. beim direkten Flip neu).
- **Marktzeit** (`candle.LastTime`) statt Wanduhr → korrekt im Replay, robust gegen UI-Hänger.

## Einstellungen (Kurzüberblick)

| Gruppe | Einstellung |
|---|---|
| Einstellungen | Ziel-Sekunden, Schriftgröße |
| Farben | unter Ziel / über Ziel / Hintergrund |
| Position | Unten links/rechts, Abstand X/Y |

## Hinweise

- Nur bei **offener Position** sichtbar (Live-Account-Position; im Replay/Backtest
  gibt es keine Position).
- In totstillem Markt ohne neue Ticks tickt die Sekundenanzeige evtl. erst beim
  nächsten Tick weiter.

## Build & Installation

- TargetFramework `net10.0-windows`, ATAS-DLLs per HintPath referenziert.
- `dotnet build -c Release`, dann `TradeTimer.dll` nach `%APPDATA%\ATAS\Indicators\` kopieren.
- ATAS neu starten bzw. Indikatorliste aktualisieren.

## Lizenz / Hinweis

Private Eigenentwicklung. **Kein Handelssignal, keine Anlageberatung** — Nutzung
auf eigenes Risiko.
