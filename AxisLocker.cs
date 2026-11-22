using System.Diagnostics;
using Avalonia;

namespace BassNotes;

sealed class AxisLocker {
	private static readonly TimeSpan ResetDelay = TimeSpan.FromMilliseconds(OperatingSystem.IsMacOS() ? 125 : 250);
	// Wheel deltas are normalized units. Keep the initial dead zone small for touchpads.
	private const double LockDistance = 0.04;
	private readonly Stopwatch lastEvent = new();
	private Vector pending;
	private bool? horizontal;

	public Vector Filter(Vector delta) {
		if (!lastEvent.IsRunning || lastEvent.Elapsed >= ResetDelay) {
			pending = default;
			horizontal = null;
		}
		lastEvent.Restart();

		if (horizontal is not null)
			return horizontal.Value ? new Vector(delta.X, 0) : new Vector(0, delta.Y);

		pending += delta;
		double x = Math.Abs(pending.X);
		double y = Math.Abs(pending.Y);
		if (x >= LockDistance && x > y) {
			horizontal = true;
			return new Vector(pending.X, 0);
		}
		if (y >= LockDistance && y > x) {
			horizontal = false;
			return new Vector(0, pending.Y);
		}
		return default;
	}

	public void Reset() => lastEvent.Reset();
}
