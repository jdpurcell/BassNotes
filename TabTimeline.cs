using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace BassNotes;

sealed class TabTimeline : UserControl {
	private static readonly IBrush RemoveBrush = Brush.Parse("#FF7777");
	private static readonly Geometry Cross = Geometry.Parse("M 1,1 L 9,9 M 9,1 L 1,9");
	private readonly List<(TabNote Note, TextBlock Fret, Button Remove)> markers = [];
	private readonly Canvas markerCanvas = new();
	private readonly TimelineDrawing drawing;
	private static readonly Cursor HandCursor = new(StandardCursorType.Hand);
	private static readonly Cursor ArrowCursor = new(StandardCursorType.Arrow);
	private Point? pointerPosition;
	private const double MarkerWidth = 22;
	private const double FirstRowTop = 9;
	private const double RowHeight = 22;
	private bool isReadOnly;
	public bool IsReadOnly {
		get => isReadOnly;
		set {
			isReadOnly = value;
			foreach (var marker in markers)
				marker.Remove.IsEnabled = !value;
			RefreshCursor();
		}
	}
	private double viewStart;
	private double viewLength;
	private double cursorTime;

	public event Action<Guid>? RemoveRequested;
	public event Action<Guid, int>? StringChangeRequested;

	public TabTimeline() {
		Height = 142;
		ClipToBounds = true;
		Background = Palette.Panel;
		drawing = new TimelineDrawing(this);
		Content = new Grid { Children = { drawing, markerCanvas } };
	}

	public void SetNotes(IReadOnlyList<TabNote> notes) {
		markerCanvas.Children.Clear();
		markers.Clear();
		foreach (var note in notes) {
			var fret = new TextBlock {
				Text = note.Fret.ToString(), Width = MarkerWidth, Height = 20,
				FontSize = 14, IsHitTestVisible = false,
				Foreground = Palette.Accent, TextAlignment = TextAlignment.Center
			};
			var remove = new Button {
				Content = new Avalonia.Controls.Shapes.Path { Data = Cross, Stroke = RemoveBrush, StrokeThickness = 2, Width = 10, Height = 10 },
				IsEnabled = !IsReadOnly, Width = 22, Height = 22, Padding = new Thickness(5), Background = Brushes.Transparent
			};
			Avalonia.Automation.AutomationProperties.SetName(remove, $"Remove {Pitch.TabStrings[note.String]} string fret {note.Fret} at {Palette.Timestamp(note.Start)}");
			ToolTip.SetTip(remove, $"Remove fret {note.Fret} at {Palette.Timestamp(note.Start)}");
			remove.Click += (_, _) => RemoveRequested?.Invoke(note.Id);
			markerCanvas.Children.Add(fret);
			markerCanvas.Children.Add(remove);
			markers.Add((note, fret, remove));
		}
		PositionMarkers();
		RefreshCursor();
		drawing.InvalidateVisual();
	}

	public void SetViewport(double start, double length, double cursor) {
		viewStart = start;
		viewLength = length;
		cursorTime = cursor;
		PositionMarkers();
		RefreshCursor();
		drawing.InvalidateVisual();
	}

	protected override Size ArrangeOverride(Size finalSize) {
		PositionMarkers(finalSize.Width);
		return base.ArrangeOverride(finalSize);
	}

	private double X(double time, double width) => (time - viewStart) / viewLength * width;

	private (TabNote Note, int Row)? StringTarget(Point point) {
		if (IsReadOnly || viewLength <= 0 || !new Rect(Bounds.Size).Contains(point) ||
			point.Y < FirstRowTop || point.Y >= FirstRowTop + Pitch.TabStrings.Length * RowHeight)
			return null;

		int row = (int)((point.Y - FirstRowTop) / RowHeight);
		TabNote? closest = null;
		double distance = MarkerWidth / 2;
		// Prefer the topmost marker when two columns coincide.
		for (int i = markers.Count - 1; i >= 0; i--) {
			var note = markers[i].Note;
			if (note.Start < viewStart || note.Start > viewStart + viewLength)
				continue;
			double delta = Math.Abs(point.X - X(note.Start, Bounds.Width));
			if (delta <= distance && (closest is null || delta < distance)) {
				closest = note;
				distance = delta;
			}
		}
		return closest is not null && new Pitch(0, closest.Midi, 0).GetFret(0, row) is not null
			? (closest, row) : null;
	}

	private void RefreshCursor() =>
		Cursor = pointerPosition is Point point && StringTarget(point) is not null ? HandCursor : ArrowCursor;

	protected override void OnPointerMoved(PointerEventArgs e) {
		base.OnPointerMoved(e);
		pointerPosition = e.GetPosition(this);
		RefreshCursor();
	}

	protected override void OnPointerExited(PointerEventArgs e) {
		base.OnPointerExited(e);
		pointerPosition = null;
		RefreshCursor();
	}

	protected override void OnPointerPressed(PointerPressedEventArgs e) {
		base.OnPointerPressed(e);
		if (e.Handled || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed ||
			StringTarget(e.GetPosition(this)) is not { } target)
			return;
		e.Handled = true;
		if (target.Note.String != target.Row)
			StringChangeRequested?.Invoke(target.Note.Id, target.Row);
	}

	private void PositionMarkers(double? availableWidth = null) {
		double width = availableWidth ?? Bounds.Width;
		foreach (var (note, fret, remove) in markers) {
			bool visible = viewLength > 0 && note.Start >= viewStart && note.Start <= viewStart + viewLength;
			fret.IsVisible = remove.IsVisible = visible;
			if (!visible)
				continue;
			double x = X(note.Start, width);
			Canvas.SetLeft(fret, x - 11);
			Canvas.SetTop(fret, 10 + note.String * 22);
			Canvas.SetLeft(remove, x - 11);
			Canvas.SetTop(remove, 119);
		}
	}

	private void DrawTimeline(DrawingContext context) {
		for (int row = 0; row < Pitch.TabStrings.Length; row++) {
			double y = 20 + row * 22;
			context.DrawLine(new Pen(Palette.Grid), new Point(0, y), new Point(Bounds.Width, y));
			context.DrawText(Palette.Text(Pitch.TabStrings[row], 11, Palette.Muted, true), new Point(3, y - 15));
		}
		// Erase string lines behind every label before the transparent labels are painted.
		// Keeping these backgrounds in one layer prevents neighbors from covering digits.
		if (viewLength > 0) {
			foreach (var (note, _, _) in markers) {
				if (note.Start < viewStart || note.Start > viewStart + viewLength)
					continue;
				context.FillRectangle(Palette.Panel,
					new Rect(X(note.Start, Bounds.Width) - MarkerWidth / 2, 10 + note.String * 22, MarkerWidth, 20));
			}
		}
		if (viewLength > 0 && cursorTime >= viewStart && cursorTime <= viewStart + viewLength) {
			double x = X(cursorTime, Bounds.Width);
			context.DrawLine(new Pen(Palette.Cursor), new Point(x, 0), new Point(x, 118));
		}
	}
	private sealed class TimelineDrawing(TabTimeline owner) : Control {
		public override void Render(DrawingContext context) => owner.DrawTimeline(context);
	}
}
