using System.Runtime.InteropServices;

namespace BassNotes;

sealed class MacPlayback : Playback {
	private const string AudioToolbox = "/System/Library/Frameworks/AudioToolbox.framework/AudioToolbox";
	private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
	private const uint LinearPcm = 0x6C70636D; // 'lpcm'
	private const uint FloatPacked = (1 << 0) | (1 << 3);
	private const uint SampleTimeValid = 1;
	private static readonly int DataSizeOffset = Marshal.OffsetOf<QueueBuffer>(nameof(QueueBuffer.DataSize)).ToInt32();
	private readonly OutputCallback refill;
	private float[] silence = [];
	private readonly nint[] buffers = new nint[3];
	private nint queue;
	private bool active;
	private int firstFrame;
	private int endFrame;
	private int nextFrame;
	private long submittedFrames;
	private double playedFrames;
	private long streamStartFrame;
	private long audioEndFrame;
	private double lastPosition;
	private bool draining;
	private int callbackError;

	public MacPlayback(AudioTrack track) : base(track) => refill = OnBufferReturned;

	public override bool IsPlaying {
		get {
			if (!active)
				return false;
			if (callbackError != 0) {
				int error = callbackError;
				Dispose();
				Check(error, "refill playback");
			}

			_ = Position;
			if (!draining || playedFrames < audioEndFrame - streamStartFrame)
				return true;

			lastPosition = (double)endFrame / Track.Rate;
			SuspendPlayback();
			return false;
		}
	}

	public override double Position {
		get {
			if (active && AudioQueueGetCurrentTime(queue, 0, out var time, 0) == 0 &&
				(time.Flags & SampleTimeValid) != 0)
			{
				playedFrames = Math.Max(playedFrames, Math.Min(time.SampleTime - streamStartFrame, submittedFrames - streamStartFrame));
				double frame = firstFrame + playedFrames;
				lastPosition = Math.Min(frame, endFrame) / Track.Rate;
			}
			return lastPosition;
		}
	}

	public override void Play(double start, double end) {
		SuspendPlayback();
		(firstFrame, endFrame) = GetFrames(start, end);
		lastPosition = (double)firstFrame / Track.Rate;
		if (firstFrame == endFrame)
			return;

		nextFrame = firstFrame;
		streamStartFrame = submittedFrames;
		audioEndFrame = submittedFrames;
		playedFrames = 0;
		draining = false;
		callbackError = 0;
		try {
			if (queue == 0)
				OpenQueue();
			active = true;
		}
		catch {
			Dispose();
			throw;
		}
	}

	private void OpenQueue() {
		submittedFrames = 0;
		audioEndFrame = 0;
		streamStartFrame = 0;
		var format = new StreamFormat {
			SampleRate = Track.Rate,
			FormatId = LinearPcm,
			FormatFlags = FloatPacked,
			BytesPerPacket = sizeof(float),
			FramesPerPacket = 1,
			BytesPerFrame = sizeof(float),
			ChannelsPerFrame = 1,
			BitsPerChannel = 32
		};

		// Delivery on the main run loop serializes refills with transport changes.
		Check(AudioQueueNewOutput(in format, refill, 0, CFRunLoopGetMain(), RunLoopMode.Value, 0, out queue),
			"open the playback device");
		silence = new float[Math.Max(1, Track.Rate / 100)];
		uint capacity = (uint)Math.Max(1, Track.Rate / 100) * sizeof(float);
		for (int i = 0; i < buffers.Length; i++) {
			Check(AudioQueueAllocateBuffer(queue, capacity, out buffers[i]), "allocate a playback buffer");
			Check(Enqueue(queue, buffers[i]), "queue audio samples");
		}
		Check(AudioQueueStart(queue, 0), "start playback");
	}

	public override void Pause() {
		_ = Position;
		SuspendPlayback();
	}

	private void SuspendPlayback() {
		if (!active)
			return;
		active = false;
		draining = true;
		// Keep the device running with silence so a seek never waits for shutdown.
	}

	public override void Dispose() {
		if (queue == 0)
			return;

		nint handle = queue;
		queue = 0;
		active = false;
		AudioQueueDispose(handle, 1);
	}

	private void OnBufferReturned(nint userData, nint handle, nint buffer) {
		if (handle != queue || callbackError != 0)
			return;
		callbackError = Enqueue(handle, buffer);
	}

	private int Enqueue(nint handle, nint buffer) {
		var info = Marshal.PtrToStructure<QueueBuffer>(buffer);
		int capacity = (int)(info.Capacity / sizeof(float));
		int filled = draining ? 0 : Math.Min(capacity, endFrame - nextFrame);
		if (filled > 0) {
			Marshal.Copy(Track.Samples, nextFrame, info.Data, filled);
			nextFrame += filled;
		}
		if (filled > 0)
			audioEndFrame = submittedFrames + filled;
		if (filled < capacity) {
			// Silence after the final sample keeps the device ready for the next seek.
			Marshal.Copy(silence, 0, info.Data + filled * sizeof(float), capacity - filled);
			draining = true;
		}
		Marshal.WriteInt32(buffer, DataSizeOffset, capacity * sizeof(float));
		int error = AudioQueueEnqueueBuffer(handle, buffer, 0, 0);
		if (error == 0)
			submittedFrames += capacity;
		return error;
	}

	private static void Check(int error, string action) {
		if (error != 0)
			throw new IOException($"macOS could not {action} (OSStatus {error}).");
	}

	private static class RunLoopMode {
		private static readonly nint Library = NativeLibrary.Load(CoreFoundation);
		public static readonly nint Value = Marshal.ReadIntPtr(NativeLibrary.GetExport(Library, "kCFRunLoopCommonModes"));
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct StreamFormat {
		public double SampleRate;
		public uint FormatId, FormatFlags, BytesPerPacket, FramesPerPacket, BytesPerFrame, ChannelsPerFrame, BitsPerChannel, Reserved;
	}

	// Only the leading fields are needed. Preserve the queue-owned capacity and data pointer.
	[StructLayout(LayoutKind.Sequential)]
	private struct QueueBuffer {
		public uint Capacity;
		public nint Data;
		public uint DataSize;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct AudioTimeStamp {
		public double SampleTime;
		public ulong HostTime;
		public double RateScalar;
		public ulong WordClockTime;
		public short Subframes, SubframeDivisor;
		public uint Counter, SmpteType, SmpteFlags;
		public short Hours, Minutes, Seconds, Frames;
		public uint Flags, Reserved;
	}

	[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
	private delegate void OutputCallback(nint userData, nint queue, nint buffer);

	[DllImport(CoreFoundation)] private static extern nint CFRunLoopGetMain();
	[DllImport(AudioToolbox)] private static extern int AudioQueueNewOutput(in StreamFormat format,
		OutputCallback callback, nint userData, nint runLoop, nint mode, uint flags, out nint queue);
	[DllImport(AudioToolbox)] private static extern int AudioQueueAllocateBuffer(nint queue, uint size, out nint buffer);
	[DllImport(AudioToolbox)] private static extern int AudioQueueEnqueueBuffer(nint queue, nint buffer, uint packetCount, nint packetDescriptions);
	[DllImport(AudioToolbox)] private static extern int AudioQueueGetCurrentTime(nint queue, nint timeline, out AudioTimeStamp time, nint discontinuity);
	[DllImport(AudioToolbox)] private static extern int AudioQueueStart(nint queue, nint startTime);
	[DllImport(AudioToolbox)] private static extern int AudioQueueDispose(nint queue, byte immediate);
}
