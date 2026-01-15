#region Using declarations
using System;
using System.IO;                                   // (para futuros usos/telemetría)
using System.Windows.Media;                         // Brushes
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;         // Draw.*
using NinjaTrader.NinjaScript.Indicators;           // SMA, EMA
using System.ComponentModel;                        // [Browsable]
using System.ComponentModel.DataAnnotations;        // [Display]
using NinjaTrader.Gui.Tools;                        // Priority
#endregion

namespace NinjaTrader.NinjaScript.Indicators
{
    public class OR730735Signal : Indicator
    {
        // Ventanas fijas (ToTime HHmmss) - APERTURA 08:30
        private const int START_TT  = 83000;  // 08:30
        private const int END_TT    = 83500;  // 08:35
        private const int EXTEND_TT = 91500;  // 09:15 (40 min después del cierre del OR)

        // ====== Constantes internas (sin nuevas props públicas) ======
        private const double MIN_RVOL = 1.50;     // RVOL mínimo (vol / SMA(vol))
        private const int    ADX_LEN  = 14;       // ADX anti-rango
        private const double MIN_ADX  = 15.0;

        private const int    SWING_STRENGTH      = 3;   // pivote confirmado
        private const int    STRUCT_BUFFER_TICKS = 2;   // buffer extra al swing

        private const int    RISK_QTY        = 1;     // 1 contrato por defecto
        private const double RISK_LOW_USD    = 100.0; // si OR-risk <= 100 => OR-stop
        private const double RISK_HIGH_USD   = 200.0; // si OR-risk >= 200 => swing-stop (si válido)
        private const double MAX_RISK_USD    = 200.0; // si stop elegido > 200 => NO TRADE

        // ====== DEBUG EN CHART (interno) ======
        private const bool SHOW_DEBUG_ON_CHART = true; // si te molesta, lo apago luego
        private const int  DBG_Y_OFFSET_TICKS  = 10;   // separación del texto respecto a la vela

        // ========= Inputs =========
        [NinjaScriptProperty]
        [Display(Name = "TickBuffer", GroupName = "01-Entrada", Order = 0)]
        public int TickBuffer { get; set; } = 1;

        [NinjaScriptProperty]
        [Display(Name = "BodyPct (0-1)", GroupName = "01-Entrada", Order = 1)]
        public double BodyPct { get; set; } = 0.50;

        [NinjaScriptProperty]
        [Display(Name = "VolFactor", GroupName = "01-Entrada", Order = 2)]
        public double VolFactor { get; set; } = 1.20;

        [NinjaScriptProperty]
        [Display(Name = "VolLen", GroupName = "01-Entrada", Order = 3)]
        public int VolLen { get; set; } = 20;

        [NinjaScriptProperty]
        [Display(Name = "ReentryBars", GroupName = "01-Entrada", Order = 4)]
        public int ReentryBars { get; set; } = 3;

        // EXISTENTES (solo corrijo el texto)
        [NinjaScriptProperty]
        [Display(Name = "Incluir vela 08:30 (6 velas)", GroupName = "01-Entrada", Order = 5)]
        public bool Include0730Bar { get; set; } = true;

        [NinjaScriptProperty]
        [Display(Name = "Anclar caja visual a 08:30", GroupName = "01-Entrada", Order = 6)]
        public bool AnchorBoxAt0730 { get; set; } = true;


        // ====== Filtros NUEVOS (calidad del rompimiento) ======
        [NinjaScriptProperty]
        [Display(Name = "UseDisplacementFilter", Description = "Exige que el cierre se aleje del trigger (evita rompimientos por 1 tick)", GroupName = "01-Entrada", Order = 7)]
        public bool UseDisplacementFilter { get; set; } = true;

        [NinjaScriptProperty]
        [Range(0, 50)]
        [Display(Name = "DisplacementTicks", Description = "Ticks mínimos de extensión del cierre más allá del trigger", GroupName = "01-Entrada", Order = 8)]
        public int DisplacementTicks { get; set; } = 3;

        [NinjaScriptProperty]
        [Display(Name = "UseCompressionFilter", Description = "Exige compresión previa (rango pequeño) antes del rompimiento", GroupName = "01-Entrada", Order = 9)]
        public bool UseCompressionFilter { get; set; } = true;

        [NinjaScriptProperty]
        [Range(2, 30)]
        [Display(Name = "CompressionLookback", Description = "Velas previas para medir compresión", GroupName = "01-Entrada", Order = 10)]
        public int CompressionLookback { get; set; } = 6;

        [NinjaScriptProperty]
        [Range(1, 200)]
        [Display(Name = "CompressionMaxRangeTicks", Description = "Rango máximo (en ticks) permitido en las velas previas", GroupName = "01-Entrada", Order = 11)]
        public int CompressionMaxRangeTicks { get; set; } = 30;

        // ====== Filtros OR (tamaño) y Tiempo (minutos tras 08:35) ======
        [NinjaScriptProperty]
        [Range(1, double.MaxValue)]
        [Display(Name = "MaxORPoints", Description = "Rango máximo del OR en puntos para habilitar señales (p. ej. 45–60)", GroupName = "02-Filtros", Order = 100)]
        public double MaxORPoints { get; set; } = 120;

        [NinjaScriptProperty]
        [Range(1, 120)]
        [Display(Name = "MaxMinutesAfterOR", Description = "Ventana máxima en minutos desde 08:35 para aceptar señales (p. ej. 30–40)", GroupName = "02-Filtros", Order = 110)]
        public int MaxMinutesAfterOR { get; set; } = 40;

        // ====== Contexto: zonas previas y premarket (visual) ======
        [NinjaScriptProperty]
        [Display(Name = "DrawPrevDayLevels", Description = "Dibuja High/Low/Close del día anterior", GroupName = "02-Contexto", Order = 120)]
        public bool DrawPrevDayLevels { get; set; } = false;

        [NinjaScriptProperty]
        [Display(Name = "DrawPremarketLevels", Description = "Dibuja High/Low del premarket (00:00–08:29)", GroupName = "02-Contexto", Order = 130)]
        public bool DrawPremarketLevels { get; set; } = false;

        [NinjaScriptProperty]
        [Display(Name = "ShowLevelLabels", Description = "Muestra etiquetas de texto junto a las líneas", GroupName = "02-Contexto", Order = 140)]
        public bool ShowLevelLabels { get; set; } = false;

        // Segmentos temporales
        [NinjaScriptProperty]
        [Display(Name = "SegmentLevels", Description = "Dibujar niveles como segmentos entre 08:30 y SegmentEndTT", GroupName = "02-Contexto", Order = 150)]
        public bool SegmentLevels { get; set; } = true;

        [NinjaScriptProperty]
        [Range(0, 235959)]
        [Display(Name = "SegmentEndTT (HHmmss)", Description = "Hora fin del segmento (por defecto 09:30 = 93000)", GroupName = "02-Contexto", Order = 160)]
        public int SegmentEndTT { get; set; } = 93000;

        // ====== NUEVO: filtro por distancia al precio ======
        [NinjaScriptProperty]
        [Display(Name = "EnableDistanceFilter", Description = "Oculta niveles lejos del precio", GroupName = "02-Contexto", Order = 170)]
        public bool EnableDistanceFilter { get; set; } = true;

