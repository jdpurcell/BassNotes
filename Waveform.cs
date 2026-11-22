using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace BassNotes;

static class Palette {
	public static readonly IBrush Background = Brush.Parse("#10161F");
	public static readonly IBrush Panel = Brush.Parse("#1A2431");
	public static readonly IBrush Grid = Brush.Parse("#324254");
	public static readonly IBrush Muted = Brush.Parse("#A4B5C8");
	public static readonly IBrush Accent = Brush.Parse("#60E0CC");
	public static readonly IBrush Selection = Brush.Parse("#304A5B");
	public static readonly IBrush Cursor = Brush.Parse("#FFD190");

	public static FormattedText Text(string value, double size, IBrush brush, bool mono = false) =>
		new(value, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
			new Typeface(mono ? "Menlo,Consolas,monospace" : FontFamily.Default), size, brush);

	public static string Timestamp(double seconds) {
		long ms = (long)Math.Round(Math.Max(0, seconds) * 1000);
		return $"{ms / 60000:00}:{ms / 1000 % 60:00}.{ms % 1000:000}";
	}
}

sealed class Waveform : Control {
	private const double RulerHeight = 30;
	public bool ShowRuler { get; set; } = true;
	private Point? press;
	private bool dragged;
	public AudioTrack? Track { get; set; }
	public double ViewStart { get; set; }
	public double ViewLength { get; set; }
	public double CursorTime { get; set; }
	public (double Start, double End)? Selection { get; set; }
	public event Action? GestureStarted;
	public event Action<double, double?>? Gesture;
	public event Action<bool>? GestureCompleted;

	public Waveform() => ClipToBounds = true;

	public override void Render(DrawingContext context) {
		double rulerHeight = ShowRuler ? RulerHeight : 0;
		double width = Bounds.Width, height = Bounds.Height;
		context.FillRectangle(Palette.Panel, new Rect(Bounds.Size));
		if (Track is not { } track || width <= 0 || height <= rulerHeight || ViewLength <= 0) {
			var empty = Palette.Text("Open a bass stem", 16, Palette.Muted);
			context.DrawText(empty, new Point((width - empty.Width) / 2, (height - empty.Height) / 2));
			return;
		}

		double plotHeight = height - rulerHeight;
		double center = rulerHeight + plotHeight / 2;
		double X(double time) => (time - ViewStart) / ViewLength * width;
		if (Selection is { } range) {
			double left = Math.Clamp(X(range.Start), 0, width);
			double right = Math.Clamp(X(range.End), 0, width);
			context.FillRectangle(Palette.Selection, new Rect(left, rulerHeight, right - left, plotHeight));
		}

		var gridPen = new Pen(Palette.Grid);
		context.DrawLine(gridPen, new Point(0, center), new Point(width, center));
		double ideal = ViewLength / Math.Max(1, width / 125);
		double magnitude = Math.Pow(10, Math.Floor(Math.Log10(ideal)));
		double step = (ideal <= magnitude ? 1 : ideal <= 2 * magnitude ? 2 : ideal <= 5 * magnitude ? 5 : 10) * magnitude;
		for (double time = Math.Ceiling(ViewStart / step) * step; time <= ViewStart + ViewLength; time += step) {
			double x = X(time);
			context.DrawLine(gridPen, new Point(x, Math.Max(0, rulerHeight - 5)), new Point(x, height));
			if (ShowRuler)
				context.DrawText(Palette.Text(Palette.Timestamp(time), 11, Palette.Muted, true), new Point(x + 5, 6));
		}

		var waveformPen = new Pen(Palette.Accent);
		double scale = plotHeight * 0.46;
		for (int x = 0; x < (int)Math.Ceiling(width); x++) {
			int start = Math.Clamp((int)(track.Rate * (ViewStart + x * ViewLength / width)), 0, track.Samples.Length - 1);
			int end = Math.Clamp((int)(track.Rate * (ViewStart + (x + 1) * ViewLength / width)), start + 1, track.Samples.Length);
			var (min, max) = track.GetPeaks(start, end);
			double y1 = center - max * scale;
			double y2 = center - min * scale;
			if (y2 - y1 < 1.0) {
				double midpoint = (y1 + y2) / 2;
				y1 = midpoint - 0.5;
				y2 = midpoint + 0.5;
			}
			context.DrawLine(waveformPen, new Point(x + 0.5, y1), new Point(x + 0.5, y2));
		}

		double cursorX = X(CursorTime);
		if (cursorX >= -1 && cursorX <= width + 1) {
			cursorX = Math.Clamp(cursorX, 0.75, Math.Max(0.75, width - 0.75));
			context.DrawLine(new Pen(Palette.Cursor, 1.5), new Point(cursorX, Math.Max(0, rulerHeight - 8)), new Point(cursorX, height));
		}
	}

