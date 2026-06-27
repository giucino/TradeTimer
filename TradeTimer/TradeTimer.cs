using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using ATAS.DataFeedsCore;
using ATAS.Indicators;
using OFT.Attributes;
using OFT.Rendering.Context;
using OFT.Rendering.Tools;

namespace TradeTimer
{
    [DisplayName("Trade Timer")]
    [HelpLink("https://giucino.github.io/TradeTimer/TradeTimer_Doku.html")]
    [Description("Zeigt an, wie lange die aktuelle Position offen ist " +
                 "(Rot = unter Ziel-Sekunden, Gruen = ueber Ziel-Sekunden) und " +
                 "optional eine Scalping-Auswertung aller Round-Trip-Trades ueber " +
                 "einen Zeitraum (Dauer/PnL, Gruppierung nach Mindestlaenge, CSV-Export).")]
    public class TradeTimer : Indicator
    {
        public enum AccountKind { Auto, Evaluation, Funded, InstantFunding }

        // ── Backing fields ─────────────────────────────────────────────
        private int _targetSeconds = 15;
        private int _fontSize = 16;
        private int _offsetX = 20;
        private int _offsetY = 20;
        private bool _bottomLeft = true;

        private Color _colorBelow = Color.FromArgb(220, 220, 50, 50);
        private Color _colorAbove = Color.FromArgb(220, 50, 205, 50);
        private Color _colorBackground = Color.FromArgb(160, 20, 20, 20);

        // ── Backing fields: Scalping-Auswertung ────────────────────────
        private bool _showStatsPanel = true;
        private int _minTradeSeconds = 15;          // IQ-Capital-Scalping-Schwelle
        private DateTime _statsFrom = DateTime.Today;
        private bool _accountWide = true;
        private bool _allAccounts = false;          // false = nur Konto dieses Charts
        private string _accountId = "";             // leer = Konto dieses Charts (TradingManager)
        private int _statsOffsetX = 20;
        private int _statsOffsetY = 20;
        private int _statsFontSize = 13;
        private int _timeOffsetHours = 2;            // Anzeige-Versatz zu IQ Capital (ATAS+2h)
        private bool _subtractCommission = true;     // PnL netto = brutto - Kommission
        private int _payoutThresholdPct = 50;        // IQ: >=50% Trades UND >=50% Profite aus >15s
        private int _requiredSessions = 10;          // IQ: erste Auszahlung nach 10 aktiven Tagen
        private int _consistencyPct = 30;            // IQ Funded: bester Tag <= 30% des Gesamtprofits
        private int _profitSplitPct = 90;            // IQ: 90% Profit-Split
        private AccountKind _accountType = AccountKind.Auto; // Eval vs Funded
        private string _evalKeyword = "EV";          // Kennung in der Konto-ID (IQFEV..)
        private decimal _profitTarget = 6000m;       // Eval-Ziel (Fallback; sonst auto nach Groesse)
        private bool _autoTier = true;               // Cap & Target autom. nach Kontogroesse (50K/100K/200K)
        private int _withdrawCapPct = 50;            // IQ: max 50% der Gesamtprofite pro Auszahlung
        private decimal _maxPayout = 2000m;          // IQ: Account-Cap (100k=$2000, 50k=$1500); 0=aus
        private decimal _hundredPctAllowance = 0m;   // optional: erste X zu 100% (nicht in offiz. Regeln)
        private bool _timerScalpWarn = true;         // Live-Timer warnt unter Scalping-Schwelle

        // ── Runtime state ──────────────────────────────────────────────
        // Zeitpunkt des ERSTEN Entries der aktuellen Position. Bleibt bei
        // Pyramiding (Aufstocken) erhalten, wird nur bei Flat (Vol = 0)
        // zurueckgesetzt. Bei einem direkten Flip (Long->Short ohne Flat)
        // wird er ueber den Vorzeichenwechsel neu gesetzt.
        private DateTime? _entryTime;
        private decimal _lastVolume;            // letzter SIGNIERTER Volume-Wert
        private RenderFont _font;

        // Marktzeit der zuletzt verarbeiteten Bar. Dient als robuste
        // Zeitquelle statt DateTime.UtcNow (korrekt im Replay/Backtest
        // und unabhaengig von UI-Haengern).
        private DateTime _lastMarketTime = DateTime.MinValue;

        // ── Runtime state: Stats ───────────────────────────────────────
        private RenderFont _statsFont;
        private volatile StatsSnapshot? _stats;  // unveraenderlicher Snapshot fuers Rendern
        private bool _historyRequested;
        private int _lastStatsBar = -1;
        private string? _lastCsvPath;
        private string? _lastError;
        private bool _collapsed;                  // Klick auf Kopfzeile klappt auf/zu
        private Rectangle _headerHitRect;         // klickbare Kopfzeile (aus letztem Render)
        private Rectangle _panelRect;             // gesamtes Panel (Hit-Test fuer Wheel)
        private int _scrollOffset;                // Scroll-Position im Detailteil (in Zeilen)
        private bool _dragging;                   // Panel wird gerade per Drag verschoben
        private bool _didDrag;                    // zwischen Down/Up tatsaechlich bewegt
        private int _dragStartX, _dragStartY;     // Mausposition bei Drag-Start
        private int _dragOrigOffX, _dragOrigOffY; // Panel-Offsets bei Drag-Start
        private int _lastBoxW;                     // Panelbreite aus letztem Render (Clamping)
        private int _lastMouseX, _lastMouseY;      // letzte Mausposition (fuer Wheel-Hit-Test)

