using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using ATAS.Indicators;
using OFT.Attributes;
using OFT.Rendering.Context;
using OFT.Rendering.Tools;

namespace TradeTimer
{
    [DisplayName("Trade Timer")]
    [HelpLink("https://giucino.github.io/TradeTimer/TradeTimer_Doku.html")]
    [Description("Zeigt an, wie lange die aktuelle Position offen ist. " +
                 "Rot = unter Ziel-Sekunden, Gruen = ueber Ziel-Sekunden.")]
    public class TradeTimer : Indicator
    {
        // ── Backing fields ─────────────────────────────────────────────
        private int _targetSeconds = 30;
        private int _fontSize = 16;
        private int _offsetX = 20;
        private int _offsetY = 20;
        private bool _bottomLeft = true;

        private Color _colorBelow = Color.FromArgb(220, 220, 50, 50);
        private Color _colorAbove = Color.FromArgb(220, 50, 205, 50);
        private Color _colorBackground = Color.FromArgb(160, 20, 20, 20);

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

        // ── Einstellungen ──────────────────────────────────────────────
        [Display(Name = "Ziel-Sekunden", GroupName = "Einstellungen", Order = 1)]
        [Range(1, 3600)]
        public int TargetSeconds
        {
            get => _targetSeconds;
            set { _targetSeconds = Math.Max(1, value); RedrawChart(); }
        }

        [Display(Name = "Schriftgroesse", GroupName = "Einstellungen", Order = 2)]
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
        [Display(Name = "Farbe unter Ziel", GroupName = "Farben", Order = 3)]
        public Color ColorBelow
        {
            get => _colorBelow;
            set { _colorBelow = value; RedrawChart(); }
        }

        [Display(Name = "Farbe ueber Ziel", GroupName = "Farben", Order = 4)]
        public Color ColorAbove
        {
            get => _colorAbove;
            set { _colorAbove = value; RedrawChart(); }
        }

        [Display(Name = "Hintergrundfarbe", GroupName = "Farben", Order = 5)]
        public Color ColorBackground
        {
            get => _colorBackground;
            set { _colorBackground = value; RedrawChart(); }
        }

        // ── Position ───────────────────────────────────────────────────
        [Display(Name = "Unten Links (aus = Unten Rechts)", GroupName = "Position", Order = 6)]
        public bool BottomLeft
        {
            get => _bottomLeft;
            set { _bottomLeft = value; RedrawChart(); }
        }

        [Display(Name = "Abstand vom Rand X (px)", GroupName = "Position", Order = 7)]
        [Range(0, 500)]
        public int OffsetX
        {
            get => _offsetX;
            set { _offsetX = value; RedrawChart(); }
        }

        [Display(Name = "Abstand vom Rand Y (px)", GroupName = "Position", Order = 8)]
        [Range(0, 500)]
        public int OffsetY
        {
            get => _offsetY;
            set { _offsetY = value; RedrawChart(); }
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

            // Optional: sichtbares Sekunden-Ticken erzwingen, wenn keine
            // neuen Ticks reinkommen. Nur bei offener Position, also keine
            // Dauerlast im Flat-Zustand.
            if (_entryTime != null)
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

        protected override void OnRender(RenderContext context, DrawingLayouts layout)
        {
            if (_entryTime == null || _font == null)
                return;

            // Verstrichene Zeit gegen Marktzeit, nicht gegen die Wanduhr.
            var now = _lastMarketTime == DateTime.MinValue
                ? DateTime.UtcNow
                : _lastMarketTime;

            var elapsed = now - _entryTime.Value;
            if (elapsed < TimeSpan.Zero)
                elapsed = TimeSpan.Zero;

            double totalSeconds = elapsed.TotalSeconds;
            bool isAbove = totalSeconds >= _targetSeconds;

            var textColor = isAbove ? _colorAbove : _colorBelow;
            var borderColor = Color.FromArgb(180, textColor.R, textColor.G, textColor.B);

            // Format: MM:SS (Minuten koennen >60 werden, daher TotalMinutes).
            string timeText = $"{(int)elapsed.TotalMinutes:D2}:{elapsed.Seconds:D2}";

            var textSize = context.MeasureString(timeText, _font);

            const int padX = 8, padY = 5;
            int boxW = textSize.Width + padX * 2;
            int boxH = textSize.Height + padY * 2;

            int posX = _bottomLeft
                ? _offsetX
                : context.ClipBounds.Width - boxW - _offsetX;

            int posY = context.ClipBounds.Height - boxH - _offsetY;

            var bgRect = new Rectangle(posX, posY, boxW, boxH);
            
            context.FillRectangle(_colorBackground, bgRect);
            context.DrawRectangle(new RenderPen(borderColor, 2), bgRect);
            context.DrawString(timeText, _font, textColor, posX + padX, posY + padY);
        }
    }
}