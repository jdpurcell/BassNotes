namespace BassNotes;

abstract class Playback(AudioTrack track) : IDisposable {
	protected AudioTrack Track { get; } = track;

	public static Playback Open(AudioTrack track) {
		if (OperatingSystem.IsMacOS())
			return new MacPlayback(track);
		if (OperatingSystem.IsWindows())
			return new WindowsPlayback(track);
		throw new PlatformNotSupportedException("Playback is available on macOS and Windows.");
	}

	protected (int First, int End) GetFrames(double start, double end) {
		int first = Track.FrameAt(start);
		int last = Math.Max(first, Track.FrameAt(end));
		return (first, last);
	}

	public abstract bool IsPlaying { get; }
	public abstract double Position { get; }
	public abstract void Play(double start, double end);
	public abstract void Pause();
	public abstract void Dispose();
}