        // ── Design-Palette (statisch, kein Per-Frame-Alloc) ────────────
        private static readonly Color CBg        = Color.FromArgb(252, 11, 16, 23);
        private static readonly Color CBorder    = Color.FromArgb(255, 35, 48, 63);
        private static readonly Color CAccent    = Color.FromArgb(255, 34, 211, 238);
        private static readonly Color CText       = Color.FromArgb(255, 223, 231, 238);
        private static readonly Color CMuted     = Color.FromArgb(255, 143, 160, 175);
        private static readonly Color CDim       = Color.FromArgb(255, 91, 107, 122);
        private static readonly Color CGreen     = Color.FromArgb(255, 52, 211, 153);
        private static readonly Color CRed       = Color.FromArgb(255, 248, 113, 113);
        private static readonly Color CAmber     = Color.FromArgb(255, 251, 191, 36);
        private static readonly Color CTrack     = Color.FromArgb(255, 28, 42, 56);
        private static readonly Color CFillGreen = Color.FromArgb(255, 47, 125, 99);
        private static readonly Color CFillRed   = Color.FromArgb(255, 140, 56, 62);
        private static readonly Color CPillText  = Color.FromArgb(255, 7, 12, 18);
        private static readonly Color CGlow      = Color.FromArgb(70, 34, 211, 238);
        private static readonly Color CTitle1    = Color.FromArgb(255, 13, 56, 66);
        private static readonly Color CTitle2    = Color.FromArgb(255, 10, 17, 28);
        private static readonly Color CGreenHi   = Color.FromArgb(255, 52, 211, 153);
        private static readonly Color CGreenLo   = Color.FromArgb(255, 16, 122, 86);
        private static readonly Color CRedHi     = Color.FromArgb(255, 248, 113, 113);
        private static readonly Color CRedLo     = Color.FromArgb(255, 150, 45, 52);
        private static readonly Color CAmberHi   = Color.FromArgb(255, 240, 180, 70);
        private static readonly Color CAmberLo   = Color.FromArgb(255, 150, 110, 30);
        private static readonly Color CHighlight = Color.FromArgb(90, 255, 255, 255);
        private static readonly Color CViolet    = Color.FromArgb(255, 167, 139, 250); // Auszahlung-Akzent
        private static readonly Color CRowText   = Color.FromArgb(255, 178, 192, 204);  // neutrale Tabellenzeile
        private static readonly Color CValue     = Color.FromArgb(255, 232, 238, 244);  // Werte (neutral hell)
        private static readonly RenderStringFormat FmtRight =
            new() { Alignment = StringAlignment.Far };
        private static readonly RenderStringFormat FmtCenter =
            new() { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        private static readonly RenderStringFormat FmtMidLeft =
            new() { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center };

        private RenderPen _penBorder;
        private RenderPen _penAccent;
        private RenderPen _penDivider;
        private RenderPen _penTick;
        private RenderPen _penGlow;            // weicher Cyan-Glow (Rahmen)
        private RenderPen _penGlowGreen;       // Glow um Payout-Badge (ok)
        private RenderPen _penGlowRed;         // Glow um Payout-Badge (gesperrt)
        private RenderPen _penGlowAmber;       // Glow um Eval-Badge (in Arbeit)
        private RenderPen _penHighlight;       // feine helle Innenkante
        private RenderFont _statsFontBig;     // Hero-PnL
        private RenderFont _statsFontSmall;   // kleine Labels
        private RenderFont _statsFontBold;    // Titel / Badge

        // ── Einstellungen ──────────────────────────────────────────────
        [Display(Name = "Ziel-Sekunden", GroupName = "Timer", Order = 1,
                 Description = "Schwelle fuer das Timer-HUD: bis zu diesem Wert ist die Anzeige rot, " +
                               "ab diesem Wert gruen. Steuert nur die Farbe des Haltedauer-Timers.")]
        [Range(1, 3600)]
        public int TargetSeconds
        {
            get => _targetSeconds;
            set { _targetSeconds = Math.Max(1, value); RedrawChart(); }
        }

        [Display(Name = "Schriftgröße", GroupName = "Timer", Order = 3,
                 Description = "Schriftgroesse des Timer-HUD (die MM:SS-Anzeige der aktuellen Haltedauer).")]
        [Range(8, 40)]
        public int FontSize
        {
            get => _fontSize;
            set
            {
                _fontSize = Math.Clamp(value, 8, 40);
                _font = new RenderFont("Consolas", _fontSize, FontStyle.Bold);
                RedrawChart();
            }
        }

        // ── Farben ─────────────────────────────────────────────────────
        [Display(Name = "Farbe unter Ziel", GroupName = "Timer", Order = 4,
                 Description = "Textfarbe des Timers, solange die Haltedauer UNTER den Ziel-Sekunden liegt.")]
        public Color ColorBelow
        {
            get => _colorBelow;
            set { _colorBelow = value; RedrawChart(); }
        }

        [Display(Name = "Farbe über Ziel", GroupName = "Timer", Order = 5,
                 Description = "Textfarbe des Timers, sobald die Haltedauer die Ziel-Sekunden ERREICHT/ueberschreitet.")]
        public Color ColorAbove
        {
            get => _colorAbove;
            set { _colorAbove = value; RedrawChart(); }
        }

        [Display(Name = "Hintergrundfarbe", GroupName = "Timer", Order = 6,
                 Description = "Hintergrundfarbe der Timer-Box (mit Transparenz/Alpha).")]
        public Color ColorBackground
        {
            get => _colorBackground;
            set { _colorBackground = value; RedrawChart(); }
        }

        // ── Position ───────────────────────────────────────────────────
        [Display(Name = "Unten Links (aus = Unten Rechts)", GroupName = "Timer", Order = 7,
                 Description = "Ecke des Timer-HUD: An = unten links, Aus = unten rechts. " +
                               "Betrifft nur den Haltedauer-Timer, nicht das Statistik-Panel.")]
        public bool BottomLeft
        {
            get => _bottomLeft;
            set { _bottomLeft = value; RedrawChart(); }
        }

        [Display(Name = "Abstand vom Rand X (px)", GroupName = "Timer", Order = 8,
                 Description = "Horizontaler Abstand des Timer-HUD vom Chartrand in Pixeln.")]
        [Range(0, 500)]
        public int OffsetX
        {
            get => _offsetX;
            set { _offsetX = value; RedrawChart(); }
        }

        [Display(Name = "Abstand vom Rand Y (px)", GroupName = "Timer", Order = 9,
                 Description = "Vertikaler Abstand des Timer-HUD vom unteren Chartrand in Pixeln.")]
        [Range(0, 500)]
        public int OffsetY
        {
            get => _offsetY;
            set { _offsetY = value; RedrawChart(); }
        }

        // ── Scalping-Auswertung ────────────────────────────────────────
        [Display(Name = "Panel anzeigen", GroupName = "Panel", Order = 50,
                 Description = "Blendet das Statistik-Panel (oben rechts) ein/aus. Der Haltedauer-Timer " +
                               "bleibt davon unberuehrt. Im Chart: Klick auf die Kopfzeile = auf/zu, " +
                               "Kopfzeile ziehen = verschieben, Mausrad = scrollen.")]
        public bool ShowStatsPanel
        {
            get => _showStatsPanel;
            set { _showStatsPanel = value; RedrawChart(); }
        }

        [Display(Name = "Mindestlänge (Sek.)", GroupName = "Payout-Regeln", Order = 30,
                 Description = "Scalping-Schwelle in Sekunden (IQ Capital: 15). Trades mit Haltedauer " +
                               "DARUNTER zaehlen als '<15s' (Scalp), DARUEBER als '>15s' (regelkonform).")]
        [Range(1, 3600)]
        public int MinTradeSeconds
        {
            get => _minTradeSeconds;
            set { _minTradeSeconds = Math.Clamp(value, 1, 3600); RecalcStats(); RedrawChart(); }
        }

        [Display(Name = "Auswertung ab", GroupName = "Daten & Konto", Order = 10,
                 Description = "Startzeitpunkt der Auswertung. Nur Trades, die DANACH geschlossen wurden, " +
                               "werden gezaehlt. Auf den Beginn deines Payout-Zyklus setzen.")]
        public DateTime StatsFrom
        {
            get => _statsFrom;
            set { _statsFrom = value; _historyRequested = false; RecalcStats(); RedrawChart(); }
        }

        [Display(Name = "Jetzt zurücksetzen (ab = jetzt)", GroupName = "Daten & Konto", Order = 11,
                 Description = "Setzt 'Auswertung ab' auf den aktuellen Zeitpunkt (z.B. nach einem Payout). " +
                               "Haken setzen = Reset ausloesen; springt automatisch zurueck.")]
        public bool ResetNow
        {
            get => false;
            set
            {
                if (!value) return;
                _statsFrom = DateTime.Now;
                _historyRequested = false;
                RecalcStats();
                RedrawChart();
            }
        }

        [Display(Name = "Alle Symbole des Kontos", GroupName = "Daten & Konto", Order = 17,
                 Description = "An = alle gehandelten Instrumente des Kontos zusammen (z.B. MNQ + NQ), " +
                               "passt zur kontoweiten IQ-Regel (empfohlen). Aus = nur das Symbol dieses Charts.")]
        public bool AccountWide
        {
            get => _accountWide;
            set { _accountWide = value; _historyRequested = false; RecalcStats(); RedrawChart(); }
        }

        [Display(Name = "Alle Konten zusammen", GroupName = "Daten & Konto", Order = 15,
                 Description = "Aus = nur das Konto dieses Charts (empfohlen bei mehreren Konten). " +
                               "An = alle ATAS-Konten zusammengezaehlt.")]
        public bool AllAccounts
        {
            get => _allAccounts;
            set { _allAccounts = value; _historyRequested = false; RecalcStats(); RedrawChart(); }
        }

        [Display(Name = "Konto-ID (leer = Chart-Konto)", GroupName = "Daten & Konto", Order = 16,
                 Description = "Optional: feste Konto-ID erzwingen. Leer = automatisch das Konto, " +
                               "das fuer diesen Chart gewaehlt ist. Wirkt nur wenn 'Alle Konten' aus ist.")]
        public string AccountId
        {
            get => _accountId;
            set { _accountId = value?.Trim() ?? ""; _historyRequested = false; RecalcStats(); RedrawChart(); }
        }

        [Display(Name = "CSV exportieren", GroupName = "Daten & Konto", Order = 20,
                 Description = "Schreibt alle Trades des Zeitraums als CSV in den Downloads-Ordner " +
                               "(Spalten: Zeit, Symbol, Richtung, Dauer, Brutto, Kommission, Netto). " +
                               "Haken setzen = exportieren; springt zurueck.")]
        public bool ExportCsvNow
        {
            get => false;
            set { if (value) { ExportCsv(); RedrawChart(); } }
        }

        [Display(Name = "Schriftgröße Panel", GroupName = "Panel", Order = 51,
                 Description = "Schriftgroesse des Statistik-Panels. Kleiner = mehr Zeilen passen ohne Scrollen.")]
        [Range(8, 30)]
        public int StatsFontSize
        {
            get => _statsFontSize;
            set
            {
                _statsFontSize = Math.Clamp(value, 8, 30);
                _statsFont = new RenderFont("Consolas", _statsFontSize, FontStyle.Regular);
                _statsFontBig = new RenderFont("Consolas", _statsFontSize + 11, FontStyle.Bold);
                _statsFontSmall = new RenderFont("Consolas", Math.Max(7, _statsFontSize - 2), FontStyle.Regular);
                _statsFontBold = new RenderFont("Consolas", _statsFontSize, FontStyle.Bold);
                RedrawChart();
            }
        }

        [Display(Name = "Panel-Abstand X (px)", GroupName = "Panel", Order = 52,
                 Description = "Horizontaler Abstand des Statistik-Panels vom RECHTEN Chartrand. " +
                               "Wird beim Verschieben per Drag automatisch aktualisiert.")]
        [Range(0, 1000)]
        public int StatsOffsetX
        {
            get => _statsOffsetX;
            set { _statsOffsetX = value; RedrawChart(); }
        }

        [Display(Name = "Panel-Abstand Y (px)", GroupName = "Panel", Order = 53,
                 Description = "Vertikaler Abstand des Statistik-Panels vom OBEREN Chartrand. " +
                               "Wird beim Verschieben per Drag automatisch aktualisiert.")]
        [Range(0, 1000)]
        public int StatsOffsetY
        {
            get => _statsOffsetY;
            set { _statsOffsetY = value; RedrawChart(); }
        }

        [Display(Name = "Zeitversatz Std. (IQ Capital)", GroupName = "Daten & Konto", Order = 18,
                 Description = "Stunden, die zur ATAS-Zeit addiert werden, damit die angezeigten " +
                               "Trade-Zeiten zu IQ Capital passen (z.B. +2). Wirkt auf Panel und CSV.")]
        [Range(-12, 12)]
        public int TimeOffsetHours
        {
            get => _timeOffsetHours;
            set { _timeOffsetHours = Math.Clamp(value, -12, 12); RedrawChart(); }
        }

        [Display(Name = "Scalping-Schwelle (%)", GroupName = "Payout-Regeln", Order = 31,
                 Description = "IQ-Capital-Regel: Payout nur erlaubt, wenn >= dieser Anteil der TRADES " +
                               "laenger als die Mindestlaenge war UND >= dieser Anteil der PROFITE aus " +
                               "diesen Trades stammt. Standard 50.")]
        [Range(0, 100)]
        public int PayoutThresholdPct
        {
            get => _payoutThresholdPct;
            set { _payoutThresholdPct = Math.Clamp(value, 0, 100); RedrawChart(); }
        }

        [Display(Name = "Benötigte Sessions", GroupName = "Payout-Regeln", Order = 32,
                 Description = "Aktive Handelstage bis zur ersten Auszahlung (IQ: 10). Das Panel zaehlt " +
                               "die unterschiedlichen Handelstage im Zeitraum und zeigt X/Soll.")]
        [Range(1, 100)]
        public int RequiredSessions
        {
            get => _requiredSessions;
            set { _requiredSessions = Math.Clamp(value, 1, 100); RedrawChart(); }
        }

        [Display(Name = "Consistency Best-Day (%)", GroupName = "Payout-Regeln", Order = 33,
                 Description = "Consistency-Regel (nur Funded): der beste einzelne Handelstag darf hoechstens " +
                               "diesen Anteil am Gesamtprofit ausmachen (IQ Funded Futures: 30).")]
        [Range(1, 100)]
        public int ConsistencyPct
        {
            get => _consistencyPct;
            set { _consistencyPct = Math.Clamp(value, 1, 100); RedrawChart(); }
        }

        [Display(Name = "Cap & Target auto (Kontogröße)", GroupName = "Daten & Konto", Order = 13,
                 Description = "An = Max-Auszahlung (Cap) und Eval-Profit-Target werden automatisch aus der " +
                               "Kontogröße in der Konto-ID gesetzt: 50K→Target 3.000/Cap 1.500, " +
                               "100K→6.000/2.000, 200K→12.000/2.500. Aus = manuelle Werte unten benutzen.")]
        public bool AutoTier
        {
            get => _autoTier;
            set { _autoTier = value; RedrawChart(); }
        }

        [Display(Name = "Kontotyp", GroupName = "Daten & Konto", Order = 12,
                 Description = "Auto = automatisch an der Konto-ID erkannt (IQFEV.. = Evaluation, " +
                               "IQFIF.. = Instant Funding, sonst Funded). Evaluation zeigt das Profit-Target; " +
                               "Funded und Instant Funding zeigen den Payout-Check (Sessions + Consistency).")]
        public AccountKind AccountType
        {
            get => _accountType;
            set { _accountType = value; RedrawChart(); }
        }

        [Display(Name = "Evaluation-Kennung (Konto-ID)", GroupName = "Daten & Konto", Order = 14,
                 Description = "Teilzeichenfolge in der Konto-ID, die ein Evaluation-Konto kennzeichnet " +
                               "(Standard 'EV', z.B. IQFEV100K). Nur relevant bei Kontotyp = Auto.")]
        public string EvalKeyword
        {
            get => _evalKeyword;
            set { _evalKeyword = value?.Trim() ?? ""; RedrawChart(); }
        }

        [Display(Name = "Profit-Target Eval (EUR)", GroupName = "Payout-Regeln", Order = 37,
                 Description = "Profit-Ziel der Evaluation (Standard 6000; je nach Tier anpassen). " +
                               "Wird im Evaluation-Modus als Fortschritt angezeigt.")]
        [Range(0, 1000000)]
        public int ProfitTarget
        {
            get => (int)_profitTarget;
            set { _profitTarget = Math.Max(0, value); RedrawChart(); }
        }

        [Display(Name = "Profit-Split (%)", GroupName = "Payout-Regeln", Order = 34,
                 Description = "Dein Anteil am Profit fuer die Auszahlungs-Schaetzung (IQ: 90). " +
                               "Geschaetzt auszahlbar = Netto-Profit x Split (ueber dem 100%-Freibetrag).")]
        [Range(0, 100)]
        public int ProfitSplitPct
        {
            get => _profitSplitPct;
            set { _profitSplitPct = Math.Clamp(value, 0, 100); RedrawChart(); }
        }

        [Display(Name = "Auszahlung max % der Profite", GroupName = "Payout-Regeln", Order = 35,
                 Description = "IQ-Regel: pro Auszahlung max. dieser Anteil der Gesamtprofite (Standard 50). " +
                               "Fliesst in die 'auszahlbar'-Schaetzung ein.")]
        [Range(1, 100)]
        public int WithdrawCapPct
        {
            get => _withdrawCapPct;
            set { _withdrawCapPct = Math.Clamp(value, 1, 100); RedrawChart(); }
        }

        [Display(Name = "Max. Auszahlung (EUR, 0=aus)", GroupName = "Payout-Regeln", Order = 36,
                 Description = "Account-Cap pro Auszahlung (IQ: 100k=2000, 50k=1500). Deckelt die " +
                               "'auszahlbar'-Schaetzung. 0 = kein Cap.")]
        [Range(0, 1000000)]
        public int MaxPayout
        {
            get => (int)_maxPayout;
            set { _maxPayout = Math.Max(0, value); RedrawChart(); }
        }

        [Display(Name = "100%-Freibetrag (EUR, 0=aus)", GroupName = "Payout-Regeln", Order = 38,
                 Description = "OPTIONAL: erste X EUR der Auszahlungsbasis zu 100% (statt Split). Steht NICHT " +
                               "in den offiziellen IQ-Auszahlungsregeln (dort flat 90%); nur falls dein Konto " +
                               "das hat. 0 = aus (empfohlen).")]
        [Range(0, 1000000)]
        public int HundredPctAllowance
        {
            get => (int)_hundredPctAllowance;
            set { _hundredPctAllowance = Math.Max(0, value); RedrawChart(); }
        }

        [Display(Name = "Timer-Warnung unter Mindestlänge", GroupName = "Timer", Order = 2,
                 Description = "An = der Haltedauer-Timer ist rot mit '!' solange die offene Position " +
                               "unter der Scalping-Mindestlaenge ist, und wird erst darueber gruen. " +
                               "Hilft, Trades nicht zu frueh zu schliessen. Aus = nutzt 'Ziel-Sekunden'.")]
        public bool TimerScalpWarn
        {
            get => _timerScalpWarn;
            set { _timerScalpWarn = value; RedrawChart(); }
        }

        [Display(Name = "Kommission abziehen (Netto-PnL)", GroupName = "Daten & Konto", Order = 19,
                 Description = "An = PnL netto (Brutto minus Kommission je Trade). Aus = Brutto-PnL " +
                               "ohne Kommission. Zum Abgleich mit IQ Capital ggf. umschalten.")]
        public bool SubtractCommission
        {
            get => _subtractCommission;
            set { _subtractCommission = value; RecalcStats(); RedrawChart(); }
        }

        // ── Ctor ───────────────────────────────────────────────────────
        public TradeTimer() : base(true)
        {
            EnableCustomDrawing = true;
            DrawAbovePrice = true;
            DataSeries[0].IsHidden = true;

            // Pflicht fuer persistentes Custom-Drawing: ohne dies zeichnet ATAS nur
            // bei DrawingLayouts.LatestBar -> das HUD verschwindet, sobald man vom
            // aktuellsten Bar wegnavigiert (Drag/Zoom/ZoomXY). Siehe CLAUDE.md.
            SubscribeToDrawingEvents(DrawingLayouts.Historical | DrawingLayouts.Final);

            _font = new RenderFont("Consolas", _fontSize, FontStyle.Bold);
            _statsFont = new RenderFont("Consolas", _statsFontSize, FontStyle.Regular);
            _statsFontBig = new RenderFont("Consolas", _statsFontSize + 11, FontStyle.Bold);
            _statsFontSmall = new RenderFont("Consolas", Math.Max(7, _statsFontSize - 2), FontStyle.Regular);
            _statsFontBold = new RenderFont("Consolas", _statsFontSize, FontStyle.Bold);
            _penBorder = new RenderPen(CBorder, 1);
            _penAccent = new RenderPen(CAccent, 1);
            _penDivider = new RenderPen(Color.FromArgb(255, 26, 38, 50), 1);
            _penTick = new RenderPen(Color.FromArgb(255, 210, 225, 235), 1);
            _penGlow = new RenderPen(CGlow, 3);
            _penGlowGreen = new RenderPen(Color.FromArgb(70, 52, 211, 153), 3);
            _penGlowRed = new RenderPen(Color.FromArgb(70, 248, 113, 113), 3);
            _penGlowAmber = new RenderPen(Color.FromArgb(70, 240, 180, 70), 3);
            _penHighlight = new RenderPen(CHighlight, 1);
        }

        // ── Kein eigener System.Timers.Timer mehr ──────────────────────
        // Der Original-Timer feuerte auf einem Fremd-Thread und rief von
        // dort RedrawChart() -> Race Conditions / Crash-Gefahr. ATAS
        // rendert ohnehin bei jedem Tick neu, solange eine Position offen
        // ist. Reicht das visuell nicht, kann unten in OnCalculate ein
        // gedrosselter RedrawChart()-Aufruf aktiviert werden.

        protected override void OnCalculate(int bar, decimal value)
        {
            // Nur auf dem aktuell entstehenden Bar arbeiten.
            if (bar != CurrentBar - 1)
                return;

            // Robuste Zeitquelle: Marktzeit der letzten Bar (in ATAS UTC).
            // Faellt im Live-Betrieb praktisch mit der Echtzeit zusammen,
            // ist aber im Replay/Backtest korrekt.
            var candle = GetCandle(bar);
            if (candle != null)
                _lastMarketTime = candle.LastTime;

            UpdatePositionState();

            // Stats: einmalig aus der Historie nachladen, danach je Bar neu
            // aggregieren (guenstig, da einmal je Bar statt je Tick).
            if (!_historyRequested)
            {
                _historyRequested = true;
                TryLoadHistory();
                RecalcStats();
            }
            else if (bar != _lastStatsBar)
            {
                _lastStatsBar = bar;
                RecalcStats();
            }

            // Optional: sichtbares Sekunden-Ticken erzwingen, wenn keine
            // neuen Ticks reinkommen. Nur bei offener Position, also keine
            // Dauerlast im Flat-Zustand.
            if (_entryTime != null)
                RedrawChart();
        }

        // Live-Hook: jede neue eigene Ausfuehrung -> Auswertung aktualisieren.
        protected override void OnNewMyTrade(MyTrade myTrade)
        {
            RecalcStats();
            RedrawChart();
        }

        // Kontowechsel im Trade-Panel -> sofort neu laden & rechnen (sofern
        // nicht per fester Konto-ID gepinnt). Die Kopfzeile zeigt das neue Konto.
        protected override void OnPortfolioChanged(Portfolio portfolio)
        {
            _historyRequested = false;
            RecalcStats();
            RedrawChart();
        }

        // Erkennt Entry, Exit, Flip und Pyramiding sauber ueber den
        // SIGNIERTEN Positionswert (Long > 0, Short < 0, Flat = 0).
        private void UpdatePositionState()
        {
            var position = TradingManager?.Position;

            // Signiertes Volumen: Long > 0, Short < 0, Flat = 0.
            // In dieser SDK-Version ist Position.Volume bereits signiert
            // (negativ bei Short). Wir nehmen den Wert direkt und runden
            // gegen Rundungsreste auf 0.
            decimal signedVolume = position?.Volume ?? 0;
            if (Math.Abs(signedVolume) < 0.0000001m)
                signedVolume = 0;

            if (signedVolume == _lastVolume)
                return; // keine relevante Aenderung

            bool wasFlat = _lastVolume == 0;
            bool isFlat = signedVolume == 0;
            bool flipped = !wasFlat && !isFlat
                             && Math.Sign(signedVolume) != Math.Sign(_lastVolume);

            if (isFlat)
            {
                // Position geschlossen -> Timer aus.
                _entryTime = null;
            }
            else if (wasFlat || flipped)
            {
                // Neue Position (aus Flat heraus ODER direkter Flip).
                // Zeitstempel = Marktzeit, Fallback auf UtcNow vor erster Bar.
                _entryTime = _lastMarketTime == DateTime.MinValue
                    ? DateTime.UtcNow
                    : _lastMarketTime;
            }
            // else: gleiche Richtung, nur Groesse geaendert (Pyramiding/
            // Teilschliessung) -> _entryTime bleibt beim ersten Entry.

            _lastVolume = signedVolume;
        }

        // ── Stats: Datenerfassung ──────────────────────────────────────

        // Best-effort: vergangene Trades fuer den Zeitraum aus der Historie
        // nachladen. Bei Fehlern wird still auf die live gepflegten
        // Collections (RawStatistics/Realtime/Replay) zurueckgegriffen.
        private void TryLoadHistory()
        {
            try
            {
                var prov = TradingStatisticsProvider;
                if (prov == null)
                    return;

                // Konto(en) fuer den Load bestimmen (gleicher Scope wie RecalcStats).
                var chartAcc = !string.IsNullOrEmpty(_accountId) ? _accountId : TradingManager?.Portfolio?.AccountID;
                ICollection<string> accs = (_allAccounts || string.IsNullOrEmpty(chartAcc))
                    ? Array.Empty<string>()
                    : new[] { chartAcc };

                var secId = TradingManager?.Security?.SecurityId;
                ICollection<string> secs = (_accountWide || string.IsNullOrEmpty(secId))
                    ? Array.Empty<string>()
                    : new[] { secId };

#pragma warning disable CS0612 // LoadHistoryAsync veraltet, aber einzige programmatische Lademoeglichkeit
                // 'from' um den Anzeige-Versatz zurueck (sowie 1 Tag Puffer), damit der
                // Filter-Rand sicher abgedeckt ist; gefiltert wird spaeter in RecalcStats.
                var loadFrom = _statsFrom.AddHours(-_timeOffsetHours).AddDays(-1);
                var task = prov.LoadHistoryAsync(loadFrom, DateTime.Now, accs, secs);
#pragma warning restore CS0612
                task?.ContinueWith(_ => { RecalcStats(); RedrawChart(); });
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
            }
        }

        // Alle fertig gematchten Round-Trips aus den drei Quellen einsammeln
        // und per Id deduplizieren (gegen Doppelzaehlung).
        private IEnumerable<HistoryMyTrade> GatherTrades()
        {
            var prov = TradingStatisticsProvider;
            if (prov == null)
                return Enumerable.Empty<HistoryMyTrade>();

            var dict = new Dictionary<long, HistoryMyTrade>();
            foreach (var s in new[] { prov.RawStatistics, prov.FilteredStatistics })
            {
                var coll = s?.HistoryMyTrades;
                if (coll == null)
                    continue;

                List<HistoryMyTrade> copy;
                try { copy = coll.ToList(); }   // defensiv: kann nebenlaeufig mutieren
                catch { continue; }

                foreach (var t in copy)
                    if (t != null)
                        dict[t.Id] = t;
            }
            return dict.Values;
        }

        private void RecalcStats()
        {
            var snap = new StatsSnapshot
            {
                From = _statsFrom,
                MinSeconds = _minTradeSeconds
            };

            try
            {
                var curSecId = TradingManager?.Security?.SecurityId;

                // Konto-Scope bestimmen: leer/AllAccounts => kein Konto-Filter,
                // sonst feste Konto-ID oder automatisch das Konto dieses Charts.
                string? targetAcc = _allAccounts
                    ? null
                    : (!string.IsNullOrEmpty(_accountId) ? _accountId : TradingManager?.Portfolio?.AccountID);
                snap.AccountLabel = _allAccounts ? "ALLE KONTEN"
                    : (string.IsNullOrEmpty(targetAcc) ? "(kein Konto)" : targetAcc);

                var dayPnl = new Dictionary<DateTime, decimal>();   // Netto-PnL je Handelstag

                foreach (var t in GatherTrades())
                {
                    if (!t.IsComplete)
                        continue;
                    // Filter in ANGEZEIGTER Zeit (inkl. Versatz), damit 'Auswertung ab'
                    // und 'Jetzt zuruecksetzen' (lokale Uhrzeit) zur Trade-Zeit passen.
                    if (Disp(t.CloseTime) < _statsFrom)
                        continue;
                    if (!string.IsNullOrEmpty(targetAcc) && t.AccountID != targetAcc)
                        continue;
                    if (!_accountWide && !string.IsNullOrEmpty(curSecId)
                        && t.Security?.SecurityId != curSecId)
                        continue;

                    var dur = t.CloseTime - t.OpenTime;
                    if (dur < TimeSpan.Zero)
                        dur = TimeSpan.Zero;

                    // ATAS speichert Commission signiert (i.d.R. negativ = Kosten).
                    // Wir nehmen den Betrag als Kosten -> netto = brutto - Kosten.
                    decimal commCost = Math.Abs(t.Commission ?? 0m);
                    decimal gross = t.PnL;
                    decimal pnl = _subtractCommission ? gross - commCost : gross;
                    bool compliant = dur.TotalSeconds >= _minTradeSeconds;
                    var row = new TradeRow
                    {
                        OpenTime = t.OpenTime,
                        CloseTime = t.CloseTime,
                        Symbol = t.Security?.Code ?? "?",
                        IsLong = DetectLong(t),
                        Duration = dur,
                        Pnl = pnl,
                        GrossPnl = gross,
                        Commission = commCost,
                        TicksPnl = t.TicksPnL,
                        Compliant = compliant
                    };

                    snap.All.Add(row);
                    snap.TotalCount++;
                    snap.TotalPnl += pnl;
                    snap.GrossSum += gross;
                    snap.CommissionTotal += commCost;   // positive Gesamtkosten
                    if (pnl >= 0) snap.WinSum += pnl; else snap.LossSum += pnl;

                    var day = Disp(t.CloseTime).Date;   // Handelstag in Anzeige-Zeit
                    dayPnl[day] = (dayPnl.TryGetValue(day, out var d) ? d : 0m) + pnl;

                    if (compliant)
                    {
                        snap.OverCount++;
                        snap.OverPnl += pnl;
                        if (pnl >= 0) { snap.OverWinSum += pnl; snap.OverWinners.Add(row); }  // nur Gewinner einzeln
                        else snap.OverLossSum += pnl;
                    }
                    else
                    {
                        snap.UnderCount++;
                        snap.UnderPnl += pnl;
                        if (pnl >= 0) { snap.UnderWinSum += pnl; snap.UnderWinners.Add(row); }
                        else snap.UnderLossSum += pnl;
                    }
                }

                // neueste zuerst
                snap.OverWinners.Sort((a, b) => b.CloseTime.CompareTo(a.CloseTime));
                snap.UnderWinners.Sort((a, b) => b.CloseTime.CompareTo(a.CloseTime));

                snap.DistinctDays = dayPnl.Count;
                snap.BestDayPnl = dayPnl.Count > 0 ? dayPnl.Values.Max() : 0m;
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
            }

            _stats = snap;
        }

        // ── Stats: CSV-Export ──────────────────────────────────────────
        private void ExportCsv()
        {
            RecalcStats();
            var snap = _stats;
            if (snap == null)
                return;

            try
            {
                // Direkt in den Downloads-Ordner des Nutzers.
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "Downloads");
                Directory.CreateDirectory(dir);

                var de = CultureInfo.GetCultureInfo("de-DE");
                var file = Path.Combine(dir,
                    $"TradeStats_{snap.From:yyyyMMdd}_{DateTime.Now:yyyyMMdd_HHmmss}.csv");

                using var w = new StreamWriter(file, false, System.Text.Encoding.UTF8);
                w.WriteLine($"# Zeiten inkl. Versatz {(_timeOffsetHours >= 0 ? "+" : "")}{_timeOffsetHours}h (IQ-Capital-Zeit)");
                w.WriteLine("OpenTime;CloseTime;Symbol;Richtung;Dauer_s;PnL_Brutto;Kommission;PnL_Netto;TicksPnL;UeberSchwelle");
                foreach (var r in snap.All.OrderBy(r => r.CloseTime))
                {
                    w.WriteLine(string.Join(";",
                        Disp(r.OpenTime).ToString("yyyy-MM-dd HH:mm:ss"),
                        Disp(r.CloseTime).ToString("yyyy-MM-dd HH:mm:ss"),
                        r.Symbol,
                        r.IsLong ? "Long" : "Short",
                        ((int)r.Duration.TotalSeconds).ToString(),
                        r.GrossPnl.ToString("0.00", de),
                        r.Commission.ToString("0.00", de),
                        (r.GrossPnl - r.Commission).ToString("0.00", de),
                        r.TicksPnl.ToString("0.00", de),
                        r.Compliant ? "ja" : "nein"));
                }

                w.WriteLine();
                w.WriteLine($"Zeitraum ab;{snap.From:yyyy-MM-dd HH:mm}");
                w.WriteLine($"Schwelle_s;{snap.MinSeconds}");
                w.WriteLine($"Trades gesamt;{snap.TotalCount}");
                w.WriteLine($"PnL gesamt;{snap.TotalPnl.ToString("0.00", de)}");
                w.WriteLine($"Trades ueber Schwelle;{snap.OverCount}");
                w.WriteLine($"PnL ueber Schwelle;{snap.OverPnl.ToString("0.00", de)}");
                w.WriteLine($"Trades unter Schwelle;{snap.UnderCount}");
                w.WriteLine($"PnL unter Schwelle;{snap.UnderPnl.ToString("0.00", de)}");
                w.WriteLine($"Gewinn-Summe;{snap.WinSum.ToString("0.00", de)}");
                w.WriteLine($"Verlust-Summe;{snap.LossSum.ToString("0.00", de)}");
                w.WriteLine($"Kommission gesamt;{snap.CommissionTotal.ToString("0.00", de)}");
                w.WriteLine($"PnL netto (mit Kommission);{(_subtractCommission ? "ja" : "nein")}");

                _lastCsvPath = file;
                _lastError = null;
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
            }
        }

