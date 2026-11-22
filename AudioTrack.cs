using System.Buffers.Binary;
using SimpleFlac;

namespace BassNotes;

sealed class AudioTrack {
	private const int PeakBlock = 128;
	private readonly (float Min, float Max)[] peaks;

	private AudioTrack(string name, int rate, float[] samples) {
		Name = name;
		Rate = rate;
		Samples = samples;
		peaks = new (float, float)[(samples.Length - 1) / PeakBlock + 1];
		for (int block = 0; block < peaks.Length; block++) {
			int start = block * PeakBlock;
			int end = start + Math.Min(PeakBlock, samples.Length - start);
			float min = float.MaxValue, max = float.MinValue;
			for (int i = start; i < end; i++) {
				min = Math.Min(min, samples[i]);
				max = Math.Max(max, samples[i]);
			}
			peaks[block] = (min, max);
		}
	}

	public string Name { get; }
	public int Rate { get; }
	public float[] Samples { get; }
	public double Duration => (double)Samples.Length / Rate;
	public int FrameAt(double time) => Math.Clamp((int)Math.Round(time * Rate), 0, Samples.Length);

	public (float Min, float Max) GetPeaks(int start, int end) {
		float min = float.MaxValue, max = float.MinValue;
		while (start < end) {
			if (start % PeakBlock == 0 && end - start >= PeakBlock) {
				var peak = peaks[start / PeakBlock];
				min = Math.Min(min, peak.Min);
				max = Math.Max(max, peak.Max);
				start += PeakBlock;
				continue;
			}

			min = Math.Min(min, Samples[start]);
			max = Math.Max(max, Samples[start]);
			start++;
		}
		return (min, max);
	}

	public static AudioTrack Load(string path, CancellationToken cancellationToken, AudioTrack? durationReferenceTrack = null) {
		cancellationToken.ThrowIfCancellationRequested();
		var (rate, samples) = Path.GetExtension(path).Equals(".flac", StringComparison.OrdinalIgnoreCase)
			? ReadFlac(path, durationReferenceTrack, cancellationToken)
			: ReadWav(path, durationReferenceTrack, cancellationToken);
		cancellationToken.ThrowIfCancellationRequested();
		return new AudioTrack(Path.GetFileName(path), rate, samples);
	}

	private static int? SampleLength(int rate, long? sourceLength, AudioTrack? reference) =>
		reference is not null ? Math.Max(1, checked((int)Math.Round(reference.Duration * rate))) :
		sourceLength is long count ? checked((int)count) : null;

	private static (int Rate, float[] Samples) ReadWav(string path, AudioTrack? reference, CancellationToken token) {
		using var wav = new WavReader(path);
		if (wav.FileSampleCount == 0)
			throw new InvalidDataException("The file contains no audio samples.");
		var samples = new float[SampleLength(wav.SampleRate, wav.FileSampleCount, reference)!.Value];
		long position = 0;
		while (wav.RunningSampleCount < wav.FileSampleCount) {
			token.ThrowIfCancellationRequested();
			var bytes = wav.ReadSamples(8192);
			for (int frame = 0; frame < bytes.Length; frame += wav.BlockAlign) {
				if (position >= samples.Length) {
					position++;
					continue;
				}
				double sum = 0;
				for (int channel = 0; channel < wav.ChannelCount; channel++) {
					int offset = frame + channel * wav.BytesPerSample;
					var sample = bytes.Slice(offset, wav.BytesPerSample);
					double value = wav.IsFloat
						? wav.BytesPerSample == 4
							? BinaryPrimitives.ReadSingleLittleEndian(sample)
							: BinaryPrimitives.ReadDoubleLittleEndian(sample)
						: ReadPcm(sample);
					sum += double.IsFinite(value) ? value : 0;
				}
				samples[(int)position++] = Normalize(sum / wav.ChannelCount);
			}
		}
		return (wav.SampleRate, samples);
	}

	private static double ReadPcm(ReadOnlySpan<byte> sample) => sample.Length switch {
		1 => (sample[0] - 128) / 128.0,
		2 => BinaryPrimitives.ReadInt16LittleEndian(sample) / 32768.0,
		3 => ((sample[0] << 8) | (sample[1] << 16) | (sample[2] << 24)) / 2147483648.0,
		_ => BinaryPrimitives.ReadInt32LittleEndian(sample) / 2147483648.0
	};

	private static (int Rate, float[] Samples) ReadFlac(string path, AudioTrack? reference, CancellationToken token) {
		using var decoder = new FlacDecoder(path, new FlacDecoder.Options {
			ConvertOutputToBytes = false,
			ValidateOutputHash = false
		});
		if (decoder.SampleRate <= 0 || decoder.ChannelCount <= 0)
			throw new InvalidDataException("Invalid FLAC audio format.");
		int? length = SampleLength(decoder.SampleRate, decoder.StreamSampleCount, reference);
		float[]? samples = length is int count ? new float[count] : null;
		// FLAC streams may omit their total sample count.
		List<float>? unknownLengthSamples = samples is null ? new List<float>() : null;
		long position = 0;
		double divisor = Math.ScaleB(1, decoder.BitsPerSample - 1) * decoder.ChannelCount;
		while (decoder.DecodeFrame()) {
			token.ThrowIfCancellationRequested();
			for (int frame = 0; frame < decoder.BufferSampleCount; frame++) {
				if (samples is not null && position >= samples.Length) {
					position++;
					continue;
				}
				double sum = 0;
				for (int channel = 0; channel < decoder.ChannelCount; channel++)
					sum += decoder.BufferSamples[channel][frame];
				float sample = Normalize(sum / divisor);
				if (samples is not null)
					samples[(int)position] = sample;
				else
					unknownLengthSamples!.Add(sample);
				position++;
			}
		}
		if (decoder.StreamSampleCount is long expected && expected != position)
			throw new InvalidDataException("The FLAC sample count does not match its declared length.");
		if (position == 0)
			throw new InvalidDataException("The file contains no audio samples.");
		return (decoder.SampleRate, samples ?? unknownLengthSamples!.ToArray());
	}

	private static float Normalize(double value) => (float)Math.Clamp(value, -1, 1);
}