        [NinjaScriptProperty]
        [Range(1, double.MaxValue)]
        [Display(Name = "DistanceFilterPoints", Description = "±puntos alrededor del precio para mostrar niveles", GroupName = "02-Contexto", Order = 180)]
        public double DistanceFilterPoints { get; set; } = 120;

        // ====== NUEVO: opción individual para Close Ayer (off por defecto) ======
        [NinjaScriptProperty]
        [Display(Name = "ShowPrevDayClose", Description = "Muestra Close del día anterior", GroupName = "02-Contexto", Order = 181)]
        public bool ShowPrevDayClose { get; set; } = false;

		[NinjaScriptProperty]
		[Display(Name = "LabelBarsShift", Description = "Desplazar etiquetas a la izquierda (en velas) respecto a 08:30", GroupName = "02-Contexto", Order = 182)]
		public int LabelBarsShift { get; set; } = 2;

		[NinjaScriptProperty]
		[Display(Name = "LabelYOffsetTicks", Description = "Desplazar etiquetas en vertical (ticks, positivo = arriba)", GroupName = "02-Contexto", Order = 183)]
		public int LabelYOffsetTicks { get; set; } = 0;

		[NinjaScriptProperty]
		[Display(Name = "MergeToleranceTicks", Description = "Funde PM-H con PreDayHigh (y PM-L con PreDayLow) si la distancia es menor o igual a este umbral", GroupName = "02-Contexto", Order = 184)]
		public int MergeToleranceTicks { get; set; } = 8;   // ~2 pts en MNQ

        // ====== NUEVO: Filtros de CONTEXTO ======
        [NinjaScriptProperty]
        [Display(Name = "UseTrendFilter", GroupName = "03-Contexto", Order = 200)]
        public bool UseTrendFilter { get; set; } = true;

        [NinjaScriptProperty]
        [Range(5, 200)]
        [Display(Name = "EmaLen", GroupName = "03-Contexto", Order = 210)]
        public int EmaLen { get; set; } = 20;

        [NinjaScriptProperty]
        [Range(0, 50)]
        [Display(Name = "SlopeMinTicks/Bar", Description = "Pendiente mínima de la EMA en ticks por barra", GroupName = "03-Contexto", Order = 220)]
        public int SlopeMinTicksPerBar { get; set; } = 1;