	private double TimeAt(double x) =>
		Math.Clamp(ViewStart + Math.Clamp(x / Bounds.Width, 0, 1) * ViewLength, 0, Track!.Duration);

	protected override void OnPointerPressed(PointerPressedEventArgs e) {
		if (Track is null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
			return;
		press = e.GetPosition(this);
		dragged = false;
		e.Pointer.Capture(this);
		GestureStarted?.Invoke();
		Gesture?.Invoke(TimeAt(press.Value.X), null);
		e.Handled = true;
	}

	protected override void OnPointerMoved(PointerEventArgs e) {
		if (press is Point start)
			UpdateGesture(start, e.GetPosition(this));
	}

	protected override void OnPointerReleased(PointerReleasedEventArgs e) {
		if (press is not Point start)
			return;
		UpdateGesture(start, e.GetPosition(this));
		press = null;
		e.Pointer.Capture(null);
		GestureCompleted?.Invoke(!dragged);
		e.Handled = true;
	}

	protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e) {
		if (press is null)
			return;
		press = null;
		GestureCompleted?.Invoke(false);
	}

	private void UpdateGesture(Point start, Point current) {
		double anchor = TimeAt(start.X);
		bool moved = Math.Abs(start.X - current.X) >= 3;
		dragged |= moved;
		Gesture?.Invoke(anchor, moved ? TimeAt(current.X) : null);
	}
}

sealed class BassTab : Control {
	private static readonly Cursor HandCursor = new(StandardCursorType.Hand);
	private static readonly Cursor ArrowCursor = new(StandardCursorType.Arrow);
	private int? hoveredString;
	public (int String, int Fret)? Fingering { get; set; }
	public Func<int, bool>? CanPlayString { get; set; }
	public event Action<int>? StringClicked;

	public void RefreshCursor() =>
		Cursor = hoveredString is int row && CanPlayString?.Invoke(row) == true ? HandCursor : ArrowCursor;

	public override void Render(DrawingContext context) {
		context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
		var pen = new Pen(Palette.Grid);
		for (int i = 0; i < Pitch.TabStrings.Length; i++) {
			double y = (i + 0.5) * Bounds.Height / Pitch.TabStrings.Length;
			context.DrawText(Palette.Text(Pitch.TabStrings[i], 12, Palette.Muted, true), new Point(0, y - 8));
			context.DrawLine(pen, new Point(20, y), new Point(Bounds.Width, y));
			if (Fingering is { } position && position.String == i) {
				var number = Palette.Text(position.Fret.ToString(), 15, Palette.Background, true);
				double x = (Bounds.Width + 20) / 2;
				context.DrawRectangle(Palette.Accent, null, new Rect(x - 14, y - 9, 28, 18), 4, 4);
				context.DrawText(number, new Point(x - number.Width / 2, y - number.Height / 2));
			}
		}
	}

	protected override void OnPointerPressed(PointerPressedEventArgs e) {
		base.OnPointerPressed(e);
		if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || Bounds.Height <= 0)
			return;
		if (StringAt(e.GetPosition(this)) is not int row || CanPlayString?.Invoke(row) != true)
			return;
		StringClicked?.Invoke(row);
		e.Handled = true;
	}

	protected override void OnPointerMoved(PointerEventArgs e) {
		base.OnPointerMoved(e);
		hoveredString = StringAt(e.GetPosition(this));
		RefreshCursor();
	}

	protected override void OnPointerExited(PointerEventArgs e) {
		base.OnPointerExited(e);
		hoveredString = null;
		RefreshCursor();
	}

	private int? StringAt(Point point) {
		if (Bounds.Height <= 0 || !new Rect(Bounds.Size).Contains(point))
			return null;
		return Math.Min((int)(point.Y / Bounds.Height * Pitch.TabStrings.Length), Pitch.TabStrings.Length - 1);
	}
}
