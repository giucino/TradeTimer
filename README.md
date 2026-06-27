# Trade Timer — ATAS Indikator

Eigenentwickelter ATAS-Indikator (C#, `net10.0-windows`) für Futures-Prop-Trading.
Zwei Werkzeuge in einem:

1. **Live-Timer-HUD** — zeigt, *wie lange die aktuelle Position offen ist*, mit
   Farbwechsel/Warnung an der Scalping-Schwelle (15 s).
2. **Scalping-/Payout-Panel** — wertet *alle* Round-Trip-Trades eines Zeitraums aus
   und prüft live die **Payout-Bedingungen** einer Prop-Firm (zugeschnitten auf
   **IQ Capital**: Scalping-Regel, Sessions, Consistency, Auszahlung).

> **Rein informativ. Kein Handelssignal, keine Anlageberatung.**

---

## 1) Live-Timer-HUD

- Haltedauer der **offenen Position** im Format `MM:SS`.
- **Rot + „!"** unter der Scalping-Mindestlänge (15 s), **Grün** darüber — warnt also
  davor, einen Trade zu früh zu schließen (Scalping-Verstoß).
- Sauberes Tracking von **Entry / Exit / Flip / Pyramiding** über das **signierte**
  `Position.Volume` (Long > 0, Short < 0, Flat = 0): Timer startet beim ersten Entry,
  bleibt beim Aufstocken/Teilschließen erhalten, Reset nur bei Flat (bzw. neu beim Flip).
- **Marktzeit** (`candle.LastTime`) statt Wanduhr → korrekt im Replay, robust gegen UI-Hänger.
- Optional als Ring-Pill mit Fortschritt zur 15-s-Marke.

## 2) Scalping-/Payout-Panel

Liest die fertig gematchten Round-Trips aus ATAS' `TradingStatisticsProvider`
(`HistoryMyTrades`) und wertet sie aus:

- **Netto-PnL** (Brutto − Kommission), getrennte **Gewinne / Verluste**.
- **Gruppen `<15s` / `>15s`** mit Gewinn-Summen und %-Anteil an den Gesamtgewinnen.
- **Profit-Split-Leiste** (>15 s vs. <15 s) und scrollbare Trade-Tabelle.
- **Payout-Check** mit allen IQ-Bedingungen (siehe unten) und Ampel-Badge
  **„PAYOUT ERLAUBT / GESPERRT"**.
- **Auszahlbar-Schätzung** = `min(Cap, Netto × 50 % × 90 %)`.
- **Kontoerkennung** automatisch aus der Konto-ID:
  - Typ: `IQFEV…` = Evaluation, `IQFIF…` = Instant Funding, sonst Funded.
  - Größe: `…50K / 100K / 200K …` → setzt **Cap & Profit-Target** automatisch.
  - **Evaluation** zeigt den **Profit-Target-Fortschritt**, **Funded/Instant** den
    vollen Payout-Check.
- **CSV-Export** aller Trades in den Downloads-Ordner.
- **Zeitversatz** (Standard +2 h) bringt die angezeigten Zeiten auf IQ-Capital-Zeit.
- **Interaktiv:** Kopfzeile **ziehen** = verschieben, **Klick** = ein-/ausklappen,
  **Mausrad** = scrollen.

### IQ-Capital-Regeln im Panel

| Bedingung | Regel | Anzeige |
|---|---|---|
| Scalping (Trades) | ≥ 50 % aller Trades länger als 15 s | `Trades >15s: ist / Soll · %` |
| Scalping (Profite) | ≥ 50 % der Profite aus Trades > 15 s | `Profite >15s: ist / Soll · %` |
| Consistency (nur Funded) | bester Tag ≤ 30 % des Gesamtprofits | `Best-Day · %` + nötiger Gesamtgewinn |
| Sessions | 10 aktive Handelstage | `Sessions X/10` (Punkte) |
| Profit-Split | 90 % für den Trader | in „Auszahlbar" verrechnet |
| Auszahlung/Request | max. 50 % der Profite, gedeckelt am Account-Cap | `Auszahlbar ~…` |
| Profit-Target (Eval) | 6 % der Kontogröße (50k=3.000 … 200k=12.000) | Profit-Target-Meter |

Alle Schwellen sind als Einstellung anpassbar.

---

## Einstellungen (4 Gruppen)

| Gruppe | Inhalt |
|---|---|
| **Timer** | Ziel-Sekunden, 15-s-Warnung, Schriftgröße, Farben, Position/Abstände |
| **Daten & Konto** | Auswertung ab, Reset, Kontotyp, Auto-Cap/Target, Konto-/Symbol-Filter, Zeitversatz, Kommission, CSV-Export |
| **Payout-Regeln** | Mindestlänge (15 s), Scalping-Schwelle (50 %), Sessions (10), Consistency (30 %), Profit-Split (90 %), Auszahlungs-Cap, Profit-Target, 100 %-Freibetrag |
| **Panel** | Panel ein/aus, Schriftgröße, Abstände |

---

## Build & Deploy

- **Build:** `dotnet build -c Release` (ATAS-DLLs per HintPath referenziert).
- **Deploy:** `TradeTimer.dll` nach **`%APPDATA%\ATAS\Indicators\`** *und*
  **`%APPDATA%\ATAS X\Indicators\`** kopieren (zwei Editionen).
- **Hot-Reload:** ATAS lädt die DLL als Schattenkopie → die Datei lässt sich **bei
  laufendem ATAS überschreiben**. Danach erscheint in ATAS ein **„Indikatoren neu
  laden"-Hinweis** — ein Klick lädt die neue Version **ohne Neustart**.
- Hinweis: ATAS lädt **nur** aus den Roaming-Pfaden, nicht aus `Documents\ATAS\Indicators`.

## Hinweise

- Der **Timer** ist nur bei **offener Position** sichtbar; das **Panel** auch im Flat-Zustand.
- Die Auswertung liest die Trade-Historie des gewählten Kontos — dafür muss ATAS die
  Statistik/Journal für den Zeitraum geladen haben.
- Die `200k`-Cap-Schätzung ist noch zu verifizieren; alle Werte sind manuell überschreibbar.

## Lizenz / Hinweis

Private Eigenentwicklung. **Kein Handelssignal, keine Anlageberatung** — Nutzung auf
eigenes Risiko. „IQ Capital" dient nur als Referenz für die abgebildeten Regeln.