        // ── Rendering ──────────────────────────────────────────────────
        protected override void OnRender(RenderContext context, DrawingLayouts layout)
        {
            if (_showStatsPanel)
                DrawStatsPanel(context);
            DrawTimer(context);
        }

        // Live-Timer als runde Pill mit Fortschritts-Ring zur Scalping-Schwelle.
        private void DrawTimer(RenderContext context)
        {
            if (_entryTime == null || _font == null)
                return;

            var now = _lastMarketTime == DateTime.MinValue ? DateTime.UtcNow : _lastMarketTime;
            var elapsed = now - _entryTime.Value;
            if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;

            double totalSeconds = elapsed.TotalSeconds;
            int threshold = _timerScalpWarn ? _minTradeSeconds : _targetSeconds;
            bool isAbove = totalSeconds >= threshold;
            double frac = threshold > 0 ? Math.Clamp(totalSeconds / threshold, 0, 1) : 1;

            var ring = isAbove ? CGreen : CRed;
            var textColor = isAbove ? _colorAbove : _colorBelow;
            string warn = (_timerScalpWarn && !isAbove) ? "! " : "";
            string timeText = $"{warn}{(int)elapsed.TotalMinutes:D2}:{elapsed.Seconds:D2}";

            var ts = context.MeasureString(timeText, _font);
            int d = ts.Height + 12;
            const int padX = 10, padY = 7, gap = 10;
            int boxH = d + padY * 2;
            int boxW = padX + d + gap + ts.Width + padX;

            int posX = _bottomLeft ? _offsetX : context.ClipBounds.Width - boxW - _offsetX;
            int posY = context.ClipBounds.Height - boxH - _offsetY;

            var rect = new Rectangle(posX, posY, boxW, boxH);
            context.FillRectangle(CBg, rect, 12);
            context.DrawRectangle(new RenderPen(Color.FromArgb(70, ring.R, ring.G, ring.B), 3), rect, 12);
            context.DrawRectangle(new RenderPen(ring, 1), rect, 12);

            int cx = posX + padX + d / 2, cy = posY + boxH / 2;
            DrawRing(context, cx, cy, d / 2, d / 2 - 4, frac, ring, CBg);

            context.DrawString(timeText, _font, textColor,
                new Rectangle(posX + padX + d + gap, posY, ts.Width + 6, boxH), FmtMidLeft);
        }

