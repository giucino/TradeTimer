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
using OFT.Attributes.Editors;
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
        private bool _autoLocalTime = true;          // ATAS-UTC -> lokale Zeit (Sommer/Winter automatisch)
        private int _timeOffsetHours = 2;            // manueller Versatz (nur wenn Auto aus)
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
        private bool _collapsed;                  // Klick auf Titel klappt Detail auf/zu
        private int _activeTab = 1;               // 0=Übersicht 1=Payout 2=Trades
        private readonly Rectangle[] _tabRects = new Rectangle[3];
        private Rectangle _titleHitRect;          // Titelzeile -> Collapse
        private Rectangle _headerHitRect;         // gesamter Kopf -> Drag
        private Rectangle _panelRect;             // gesamtes Panel (Hit-Test fuer Wheel)
        private int _scrollOffset;                // Scroll-Position im Detailteil (in Zeilen)
        private bool _dragging;                   // Panel wird gerade per Drag verschoben
        private bool _didDrag;                    // zwischen Down/Up tatsaechlich bewegt
        private int _dragStartX, _dragStartY;     // Mausposition bei Drag-Start
        private int _dragOrigOffX, _dragOrigOffY; // Panel-Offsets bei Drag-Start
        private int _lastBoxW;                     // Panelbreite aus letztem Render (Clamping)
        private int _lastMouseX, _lastMouseY;      // letzte Mausposition (fuer Wheel-Hit-Test)

        // ── Schwebendes Fenster (Variante A: zusaetzlich zum Chart-Panel) ──
        private bool _showWindow;                  // Fenster oeffnen
        private bool _windowTopmost = true;        // immer im Vordergrund
        private PanelWindow? _window;              // WPF-Fenster (auf UI-Thread)
        private int _winTab = 1;                   // aktiver Reiter im Fenster (eigener Zustand)
        private int _winScroll;                    // Trades-Scroll im Fenster (eigener Zustand)
        private int _winBoxW, _winBoxH;            // gerenderte Panel-Groesse (Fenster)
        private readonly Rectangle[] _winTabRects = new Rectangle[3];
        private Rectangle _winHeaderRect;          // Kopf-Trefferbereich im Fenster (Drag)
        private Rectangle _winCloseRect;           // X-Trefferbereich im Fenster
        private int _winLastTick;                  // Render-Drossel (ms)

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
        [Tab(TabName = "Timer", TabOrder = 1)]
        [Display(Name = "Ziel-Sekunden", GroupName = "Timer", Order = 101,
                 Description = "Schwelle fuer das Timer-HUD: bis zu diesem Wert ist die Anzeige rot, " +
                               "ab diesem Wert gruen. Steuert nur die Farbe des Haltedauer-Timers. (Standard: 15)")]
        [Range(1, 3600)]
        public int TargetSeconds
        {
            get => _targetSeconds;
            set { _targetSeconds = Math.Max(1, value); RedrawChart(); }
        }

        [Tab(TabName = "Timer", TabOrder = 1)]
        [Display(Name = "Schriftgröße", GroupName = "Timer", Order = 103,
                 Description = "Schriftgroesse des Timer-HUD (die MM:SS-Anzeige der aktuellen Haltedauer). (Standard: 16)")]
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

        [Tab(TabName = "Timer", TabOrder = 1)]
        [Display(Name = "Farbe unter Ziel", GroupName = "Farben", Order = 110,
                 Description = "Textfarbe des Timers, solange die Haltedauer UNTER den Ziel-Sekunden liegt.")]
        public Color ColorBelow
        {
            get => _colorBelow;
            set { _colorBelow = value; RedrawChart(); }
        }

        [Tab(TabName = "Timer", TabOrder = 1)]
        [Display(Name = "Farbe über Ziel", GroupName = "Farben", Order = 111,
                 Description = "Textfarbe des Timers, sobald die Haltedauer die Ziel-Sekunden ERREICHT/ueberschreitet.")]
        public Color ColorAbove
        {
            get => _colorAbove;
            set { _colorAbove = value; RedrawChart(); }
        }

        [Tab(TabName = "Timer", TabOrder = 1)]
        [Display(Name = "Hintergrundfarbe", GroupName = "Farben", Order = 112,
                 Description = "Hintergrundfarbe der Timer-Box (mit Transparenz/Alpha).")]
        public Color ColorBackground
        {
            get => _colorBackground;
            set { _colorBackground = value; RedrawChart(); }
        }

        [Tab(TabName = "Timer", TabOrder = 1)]
        [Display(Name = "Unten Links (aus = Unten Rechts)", GroupName = "Position", Order = 120,
                 Description = "Ecke des Timer-HUD: An = unten links, Aus = unten rechts. " +
                               "Betrifft nur den Haltedauer-Timer, nicht das Statistik-Panel.")]
        public bool BottomLeft
        {
            get => _bottomLeft;
            set { _bottomLeft = value; RedrawChart(); }
        }

        [Tab(TabName = "Timer", TabOrder = 1)]
        [Display(Name = "Abstand vom Rand X (px)", GroupName = "Position", Order = 121,
                 Description = "Horizontaler Abstand des Timer-HUD vom Chartrand in Pixeln. (Standard: 20)")]
        [Range(0, 500)]
        public int OffsetX
        {
            get => _offsetX;
            set { _offsetX = value; RedrawChart(); }
        }

        [Tab(TabName = "Timer", TabOrder = 1)]
        [Display(Name = "Abstand vom Rand Y (px)", GroupName = "Position", Order = 122,
                 Description = "Vertikaler Abstand des Timer-HUD vom unteren Chartrand in Pixeln. (Standard: 20)")]
        [Range(0, 500)]
        public int OffsetY
        {
            get => _offsetY;
            set { _offsetY = value; RedrawChart(); }
        }

        [Tab(TabName = "Timer", TabOrder = 1)]
        [Display(Name = "Panel anzeigen", GroupName = "Panel", Order = 401,
                 Description = "Blendet das Statistik-Panel (oben rechts) ein/aus. Der Haltedauer-Timer " +
                               "bleibt davon unberuehrt. Im Chart: Klick auf die Kopfzeile = auf/zu, " +
                               "Kopfzeile ziehen = verschieben, Mausrad = scrollen.")]
        public bool ShowStatsPanel
        {
            get => _showStatsPanel;
            set { _showStatsPanel = value; RedrawChart(); }
        }

        [Tab(TabName = "Payout", TabOrder = 3)]
        [Display(Name = "Mindestlänge (Sek.)", GroupName = "Scalping", Order = 301,
                 Description = "Scalping-Schwelle in Sekunden (IQ Capital: 15). Trades mit Haltedauer " +
                               "DARUNTER zaehlen als '<15s' (Scalp), DARUEBER als '>15s' (regelkonform). (Standard: 15)")]
        [Range(1, 3600)]
        public int MinTradeSeconds
        {
            get => _minTradeSeconds;
            set { _minTradeSeconds = Math.Clamp(value, 1, 3600); RecalcStats(); RedrawChart(); }
        }

        [Tab(TabName = "Auswertung", TabOrder = 2)]
        [Display(Name = "Auswertung ab", GroupName = "Zeitraum", Order = 201,
                 Description = "Startzeitpunkt der Auswertung. Nur Trades, die DANACH geschlossen wurden, " +
                               "werden gezaehlt. Auf den Beginn deines Payout-Zyklus setzen.")]
        public DateTime StatsFrom
        {
            get => _statsFrom;
            set { _statsFrom = value; _historyRequested = false; RecalcStats(); RedrawChart(); }
        }

        [Tab(TabName = "Auswertung", TabOrder = 2)]
        [Display(Name = "Jetzt zurücksetzen (ab = jetzt)", GroupName = "Zeitraum", Order = 202,
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

        [Tab(TabName = "Auswertung", TabOrder = 2)]
        [Display(Name = "Alle Symbole des Kontos", GroupName = "Konto", Order = 214,
                 Description = "An = alle gehandelten Instrumente des Kontos zusammen (z.B. MNQ + NQ), " +
                               "passt zur kontoweiten IQ-Regel (empfohlen). Aus = nur das Symbol dieses Charts.")]
        public bool AccountWide
        {
            get => _accountWide;
            set { _accountWide = value; _historyRequested = false; RecalcStats(); RedrawChart(); }
        }

        [Tab(TabName = "Auswertung", TabOrder = 2)]
        [Display(Name = "Alle Konten zusammen", GroupName = "Konto", Order = 212,
                 Description = "Aus = nur das Konto dieses Charts (empfohlen bei mehreren Konten). " +
                               "An = alle ATAS-Konten zusammengezaehlt.")]
        public bool AllAccounts
        {
            get => _allAccounts;
            set { _allAccounts = value; _historyRequested = false; RecalcStats(); RedrawChart(); }
        }

        [Tab(TabName = "Auswertung", TabOrder = 2)]
        [Display(Name = "Konto-ID (leer = Chart-Konto)", GroupName = "Konto", Order = 213,
                 Description = "Optional: feste Konto-ID erzwingen. Leer = automatisch das Konto, " +
                               "das fuer diesen Chart gewaehlt ist. Wirkt nur wenn 'Alle Konten' aus ist.")]
        [VisibleWhen(nameof(AllAccounts), false)]
        public string AccountId
        {
            get => _accountId;
            set { _accountId = value?.Trim() ?? ""; _historyRequested = false; RecalcStats(); RedrawChart(); }
        }

        [Tab(TabName = "Auswertung", TabOrder = 2)]
        [Display(Name = "CSV exportieren", GroupName = "Export", Order = 230,
                 Description = "Schreibt alle Trades des Zeitraums als CSV in den Downloads-Ordner " +
                               "(Spalten: Zeit, Symbol, Richtung, Dauer, Brutto, Kommission, Netto). " +
                               "Haken setzen = exportieren; springt zurueck.")]
        public bool ExportCsvNow
        {
            get => false;
            set { if (value) { ExportCsv(); RedrawChart(); } }
        }

        [Tab(TabName = "Timer", TabOrder = 1)]
        [Display(Name = "Schriftgröße Panel", GroupName = "Panel", Order = 402,
                 Description = "Schriftgroesse des Statistik-Panels. Kleiner = mehr Zeilen passen ohne Scrollen. (Standard: 13)")]
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

        [Tab(TabName = "Timer", TabOrder = 1)]
        [Display(Name = "Panel-Abstand X (px)", GroupName = "Panel", Order = 403,
                 Description = "Horizontaler Abstand des Statistik-Panels vom RECHTEN Chartrand. " +
                               "Wird beim Verschieben per Drag automatisch aktualisiert. (Standard: 20)")]
        [Range(0, 1000)]
        public int StatsOffsetX
        {
            get => _statsOffsetX;
            set { _statsOffsetX = value; RedrawChart(); }
        }

        [Tab(TabName = "Timer", TabOrder = 1)]
        [Display(Name = "Panel-Abstand Y (px)", GroupName = "Panel", Order = 404,
                 Description = "Vertikaler Abstand des Statistik-Panels vom OBEREN Chartrand. " +
                               "Wird beim Verschieben per Drag automatisch aktualisiert. (Standard: 20)")]
        [Range(0, 1000)]
        public int StatsOffsetY
        {
            get => _statsOffsetY;
            set { _statsOffsetY = value; RedrawChart(); }
        }

        [Tab(TabName = "Timer", TabOrder = 1)]
        [Display(Name = "Als Fenster öffnen", GroupName = "Fenster", Order = 410,
                 Description = "Öffnet ZUSÄTZLICH ein schwebendes Fenster, das du frei platzieren kannst " +
                               "(auch auf einem 2. Monitor). Das Chart-Panel bleibt unabhängig davon.")]
        public bool ShowWindow
        {
            get => _showWindow;
            set { _showWindow = value; if (!value) CloseWindow(); RedrawChart(); }
        }

        [Tab(TabName = "Timer", TabOrder = 1)]
        [Display(Name = "Fenster immer im Vordergrund", GroupName = "Fenster", Order = 411,
                 Description = "Hält das schwebende Fenster über anderen Fenstern (Topmost).")]
        [VisibleWhen(nameof(ShowWindow), true)]
        public bool WindowTopmost
        {
            get => _windowTopmost;
            set { _windowTopmost = value; ApplyTopmost(); }
        }

        [Tab(TabName = "Auswertung", TabOrder = 2)]
        [Display(Name = "Lokalzeit automatisch", GroupName = "Zeit & Kosten", Order = 220,
                 Description = "An = ATAS-UTC wird in deine Systemzeitzone gewandelt (Sommer-/Winterzeit " +
                               "automatisch). Passt ganzjaehrig zu IQ Capital, kein manuelles Umstellen. " +
                               "Aus = fester 'Zeitversatz Std.' unten.")]
        public bool AutoLocalTime
        {
            get => _autoLocalTime;
            set { _autoLocalTime = value; RecalcStats(); RedrawChart(); }
        }

        [Tab(TabName = "Auswertung", TabOrder = 2)]
        [Display(Name = "Zeitversatz Std. (manuell)", GroupName = "Zeit & Kosten", Order = 221,
                 Description = "Fester Versatz zur ATAS-UTC-Zeit (z.B. +2 = MESZ Sommer, +1 = MEZ Winter). " +
                               "Nur wirksam wenn 'Lokalzeit automatisch' AUS ist. (Standard: 2)")]
        [Range(-12, 12)]
        [NumericEditor(NumericEditorTypes.TrackBar, -12.0, 12.0, Step = 1.0)]
        [VisibleWhen(nameof(AutoLocalTime), false)]
        public int TimeOffsetHours
        {
            get => _timeOffsetHours;
            set { _timeOffsetHours = Math.Clamp(value, -12, 12); RecalcStats(); RedrawChart(); }
        }

        [Tab(TabName = "Payout", TabOrder = 3)]
        [Display(Name = "Scalping-Schwelle (%)", GroupName = "Scalping", Order = 302,
                 Description = "IQ-Capital-Regel: Payout nur erlaubt, wenn >= dieser Anteil der TRADES " +
                               "laenger als die Mindestlaenge war UND >= dieser Anteil der PROFITE aus " +
                               "diesen Trades stammt. (Standard: 50)")]
        [Range(0, 100)]
        public int PayoutThresholdPct
        {
            get => _payoutThresholdPct;
            set { _payoutThresholdPct = Math.Clamp(value, 0, 100); RedrawChart(); }
        }

        [Tab(TabName = "Payout", TabOrder = 3)]
        [Display(Name = "Benötigte Sessions", GroupName = "Consistency & Sessions", Order = 310,
                 Description = "Aktive Handelstage bis zur ersten Auszahlung (IQ: 10). Das Panel zaehlt " +
                               "die unterschiedlichen Handelstage im Zeitraum und zeigt X/Soll. (Standard: 10)")]
        [Range(1, 100)]
        public int RequiredSessions
        {
            get => _requiredSessions;
            set { _requiredSessions = Math.Clamp(value, 1, 100); RedrawChart(); }
        }

        [Tab(TabName = "Payout", TabOrder = 3)]
        [Display(Name = "Consistency Best-Day (%)", GroupName = "Consistency & Sessions", Order = 311,
                 Description = "Consistency-Regel (nur Funded): der beste einzelne Handelstag darf hoechstens " +
                               "diesen Anteil am Gesamtprofit ausmachen (IQ Funded Futures: 30). (Standard: 30)")]
        [Range(1, 100)]
        public int ConsistencyPct
        {
            get => _consistencyPct;
            set { _consistencyPct = Math.Clamp(value, 1, 100); RedrawChart(); }
        }

        [Tab(TabName = "Auswertung", TabOrder = 2)]
        [Display(Name = "Cap & Target auto (Kontogröße)", GroupName = "Konto", Order = 215,
                 Description = "An = Max-Auszahlung (Cap) und Eval-Profit-Target werden automatisch aus der " +
                               "Kontogröße in der Konto-ID gesetzt: 50K→Target 3.000/Cap 1.500, " +
                               "100K→6.000/2.000, 200K→12.000/2.500. Aus = manuelle Werte im Reiter Payout.")]
        public bool AutoTier
        {
            get => _autoTier;
            set { _autoTier = value; RedrawChart(); }
        }

        [Tab(TabName = "Auswertung", TabOrder = 2)]
        [Display(Name = "Kontotyp", GroupName = "Konto", Order = 210,
                 Description = "Auto = automatisch an der Konto-ID erkannt (IQFEV.. = Evaluation, " +
                               "IQFIF.. = Instant Funding, sonst Funded). Evaluation zeigt das Profit-Target; " +
                               "Funded und Instant Funding zeigen den Payout-Check (Sessions + Consistency).")]
        public AccountKind AccountType
        {
            get => _accountType;
            set { _accountType = value; RedrawChart(); }
        }

        [Tab(TabName = "Auswertung", TabOrder = 2)]
        [Display(Name = "Evaluation-Kennung (Konto-ID)", GroupName = "Konto", Order = 211,
                 Description = "Teilzeichenfolge in der Konto-ID, die ein Evaluation-Konto kennzeichnet " +
                               "(Standard 'EV', z.B. IQFEV100K). Nur relevant bei Kontotyp = Auto.")]
        [VisibleWhen(nameof(AccountType), AccountKind.Auto)]
        public string EvalKeyword
        {
            get => _evalKeyword;
            set { _evalKeyword = value?.Trim() ?? ""; RedrawChart(); }
        }

        [Tab(TabName = "Payout", TabOrder = 3)]
        [Display(Name = "Profit-Target Eval (USD)", GroupName = "Ziel", Order = 330,
                 Description = "Profit-Ziel der Evaluation (Standard 6000; je nach Tier anpassen). " +
                               "Wird im Evaluation-Modus als Fortschritt angezeigt. Nur wirksam wenn " +
                               "'Cap & Target auto' AUS ist.")]
        [Range(0, 1000000)]
        [VisibleWhen(nameof(AutoTier), false)]
        public int ProfitTarget
        {
            get => (int)_profitTarget;
            set { _profitTarget = Math.Max(0, value); RedrawChart(); }
        }

        [Tab(TabName = "Payout", TabOrder = 3)]
        [Display(Name = "Profit-Split (%)", GroupName = "Auszahlung", Order = 320,
                 Description = "Dein Anteil am Profit fuer die Auszahlungs-Schaetzung (IQ: 90). " +
                               "Geschaetzt auszahlbar = Netto-Profit x Split (ueber dem 100%-Freibetrag). (Standard: 90)")]
        [Range(0, 100)]
        public int ProfitSplitPct
        {
            get => _profitSplitPct;
            set { _profitSplitPct = Math.Clamp(value, 0, 100); RedrawChart(); }
        }

        [Tab(TabName = "Payout", TabOrder = 3)]
        [Display(Name = "Auszahlung max % der Profite", GroupName = "Auszahlung", Order = 321,
                 Description = "IQ-Regel: pro Auszahlung max. dieser Anteil der Gesamtprofite. " +
                               "Fliesst in die 'auszahlbar'-Schaetzung ein. (Standard: 50)")]
        [Range(1, 100)]
        public int WithdrawCapPct
        {
            get => _withdrawCapPct;
            set { _withdrawCapPct = Math.Clamp(value, 1, 100); RedrawChart(); }
        }

        [Tab(TabName = "Payout", TabOrder = 3)]
        [Display(Name = "Max. Auszahlung (USD, 0=aus)", GroupName = "Auszahlung", Order = 322,
                 Description = "Account-Cap pro Auszahlung (IQ: 100k=2000, 50k=1500). Deckelt die " +
                               "'auszahlbar'-Schaetzung. 0 = kein Cap. Nur wirksam wenn 'Cap & Target auto' AUS ist.")]
        [Range(0, 1000000)]
        [VisibleWhen(nameof(AutoTier), false)]
        public int MaxPayout
        {
            get => (int)_maxPayout;
            set { _maxPayout = Math.Max(0, value); RedrawChart(); }
        }

        [Tab(TabName = "Payout", TabOrder = 3)]
        [Display(Name = "100%-Freibetrag (USD, 0=aus)", GroupName = "Auszahlung", Order = 323,
                 Description = "OPTIONAL: erste X USD der Auszahlungsbasis zu 100% (statt Split). Steht NICHT " +
                               "in den offiziellen IQ-Auszahlungsregeln (dort flat 90%); nur falls dein Konto " +
                               "das hat. 0 = aus (empfohlen).")]
        [Range(0, 1000000)]
        public int HundredPctAllowance
        {
            get => (int)_hundredPctAllowance;
            set { _hundredPctAllowance = Math.Max(0, value); RedrawChart(); }
        }

        [Tab(TabName = "Timer", TabOrder = 1)]
        [Display(Name = "Timer-Warnung unter Mindestlänge", GroupName = "Timer", Order = 102,
                 Description = "An = der Haltedauer-Timer ist rot mit '!' solange die offene Position " +
                               "unter der Scalping-Mindestlaenge ist, und wird erst darueber gruen. " +
                               "Hilft, Trades nicht zu frueh zu schliessen. Aus = nutzt 'Ziel-Sekunden'.")]
        public bool TimerScalpWarn
        {
            get => _timerScalpWarn;
            set { _timerScalpWarn = value; RedrawChart(); }
        }

        [Tab(TabName = "Auswertung", TabOrder = 2)]
        [Display(Name = "Kommission abziehen (Netto-PnL)", GroupName = "Zeit & Kosten", Order = 222,
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

        // ── Lebenszyklus: schwebendes Fenster oeffnen/schliessen ───────
        protected override void OnInitialize()
        {
            base.OnInitialize();
            // Fenster erscheint beim ersten OnRender (RenderWindow), sobald Daten da sind.
        }

        protected override void OnDispose()
        {
            CloseWindow();
            base.OnDispose();
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
                DrawStatsPanel(new AtasCanvas(context), context.ClipBounds.Width, context.ClipBounds.Height,
                               -1, -1, _collapsed, _activeTab, ref _scrollOffset);

            // Live-Timer NUR auf der Final-Ebene: die Historical-Ebene ist gecacht und
            // aktualisiert nicht je Tick -> dort bliebe ein veraltetes Pill stehen. Beim
            // Wechsel rot<->gruen aendert sich durch das '!' die Breite/Position, sodass
            // altes (rot) und neues (gruen) Pill sichtbar ueberlappen wuerden.
            if (layout == DrawingLayouts.Final)
            {
                DrawTimer(context);
                if (_showWindow)
                    RenderWindow(false);   // schwebendes Fenster aus demselben Zeichen-Code spiegeln
            }
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

        private void DrawStatsPanel(IPanelCanvas context, int viewW, int viewH,
                                    int forceX, int forceY, bool collapsed, int activeTab, ref int scrollOffset)
        {
            var snap = _stats;
            if (snap == null || _statsFont == null)
                return;
            bool windowMode = forceX >= 0;

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
            // Trade-Tabelle nur fuer den Trades-Reiter aufbauen
            bool tradesTab = activeTab == 2;
            var body = new List<(string text, Color color)>();
            if (!collapsed && tradesTab)
            {
                AddSection(body, $"< {snap.MinSeconds}s", snap.UnderCount, snap.UnderWinSum,
                           underWinShare, snap.UnderWinners, snap.WinSum, de, CMuted, CGreen);
                AddSection(body, $"> {snap.MinSeconds}s", snap.OverCount, snap.OverWinSum,
                           overWinShare, snap.OverWinners, snap.WinSum, de, CMuted, CGreen);
                if (_lastCsvPath != null) { body.Add((Sep, CDim)); body.Add(($"CSV: {Path.GetFileName(_lastCsvPath)}", CDim)); }
                if (_lastError != null) body.Add(($"Hinweis: {Trunc(_lastError, 80)}", CRed));
            }

            // ── Masse ─────────────────────────────────────────────────────
            const int padX = 16, padYt = 14, padYb = 14;
            int lineH = context.MeasureString("0", _statsFont).Height + 8;
            int sH = context.MeasureString("0", _statsFontSmall).Height + 5;
            int bigH = context.MeasureString("0", _statsFontBig).Height;
            int divH = 14, meterH = lineH + 18, badgeH = lineH + 12, tabH = lineH + 10, chipH = lineH + 6, gBlock = 12;
            int splitH = sH + 28;
            int targetMeterH = lineH * 2 + 22;   // grosser Profit-Target-Block (Label/%, Balken, Werte)

            string tzNote = _autoLocalTime ? "  ·  Lokalzeit" : (_timeOffsetHours != 0 ? $"  ·  +{_timeOffsetHours}h" : "");
            string strSub = $"{kindLabel}  ·  ab {snap.From:dd.MM.yy}{tzNote}";
            int minTrades = (int)Math.Ceiling((double)(thr / 100m) * snap.TotalCount);
            decimal minProfit = thr / 100m * snap.WinSum;
            string mTrades = $"{snap.OverCount} / Soll {minTrades} · {tradeShareOver.ToString("0.0", de)}%";
            string mProfit = $"{Money(snap.OverWinSum, de)} / Soll {Money(minProfit, de)} · {overWinShare.ToString("0.0", de)}%";
            string strHero = $"{Money(netTotal, de)} $";
            bool showAusz = !isEval;   // in der Evaluation gibt es noch keine Auszahlung
            string chipAusz = $"Auszahlbar ~{Money(payoutEst, de)}";

            string badge = isEval
                ? (targetOk ? "EVALUATION · ZIEL ERREICHT" : $"EVALUATION · Ziel {targetShare.ToString("0", de)}%")
                : (payoutOk ? "PAYOUT ERLAUBT" : $"PAYOUT GESPERRT · {fundedMissing} offen");
            var badgeCol = statusOk ? CGreenHi : (isEval ? CAmberHi : CRedHi);
            var badgeGlow = statusOk ? _penGlowGreen : (isEval ? _penGlowAmber : _penGlowRed);

            var chips = new List<(string label, bool ok)>();
            if (isEval) { chips.Add(("Ziel", targetOk)); chips.Add(("Trades", tradeOk)); chips.Add(("Profite", profitOk)); }
            else { chips.Add(("Trades", tradeOk)); chips.Add(("Profite", profitOk)); chips.Add(("Best-Day", consistencyOk)); chips.Add(("Sessions", sessionsOk)); }

            // Kopf-Hoehe (immer sichtbar) + Inhalts-Hoehe je Reiter
            int headerH = padYt + lineH + sH + divH + (sH + bigH) + gBlock + badgeH + gBlock + chipH + gBlock + tabH + gBlock;
            int uebersichtH = showAusz
                ? lineH * 2 + divH + lineH * 3 + divH + lineH + sH + divH + splitH
                : lineH * 2 + divH + lineH * 3 + divH + splitH;   // ohne Auszahlbar-Block (Eval)
            int payoutH = isEval ? targetMeterH + meterH * 2 : meterH * 4 + sH;

            // Breite
            int chipAuszW = showAusz ? context.MeasureString(chipAusz, _statsFontSmall).Width + 22 : 0;
            int innerW = 360;
            innerW = Math.Max(innerW, context.MeasureString(strHero, _statsFontBig).Width + chipAuszW + 18);
            innerW = Math.Max(innerW, context.MeasureString("▾ TRADE TIMER", _statsFontBold).Width
                                       + 16 + context.MeasureString(snap.AccountLabel, _statsFontSmall).Width);
            innerW = Math.Max(innerW, context.MeasureString(ColHeader, _statsFont).Width);
            innerW = Math.Max(innerW, context.MeasureString($"Profite >{snap.MinSeconds}s", _statsFont).Width
                                       + 30 + context.MeasureString(mProfit, _statsFont).Width + 14);
            innerW = Math.Max(innerW, context.MeasureString($"   Netto × {_withdrawCapPct}% × {_profitSplitPct}% · Cap {Money(effCap, de)}", _statsFontSmall).Width);
            int boxW = innerW + padX * 2;
            if (windowMode) _winBoxW = boxW; else _lastBoxW = boxW;

            int posX = windowMode ? forceX : viewW - boxW - _statsOffsetX;
            int posY = windowMode ? forceY : _statsOffsetY;
            if (posX < 0) posX = 0;
            if (posY < 0) posY = 0;

            // ── Trades-Reiter: Scroll-Viewport ────────────────────────────
            int contentTop = posY + headerH;
            var drawBody = new List<(string text, Color color)>();
            int contentH = 0;
            if (!collapsed)
            {
                if (activeTab == 0) contentH = uebersichtH;
                else if (activeTab == 1) contentH = payoutH;
                else
                {
                    int avail = viewH - contentTop - padYb - 4;
                    int cap = Math.Max(1, avail / lineH);
                    if (body.Count <= cap) { scrollOffset = 0; drawBody.AddRange(body); }
                    else
                    {
                        int maxOff = Math.Max(0, body.Count - (cap - 1));
                        scrollOffset = Math.Clamp(scrollOffset, 0, maxOff);
                        bool top = scrollOffset > 0;
                        int rows = cap - (top ? 1 : 0);
                        bool more = scrollOffset + rows < body.Count;
                        if (more) rows -= 1;
                        int end = Math.Min(body.Count, scrollOffset + rows);
                        if (top) drawBody.Add(($"  ▲ {scrollOffset} weitere oben", CDim));
                        for (int i = scrollOffset; i < end; i++) drawBody.Add(body[i]);
                        int restN = body.Count - end;
                        if (restN > 0) drawBody.Add(($"  ▼ {restN} weitere unten", CDim));
                    }
                    contentH = drawBody.Count * lineH;
                }
            }
            else scrollOffset = 0;

            int boxH = headerH + contentH + (collapsed ? 0 : padYb);

            // ── Panel-Hintergrund ─────────────────────────────────────────
            var panelRect = new Rectangle(posX, posY, boxW, boxH);
            var headerRect = new Rectangle(posX, posY, boxW, headerH);
            if (windowMode) { _winBoxH = boxH; _winHeaderRect = headerRect; }
            else { _panelRect = panelRect; _headerHitRect = headerRect; }
            context.FillRectangle(CBg, panelRect, 10);
            context.DrawRectangle(_penGlow, panelRect, 10);
            context.DrawRectangle(_penAccent, panelRect, 10);

            int x = posX + padX;
            int y = posY + padYt;

            // Titel (Titelzeile klickbar = collapse)
            context.FillGradient(CTitle1, CTitle2, new Rectangle(posX + 3, posY + 3, boxW - 6, lineH + padYt - 5));
            context.DrawString($"{(collapsed ? "▸" : "▾")} TRADE TIMER", _statsFontBold, CAccent, x, y);
            context.DrawString(snap.AccountLabel, _statsFontSmall, CDim,
                new Rectangle(x, y, innerW - (windowMode ? 26 : 0), lineH), FmtRight);
            if (windowMode)
            {
                // Close-X in die Bitmap zeichnen (oben rechts), Trefferbereich merken
                _winCloseRect = new Rectangle(posX + boxW - 32, posY, 32, 28);
                int xs = posX + boxW - 22, ys = posY + 11;
                var xpen = new RenderPen(CMuted, 2);
                context.DrawLine(xpen, xs, ys, xs + 9, ys + 9);
                context.DrawLine(xpen, xs + 9, ys, xs, ys + 9);
            }
            else _titleHitRect = new Rectangle(posX, posY, boxW, lineH + padYt - 2);
            y += lineH;
            context.DrawString(strSub, _statsFontSmall, CDim, x, y); y += sH;
            y += divH / 2; context.DrawLine(_penDivider, x, y, x + innerW, y); y += divH - divH / 2;

            // Hero: NETTO + grosse Zahl + Auszahlbar-Chip rechts (nur Funded/Instant)
            if (showAusz)
            {
                var auszRect = new Rectangle(x + innerW - chipAuszW, y, chipAuszW, sH + bigH - 2);
                context.FillRectangle(Color.FromArgb(255, 17, 22, 31), auszRect, 8);
                context.DrawRectangle(new RenderPen(Color.FromArgb(120, 167, 139, 250), 1), auszRect, 8);
                context.DrawString("Auszahlbar", _statsFontSmall, CDim, auszRect.X + 10, auszRect.Y + 5);
                context.DrawString($"~{Money(payoutEst, de)}", _statsFontBold, CViolet, auszRect.X + 10, auszRect.Y + 5 + sH);
            }
            context.DrawString("NETTO", _statsFontSmall, CDim, x, y); y += sH;
            context.DrawString(strHero, _statsFontBig, netTotal >= 0 ? CGreenHi : CRedHi, x, y); y += bigH; y += gBlock;

            // Status-Badge (volle Breite)
            var badgeRect = new Rectangle(x, y, innerW, badgeH);
            int brad = badgeH / 2;
            context.DrawRectangle(badgeGlow, new Rectangle(badgeRect.X - 1, badgeRect.Y - 1, badgeRect.Width + 2, badgeRect.Height + 2), brad + 1);
            context.FillRectangle(badgeCol, badgeRect, brad);
            context.DrawRectangle(_penHighlight, new Rectangle(badgeRect.X + 2, badgeRect.Y + 1, badgeRect.Width - 4, badgeRect.Height - 2), brad);
            context.DrawString(badge, _statsFontBold, CPillText, badgeRect, FmtCenter);
            y += badgeH + gBlock;

            // Ampel-Chips (erfuellt = gruen, offen = rot)
            int cxp = x;
            foreach (var (clabel, cok) in chips)
            {
                int cw = context.MeasureString(clabel, _statsFontSmall).Width + 34;
                if (cxp + cw > x + innerW) break;
                int crad = (chipH - 2) / 2;
                var cr = new Rectangle(cxp, y, cw, chipH - 2);
                context.FillRectangle(cok ? Color.FromArgb(255, 14, 32, 24) : Color.FromArgb(255, 42, 18, 22), cr, crad);
                context.DrawRectangle(new RenderPen(cok ? Color.FromArgb(120, 52, 211, 153) : Color.FromArgb(120, 248, 113, 113), 1), cr, crad);
                // Haken / Kreuz mit Linien zeichnen (Font-unabhaengig)
                var markPen = new RenderPen(cok ? CGreenHi : CRedHi, 2);
                int mx = cr.X + 14, my = cr.Y + crad;
                if (cok)
                {
                    context.DrawLine(markPen, mx - 4, my, mx - 1, my + 3);
                    context.DrawLine(markPen, mx - 1, my + 3, mx + 5, my - 4);
                }
                else
                {
                    context.DrawLine(markPen, mx - 4, my - 4, mx + 4, my + 4);
                    context.DrawLine(markPen, mx - 4, my + 4, mx + 4, my - 4);
                }
                context.DrawString(clabel, _statsFontSmall, cok ? Color.FromArgb(255, 159, 233, 200) : Color.FromArgb(255, 248, 160, 160),
                    new Rectangle(cr.X + 25, cr.Y, cw - 28, chipH - 2), FmtMidLeft);
                cxp += cw + 6;
            }
            y += chipH + gBlock;

            // Tab-Leiste
            var tabBar = new Rectangle(x, y, innerW, tabH);
            context.FillRectangle(Color.FromArgb(255, 16, 22, 31), tabBar, 6);
            string[] tabNames = { "Übersicht", isEval ? "Challenge" : "Payout", "Trades" };
            int twi = innerW / 3;
            for (int i = 0; i < 3; i++)
            {
                var tr = new Rectangle(x + i * twi, y, i == 2 ? innerW - 2 * twi : twi, tabH);
                if (windowMode) _winTabRects[i] = tr; else _tabRects[i] = tr;
                bool active = activeTab == i && !collapsed;
                if (active)
                {
                    context.FillRectangle(Color.FromArgb(255, 12, 43, 51), new Rectangle(tr.X + 2, tr.Y + 2, tr.Width - 4, tr.Height - 4), 5);
                    context.DrawRectangle(new RenderPen(Color.FromArgb(150, 34, 211, 238), 1), new Rectangle(tr.X + 2, tr.Y + 2, tr.Width - 4, tr.Height - 4), 5);
                }
                context.DrawString(tabNames[i], active ? _statsFontBold : _statsFontSmall, active ? CAccent : CMuted, tr, FmtCenter);
            }
            y += tabH + gBlock;

            if (collapsed) return;

            // ── Reiter-Inhalt ─────────────────────────────────────────────
            if (activeTab == 0)   // Übersicht
            {
                DrawKV(context, x, y, innerW, lineH, "Brutto", Money(snap.GrossSum, de), CGreenHi); y += lineH;
                DrawKV(context, x, y, innerW, lineH, "Kommission", Money(-snap.CommissionTotal, de), CRedHi); y += lineH;
                y += divH / 2; context.DrawLine(_penDivider, x, y, x + innerW, y); y += divH - divH / 2;
                DrawKV(context, x, y, innerW, lineH, "Gewinne", Money(snap.WinSum, de), CGreenHi); y += lineH;
                DrawKV(context, x, y, innerW, lineH, "Verluste", Money(snap.LossSum, de), CRedHi); y += lineH;
                int winCnt = snap.UnderWinners.Count + snap.OverWinners.Count;
                int wr = snap.TotalCount > 0 ? (int)Math.Round(100.0 * winCnt / snap.TotalCount) : 0;
                DrawKV(context, x, y, innerW, lineH, "Trefferquote", $"{wr}% · {snap.TotalCount} Trades", CValue); y += lineH;
                y += divH / 2; context.DrawLine(_penDivider, x, y, x + innerW, y); y += divH - divH / 2;
                if (showAusz)
                {
                    DrawKV(context, x, y, innerW, lineH, "Auszahlbar", $"~{Money(payoutEst, de)}", CViolet); y += lineH;
                    context.DrawString($"   Netto × {_withdrawCapPct}% × {_profitSplitPct}%{(effCap > 0 ? $" · Cap {Money(effCap, de)}" : "")}", _statsFontSmall, CDim, x, y); y += sH;
                    y += divH / 2; context.DrawLine(_penDivider, x, y, x + innerW, y); y += divH - divH / 2;
                }
                DrawSplitBar(context, x, y, innerW, sH, snap.MinSeconds, snap.OverWinSum, overWinShare, snap.UnderWinSum, underWinShare, de); y += splitH;
            }
            else if (activeTab == 1)   // Payout
            {
                if (isEval)
                {
                    DrawTargetMeter(context, x, y, innerW, lineH, "PROFIT-TARGET",
                        Money(np, de), Money(effTarget, de), targetShare,
                        Money(Math.Max(0m, effTarget - np), de), targetOk); y += targetMeterH;
                    DrawMeter(context, x, y, innerW, lineH, $"Trades >{snap.MinSeconds}s",
                        mTrades, (double)(tradeShareOver / 100m), (double)(thr / 100m), tradeOk); y += meterH;
                    DrawMeter(context, x, y, innerW, lineH, $"Profite >{snap.MinSeconds}s",
                        mProfit, (double)(overWinShare / 100m), (double)(thr / 100m), profitOk); y += meterH;
                }
                else
                {
                    DrawMeter(context, x, y, innerW, lineH, $"Trades >{snap.MinSeconds}s",
                        mTrades, (double)(tradeShareOver / 100m), (double)(thr / 100m), tradeOk); y += meterH;
                    DrawMeter(context, x, y, innerW, lineH, $"Profite >{snap.MinSeconds}s",
                        mProfit, (double)(overWinShare / 100m), (double)(thr / 100m), profitOk); y += meterH;
                    DrawMeter(context, x, y, innerW, lineH, "Best-Day",
                        $"{Money(snap.BestDayPnl, de)} · {bestDayShare.ToString("0.0", de)}%",
                        (double)(bestDayShare / 100m), (double)(_consistencyPct / 100m), consistencyOk); y += meterH;
                    string consLine = consRequired > 0m
                        ? $"  Consistency: nötig {consRequired.ToString("#,##0.00", de)} $  ·  aktuell {Money(netTotal, de)}"
                        : "  Consistency: noch kein Gewinn-Tag";
                    context.DrawString(consLine, _statsFontSmall, consistencyOk ? CGreen : CAmber, x, y); y += sH;
                    DrawSessionsRow(context, x, y, innerW, lineH, _requiredSessions, snap.DistinctDays, sessionsOk); y += meterH;
                }
            }
            else   // Trades
            {
                foreach (var (text, color) in drawBody)
                {
                    context.DrawString(text, _statsFont, color, x, y);
                    y += lineH;
                }
            }
        }

        // Grosser Profit-Target-Block: Label + % oben, dicker Balken, Werte-Zeile darunter.
        private void DrawTargetMeter(IPanelCanvas context, int x, int y, int w, int lineH,
                                     string label, string npStr, string targetStr, decimal share, string remainStr, bool ok)
        {
            var de = CultureInfo.GetCultureInfo("de-DE");
            var col = ok ? CGreenHi : CAmberHi;
            context.DrawString(label, _statsFont, CMuted, x, y);
            context.DrawString($"{share.ToString("0.0", de)} %", _statsFontBold, col, new Rectangle(x, y, w, lineH), FmtRight);
            int by = y + lineH + 2, bh = 16;
            context.FillRectangle(CTrack, new Rectangle(x, by, w, bh), bh / 2);
            int fw = (int)(w * Math.Clamp((double)(share / 100m), 0, 1));
            if (fw > 0) context.FillRectangle(col, new Rectangle(x, by, fw, bh), bh / 2);
            int vy = by + bh + 4;
            context.DrawString($"{npStr} / {targetStr} $", _statsFont, CValue, x, vy);
            context.DrawString(ok ? "Ziel erreicht" : $"noch {remainStr} $", _statsFont, col,
                new Rectangle(x, vy, w, lineH), FmtRight);
        }

        // Schluessel-Wert-Zeile: Label links (gedaempft), Wert rechts (farbig).
        private void DrawKV(IPanelCanvas context, int x, int y, int w, int lineH, string key, string val, Color valCol)
        {
            context.DrawString(key, _statsFont, CMuted, x, y);
            context.DrawString(val, _statsFont, valCol, new Rectangle(x, y, w, lineH), FmtRight);
        }

        // Zeichnet ein Text-Segment und gibt seine Breite zurueck (fuer linksbuendige Mehrfarb-Zeilen).
        private int DrawSeg(IPanelCanvas context, string s, RenderFont font, Color col, int x, int y)
        {
            context.DrawString(s, font, col, x, y);
            return context.MeasureString(s, font).Width;
        }

        // Fortschrittsbalken: Label links, Wert (neutral weiss) rechts, Status-Punkt, 50%-Kerbe.
        private void DrawMeter(IPanelCanvas context, int x, int y, int w, int lineH,
                               string label, string val, double frac, double tick, bool ok)
        {
            context.DrawString(label, _statsFont, CMuted, x, y);
            context.DrawString(val, _statsFont, CValue, new Rectangle(x, y, w - 14, lineH), FmtRight);
            context.FillEllipse(ok ? CGreenHi : CRedHi, new Rectangle(x + w - 9, y + lineH / 2 - 4, 8, 8));
            int ty = y + lineH + 3, bh = 8;
            context.FillRectangle(CTrack, new Rectangle(x, ty, w, bh), bh / 2);
            int fw = (int)(w * Math.Clamp(frac, 0, 1));
            if (fw > 0)
                context.FillGradient(ok ? CGreenHi : CRedHi, ok ? CGreenLo : CRedLo,
                    new Rectangle(x, ty, fw, bh));
            int tx = x + (int)(w * Math.Clamp(tick, 0, 1));
            context.DrawLine(_penTick, tx, ty - 3, tx, ty + bh + 3);
        }

        // Segmentierte Leiste: Gewinne >15s (gruen) vs <15s (bernstein), beide mit EUR + %.
        private void DrawSplitBar(IPanelCanvas context, int x, int y, int w, int sH, int minSec,
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
            if (ow > 0) context.FillGradient(CGreenHi, CGreenLo, new Rectangle(x, by, ow, bh));
            if (uw > 0) context.FillGradient(CAmberHi, CAmberLo, new Rectangle(x + ow, by, uw, bh));
            if (ow > 44)
                context.DrawString($"{overShare.ToString("0.0", de)}%", _statsFontSmall, CPillText,
                    new Rectangle(x, by, ow, bh), FmtCenter);
            if (uw > 44)
                context.DrawString($"{underShare.ToString("0.0", de)}%", _statsFontSmall, CPillText,
                    new Rectangle(x + ow, by, uw, bh), FmtCenter);
        }

        // Session-Reihe: Punkte, gefuellt (mit Glow) = aktive Tage.
        private void DrawSessionsRow(IPanelCanvas context, int x, int y, int w, int lineH,
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
                if (!_didDrag)   // reiner Klick (ohne Bewegung)
                {
                    // 1) Reiter angeklickt -> wechseln (und ggf. aufklappen)
                    bool hitTab = false;
                    for (int i = 0; i < 3; i++)
                        if (_tabRects[i].Contains(e.X, e.Y))
                        {
                            _activeTab = i; _scrollOffset = 0; _collapsed = false; hitTab = true; break;
                        }
                    // 2) sonst Titelzeile -> Detail ein/ausklappen
                    if (!hitTab && _titleHitRect.Contains(e.X, e.Y))
                        _collapsed = !_collapsed;
                }
                RedrawChart();
                return true;
            }
            return base.ProcessMouseUp(e);
        }

        // Mausrad ueber dem Panel scrollt die Trade-Liste (nur im Trades-Reiter).
        public override bool ProcessMouseWheel(int delta)
        {
            if (_showStatsPanel && !_collapsed && _activeTab == 2 && _stats != null
                && _panelRect.Contains(_lastMouseX, _lastMouseY))
            {
                _scrollOffset += delta > 0 ? -2 : 2;   // Rad hoch = nach oben
                if (_scrollOffset < 0) _scrollOffset = 0;
                RedrawChart();
                return true;
            }
            return base.ProcessMouseWheel(delta);
        }

        // Anzeige-Zeit: ATAS speichert UTC. Auto = in Systemzeitzone wandeln
        // (Sommer-/Winterzeit automatisch); sonst fester manueller Versatz.
        private DateTime Disp(DateTime t)
            => _autoLocalTime
                ? TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(t, DateTimeKind.Utc), TimeZoneInfo.Local)
                : t.AddHours(_timeOffsetHours);

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

        // ── Schwebendes Fenster (pixelgleicher Spiegel des Chart-Panels) ──
        // ATAS (WPF) hat eine Application.Current -> deren UI-Thread nutzen.
        // ATAS X hat u.U. KEINE WPF-Application -> dann eigener STA-UI-Thread,
        // damit das Fenster in BEIDEN Editionen funktioniert.
        private static System.Windows.Threading.Dispatcher? _ownDispatcher;
        private static readonly object _dispLock = new();

        private static System.Windows.Threading.Dispatcher? UiDisp
            => System.Windows.Application.Current?.Dispatcher ?? EnsureOwnDispatcher();

        private static System.Windows.Threading.Dispatcher EnsureOwnDispatcher()
        {
            if (_ownDispatcher != null) return _ownDispatcher;
            lock (_dispLock)
            {
                if (_ownDispatcher != null) return _ownDispatcher;
                var ready = new System.Threading.ManualResetEventSlim(false);
                System.Windows.Threading.Dispatcher? d = null;
                var t = new System.Threading.Thread(() =>
                {
                    try
                    {
                        // Minimale WPF-Application, falls der Host keine hat
                        // (Ressourcen/Transparenz). Nur wenn wirklich keine da ist.
                        if (System.Windows.Application.Current == null)
                            _ = new System.Windows.Application
                            { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
                    }
                    catch { }
                    d = System.Windows.Threading.Dispatcher.CurrentDispatcher;
                    ready.Set();
                    System.Windows.Threading.Dispatcher.Run();
                })
                {
                    IsBackground = true,
                    Name = "TradeTimerWindowUI"
                };
                t.SetApartmentState(System.Threading.ApartmentState.STA);
                t.Start();
                ready.Wait();
                _ownDispatcher = d;
                return _ownDispatcher!;
            }
        }

        private void ApplyTopmost()
            => UiDisp?.BeginInvoke(new Action(() => { try { if (_window != null) _window.Topmost = _windowTopmost; } catch { } }));

        private void CloseWindow()
        {
            var disp = UiDisp;
            if (disp == null) { _window = null; return; }
            disp.BeginInvoke(new Action(CloseWindowUi));
        }

        private void CloseWindowUi()
        {
            try { _window?.Close(); } catch { }
            _window = null;
        }

        // Klick aufs X im Fenster.
        private void CloseFromWindow()
        {
            _showWindow = false;
            CloseWindowUi();
        }

        // Rendert das Panel mit DERSELBEN Logik in eine Bitmap und zeigt sie im
        // Fenster -> 100% identisch zum Chart-Panel. Aufruf aus OnRender (gedrosselt).
        private void RenderWindow(bool force)
        {
            if (!_showWindow || _stats == null || _statsFont == null) return;
            if (!force)
            {
                int now = Environment.TickCount;
                if (now - _winLastTick < 120) return;
                _winLastTick = now;
            }
            System.Windows.Media.Imaging.BitmapSource src;
            int bw, bh;
            try { src = BuildPanelBitmap(out bw, out bh); }
            catch (Exception ex) { _lastError = ex.Message; return; }

            UiDisp?.BeginInvoke(new Action(() =>
            {
                try
                {
                    if (!_showWindow || IsDisposed) return;
                    if (_window == null) _window = new PanelWindow(this);
                    _window.Topmost = _windowTopmost;
                    _window.SetImage(src, bw, bh);
                    if (!_window.IsVisible) _window.Show();
                }
                catch { }
            }));
        }

        // Synchroner Refresh vom UI-Thread (Reiter-Klick / Scrollen im Fenster).
        private void RefreshWindowUi()
        {
            try
            {
                if (_window == null || _stats == null || _statsFont == null) return;
                var src = BuildPanelBitmap(out int bw, out int bh);
                _window.SetImage(src, bw, bh);
            }
            catch (Exception ex) { _lastError = ex.Message; }
        }

        private System.Windows.Media.Imaging.BitmapSource BuildPanelBitmap(out int bw, out int bh)
        {
            const int W = 760, H = 1000;
            const int layoutH = 640;   // begrenzt die Trades-Liste -> Pagination/Scroll wie im Panel
            using var bmp = new System.Drawing.Bitmap(W, H, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            using (var g = System.Drawing.Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                g.Clear(System.Drawing.Color.Transparent);
                DrawStatsPanel(new GdiCanvas(g), W, layoutH, 0, 0, false, _winTab, ref _winScroll);
            }
            bw = Math.Clamp(_winBoxW, 1, W);
            bh = Math.Clamp(_winBoxH, 1, H);
            return ToBitmapSource(bmp, bw, bh);
        }

        private static System.Windows.Media.Imaging.BitmapSource ToBitmapSource(System.Drawing.Bitmap bmp, int w, int h)
        {
            var data = bmp.LockBits(new Rectangle(0, 0, w, h),
                System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            try
            {
                var src = System.Windows.Media.Imaging.BitmapSource.Create(
                    w, h, 96, 96, System.Windows.Media.PixelFormats.Pbgra32, null,
                    data.Scan0, data.Stride * bmp.Height, data.Stride);
                src.Freeze();
                return src;
            }
            finally { bmp.UnlockBits(data); }
        }

        // ── Zeichen-Abstraktion: Chart (ATAS) und Fenster (GDI+) teilen DrawStatsPanel ──
        private interface IPanelCanvas
        {
            Size MeasureString(string s, RenderFont f);
            void DrawString(string s, RenderFont f, Color c, int x, int y);
            void DrawString(string s, RenderFont f, Color c, Rectangle r, RenderStringFormat fmt);
            void FillRectangle(Color c, Rectangle r);
            void FillRectangle(Color c, Rectangle r, int radius);
            void FillGradient(Color c1, Color c2, Rectangle r);
            void DrawRectangle(RenderPen p, Rectangle r, int radius);
            void DrawLine(RenderPen p, int x1, int y1, int x2, int y2);
            void FillEllipse(Color c, Rectangle r);
            void SetClip(Rectangle r);
            void ResetClip();
        }

        private sealed class AtasCanvas : IPanelCanvas
        {
            private readonly RenderContext _c;
            public AtasCanvas(RenderContext c) { _c = c; }
            public Size MeasureString(string s, RenderFont f) => _c.MeasureString(s, f);
            public void DrawString(string s, RenderFont f, Color c, int x, int y) => _c.DrawString(s, f, c, x, y);
            public void DrawString(string s, RenderFont f, Color c, Rectangle r, RenderStringFormat fmt) => _c.DrawString(s, f, c, r, fmt);
            public void FillRectangle(Color c, Rectangle r) => _c.FillRectangle(c, r);
            public void FillRectangle(Color c, Rectangle r, int radius) => _c.FillRectangle(c, r, radius);
            public void FillGradient(Color c1, Color c2, Rectangle r) => _c.FillRectangle(c1, c2, r);
            public void DrawRectangle(RenderPen p, Rectangle r, int radius) => _c.DrawRectangle(p, r, radius);
            public void DrawLine(RenderPen p, int x1, int y1, int x2, int y2) => _c.DrawLine(p, x1, y1, x2, y2);
            public void FillEllipse(Color c, Rectangle r) => _c.FillEllipse(c, r);
            public void SetClip(Rectangle r) => _c.SetClip(r);
            public void ResetClip() => _c.ResetClip();
        }

        private sealed class GdiCanvas : IPanelCanvas
        {
            private readonly System.Drawing.Graphics _g;
            public GdiCanvas(System.Drawing.Graphics g) { _g = g; }

            private static System.Drawing.Font F(RenderFont f) => new(f.FontFamily, f.Size, f.Style);
            private static System.Drawing.StringFormat SF(RenderStringFormat fmt) => new()
            {
                Alignment = fmt.Alignment,
                LineAlignment = fmt.LineAlignment,
                FormatFlags = fmt.FormatFlags | System.Drawing.StringFormatFlags.NoWrap,
                Trimming = System.Drawing.StringTrimming.None
            };

            public Size MeasureString(string s, RenderFont f)
            {
                using var fo = F(f);
                var sz = _g.MeasureString(s ?? "", fo, int.MaxValue, System.Drawing.StringFormat.GenericTypographic);
                return new Size((int)Math.Ceiling(sz.Width), (int)Math.Ceiling(sz.Height));
            }
            public void DrawString(string s, RenderFont f, Color c, int x, int y)
            {
                using var fo = F(f);
                using var br = new System.Drawing.SolidBrush(c);
                _g.DrawString(s ?? "", fo, br, x, y, System.Drawing.StringFormat.GenericTypographic);
            }
            public void DrawString(string s, RenderFont f, Color c, Rectangle r, RenderStringFormat fmt)
            {
                using var fo = F(f);
                using var br = new System.Drawing.SolidBrush(c);
                using var sf = SF(fmt);
                _g.DrawString(s ?? "", fo, br, r, sf);
            }
            public void FillRectangle(Color c, Rectangle r)
            {
                if (r.Width <= 0 || r.Height <= 0) return;
                using var br = new System.Drawing.SolidBrush(c);
                _g.FillRectangle(br, r);
            }
            public void FillRectangle(Color c, Rectangle r, int radius)
            {
                if (r.Width <= 0 || r.Height <= 0) return;
                using var br = new System.Drawing.SolidBrush(c);
                using var p = Round(r, radius);
                _g.FillPath(br, p);
            }
            public void FillGradient(Color c1, Color c2, Rectangle r)
            {
                if (r.Width <= 0 || r.Height <= 0) return;
                using var br = new System.Drawing.Drawing2D.LinearGradientBrush(
                    new Rectangle(r.X, r.Y - 1, r.Width, r.Height + 2), c1, c2, 90f);
                _g.FillRectangle(br, r);
            }
            public void DrawRectangle(RenderPen pen, Rectangle r, int radius)
            {
                if (r.Width <= 0 || r.Height <= 0) return;
                using var p = new System.Drawing.Pen(pen.Color, pen.Width);
                using var path = Round(r, radius);
                _g.DrawPath(p, path);
            }
            public void DrawLine(RenderPen pen, int x1, int y1, int x2, int y2)
            {
                using var p = new System.Drawing.Pen(pen.Color, pen.Width);
                _g.DrawLine(p, x1, y1, x2, y2);
            }
            public void FillEllipse(Color c, Rectangle r)
            {
                if (r.Width <= 0 || r.Height <= 0) return;
                using var br = new System.Drawing.SolidBrush(c);
                _g.FillEllipse(br, r);
            }
            public void SetClip(Rectangle r) => _g.SetClip(r);
            public void ResetClip() => _g.ResetClip();

            private static System.Drawing.Drawing2D.GraphicsPath Round(Rectangle r, int radius)
            {
                var path = new System.Drawing.Drawing2D.GraphicsPath();
                int d = Math.Max(0, Math.Min(radius, Math.Min(r.Width, r.Height) / 2)) * 2;
                if (d <= 0) { path.AddRectangle(r); return path; }
                path.AddArc(r.X, r.Y, d, d, 180, 90);
                path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
                path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
                path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
                path.CloseFigure();
                return path;
            }
        }

        // Schwebendes Fenster: zeigt die gerenderte Panel-Bitmap (1:1) + X + Interaktion.
        private sealed class PanelWindow : System.Windows.Window
        {
            private readonly TradeTimer _owner;
            private readonly System.Windows.Controls.Image _img;

            public PanelWindow(TradeTimer owner)
            {
                _owner = owner;
                Title = "Trade Timer";
                WindowStyle = System.Windows.WindowStyle.None;
                ResizeMode = System.Windows.ResizeMode.NoResize;          // kein Ziehen/Vergroessern
                SizeToContent = System.Windows.SizeToContent.WidthAndHeight;
                ShowInTaskbar = false;
                AllowsTransparency = true;
                Background = System.Windows.Media.Brushes.Transparent;

                _img = new System.Windows.Controls.Image { Stretch = System.Windows.Media.Stretch.None };
                System.Windows.Media.RenderOptions.SetBitmapScalingMode(
                    _img, System.Windows.Media.BitmapScalingMode.NearestNeighbor);
                Content = _img;

                _img.MouseLeftButtonDown += OnImageDown;
                _img.MouseWheel += OnImageWheel;
            }

            public void SetImage(System.Windows.Media.Imaging.BitmapSource src, int bw, int bh)
            {
                _img.Source = src;
                _img.Width = bw;
                _img.Height = bh;
            }

            private void OnImageDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
            {
                var p = e.GetPosition(_img);
                int mx = (int)p.X, my = (int)p.Y;
                if (_owner._winCloseRect.Contains(mx, my))
                {
                    e.Handled = true;
                    try { _owner.CloseFromWindow(); } catch { }
                    return;
                }
                for (int i = 0; i < 3; i++)
                {
                    if (_owner._winTabRects[i].Contains(mx, my))
                    {
                        _owner._winTab = i;
                        _owner._winScroll = 0;
                        _owner.RefreshWindowUi();
                        e.Handled = true;
                        return;
                    }
                }
                try { DragMove(); } catch { }   // sonst Fenster verschieben
            }

            private void OnImageWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
            {
                if (_owner._winTab != 2) return;   // nur Trades scrollen
                _owner._winScroll = Math.Max(0, _owner._winScroll + (e.Delta > 0 ? -1 : 1));
                _owner.RefreshWindowUi();
                e.Handled = true;
            }
        }

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