        [NinjaScriptProperty]
        [Display(Name = "UseLevelProximity", Description = "Evitar romper hacia niveles cercanos (PDH/PDL/PM-H/PM-L)", GroupName = "03-Contexto", Order = 230)]
        public bool UseLevelProximity { get; set; } = false;

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name = "LevelProximityTicks", Description = "Distancia mínima al nivel objetivo en ticks", GroupName = "03-Contexto", Order = 240)]
        public int LevelProximityTicks { get; set; } = 8;

        [NinjaScriptProperty]
        [Display(Name = "UseVolFlex", Description = "Permitir tolerancia de volumen", GroupName = "03-Contexto", Order = 250)]
        public bool UseVolFlex { get; set; } = true;

        [NinjaScriptProperty]
        [Range(0.0, 0.5)]
        [Display(Name = "VolFlexPct (0-0.5)", Description = "Relaja el umbral de volumen en este porcentaje (ej. 0.10 = 10%)", GroupName = "03-Contexto", Order = 260)]
        public double VolFlexPct { get; set; } = 0.10;

		// ====== Mostrar EMA en el chart ======
		[NinjaScriptProperty]
		[Display(Name = "ShowEMAPlot", Description = "Pinta la EMA de tendencia en el panel de precio", GroupName = "03-Contexto", Order = 265)]
		public bool ShowEMAPlot { get; set; } = true;

        // ========= Estado =========
        private bool dayInit, rangeActive, rangeFixed;
        private int  startBar = -1;                  // índice de la PRIMERA vela del OR (08:30 ó 08:31)
        private double rHigh = double.MinValue, rLow = double.MaxValue;

        // Rupturas débiles / trampa
        private int  weakDir = 0;                    // +1=up, -1=down
        private int  weakBar = -1;
        private bool reentered = false;
        private bool waitingSecond = false;

        // Indicadores
        private SMA smaVol;
        private EMA emaTrend;
        private ADX adxChop;
        private Swing swingPts;

        // Control de cierre de OR y filtros
        private DateTime orCloseTime = DateTime.MinValue;
        private string lastFilterReason = string.Empty;

        // Contexto (día anterior y premarket)
        private bool prevDayReady = false;
        private double prevDayHigh = double.NaN, prevDayLow = double.NaN, prevDayClose = double.NaN;

        private double preHigh = double.NaN, preLow = double.NaN;
        private bool premarketFrozen = false;

        // Dibujo único por día
        private bool levelsDrawnToday = false;
        private int openingBarIndex = -1;

        // Fin de segmento (índice de barra de fin, p. ej. 09:30)
        private int segmentEndIndex = -1;

        // Frecuencia: 1 LONG y 1 SHORT por ventana
        private bool longSignalPaintedThisWindow  = false;
        private bool shortSignalPaintedThisWindow = false;

        // Debug panel
        private string lastDiag = "";

        private string Tag(string key) => $"{key}_{Times[0][0]:yyyyMMdd}";

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "OR730735Signal";
                Calculate = Calculate.OnBarClose;
                IsOverlay = true;
                IsSuspendedWhileInactive = true;
                DrawOnPricePanel = true;

                // === Preset de defaults (para no configurar 4 pantallas) ===
                TickBuffer = 1;
                BodyPct = 0.50;
                VolFactor = 1.20;
                VolLen = 20;
                ReentryBars = 3;
                Include0730Bar = true;
                AnchorBoxAt0730 = true;
                MaxORPoints = 120;
                MaxMinutesAfterOR = 40;
                DrawPrevDayLevels = false;
                DrawPremarketLevels = false;
                ShowLevelLabels = false;
                SegmentLevels = true;
                SegmentEndTT = 93000;
                EnableDistanceFilter = true;
                DistanceFilterPoints = 120;
                ShowPrevDayClose = false;
                LabelBarsShift = 2;
                LabelYOffsetTicks = 0;
                MergeToleranceTicks = 8;
                UseTrendFilter = true;
                EmaLen = 20;
                SlopeMinTicksPerBar = 1;
                UseLevelProximity = false;
                LevelProximityTicks = 8;
                UseVolFlex = true;
                VolFlexPct = 0.10;
                ShowEMAPlot = true;


                MaxORPoints = 50;
                MaxMinutesAfterOR = 40;              // <<< por defecto 40 min

                DrawPrevDayLevels = true;
                DrawPremarketLevels = true;
                ShowLevelLabels = true;

                SegmentLevels = true;
                SegmentEndTT = 93000; // 09:30

                EnableDistanceFilter = true;
                DistanceFilterPoints = 120;

                ShowPrevDayClose = false;

                Include0730Bar = true;               // <<< recomendado: OR incluye 08:30
                AnchorBoxAt0730 = true;

				AddPlot(Brushes.DeepSkyBlue, "EMAtrend");   // Plot[0]
            }
            else if (State == State.DataLoaded)
            {
                smaVol   = SMA(Volume, Math.Max(VolLen, 2));
                emaTrend = EMA(Close,  Math.Max(EmaLen, 2));

                adxChop  = ADX(ADX_LEN);
                swingPts = Swing(SWING_STRENGTH);
            }
        }

        // ===== Helpers =====
        private int MinutesSinceORClose(DateTime t)
        {
            if (orCloseTime == DateTime.MinValue)
                return int.MaxValue;
            return (int)Math.Floor((t - orCloseTime).TotalMinutes);
        }

        private bool InWindowPostOR(int tt) => tt > END_TT && tt <= EXTEND_TT;

        private bool PassesFilters(out string reason)
        {
            reason = string.Empty;

            if (!rangeFixed)
            {
                reason = "Caja OR aún no congelada";
                return false;
            }

            if (double.IsNaN(rHigh) || double.IsNaN(rLow) || rHigh <= rLow)
            {
                reason = "OR inválido (High/Low)";
                return false;
            }

            double orRange = Math.Abs(rHigh - rLow);
            if (orRange > MaxORPoints)
            {
                reason = $"OR grande: {orRange:F2} > {MaxORPoints:F2}";
                return false;
            }

            int mins = MinutesSinceORClose(Time[0]);
            if (mins < 0)
            {
                reason = "Antes del cierre OR";
                return false;
            }
            if (mins > MaxMinutesAfterOR)
            {
                reason = $"Fuera de ventana: {mins}m > {MaxMinutesAfterOR}m";
                return false;
            }

            return true;
        }

        private bool TryComputePreviousDayLevels(out double pdh, out double pdl, out double pdc)
        {
            pdh = double.MinValue; pdl = double.MaxValue; pdc = double.NaN;
            DateTime prevDate = Time[0].Date.AddDays(-1);
            bool foundAny = false;

            for (int i = 1; i <= CurrentBar; i++)
            {
                if (Time[i].Date == prevDate)
                {
                    if (!foundAny) { pdc = Close[i]; foundAny = true; }
                    if (High[i] > pdh) pdh = High[i];
                    if (Low[i]  < pdl) pdl = Low[i];
                }
                else if (foundAny)
                    break;
            }
            if (!foundAny) { pdh = pdl = double.NaN; }
            return foundAny;
        }

        private bool IsWithinDistance(double level)
        {
            if (!EnableDistanceFilter) return true;
            return Math.Abs(level - Close[0]) <= DistanceFilterPoints;
        }

        private void RemoveLevelGraphics(string baseId)
        {
            RemoveDrawObject(baseId + "_lbl");
            RemoveDrawObject(baseId + "_seg");
        }

        private void DrawLabelAt0730(string id, string label, double price)
        {
            if (!ShowLevelLabels) return;

            int baseBarsAgo = (openingBarIndex >= 0) ? Math.Max(0, CurrentBar - openingBarIndex) : 0;
            int barsAgoForLabel = Math.Min(CurrentBar, baseBarsAgo + Math.Max(0, LabelBarsShift));

            double y = price + (LabelYOffsetTicks * TickSize);

            Draw.Text(this, id + "_lbl", label, barsAgoForLabel, y, Brushes.Gray);
        }

        private void DrawLevelSegment(string id, double price, Brush color, int startAgo, int endAgo)
        {
            if (double.IsNaN(price)) return;
            double half = Math.Max(TickSize * 0.2, TickSize * 0.2);
            var sc = (color as SolidColorBrush)?.Color ?? Colors.Gray;
            var fill = new SolidColorBrush(Color.FromArgb(45, sc.R, sc.G, sc.B));
            Draw.Rectangle(this, id + "_seg", false, startAgo, price + half, endAgo, price - half, fill, color, 1);
        }

        private void DrawContextLabelsAt0730()
        {
            string dayTag = Times[0][0].ToString("yyyyMMdd");
            double tolMerge = MergeToleranceTicks * TickSize;
            double tolStack = Math.Max(TickSize, 2 * TickSize);

            prevDayReady = TryComputePreviousDayLevels(out prevDayHigh, out prevDayLow, out prevDayClose);

            bool hasPDH = DrawPrevDayLevels && prevDayReady && !double.IsNaN(prevDayHigh);
            bool hasPMH = DrawPremarketLevels && premarketFrozen && !double.IsNaN(preHigh);

            if (hasPDH && hasPMH)
            {
                double d = Math.Abs(prevDayHigh - preHigh);
                if (d <= tolMerge)
                {
                    double y = (prevDayHigh + preHigh) * 0.5;
                    DrawLabelAt0730($"PDxPM_H_{dayTag}", "PM-H / PreDayHigh", y);
                    RemoveLevelGraphics($"PDH_{dayTag}");
                    RemoveLevelGraphics($"PMH_{dayTag}");
                }
                else
                {
                    double shift = (d <= tolStack ? TickSize : 0.0);
                    DrawLabelAt0730($"PMH_{dayTag}", "PM-H", preHigh);
                    DrawLabelAt0730($"PDH_{dayTag}", "PreDayHigh", prevDayHigh + shift);
                }
            }
            else
            {
                if (hasPMH) DrawLabelAt0730($"PMH_{dayTag}", "PM-H", preHigh);
                if (hasPDH) DrawLabelAt0730($"PDH_{dayTag}", "PreDayHigh", prevDayHigh);
            }

            bool hasPDL = DrawPrevDayLevels && prevDayReady && !double.IsNaN(prevDayLow);
            bool hasPML = DrawPremarketLevels && premarketFrozen && !double.IsNaN(preLow);

            if (hasPDL && hasPML)
            {
                double d = Math.Abs(prevDayLow - preLow);
                if (d <= tolMerge)
                {
                    double y = (prevDayLow + preLow) * 0.5;
                    DrawLabelAt0730($"PDxPM_L_{dayTag}", "PM-L / PreDayLow", y);
                    RemoveLevelGraphics($"PDL_{dayTag}");
                    RemoveLevelGraphics($"PML_{dayTag}");
                }
                else
                {
                    double shift = (d <= tolStack ? -TickSize : 0.0);
                    DrawLabelAt0730($"PML_{dayTag}", "PM-L", preLow);
                    DrawLabelAt0730($"PDL_{dayTag}", "PreDayLow", prevDayLow + shift);
                }
            }
            else
            {
                if (hasPML) DrawLabelAt0730($"PML_{dayTag}", "PM-L", preLow);
                if (hasPDL) DrawLabelAt0730($"PDL_{dayTag}", "PreDayLow", prevDayLow);
            }

            if (DrawPrevDayLevels && prevDayReady)
            {
                if (ShowPrevDayClose)
                    DrawLabelAt0730($"PDC_{dayTag}", "PreDayClose", prevDayClose);
                else
                    RemoveLevelGraphics($"PDC_{dayTag}");
            }
        }

        private void UpdateLevelSegments()
        {
            if (!levelsDrawnToday || !SegmentLevels) return;

            string dayTag = Times[0][0].ToString("yyyyMMdd");
            int startAgo = (openingBarIndex >= 0) ? Math.Max(0, CurrentBar - openingBarIndex) : 0;
            int endAgo   = (segmentEndIndex >= 0) ? Math.Max(0, CurrentBar - segmentEndIndex) : 0;

            if (DrawPrevDayLevels && prevDayReady)
            {
                if (IsWithinDistance(prevDayHigh))
                    DrawLevelSegment($"PDH_{dayTag}", prevDayHigh, Brushes.Red, startAgo, endAgo);
                else
                    RemoveLevelGraphics($"PDH_{dayTag}");

                if (IsWithinDistance(prevDayLow))
                    DrawLevelSegment($"PDL_{dayTag}", prevDayLow, Brushes.Red, startAgo, endAgo);
                else
                    RemoveLevelGraphics($"PDL_{dayTag}");

                if (ShowPrevDayClose)
                {
                    if (IsWithinDistance(prevDayClose))
                        DrawLevelSegment($"PDC_{dayTag}", prevDayClose, Brushes.IndianRed, startAgo, endAgo);
                    else
                        RemoveLevelGraphics($"PDC_{dayTag}");
                }
                else
                {
                    RemoveLevelGraphics($"PDC_{dayTag}");
                }
            }

            if (DrawPremarketLevels && premarketFrozen)
            {
                if (IsWithinDistance(preHigh))
                    DrawLevelSegment($"PMH_{dayTag}", preHigh, Brushes.SteelBlue, startAgo, endAgo);
                else
                    RemoveLevelGraphics($"PMH_{dayTag}");

                if (IsWithinDistance(preLow))
                    DrawLevelSegment($"PML_{dayTag}", preLow, Brushes.SteelBlue, startAgo, endAgo);
                else
                    RemoveLevelGraphics($"PML_{dayTag}");
            }
        }

        // ======= CONTEXTO: helpers =======
        private bool TrendOK(bool isLong, out string reason)
        {
            reason = "";
            if (!UseTrendFilter)
                return true;

            if (emaTrend == null || CurrentBar < Math.Max(EmaLen, 2))
            {
                reason = "EMA no lista";
                return false;
            }

            double emaNow = emaTrend[0];
            double emaPrev = emaTrend[1];
            double slope = emaNow - emaPrev;
            double minSlope = SlopeMinTicksPerBar * TickSize;

            if (isLong)
            {
                if (!(Close[0] > emaNow)) { reason = "Close<=EMA"; return false; }
                if (!(slope >  minSlope)) { reason = "Slope EMA débil"; return false; }
            }
            else
            {
                if (!(Close[0] < emaNow)) { reason = "Close>=EMA"; return false; }
                if (!(slope < -minSlope)) { reason = "Slope EMA débil"; return false; }
            }
            return true;
        }

        private bool LevelProximityOK(bool isLong, out string reason)
        {
            reason = "";
            if (!UseLevelProximity) return true;

            double prox = LevelProximityTicks * TickSize;
            double nearest = double.PositiveInfinity;

            if (isLong)
            {
                if (DrawPrevDayLevels && prevDayReady && !double.IsNaN(prevDayHigh))
                    nearest = Math.Min(nearest, Math.Abs(prevDayHigh - Close[0]));
                if (DrawPremarketLevels && premarketFrozen && !double.IsNaN(preHigh))
                    nearest = Math.Min(nearest, Math.Abs(preHigh - Close[0]));
            }
            else
            {
                if (DrawPrevDayLevels && prevDayReady && !double.IsNaN(prevDayLow))
                    nearest = Math.Min(nearest, Math.Abs(prevDayLow - Close[0]));
                if (DrawPremarketLevels && premarketFrozen && !double.IsNaN(preLow))
                    nearest = Math.Min(nearest, Math.Abs(preLow - Close[0]));
            }

            if (double.IsPositiveInfinity(nearest)) return true;

            if (nearest < prox)
            {
                reason = $"Nivel cerca ({nearest/TickSize:F0}t < {LevelProximityTicks}t)";
                return false;
            }
            return true;
        }

        private bool AdxOK(out string reason)
        {
            reason = "";
            if (adxChop == null || CurrentBar < ADX_LEN + 2)
            {
                reason = "ADX no listo";
                return false;
            }
            double a = adxChop[0];
            if (a < MIN_ADX)
            {
                reason = $"ADX bajo ({a:F1} < {MIN_ADX:F0})";
                return false;
            }
            return true;
        }

        private double RiskUsd(double entry, double stop)
        {
            double pv = Instrument.MasterInstrument.PointValue;
            return Math.Abs(entry - stop) * pv * Math.Max(1, RISK_QTY);
        }

        private bool TrySelectStop(bool isLong, double entry, out double stopSelected, out double riskSelected, out string why)
        {
            stopSelected = double.NaN;
            riskSelected = double.NaN;
            why = "";

            double buffer = TickBuffer * TickSize;

            // 1) OR-stop (default cuando el riesgo es aceptable)
            double stopOR = isLong ? (rLow - buffer) : (rHigh + buffer);
            double riskOR = RiskUsd(entry, stopOR);

            // 2) Estructura (Swing) — se usa solo cuando riskOR > 200
            bool swingValid = false;
            double stopSwing = double.NaN;
            double riskSwing = double.PositiveInfinity;

            if (swingPts != null)
            {
                if (isLong)
                {
                    double swLow = swingPts.SwingLow[0];
                    // válido si existe y queda por debajo del entry (stop lógico para long)
                    if (!double.IsNaN(swLow))
                    {
                        double candidate = swLow - (STRUCT_BUFFER_TICKS * TickSize);
                        if (candidate < entry)
                        {
                            swingValid = true;
                            stopSwing = candidate;
                            riskSwing = RiskUsd(entry, stopSwing);
                        }
                    }
                }
                else
                {
                    double swHigh = swingPts.SwingHigh[0];
                    // válido si existe y queda por encima del entry (stop lógico para short)
                    if (!double.IsNaN(swHigh))
                    {
                        double candidate = swHigh + (STRUCT_BUFFER_TICKS * TickSize);
                        if (candidate > entry)
                        {
                            swingValid = true;
                            stopSwing = candidate;
                            riskSwing = RiskUsd(entry, stopSwing);
                        }
                    }
                }
            }

            // ===== Política solicitada =====
            // Si riskOR <= 200 => usar OR-stop
            // Si riskOR  > 200 => usar última estructura (Swing). Si no hay, NO TRADE.
            if (riskOR <= RISK_HIGH_USD)
            {
                stopSelected = stopOR;
                riskSelected = riskOR;
                why = $"SL=OR (riskOR={riskOR:F0}<= {RISK_HIGH_USD:F0})";
            }
            else
            {
                if (!swingValid)
                {
                    why = $"NO TRADE (riskOR={riskOR:F0}> {RISK_HIGH_USD:F0} y no hay swing válido)";
                    return false;
                }

                stopSelected = stopSwing;
                riskSelected = riskSwing;
                why = $"SL=SWING (riskOR={riskOR:F0}> {RISK_HIGH_USD:F0})";
            }

            // Límite duro (si lo quieres conservar)
            if (riskSelected > MAX_RISK_USD)
            {
                why = $"NO TRADE (riskSel={riskSelected:F0} > {MAX_RISK_USD:F0})";
                return false;
            }

            return true;
        }

		private void DebugOnChart(string msg)
		{
		    if (!SHOW_DEBUG_ON_CHART) return;
		
		    lastDiag = msg;
		
		    // ÚNICO panel fijo (se sobre-escribe siempre, no se acumula)
		    Draw.TextFixed(
		        this,
		        Tag("DBG_PANEL"),
		        lastDiag,
		        TextPosition.TopLeft,
		        Brushes.LightGray,
		        new SimpleFont("Consolas", 12),
		        Brushes.Transparent,
		        Brushes.Transparent,
		        0
		    );
		}
		
		// Overload para llamadas existentes DebugOnChart(msg, true/false)
		private void DebugOnChart(string msg, bool isLong)
		{
		    DebugOnChart(msg);
		}



        protected override void OnBarUpdate()
        {
            if (CurrentBar < Math.Max(Math.Max(VolLen, EmaLen), 10)) return;

			// ---- plot de EMA para validación visual ----
			if (emaTrend != null)
			{
			    Values[0][0] = emaTrend[0];
			    if (!ShowEMAPlot)
			        PlotBrushes[0][0] = Brushes.Transparent;
			    else
			        PlotBrushes[0][0] = Brushes.DeepSkyBlue;
			}

            // Reset por cambio de día
            if (!dayInit || Time[0].Date != Time[1].Date)
                ResetDay();

            int tt = ToTime(Time[0]);

            // Premarket 00:00–08:29
            if (tt < START_TT)
            {
                if (double.IsNaN(preHigh)) preHigh = High[0]; else preHigh = Math.Max(preHigh, High[0]);
                if (double.IsNaN(preLow))  preLow  = Low[0];  else preLow  = Math.Min(preLow,  Low[0]);
            }

            // 08:30: congelar premarket, dibujar etiquetas y reset flags de ventana
            if (tt == START_TT && !levelsDrawnToday)
            {
                premarketFrozen = true;
                openingBarIndex = CurrentBar;
                DrawContextLabelsAt0730();
                levelsDrawnToday = true;

                longSignalPaintedThisWindow  = false;
                shortSignalPaintedThisWindow = false;

                // limpiar panel debug al inicio del día
                Draw.TextFixed(this, Tag("DBG_PANEL"), "", TextPosition.TopLeft, Brushes.Transparent, new SimpleFont("Consolas", 12),
                    Brushes.Transparent, Brushes.Transparent, 0);
            }

            // fin de segmento (p.ej. 09:30)
            if (SegmentLevels && segmentEndIndex < 0 && tt >= SegmentEndTT)
                segmentEndIndex = CurrentBar;

            // === Inicio del OR ===
            int startInclTT = START_TT + (Include0730Bar ? 0 : 100);

            if (!rangeActive && !rangeFixed && tt == startInclTT)
            {
                rangeActive = true;
                startBar = CurrentBar;
                rHigh = High[0];
                rLow  = Low[0];
            }

            // === Construcción del OR ===
            if (rangeActive && !rangeFixed)
            {
                int barsIntoOR   = CurrentBar - startBar;
                int lastBarIndex = Include0730Bar ? 5 : 4;

                if (barsIntoOR >= 0 && barsIntoOR <= lastBarIndex)
                {
                    rHigh = Math.Max(rHigh, High[0]);
                    rLow  = Math.Min(rLow,  Low[0]);

                    int visualStartBar = AnchorBoxAt0730
                        ? startBar - (Include0730Bar ? 0 : 1)
                        : startBar;

                    int startAgo = Math.Max(0, CurrentBar - visualStartBar);
                    Draw.Rectangle(this, Tag("BOX"), false, startAgo, rHigh, 0, rLow,
                                   new SolidColorBrush(Color.FromArgb(80, 255, 215, 0)),
                                   Brushes.Gold, 1);
                }
            }

            // === Congelar OR al cerrar 08:35 ===
            if (!rangeFixed && rangeActive && tt == END_TT)
            {
                rangeFixed = true;
                orCloseTime = Time[0];

                int visualStartBar = AnchorBoxAt0730
                    ? startBar - (Include0730Bar ? 0 : 1)
                    : startBar;

                int startAgo = Math.Max(0, CurrentBar - visualStartBar);
                Draw.Rectangle(this, Tag("BOX"), false, startAgo, rHigh, 0, rLow,
                               new SolidColorBrush(Color.FromArgb(80, 255, 215, 0)),
                               Brushes.Gold, 1);
            }

            // === Mantener caja hasta 09:15 ===
            if (rangeFixed && tt <= EXTEND_TT && startBar >= 0)
            {
                int visualStartBar = AnchorBoxAt0730
                    ? startBar - (Include0730Bar ? 0 : 1)
                    : startBar;

                int startAgo = Math.Max(0, CurrentBar - visualStartBar);
                Draw.Rectangle(this, Tag("BOX"), false, startAgo, rHigh, 0, rLow,
                               new SolidColorBrush(Color.FromArgb(80, 255, 215, 0)),
                               Brushes.Gold, 1);
            }

            // === Actualizar segmentos/etiquetas según distancia ===
            UpdateLevelSegments();

            // === Señales: Breakout de calidad 08:35–09:15 ===
            if (rangeFixed && InWindowPostOR(tt))
            {
                // Filtros generales
                if (!PassesFilters(out lastFilterReason))
                {
                    Print($"{Time[0]:yyyy-MM-dd HH:mm} NO SIGNAL | Filtro general: {lastFilterReason}");
                    return;
                }

                double buffer   = TickBuffer * TickSize;
                double barRange = Math.Max(High[0] - Low[0], TickSize);
                double body     = Math.Abs(Close[0] - Open[0]);
                bool bodyOK     = body >= BodyPct * barRange;

                double volSMA   = smaVol[0];
                double volNow   = Volume[0];

                double volThreshold = VolFactor * volSMA * (UseVolFlex ? (1.0 - VolFlexPct) : 1.0);
                bool volOKLegacy    = volNow >= volThreshold;

                double rvol = (volSMA > 0 ? (volNow / volSMA) : 0.0);
                bool rvolOK = rvol >= MIN_RVOL;

                bool adxOK = AdxOK(out string adxReason);

                double upTrig   = rHigh + buffer;
                double dnTrig   = rLow  - buffer;
                bool crossesUp   = Close[0] >= upTrig;
                bool crossesDown = Close[0] <= dnTrig;


                // ---- Filtros de calidad (displacement + compresión) ----
                bool compressionOK = true;
                double preRangeTicks = double.NaN;

                if (UseCompressionFilter)
                {
                    if (CurrentBar <= CompressionLookback)
                    {
                        compressionOK = false;
                    }
                    else
                    {
                        double preHi = High[1];
                        double preLo = Low[1];
                        for (int i = 2; i <= CompressionLookback; i++)
                        {
                            preHi = Math.Max(preHi, High[i]);
                            preLo = Math.Min(preLo, Low[i]);
                        }
                        preRangeTicks = (preHi - preLo) / TickSize;
                        compressionOK = preRangeTicks <= CompressionMaxRangeTicks;
                    }
                }

                double extLongTicks  = (Close[0] - upTrig) / TickSize;   // cierre vs trigger long
                double extShortTicks = (dnTrig - Close[0]) / TickSize;   // cierre vs trigger short
                bool displacementLongOK  = !UseDisplacementFilter || (crossesUp   && extLongTicks  >= DisplacementTicks);
                bool displacementShortOK = !UseDisplacementFilter || (crossesDown && extShortTicks >= DisplacementTicks);

                bool trendLongOK  = TrendOK(true,  out string trL);
                bool trendShortOK = TrendOK(false, out string trS);
                bool levelLongOK  = LevelProximityOK(true,  out string lvL);
                bool levelShortOK = LevelProximityOK(false, out string lvS);

                // ===== LONG attempt =====
                if (crossesUp)
                {
                    if (longSignalPaintedThisWindow)
                    {
                        DebugOnChart($"SKIP LONG: already signaled | {Time[0]:HH:mm}", true);
                    }
                    else
                    {
                        // arma razón principal (primera que falle)
                        string whyFail = "";
                    if (UseCompressionFilter && !compressionOK)
                        whyFail = $"Compression fail (preRange={preRangeTicks:F0}t > {CompressionMaxRangeTicks}t)";
                    else if (UseDisplacementFilter && !displacementLongOK)
                        whyFail = $"Displacement fail (ext={extLongTicks:F1}t < {DisplacementTicks}t)";

                        if (!bodyOK)          whyFail = $"Body fail ({body/barRange:F2} < {BodyPct:F2})";
                        else if (!volOKLegacy)whyFail = $"Vol fail ({volNow:F0} < {volThreshold:F0})";
                        else if (!rvolOK)     whyFail = $"RVOL fail ({rvol:F2} < {MIN_RVOL:F2})";
                        else if (!adxOK)      whyFail = adxReason;
                        else if (!trendLongOK)whyFail = $"Trend fail ({trL})";
                        else if (!levelLongOK)whyFail = $"Level fail ({lvL})";

                        bool allOK = bodyOK && volOKLegacy && rvolOK && adxOK && trendLongOK && levelLongOK;

                        double entry = Close[0];

                        if (allOK)
                        {
                            if (TrySelectStop(true, entry, out double stopSel, out double riskSel, out string whyStop))
                            {
                                Draw.ArrowUp(this, Tag("BreakoutUp"), false, 0, Low[0] - 2 * TickSize, Brushes.White);
//                                Draw.HorizontalLine(this, Tag("SL_LONG"), stopSel, Brushes.Red);

                                string msg = $"LONG OK {Time[0]:HH:mm} | entry={entry:F2} SL={stopSel:F2} risk$={riskSel:F0} | RVOL={rvol:F2} ADX={adxChop[0]:F1} | {whyStop}";
                                DebugOnChart(msg, true);
                                Print($"{Time[0]:yyyy-MM-dd HH:mm} {msg}");
                                longSignalPaintedThisWindow = true;

                                waitingSecond = false; weakDir = 0; weakBar = -1; reentered = false;
                            }
                            else
                            {
                                string msg = $"NO TRADE LONG {Time[0]:HH:mm} | entry={entry:F2} | RVOL={rvol:F2} ADX={adxChop[0]:F1} | {whyStop}";
                                DebugOnChart(msg, true);
                                Print($"{Time[0]:yyyy-MM-dd HH:mm} {msg}");
                            }
                        }
                        else
                        {
                            string msg = $"NO SIGNAL LONG {Time[0]:HH:mm} | {whyFail} | RVOL={rvol:F2} ADX={(adxChop!=null?adxChop[0]:double.NaN):F1}";
                            DebugOnChart(msg, true);
                            Print($"{Time[0]:yyyy-MM-dd HH:mm} {msg}");
                        }
                    }
                }

                // ===== SHORT attempt =====
                if (crossesDown)
                {
                    if (shortSignalPaintedThisWindow)
                    {
                        DebugOnChart($"SKIP SHORT: already signaled | {Time[0]:HH:mm}", false);
                    }
                    else
                    {
                        string whyFail = "";
                    if (UseCompressionFilter && !compressionOK)
                        whyFail = $"Compression fail (preRange={preRangeTicks:F0}t > {CompressionMaxRangeTicks}t)";
                    else if (UseDisplacementFilter && !displacementShortOK)
                        whyFail = $"Displacement fail (ext={extShortTicks:F1}t < {DisplacementTicks}t)";

                        if (!bodyOK)           whyFail = $"Body fail ({body/barRange:F2} < {BodyPct:F2})";
                        else if (!volOKLegacy) whyFail = $"Vol fail ({volNow:F0} < {volThreshold:F0})";
                        else if (!rvolOK)      whyFail = $"RVOL fail ({rvol:F2} < {MIN_RVOL:F2})";
                        else if (!adxOK)       whyFail = adxReason;
                        else if (!trendShortOK)whyFail = $"Trend fail ({trS})";
                        else if (!levelShortOK)whyFail = $"Level fail ({lvS})";

                        bool allOK = bodyOK && volOKLegacy && rvolOK && adxOK && trendShortOK && levelShortOK;

                        double entry = Close[0];

                        if (allOK)
                        {
                            if (TrySelectStop(false, entry, out double stopSel, out double riskSel, out string whyStop))
                            {
                                Draw.ArrowDown(this, Tag("BreakoutDn"), false, 0, High[0] + 2 * TickSize, Brushes.White);
//                                Draw.HorizontalLine(this, Tag("SL_SHORT"), stopSel, Brushes.Red);

                                string msg = $"SHORT OK {Time[0]:HH:mm} | entry={entry:F2} SL={stopSel:F2} risk$={riskSel:F0} | RVOL={rvol:F2} ADX={adxChop[0]:F1} | {whyStop}";
                                DebugOnChart(msg, false);
                                Print($"{Time[0]:yyyy-MM-dd HH:mm} {msg}");
                                shortSignalPaintedThisWindow = true;

                                waitingSecond = false; weakDir = 0; weakBar = -1; reentered = false;
                            }
                            else
                            {
                                string msg = $"NO TRADE SHORT {Time[0]:HH:mm} | entry={entry:F2} | RVOL={rvol:F2} ADX={adxChop[0]:F1} | {whyStop}";
                                DebugOnChart(msg, false);
                                Print($"{Time[0]:yyyy-MM-dd HH:mm} {msg}");
                            }
                        }
                        else
                        {
                            string msg = $"NO SIGNAL SHORT {Time[0]:HH:mm} | {whyFail} | RVOL={rvol:F2} ADX={(adxChop!=null?adxChop[0]:double.NaN):F1}";
                            DebugOnChart(msg, false);
                            Print($"{Time[0]:yyyy-MM-dd HH:mm} {msg}");
                        }
                    }
                }
            }

            // === Trampa: weak → reingreso → ruptura opuesta ≤ ReentryBars ===
            if (rangeFixed && weakDir != 0 && weakBar >= 0)
            {
                if (!reentered && Close[0] <= rHigh && Close[0] >= rLow)
                    reentered = true;

                double buffer = TickBuffer * TickSize;
                bool oppositeBreak =
                    (weakDir == +1 && Close[0] <= (rLow  - buffer)) ||
                    (weakDir == -1 && Close[0] >= (rHigh + buffer));

                if (reentered && oppositeBreak && CurrentBar - weakBar <= Math.Max(1, ReentryBars))
                {
                    Draw.Diamond(this, Tag($"False_{CurrentBar}"), false, 0,
                        weakDir == +1 ? (High[0] + 2*TickSize) : (Low[0] - 2*TickSize),
                        Brushes.MediumPurple);
                    waitingSecond = true;
                    weakBar = -1; reentered = false;
                }

                if (weakBar >= 0 && CurrentBar - weakBar > Math.Max(1, ReentryBars))
                {
                    weakDir = 0; weakBar = -1; reentered = false; waitingSecond = false;
                }
            }
        }

        private void ResetDay()
        {
            dayInit = true;
            rangeActive = false;
            rangeFixed = false;

            startBar = -1;
            rHigh = double.MinValue;
            rLow  = double.MaxValue;

            weakDir = 0; weakBar = -1; reentered = false; waitingSecond = false;

            orCloseTime = DateTime.MinValue;

            prevDayReady = false;
            prevDayHigh = prevDayLow = prevDayClose = double.NaN;

            preHigh = preLow = double.NaN;
            premarketFrozen = false;

            levelsDrawnToday = false;
            openingBarIndex = -1;

            segmentEndIndex = -1;

            longSignalPaintedThisWindow  = false;
            shortSignalPaintedThisWindow = false;

            lastDiag = "";

            RemoveDrawObject(Tag("BOX"));
            RemoveDrawObject(Tag("BreakoutUp"));
            RemoveDrawObject(Tag("BreakoutDn"));
            RemoveDrawObject(Tag("SL_LONG"));
            RemoveDrawObject(Tag("SL_SHORT"));
            RemoveDrawObject(Tag("DBG_PANEL"));
        }
    }
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private OR730735Signal[] cacheOR730735Signal;
		public OR730735Signal OR730735Signal(int tickBuffer, double bodyPct, double volFactor, int volLen, int reentryBars, bool include0730Bar, bool anchorBoxAt0730, bool useDisplacementFilter, int displacementTicks, bool useCompressionFilter, int compressionLookback, int compressionMaxRangeTicks, double maxORPoints, int maxMinutesAfterOR, bool drawPrevDayLevels, bool drawPremarketLevels, bool showLevelLabels, bool segmentLevels, int segmentEndTT, bool enableDistanceFilter, double distanceFilterPoints, bool showPrevDayClose, int labelBarsShift, int labelYOffsetTicks, int mergeToleranceTicks, bool useTrendFilter, int emaLen, int slopeMinTicksPerBar, bool useLevelProximity, int levelProximityTicks, bool useVolFlex, double volFlexPct, bool showEMAPlot)
		{
			return OR730735Signal(Input, tickBuffer, bodyPct, volFactor, volLen, reentryBars, include0730Bar, anchorBoxAt0730, useDisplacementFilter, displacementTicks, useCompressionFilter, compressionLookback, compressionMaxRangeTicks, maxORPoints, maxMinutesAfterOR, drawPrevDayLevels, drawPremarketLevels, showLevelLabels, segmentLevels, segmentEndTT, enableDistanceFilter, distanceFilterPoints, showPrevDayClose, labelBarsShift, labelYOffsetTicks, mergeToleranceTicks, useTrendFilter, emaLen, slopeMinTicksPerBar, useLevelProximity, levelProximityTicks, useVolFlex, volFlexPct, showEMAPlot);
		}

		public OR730735Signal OR730735Signal(ISeries<double> input, int tickBuffer, double bodyPct, double volFactor, int volLen, int reentryBars, bool include0730Bar, bool anchorBoxAt0730, bool useDisplacementFilter, int displacementTicks, bool useCompressionFilter, int compressionLookback, int compressionMaxRangeTicks, double maxORPoints, int maxMinutesAfterOR, bool drawPrevDayLevels, bool drawPremarketLevels, bool showLevelLabels, bool segmentLevels, int segmentEndTT, bool enableDistanceFilter, double distanceFilterPoints, bool showPrevDayClose, int labelBarsShift, int labelYOffsetTicks, int mergeToleranceTicks, bool useTrendFilter, int emaLen, int slopeMinTicksPerBar, bool useLevelProximity, int levelProximityTicks, bool useVolFlex, double volFlexPct, bool showEMAPlot)
		{
			if (cacheOR730735Signal != null)
				for (int idx = 0; idx < cacheOR730735Signal.Length; idx++)
					if (cacheOR730735Signal[idx] != null && cacheOR730735Signal[idx].TickBuffer == tickBuffer && cacheOR730735Signal[idx].BodyPct == bodyPct && cacheOR730735Signal[idx].VolFactor == volFactor && cacheOR730735Signal[idx].VolLen == volLen && cacheOR730735Signal[idx].ReentryBars == reentryBars && cacheOR730735Signal[idx].Include0730Bar == include0730Bar && cacheOR730735Signal[idx].AnchorBoxAt0730 == anchorBoxAt0730 && cacheOR730735Signal[idx].UseDisplacementFilter == useDisplacementFilter && cacheOR730735Signal[idx].DisplacementTicks == displacementTicks && cacheOR730735Signal[idx].UseCompressionFilter == useCompressionFilter && cacheOR730735Signal[idx].CompressionLookback == compressionLookback && cacheOR730735Signal[idx].CompressionMaxRangeTicks == compressionMaxRangeTicks && cacheOR730735Signal[idx].MaxORPoints == maxORPoints && cacheOR730735Signal[idx].MaxMinutesAfterOR == maxMinutesAfterOR && cacheOR730735Signal[idx].DrawPrevDayLevels == drawPrevDayLevels && cacheOR730735Signal[idx].DrawPremarketLevels == drawPremarketLevels && cacheOR730735Signal[idx].ShowLevelLabels == showLevelLabels && cacheOR730735Signal[idx].SegmentLevels == segmentLevels && cacheOR730735Signal[idx].SegmentEndTT == segmentEndTT && cacheOR730735Signal[idx].EnableDistanceFilter == enableDistanceFilter && cacheOR730735Signal[idx].DistanceFilterPoints == distanceFilterPoints && cacheOR730735Signal[idx].ShowPrevDayClose == showPrevDayClose && cacheOR730735Signal[idx].LabelBarsShift == labelBarsShift && cacheOR730735Signal[idx].LabelYOffsetTicks == labelYOffsetTicks && cacheOR730735Signal[idx].MergeToleranceTicks == mergeToleranceTicks && cacheOR730735Signal[idx].UseTrendFilter == useTrendFilter && cacheOR730735Signal[idx].EmaLen == emaLen && cacheOR730735Signal[idx].SlopeMinTicksPerBar == slopeMinTicksPerBar && cacheOR730735Signal[idx].UseLevelProximity == useLevelProximity && cacheOR730735Signal[idx].LevelProximityTicks == levelProximityTicks && cacheOR730735Signal[idx].UseVolFlex == useVolFlex && cacheOR730735Signal[idx].VolFlexPct == volFlexPct && cacheOR730735Signal[idx].ShowEMAPlot == showEMAPlot && cacheOR730735Signal[idx].EqualsInput(input))
						return cacheOR730735Signal[idx];
			return CacheIndicator<OR730735Signal>(new OR730735Signal(){ TickBuffer = tickBuffer, BodyPct = bodyPct, VolFactor = volFactor, VolLen = volLen, ReentryBars = reentryBars, Include0730Bar = include0730Bar, AnchorBoxAt0730 = anchorBoxAt0730, UseDisplacementFilter = useDisplacementFilter, DisplacementTicks = displacementTicks, UseCompressionFilter = useCompressionFilter, CompressionLookback = compressionLookback, CompressionMaxRangeTicks = compressionMaxRangeTicks, MaxORPoints = maxORPoints, MaxMinutesAfterOR = maxMinutesAfterOR, DrawPrevDayLevels = drawPrevDayLevels, DrawPremarketLevels = drawPremarketLevels, ShowLevelLabels = showLevelLabels, SegmentLevels = segmentLevels, SegmentEndTT = segmentEndTT, EnableDistanceFilter = enableDistanceFilter, DistanceFilterPoints = distanceFilterPoints, ShowPrevDayClose = showPrevDayClose, LabelBarsShift = labelBarsShift, LabelYOffsetTicks = labelYOffsetTicks, MergeToleranceTicks = mergeToleranceTicks, UseTrendFilter = useTrendFilter, EmaLen = emaLen, SlopeMinTicksPerBar = slopeMinTicksPerBar, UseLevelProximity = useLevelProximity, LevelProximityTicks = levelProximityTicks, UseVolFlex = useVolFlex, VolFlexPct = volFlexPct, ShowEMAPlot = showEMAPlot }, input, ref cacheOR730735Signal);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.OR730735Signal OR730735Signal(int tickBuffer, double bodyPct, double volFactor, int volLen, int reentryBars, bool include0730Bar, bool anchorBoxAt0730, bool useDisplacementFilter, int displacementTicks, bool useCompressionFilter, int compressionLookback, int compressionMaxRangeTicks, double maxORPoints, int maxMinutesAfterOR, bool drawPrevDayLevels, bool drawPremarketLevels, bool showLevelLabels, bool segmentLevels, int segmentEndTT, bool enableDistanceFilter, double distanceFilterPoints, bool showPrevDayClose, int labelBarsShift, int labelYOffsetTicks, int mergeToleranceTicks, bool useTrendFilter, int emaLen, int slopeMinTicksPerBar, bool useLevelProximity, int levelProximityTicks, bool useVolFlex, double volFlexPct, bool showEMAPlot)
		{
			return indicator.OR730735Signal(Input, tickBuffer, bodyPct, volFactor, volLen, reentryBars, include0730Bar, anchorBoxAt0730, useDisplacementFilter, displacementTicks, useCompressionFilter, compressionLookback, compressionMaxRangeTicks, maxORPoints, maxMinutesAfterOR, drawPrevDayLevels, drawPremarketLevels, showLevelLabels, segmentLevels, segmentEndTT, enableDistanceFilter, distanceFilterPoints, showPrevDayClose, labelBarsShift, labelYOffsetTicks, mergeToleranceTicks, useTrendFilter, emaLen, slopeMinTicksPerBar, useLevelProximity, levelProximityTicks, useVolFlex, volFlexPct, showEMAPlot);
		}

		public Indicators.OR730735Signal OR730735Signal(ISeries<double> input , int tickBuffer, double bodyPct, double volFactor, int volLen, int reentryBars, bool include0730Bar, bool anchorBoxAt0730, bool useDisplacementFilter, int displacementTicks, bool useCompressionFilter, int compressionLookback, int compressionMaxRangeTicks, double maxORPoints, int maxMinutesAfterOR, bool drawPrevDayLevels, bool drawPremarketLevels, bool showLevelLabels, bool segmentLevels, int segmentEndTT, bool enableDistanceFilter, double distanceFilterPoints, bool showPrevDayClose, int labelBarsShift, int labelYOffsetTicks, int mergeToleranceTicks, bool useTrendFilter, int emaLen, int slopeMinTicksPerBar, bool useLevelProximity, int levelProximityTicks, bool useVolFlex, double volFlexPct, bool showEMAPlot)
		{
			return indicator.OR730735Signal(input, tickBuffer, bodyPct, volFactor, volLen, reentryBars, include0730Bar, anchorBoxAt0730, useDisplacementFilter, displacementTicks, useCompressionFilter, compressionLookback, compressionMaxRangeTicks, maxORPoints, maxMinutesAfterOR, drawPrevDayLevels, drawPremarketLevels, showLevelLabels, segmentLevels, segmentEndTT, enableDistanceFilter, distanceFilterPoints, showPrevDayClose, labelBarsShift, labelYOffsetTicks, mergeToleranceTicks, useTrendFilter, emaLen, slopeMinTicksPerBar, useLevelProximity, levelProximityTicks, useVolFlex, volFlexPct, showEMAPlot);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.OR730735Signal OR730735Signal(int tickBuffer, double bodyPct, double volFactor, int volLen, int reentryBars, bool include0730Bar, bool anchorBoxAt0730, bool useDisplacementFilter, int displacementTicks, bool useCompressionFilter, int compressionLookback, int compressionMaxRangeTicks, double maxORPoints, int maxMinutesAfterOR, bool drawPrevDayLevels, bool drawPremarketLevels, bool showLevelLabels, bool segmentLevels, int segmentEndTT, bool enableDistanceFilter, double distanceFilterPoints, bool showPrevDayClose, int labelBarsShift, int labelYOffsetTicks, int mergeToleranceTicks, bool useTrendFilter, int emaLen, int slopeMinTicksPerBar, bool useLevelProximity, int levelProximityTicks, bool useVolFlex, double volFlexPct, bool showEMAPlot)
		{
			return indicator.OR730735Signal(Input, tickBuffer, bodyPct, volFactor, volLen, reentryBars, include0730Bar, anchorBoxAt0730, useDisplacementFilter, displacementTicks, useCompressionFilter, compressionLookback, compressionMaxRangeTicks, maxORPoints, maxMinutesAfterOR, drawPrevDayLevels, drawPremarketLevels, showLevelLabels, segmentLevels, segmentEndTT, enableDistanceFilter, distanceFilterPoints, showPrevDayClose, labelBarsShift, labelYOffsetTicks, mergeToleranceTicks, useTrendFilter, emaLen, slopeMinTicksPerBar, useLevelProximity, levelProximityTicks, useVolFlex, volFlexPct, showEMAPlot);
		}

		public Indicators.OR730735Signal OR730735Signal(ISeries<double> input , int tickBuffer, double bodyPct, double volFactor, int volLen, int reentryBars, bool include0730Bar, bool anchorBoxAt0730, bool useDisplacementFilter, int displacementTicks, bool useCompressionFilter, int compressionLookback, int compressionMaxRangeTicks, double maxORPoints, int maxMinutesAfterOR, bool drawPrevDayLevels, bool drawPremarketLevels, bool showLevelLabels, bool segmentLevels, int segmentEndTT, bool enableDistanceFilter, double distanceFilterPoints, bool showPrevDayClose, int labelBarsShift, int labelYOffsetTicks, int mergeToleranceTicks, bool useTrendFilter, int emaLen, int slopeMinTicksPerBar, bool useLevelProximity, int levelProximityTicks, bool useVolFlex, double volFlexPct, bool showEMAPlot)
		{
			return indicator.OR730735Signal(input, tickBuffer, bodyPct, volFactor, volLen, reentryBars, include0730Bar, anchorBoxAt0730, useDisplacementFilter, displacementTicks, useCompressionFilter, compressionLookback, compressionMaxRangeTicks, maxORPoints, maxMinutesAfterOR, drawPrevDayLevels, drawPremarketLevels, showLevelLabels, segmentLevels, segmentEndTT, enableDistanceFilter, distanceFilterPoints, showPrevDayClose, labelBarsShift, labelYOffsetTicks, mergeToleranceTicks, useTrendFilter, emaLen, slopeMinTicksPerBar, useLevelProximity, levelProximityTicks, useVolFlex, volFlexPct, showEMAPlot);
		}
	}
}

#endregion