        // Fortschritts-Ring: Track-Scheibe, Fortschritts-Tortenstueck, Mitte ausstanzen.
        private void DrawRing(RenderContext context, int cx, int cy, int r, int rin,
                              double frac, Color color, Color hole)
        {
            context.FillEllipse(CTrack, new Rectangle(cx - r, cy - r, r * 2, r * 2));
            if (frac > 0)
                context.FillPie(color, new Rectangle(cx - r, cy - r, r * 2, r * 2),
                                -90f, (float)(Math.Clamp(frac, 0, 1) * 360));
            context.FillEllipse(hole, new Rectangle(cx - rin, cy - rin, rin * 2, rin * 2));
        }

        private static readonly string ColHeader =
            $"{"Datum/Zeit",-14} {"Sym",-6} {"R",-1} {"Dauer",6} {"PnL",10} {"%",6}";
        private const string Sep = "──────────────────────────────────────────────";

        private void DrawStatsPanel(RenderContext context)
        {
            var snap = _stats;
            if (snap == null || _statsFont == null)
                return;

            var de = CultureInfo.GetCultureInfo("de-DE");

            // ── Kennzahlen ────────────────────────────────────────────────
            decimal overWinShare = snap.WinSum != 0 ? snap.OverWinSum / snap.WinSum * 100m : 0m;
            decimal underWinShare = snap.WinSum != 0 ? snap.UnderWinSum / snap.WinSum * 100m : 0m;
            decimal netTotal = snap.GrossSum - snap.CommissionTotal;
            decimal np = Math.Max(0m, netTotal);
            // Kontogroesse aus der Konto-ID (50K/100K/200K) -> Cap & Target automatisch.
            int sizeK = DetectSizeK(snap.AccountLabel);
            decimal effCap = (_autoTier && sizeK > 0)
                ? (sizeK == 50 ? 1500m : sizeK == 100 ? 2000m : sizeK == 200 ? 2500m : _maxPayout)
                : _maxPayout;
            decimal effTarget = (_autoTier && sizeK > 0) ? sizeK * 60m : _profitTarget; // 6% der Groesse
            // IQ: pro Auszahlung max. 50% der Profite -> davon 90% (Split), gedeckelt am Account-Cap.
            decimal basis = np * _withdrawCapPct / 100m;
            decimal allow = Math.Min(basis, _hundredPctAllowance);        // optionale 100%-Stufe
            decimal payoutEst = allow + (basis - allow) * _profitSplitPct / 100m;
            if (effCap > 0) payoutEst = Math.Min(payoutEst, effCap);

            // Kontotyp: Auto = an der Konto-ID erkennen
            // (IQFEV.. = Evaluation, IQFIF.. = Instant Funding, sonst Funded)
            bool isEval, isInstant;
            switch (_accountType)
            {
                case AccountKind.Evaluation: isEval = true; isInstant = false; break;
                case AccountKind.Funded: isEval = false; isInstant = false; break;
                case AccountKind.InstantFunding: isEval = false; isInstant = true; break;
                default:
                    isEval = !string.IsNullOrEmpty(_evalKeyword)
                             && snap.AccountLabel.IndexOf(_evalKeyword, StringComparison.OrdinalIgnoreCase) >= 0;
                    isInstant = !isEval
                                && snap.AccountLabel.IndexOf("IF", StringComparison.OrdinalIgnoreCase) >= 0;
                    break;
            }
            // Eval -> Profit-Target; Funded UND Instant Funding -> Payout-Check.
            string kindLabel = (isEval ? "Evaluation" : (isInstant ? "Instant Funding" : "Funded"))
                               + (sizeK > 0 ? $" {sizeK}k" : "");

            decimal thr = _payoutThresholdPct;
            decimal tradeShareOver = snap.TotalCount > 0 ? snap.OverCount * 100m / snap.TotalCount : 0m;
            decimal bestDayShare = netTotal > 0 ? snap.BestDayPnl / netTotal * 100m : 0m;
            // Consistency: noetiger Gesamtgewinn = bester Tag / Consistency% (IQ-Formel)
            decimal consRequired = (snap.BestDayPnl > 0m && _consistencyPct > 0)
                ? snap.BestDayPnl / (_consistencyPct / 100m) : 0m;
            decimal targetShare = effTarget > 0 ? np / effTarget * 100m : 100m;
            bool tradeOk = tradeShareOver >= thr;
            bool profitOk = overWinShare >= thr;
            bool consistencyOk = netTotal <= 0 || bestDayShare <= _consistencyPct;
            bool sessionsOk = snap.DistinctDays >= _requiredSessions;
            bool targetOk = effTarget <= 0 || np >= effTarget;
            bool payoutOk = tradeOk && profitOk && consistencyOk && sessionsOk;
            bool statusOk = isEval ? targetOk : payoutOk;
            int fundedMissing = (!tradeOk ? 1 : 0) + (!profitOk ? 1 : 0) + (!consistencyOk ? 1 : 0) + (!sessionsOk ? 1 : 0);
            int meterCount = isEval ? 3 : 4;   // Eval: Trades,Profite,Target | Funded: +Best-Day,Sessions

            // "Was fehlt"-Zeilen je nach Kontotyp
            var fehlt = new List<string>();
            if (!tradeOk) fehlt.Add($"noch {Math.Max(0, snap.TotalCount - 2 * snap.OverCount)} Trades >{snap.MinSeconds}s");
            if (!profitOk) fehlt.Add($"noch {Money(Math.Max(0m, snap.WinSum - 2 * snap.OverWinSum), de)} Profit aus >{snap.MinSeconds}s");
            if (isEval)
            {
                if (!targetOk) fehlt.Insert(0, $"noch {Money(effTarget - np, de)} bis Profit-Target");
            }
            else
            {
                if (!consistencyOk) fehlt.Add($"Best-Day über Consistency ({_consistencyPct}%)");
                if (!sessionsOk) fehlt.Add($"noch {_requiredSessions - snap.DistinctDays} aktive Sessions");
            }

            // ── Detailteil (Text-Tabelle, scrollbar) ──────────────────────
            var body = new List<(string text, Color color)>();
            if (!_collapsed)
            {
                AddSection(body, $"< {snap.MinSeconds}s", snap.UnderCount, snap.UnderWinSum,
                           underWinShare, snap.UnderWinners, snap.WinSum, de, CMuted, CGreen);
                AddSection(body, $"> {snap.MinSeconds}s", snap.OverCount, snap.OverWinSum,
                           overWinShare, snap.OverWinners, snap.WinSum, de, CMuted, CGreen);
                if (_lastCsvPath != null) { body.Add((Sep, CDim)); body.Add(($"CSV: {_lastCsvPath}", CDim)); }
                if (_lastError != null) body.Add(($"Hinweis: {Trunc(_lastError, 80)}", CRed));
            }

            // ── Masse & Kopf-Strings ──────────────────────────────────────
            const int padX = 14, padYt = 12, padYb = 12;
            int lineH = context.MeasureString("0", _statsFont).Height + 4;
            int sH = context.MeasureString("0", _statsFontSmall).Height + 2;
            int bigH = context.MeasureString("0", _statsFontBig).Height;
            int divH = 11;
            int meterH = lineH + 16;
            int pillH = lineH + 12;
            int splitH = sH + 26;            // Profit-Split-Leiste

            string strDate = $"ab {snap.From:dd.MM.yy}{(_timeOffsetHours != 0 ? $"  ·  Zeit {(_timeOffsetHours >= 0 ? "+" : "")}{_timeOffsetHours}h" : "")}  ·  {kindLabel}";
            string strHero = $"{Money(netTotal, de)} €";
            string strGV = $"Gewinne {Money(snap.WinSum, de)}     Verluste {Money(snap.LossSum, de)}";
            string strBrutto = $"Brutto {Money(snap.GrossSum, de)}   ·   Kommission {Money(-snap.CommissionTotal, de)}";
            string strAusz = $"Auszahlbar ~{Money(payoutEst, de)}   ·   max {_withdrawCapPct}%·{_profitSplitPct}%{(effCap > 0 ? $"  Cap {Money(effCap, de)}" : "")}";
            // Scalping wie im IQ-Dialog: aktuell / Minimum (Minimum = Schwelle% von Gesamt)
            int minTrades = (int)Math.Ceiling((double)(thr / 100m) * snap.TotalCount);
            decimal minProfit = thr / 100m * snap.WinSum;
            string mTrades = $"{snap.OverCount} / Soll {minTrades} · {tradeShareOver.ToString("0.0", de)}%";
            string mProfit = $"{Money(snap.OverWinSum, de)} / Soll {Money(minProfit, de)} · {overWinShare.ToString("0.0", de)}%";

            // Kopf (immer sichtbar): Titel, Datum, Hero-Block (Netto, Zahl, G/V, Brutto/Komm, Auszahlbar)
            int heroBlock = sH + bigH + lineH + sH + sH;
            int headerH = padYt + lineH + sH + divH + heroBlock + divH
                + pillH + meterCount * meterH + (isEval ? 0 : sH)   // +Consistency-Zeile (Funded)
                + splitH + fehlt.Count * lineH + 6;

            // Breite: groesste benoetigte Zeile (verhindert Ueberlappung/Ueberlauf)
            int innerW = 360;
            innerW = Math.Max(innerW, context.MeasureString(strHero, _statsFontBig).Width + 8);
            innerW = Math.Max(innerW, context.MeasureString($"{(_collapsed ? "▸" : "▾")} TRADE TIMER", _statsFontBold).Width
                                       + 16 + context.MeasureString(snap.AccountLabel, _statsFontSmall).Width);
            foreach (var s in new[] { strDate, strGV, strBrutto, strAusz })
                innerW = Math.Max(innerW, context.MeasureString(s, _statsFont).Width);
            foreach (var (t, _) in body) innerW = Math.Max(innerW, context.MeasureString(t, _statsFont).Width);
            // Meter-Zeilen (Label links + Wert rechts + Status-Punkt) muessen reinpassen
            innerW = Math.Max(innerW, context.MeasureString($"Trades >{snap.MinSeconds}s", _statsFont).Width
                                       + 30 + context.MeasureString(mTrades, _statsFont).Width + 14);
            innerW = Math.Max(innerW, context.MeasureString($"Profite >{snap.MinSeconds}s", _statsFont).Width
                                       + 30 + context.MeasureString(mProfit, _statsFont).Width + 14);
            int boxW = innerW + padX * 2;
            _lastBoxW = boxW;

            int posX = context.ClipBounds.Width - boxW - _statsOffsetX;
            int posY = _statsOffsetY;
            if (posX < 0) posX = 0;
            if (posY < 0) posY = 0;

            // ── Scroll-Viewport fuer den Detailteil ───────────────────────
            int bodyTop = posY + headerH;
            int availBody = context.ClipBounds.Height - bodyTop - padYb - 4;
            int bodyCap = Math.Max(1, availBody / lineH);
            bool scrollable = body.Count > bodyCap;

            var drawBody = new List<(string text, Color color)>();
            if (_collapsed)
            {
                _scrollOffset = 0;
            }
            else if (!scrollable)
            {
                _scrollOffset = 0;
                drawBody.AddRange(body);
            }
            else
            {
                int maxOffset = Math.Max(0, body.Count - (bodyCap - 1));
                _scrollOffset = Math.Clamp(_scrollOffset, 0, maxOffset);
                bool top = _scrollOffset > 0;
                int rows = bodyCap - (top ? 1 : 0);
                bool more = _scrollOffset + rows < body.Count;
                if (more) rows -= 1;
                int end = Math.Min(body.Count, _scrollOffset + rows);
                if (top) drawBody.Add(($"  ▲ {_scrollOffset} weitere oben", CDim));
                for (int i = _scrollOffset; i < end; i++) drawBody.Add(body[i]);
                int restN = body.Count - end;
                if (restN > 0) drawBody.Add(($"  ▼ {restN} weitere unten", CDim));
            }

            int boxH = headerH + drawBody.Count * lineH + padYb;

            // ── Panel-Hintergrund ─────────────────────────────────────────
            _panelRect = new Rectangle(posX, posY, boxW, boxH);
            _headerHitRect = new Rectangle(posX, posY, boxW, headerH);
            context.FillRectangle(CBg, _panelRect, 10);
            context.DrawRectangle(_penGlow, _panelRect, 10);                 // weicher Neon-Glow
            context.DrawRectangle(_penAccent, _panelRect, 10);               // scharfe Cyan-Kante

            int x = posX + padX;
            int y = posY + padYt;

            // Titel auf Gradient-Leiste
            context.FillRectangle(CTitle1, CTitle2, new Rectangle(posX + 3, posY + 3, boxW - 6, lineH + padYt - 5));
            context.DrawString($"{(_collapsed ? "▸" : "▾")} TRADE TIMER", _statsFontBold, CAccent, x, y);
            context.DrawString(snap.AccountLabel, _statsFontSmall, CDim,
                new Rectangle(x, y, innerW, lineH), FmtRight);
            y += lineH;
            context.DrawString(strDate, _statsFontSmall, CDim, x, y); y += sH;
            y += divH / 2; context.DrawLine(_penDivider, x, y, x + innerW, y); y += divH - divH / 2;

            // Hero: Netto gross; darunter Gewinne/Verluste, Brutto/Kommission, Auszahlbar (alles linksbuendig)
            context.DrawString("Netto-PnL", _statsFontSmall, CDim, x, y); y += sH;
            context.DrawString(strHero, _statsFontBig, netTotal >= 0 ? CGreenHi : CRedHi, x, y); y += bigH;
            {
                int gx = x;
                gx += DrawSeg(context, "Gewinne ", _statsFont, CMuted, gx, y);
                gx += DrawSeg(context, Money(snap.WinSum, de), _statsFont, CGreenHi, gx, y);
                gx += DrawSeg(context, "     Verluste ", _statsFont, CMuted, gx, y);
                DrawSeg(context, Money(snap.LossSum, de), _statsFont, CRedHi, gx, y);
            }
            y += lineH;
            context.DrawString(strBrutto, _statsFontSmall, CMuted, x, y); y += sH;
            context.DrawString(strAusz, _statsFontSmall, CViolet, x, y); y += sH;
            y += divH / 2; context.DrawLine(_penDivider, x, y, x + innerW, y); y += divH - divH / 2;

            // Status-Pille: Evaluation = Profit-Target, Funded = Payout (farbig, mit Glow)
            var pill = new Rectangle(x, y, innerW, pillH - 6);
            int prad = (pillH - 6) / 2;
            var pillCol = statusOk ? CGreenHi : (isEval ? CAmberHi : CRedHi);
            var pillGlow = statusOk ? _penGlowGreen : (isEval ? _penGlowAmber : _penGlowRed);
            string pillText = isEval
                ? (targetOk ? "EVALUATION · ZIEL ERREICHT" : $"EVALUATION · noch {Money(effTarget - np, de)}")
                : (payoutOk ? "PAYOUT ERLAUBT" : $"PAYOUT GESPERRT · {fundedMissing} offen");
            context.DrawRectangle(pillGlow,
                new Rectangle(pill.X - 1, pill.Y - 1, pill.Width + 2, pill.Height + 2), prad + 1);
            context.FillRectangle(pillCol, pill, prad);
            context.DrawRectangle(_penHighlight,
                new Rectangle(pill.X + 2, pill.Y + 1, pill.Width - 4, pill.Height - 2), prad);
            context.DrawString(pillText, _statsFontBold, CPillText, pill, FmtCenter);
            y += pillH;

            // Meter: Scalping immer; dann Eval = Profit-Target, Funded = Best-Day + Sessions
            DrawMeter(context, x, y, innerW, lineH, $"Trades >{snap.MinSeconds}s",
                mTrades, (double)(tradeShareOver / 100m), (double)(thr / 100m), tradeOk); y += meterH;
            DrawMeter(context, x, y, innerW, lineH, $"Profite >{snap.MinSeconds}s",
                mProfit, (double)(overWinShare / 100m), (double)(thr / 100m), profitOk); y += meterH;
            if (isEval)
            {
                DrawMeter(context, x, y, innerW, lineH, "Profit-Target",
                    $"{Money(np, de)} / {Money(effTarget, de)} · {targetShare.ToString("0.0", de)}%",
                    (double)(targetShare / 100m), 1.0, targetOk); y += meterH;
            }
            else
            {
                DrawMeter(context, x, y, innerW, lineH, "Best-Day",
                    $"{Money(snap.BestDayPnl, de)} · {bestDayShare.ToString("0.0", de)}%",
                    (double)(bestDayShare / 100m), (double)(_consistencyPct / 100m), consistencyOk); y += meterH;
                // Consistency: noetiger Gesamtgewinn (Best-Day / Consistency%)
                string consLine = consRequired > 0m
                    ? $"  Consistency: nötig {consRequired.ToString("#,##0.00", de)} €  ·  aktuell {Money(netTotal, de)}"
                    : "  Consistency: noch kein Gewinn-Tag";
                context.DrawString(consLine, _statsFontSmall, consistencyOk ? CGreen : CAmber, x, y); y += sH;
                DrawSessionsRow(context, x, y, innerW, lineH, _requiredSessions, snap.DistinctDays, sessionsOk); y += meterH;
            }

            // Profit-Split >15s / <15s (beide Summen + beide Prozente)
            DrawSplitBar(context, x, y, innerW, sH, snap.MinSeconds,
                         snap.OverWinSum, overWinShare, snap.UnderWinSum, underWinShare, de); y += splitH;

            // Was fehlt
            foreach (var f in fehlt)
            {
                context.DrawString($"› {f}", _statsFontSmall, CAmber, x, y); y += lineH;
            }

            // Detail-Tabelle (Text, scrollbar)
            y = bodyTop;
            foreach (var (text, color) in drawBody)
            {
                context.DrawString(text, _statsFont, color, x, y);
                y += lineH;
            }
        }

