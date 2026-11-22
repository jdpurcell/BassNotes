namespace BassNotes;

readonly record struct Pitch(double Frequency, int Midi, double Cents) {
	private static readonly string[] Names = ["C", "C♯", "D", "D♯", "E", "F", "F♯", "G", "G♯", "A", "A♯", "B"];
	public static readonly string[] TabStrings = ["G", "D", "A", "E", "B"];
	private static readonly int[] OpenMidi = [43, 38, 33, 28, 23];

	public string Note => $"{Names[Midi % 12]}{Midi / 12 - 1}";

	public (int String, int Fret)? GetFingering(int semitones) {
		for (int i = 0; i < OpenMidi.Length; i++) {
			if (GetFret(semitones, i) is int fret)
				return (i, fret);
		}
		return null;
	}

	public int? GetFret(int semitones, int stringIndex) {
		if ((uint)stringIndex >= OpenMidi.Length)
			return null;
		int fret = Midi + semitones - OpenMidi[stringIndex];
		return fret is >= 0 and <= 24 ? fret : null;
	}
}

static class PitchDetector {
	public const double MinimumDuration = 0.075;
	private const double LowestHz = 16;
	private const double HighestHz = 535;

	public static Pitch? Detect(float[] samples, int rate, int start, int end, CancellationToken token) {
		int length = end - start;
		if (length < rate * MinimumDuration)
			return null;

		int window = Math.Min(length, (int)(rate * 0.18));
		int frames = Math.Clamp(1 + (length - window) / Math.Max(1, window / 2), 1, 15);
		var frequencies = new List<double>();
		for (int i = 0; i < frames; i++) {
			token.ThrowIfCancellationRequested();
			int offset = frames == 1 ? (length - window) / 2 : (int)((long)(length - window) * i / (frames - 1));
			if (DetectFrame(samples.AsSpan(start + offset, window), rate) is double frequency)
				frequencies.Add(frequency);
		}

		if (frequencies.Count == 0)
			return null;

		// Compare estimates in semitones so adjacent frames can straddle a note boundary.
		frequencies.Sort();
		double center = frequencies[frequencies.Count / 2];
		var consistent = frequencies.Where(hz => Math.Abs(1200 * Math.Log2(hz / center)) <= 50).ToArray();
		if (consistent.Length * 2 <= frequencies.Count && frequencies.Count > 1)
			return null;

		double hz = consistent[consistent.Length / 2];
		double exactMidi = 69 + 12 * Math.Log2(hz / 440);
		int midi = (int)Math.Round(exactMidi);
		return new Pitch(hz, midi, (exactMidi - midi) * 100);
	}

	private static double? DetectFrame(ReadOnlySpan<float> frame, int rate) {
		// Average adjacent samples to reduce the cost of the YIN difference calculation.
		int stride = Math.Max(1, rate / 6000);
		double analysisRate = (double)rate / stride;
		var signal = new double[frame.Length / stride];
		double sum = 0, squares = 0;
		for (int i = 0; i < signal.Length; i++) {
			double value = 0;
			for (int j = 0; j < stride; j++)
				value += frame[i * stride + j];
			value /= stride;
			signal[i] = value;
			sum += value;
			squares += value * value;
		}

		if (signal.Length < 4 || (squares - sum * sum / signal.Length) / signal.Length < 1e-8)
			return null;

		int minLag = Math.Max(2, (int)(analysisRate / HighestHz));
		int maxLag = Math.Min(signal.Length / 2, (int)Math.Ceiling(analysisRate / LowestHz) + 1);
		int compareLength = signal.Length - maxLag;
		var normalized = new double[maxLag + 1];
		normalized[0] = 1;
		double running = 0;
		for (int lag = 1; lag <= maxLag; lag++) {
			double difference = 0;
			for (int i = 0; i < compareLength; i++) {
				double delta = signal[i] - signal[i + lag];
				difference += delta * delta;
			}
			running += difference;
			normalized[lag] = running > 0 ? difference * lag / running : 1;
		}

		for (int lag = minLag; lag < maxLag; lag++) {
			if (normalized[lag] > 0.10 || normalized[lag] > normalized[lag + 1] ||
				normalized[lag] >= normalized[lag - 1])
				continue;

			// A stronger match at twice the lag can reveal a quiet fundamental.
			double shortScore = InterpolatedScore(normalized, lag);
			int candidate = lag * 2;
			for (int longer = candidate - 2; longer <= candidate + 2 && longer < maxLag; longer++) {
				if (longer > lag && shortScore > 0.012 &&
					normalized[longer] < normalized[longer - 1] && normalized[longer] <= normalized[longer + 1] &&
					InterpolatedScore(normalized, longer) < shortScore * 0.35)
				{
					lag = longer;
					break;
				}
			}

			double adjustment = InterpolatedLagOffset(normalized, lag);
			double frequency = analysisRate / (lag + adjustment);
			return frequency is >= LowestHz and <= HighestHz ? frequency : null;
		}
		return null;
	}
	private static double InterpolatedLagOffset(double[] scores, int lag) =>
		0.5 * (scores[lag - 1] - scores[lag + 1]) / (scores[lag - 1] - 2 * scores[lag] + scores[lag + 1]);

	private static double InterpolatedScore(double[] scores, int lag) =>
		scores[lag] - 0.25 * (scores[lag - 1] - scores[lag + 1]) * InterpolatedLagOffset(scores, lag);
}