        // Zeichnet ein Text-Segment und gibt seine Breite zurueck (fuer linksbuendige Mehrfarb-Zeilen).
        private int DrawSeg(RenderContext context, string s, RenderFont font, Color col, int x, int y)
        {
            context.DrawString(s, font, col, x, y);
            return context.MeasureString(s, font).Width;
        }

        // Fortschrittsbalken: Label links, Wert (neutral weiss) rechts, Status-Punkt, 50%-Kerbe.
        private void DrawMeter(RenderContext context, int x, int y, int w, int lineH,
                               string label, string val, double frac, double tick, bool ok)
        {
            context.DrawString(label, _statsFont, CMuted, x, y);
            context.DrawString(val, _statsFont, CValue, new Rectangle(x, y, w - 14, lineH), FmtRight);
            context.FillEllipse(ok ? CGreenHi : CRedHi, new Rectangle(x + w - 9, y + lineH / 2 - 4, 8, 8));
            int ty = y + lineH + 3, bh = 8;
            context.FillRectangle(CTrack, new Rectangle(x, ty, w, bh), bh / 2);
            int fw = (int)(w * Math.Clamp(frac, 0, 1));
            if (fw > 0)
                context.FillRectangle(ok ? CGreenHi : CRedHi, ok ? CGreenLo : CRedLo,
                    new Rectangle(x, ty, fw, bh));
            int tx = x + (int)(w * Math.Clamp(tick, 0, 1));
            context.DrawLine(_penTick, tx, ty - 3, tx, ty + bh + 3);
        }

        // Segmentierte Leiste: Gewinne >15s (gruen) vs <15s (bernstein), beide mit EUR + %.
        private void DrawSplitBar(RenderContext context, int x, int y, int w, int sH, int minSec,
                                  decimal overSum, decimal overShare, decimal underSum, decimal underShare,
                                  CultureInfo de)
        {
            context.DrawString(
                $"Profit-Split  >{minSec}s {Money(overSum, de)}  ·  <{minSec}s {Money(underSum, de)}",
                _statsFontSmall, CMuted, x, y);
            int by = y + sH + 2, bh = 18;
            context.FillRectangle(CTrack, new Rectangle(x, by, w, bh), bh / 2);
            int ow = (int)(w * Math.Clamp((double)(overShare / 100m), 0, 1));
            int uw = Math.Min(w - ow, (int)(w * Math.Clamp((double)(underShare / 100m), 0, 1)));
            if (ow > 0) context.FillRectangle(CGreenHi, CGreenLo, new Rectangle(x, by, ow, bh));
            if (uw > 0) context.FillRectangle(CAmberHi, CAmberLo, new Rectangle(x + ow, by, uw, bh));
            if (ow > 44)
                context.DrawString($"{overShare.ToString("0.0", de)}%", _statsFontSmall, CPillText,
                    new Rectangle(x, by, ow, bh), FmtCenter);
            if (uw > 44)
                context.DrawString($"{underShare.ToString("0.0", de)}%", _statsFontSmall, CPillText,
                    new Rectangle(x + ow, by, uw, bh), FmtCenter);
        }

        // Session-Reihe: Punkte, gefuellt (mit Glow) = aktive Tage.
        private void DrawSessionsRow(RenderContext context, int x, int y, int w, int lineH,
                                     int total, int filled, bool ok)
        {
            context.DrawString("Sessions", _statsFont, CMuted, x, y);
            context.DrawString($"{filled}/{total}", _statsFont, ok ? CGreenHi : CRedHi,
                new Rectangle(x, y, w, lineH), FmtRight);
            int n = Math.Max(1, total);
            int d = 11;
            int gap = n > 1 ? Math.Max(2, (w - n * d) / (n - 1)) : 0;
            int ty = y + lineH + 2;
            for (int i = 0; i < n; i++)
            {
                int cxp = x + i * (d + gap);
                if (i < filled)
                {
                    context.FillEllipse(CGlow, new Rectangle(cxp - 2, ty - 2, d + 4, d + 4)); // Glow
                    context.FillEllipse(CAccent, new Rectangle(cxp, ty, d, d));
                }
                else
                {
                    context.FillEllipse(CTrack, new Rectangle(cxp, ty, d, d));
                }
            }
        }

        private void AddSection(List<(string, Color)> lines, string label, int count,
            decimal winSum, decimal winShare, List<TradeRow> winners, decimal totalWins,
            CultureInfo de, Color grey, Color green)
        {
            int winCount = winners.Count;
            lines.Add((Sep, grey));
            // Nur Gewinne (Gewinner-Trades) ausweisen; Anzahl = Gewinner, nicht Gesamt.
            lines.Add(($"{label}:  Gewinne {Money(winSum, de)} ({winShare.ToString("0.0", de)}%)  ·  {winCount} Trades",
                       green));
            if (winCount == 0)
            {
                lines.Add(("  (keine Gewinn-Trades)", grey));
                return;
            }
            lines.Add((ColHeader, grey));
            foreach (var r in winners)   // ALLE Gewinner vollstaendig (Scroll uebernimmt die Begrenzung)
            {
                decimal s = totalWins != 0 ? r.Pnl / totalWins * 100m : 0m;
                lines.Add((
                    $"{Disp(r.CloseTime):dd.MM HH:mm:ss} " +
                    $"{Trunc(r.Symbol, 6),-6} " +
                    $"{(r.IsLong ? "L" : "S"),-1} " +
                    $"{Dur(r.Duration),6} " +
                    $"{Money(r.Pnl, de),10} " +
                    $"{s.ToString("0.0", de),5}%", CRowText));   // neutrale Zeile (weniger Grün)
            }
        }

        // ── Maus: Klick = ein/aus, Drag an der Kopfzeile = verschieben ────
        public override bool ProcessMouseDown(OFT.Rendering.Control.RenderControlMouseEventArgs e)
        {
            if (_showStatsPanel && _stats != null
                && e.Button == OFT.Rendering.Control.RenderControlMouseButtons.Left
                && _headerHitRect.Contains(e.X, e.Y))
            {
                _dragging = true;
                _didDrag = false;
                _dragStartX = e.X;
                _dragStartY = e.Y;
                _dragOrigOffX = _statsOffsetX;
                _dragOrigOffY = _statsOffsetY;
                return true;
            }
            return base.ProcessMouseDown(e);
        }

        public override bool ProcessMouseMove(OFT.Rendering.Control.RenderControlMouseEventArgs e)
        {
            _lastMouseX = e.X;
            _lastMouseY = e.Y;
            if (_dragging)
            {
                int dx = e.X - _dragStartX;
                int dy = e.Y - _dragStartY;
                if (Math.Abs(dx) + Math.Abs(dy) > 3)
                    _didDrag = true;
                // Offset X ist Abstand vom RECHTEN Rand -> Bewegung nach rechts verkleinert ihn.
                _statsOffsetX = Math.Max(0, _dragOrigOffX - dx);
                _statsOffsetY = Math.Max(0, _dragOrigOffY + dy);
                RedrawChart();
                return true;
            }
            return base.ProcessMouseMove(e);
        }

        public override bool ProcessMouseUp(OFT.Rendering.Control.RenderControlMouseEventArgs e)
        {
            if (_dragging)
            {
                _dragging = false;
                if (!_didDrag)           // reiner Klick (ohne Bewegung) -> auf/zu
                    _collapsed = !_collapsed;
                RedrawChart();
                return true;
            }
            return base.ProcessMouseUp(e);
        }

        // Mausrad ueber dem Panel scrollt den Detailteil.
        public override bool ProcessMouseWheel(int delta)
        {
            if (_showStatsPanel && !_collapsed && _stats != null
                && _panelRect.Contains(_lastMouseX, _lastMouseY))
            {
                _scrollOffset += delta > 0 ? -2 : 2;   // Rad hoch = nach oben
                if (_scrollOffset < 0) _scrollOffset = 0;
                RedrawChart();
                return true;
            }
            return base.ProcessMouseWheel(delta);
        }

        private DateTime Disp(DateTime t) => t.AddHours(_timeOffsetHours);

        // Kontogroesse (in 1000) aus der Konto-ID lesen: IQF..200K/100K/50K..
        private static int DetectSizeK(string acc)
        {
            if (string.IsNullOrEmpty(acc)) return 0;
            if (acc.IndexOf("200K", StringComparison.OrdinalIgnoreCase) >= 0) return 200;
            if (acc.IndexOf("100K", StringComparison.OrdinalIgnoreCase) >= 0) return 100;
            if (acc.IndexOf("50K", StringComparison.OrdinalIgnoreCase) >= 0) return 50;
            if (acc.IndexOf("25K", StringComparison.OrdinalIgnoreCase) >= 0) return 25;
            return 0;
        }

        // Richtung robust bestimmen: Entry-/Exit-Trade, sonst aus Preisbewegung
        // vs. PricePnL (ohne Kommission). Verhindert "immer Long" wenn die
        // gematchten Trades keine Enter/Exit-Referenz tragen.
        private static bool DetectLong(HistoryMyTrade t)
        {
            if (t.EnterTrade != null) return t.EnterTrade.OrderDirection != OrderDirections.Sell;
            if (t.ExitTrade != null) return t.ExitTrade.OrderDirection == OrderDirections.Sell;
            if (t.ClosePrice != t.OpenPrice)
                return Math.Sign(t.PricePnL) == Math.Sign(t.ClosePrice - t.OpenPrice);
            return true;
        }

        private static string Money(decimal v, CultureInfo c) => v.ToString("+0.00;-0.00;0.00", c);

        private static string Dur(TimeSpan t)
            => t.TotalHours >= 1
                ? $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}"
                : $"{t.Minutes:D2}:{t.Seconds:D2}";

        private static string Trunc(string s, int max)
            => string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s.Substring(0, max));

        // ── Datenklassen ───────────────────────────────────────────────
        private sealed class TradeRow
        {
            public DateTime OpenTime;
            public DateTime CloseTime;
            public string Symbol = "";
            public bool IsLong;
            public TimeSpan Duration;
            public decimal Pnl;          // gemaess Einstellung netto oder brutto
            public decimal GrossPnl;     // immer brutto (ohne Kommission)
            public decimal Commission;
            public decimal TicksPnl;
            public bool Compliant;
        }

        private sealed class StatsSnapshot
        {
            public readonly List<TradeRow> All = new();            // alle Trades, fuer CSV
            public readonly List<TradeRow> UnderWinners = new();   // <min, nur Gewinner
            public readonly List<TradeRow> OverWinners = new();    // >=min, nur Gewinner
            public int UnderCount, OverCount;                      // alle Trades je Gruppe
            public decimal UnderPnl, OverPnl;                      // netto je Gruppe (inkl. Verluste)
            public decimal UnderWinSum, UnderLossSum;              // G/V getrennt je Gruppe
            public decimal OverWinSum, OverLossSum;
            public decimal TotalPnl;      // gemaess Toggle (netto oder brutto)
            public decimal GrossSum;      // immer brutto
            public int TotalCount;
            public decimal WinSum;
            public decimal LossSum;
            public decimal CommissionTotal;
            public int DistinctDays;        // aktive Handelstage (Sessions)
            public decimal BestDayPnl;      // bester einzelner Handelstag (netto)
            public DateTime From;
            public int MinSeconds;
            public string AccountLabel = "";
        }
    }
}
