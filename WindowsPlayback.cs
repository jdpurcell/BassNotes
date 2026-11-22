using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.Versioning;

namespace BassNotes;

[SupportedOSPlatform("windows")]
sealed partial class WindowsPlayback(AudioTrack track) : Playback(track) {
	private const uint AutoConvertPcm = 0x80000000;
	private const uint DefaultQualityResampler = 0x08000000;
	private const long BufferDuration = 1_000_000; // 100 ms in WASAPI's 100 ns units.
	private const int RenderFlow = 0;
	private const int MultimediaRole = 1;
	private const int SharedMode = 0;
	private const uint AllClassContexts = 23;
	private const ushort IeeeFloatFormat = 3;
	private Thread? renderThread;
	private CancellationTokenSource? cancellation;
	private Exception? renderError;
	private volatile bool playing;
	private int positionFrame;
	private static readonly StrategyBasedComWrappers ComWrappers = new();

	public override bool IsPlaying {
		get {
			bool active = playing;
			if (Interlocked.Exchange(ref renderError, null) is { } error)
				throw error;
			return active;
		}
	}

	public override double Position => (double)Volatile.Read(ref positionFrame) / Track.Rate;

	public override void Play(double start, double end) {
		Pause();
		var (first, last) = GetFrames(start, end);
		Volatile.Write(ref positionFrame, first);
		renderError = null;
		if (first == last)
			return;

		var ready = new TaskCompletionSource();
		cancellation = new CancellationTokenSource();
		var token = cancellation.Token;
		var thread = new Thread(() => Render(first, last, ready, token)) { IsBackground = true };
		try {
			thread.SetApartmentState(ApartmentState.MTA);
			thread.Start();
			renderThread = thread;
			ready.Task.GetAwaiter().GetResult();
		}
		catch {
			Pause();
			throw;
		}
	}

	public override void Pause() {
		cancellation?.Cancel();
		renderThread?.Join();
		renderThread = null;
		cancellation?.Dispose();
		cancellation = null;
	}

	public override void Dispose() => Pause();

	private void Render(int first, int end, TaskCompletionSource ready, CancellationToken token) {
		IMMDeviceEnumerator? enumerator = null;
		IMMDevice? endpoint = null;
		IAudioClient? client = null;
		IAudioRenderClient? renderer = null;
		bool comInitialized = false;
		try {
			Marshal.ThrowExceptionForHR(CoInitializeEx(0, 0)); // COINIT_MULTITHREADED
			comInitialized = true;
			// Keep each WASAPI interface and its calls on this render thread.
			Guid enumeratorClass = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
			Guid enumeratorId = typeof(IMMDeviceEnumerator).GUID;
			Marshal.ThrowExceptionForHR(CoCreateInstance(in enumeratorClass, 0, AllClassContexts, in enumeratorId, out nint enumeratorPointer));
			enumerator = WrapOwned<IMMDeviceEnumerator>(enumeratorPointer);
			enumerator.GetDefaultAudioEndpoint(RenderFlow, MultimediaRole, out nint endpointPointer);
			endpoint = WrapOwned<IMMDevice>(endpointPointer);
			Guid audioClientId = typeof(IAudioClient).GUID;
			endpoint.Activate(in audioClientId, AllClassContexts, 0, out nint clientPointer);
			client = WrapOwned<IAudioClient>(clientPointer);
			var format = new WaveFormat {
				FormatTag = IeeeFloatFormat,
				Channels = 1,
				SamplesPerSecond = (uint)Track.Rate,
				BytesPerSecond = (uint)Track.Rate * sizeof(float),
				BlockAlign = sizeof(float),
				BitsPerSample = 32
			};
			client.Initialize(SharedMode, AutoConvertPcm | DefaultQualityResampler, BufferDuration, 0, in format, 0);
			client.GetBufferSize(out uint bufferFrames);
			Guid renderClientId = typeof(IAudioRenderClient).GUID;
			client.GetService(in renderClientId, out nint rendererPointer);
			renderer = WrapOwned<IAudioRenderClient>(rendererPointer);

			int next = first;
			next += WriteFrames(renderer, next, end, bufferFrames);
			client.Start();
			try {
				playing = true;
				ready.SetResult();
				while (true) {
					client.GetCurrentPadding(out uint padding);
					Volatile.Write(ref positionFrame, Math.Clamp(next - (int)padding, first, end));
					if (token.IsCancellationRequested || (next == end && padding == 0))
						break;

					next += WriteFrames(renderer, next, end, bufferFrames - padding);
					token.WaitHandle.WaitOne(10);
				}
			}
			finally {
				client.Stop();
			}
		}
		catch (Exception ex) {
			var error = new IOException($"Windows audio playback failed. {ex.Message}", ex);
			if (!ready.TrySetException(error))
				Volatile.Write(ref renderError, error);
		}
		finally {
			playing = false;
			if (renderer is not null)
				((ComObject)(object)renderer).FinalRelease();
			if (client is not null)
				((ComObject)(object)client).FinalRelease();
			if (endpoint is not null)
				((ComObject)(object)endpoint).FinalRelease();
			if (enumerator is not null)
				((ComObject)(object)enumerator).FinalRelease();
			if (comInitialized)
				CoUninitialize();
		}
	}

	// Wrap each owned native out pointer, then release its original reference.
	// Unique wrappers allow deterministic cleanup on the render thread.
	private static T WrapOwned<T>(nint pointer) where T : class {
		ComObject? wrapper = null;
		try {
			wrapper = (ComObject)ComWrappers.GetOrCreateObjectForComInstance(pointer, CreateObjectFlags.UniqueInstance);
			return (T)(object)wrapper;
		}
		catch {
			wrapper?.FinalRelease();
			throw;
		}
		finally {
			Marshal.Release(pointer);
		}
	}

	[LibraryImport("ole32.dll")]
	private static partial int CoInitializeEx(nint reserved, uint flags);

	[LibraryImport("ole32.dll")]
	private static partial void CoUninitialize();

	[LibraryImport("ole32.dll")]
	private static partial int CoCreateInstance(in Guid classId, nint outer, uint context, in Guid interfaceId, out nint instance);

	private int WriteFrames(IAudioRenderClient renderer, int start, int end, uint available) {
		int count = Math.Min(end - start, (int)available);
		if (count == 0)
			return 0;

		renderer.GetBuffer((uint)count, out nint buffer);
		bool copied = false;
		try {
			Marshal.Copy(Track.Samples, start, buffer, count);
			copied = true;
		}
		finally {
			renderer.ReleaseBuffer(copied ? (uint)count : 0, 0);
		}
		return count;
	}

	[StructLayout(LayoutKind.Sequential, Pack = 2)]
	internal struct WaveFormat {
		public ushort FormatTag, Channels;
		public uint SamplesPerSecond, BytesPerSecond;
		public ushort BlockAlign, BitsPerSample, ExtraSize;
	}

	// COM declarations retain the native vtable order, including unused methods.
	[GeneratedComInterface(Options = ComInterfaceOptions.ComObjectWrapper), Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
	internal partial interface IMMDeviceEnumerator {
		void EnumAudioEndpoints(int dataFlow, uint stateMask, out nint devices);
		void GetDefaultAudioEndpoint(int dataFlow, int role, out nint endpoint);
	}

	[GeneratedComInterface(Options = ComInterfaceOptions.ComObjectWrapper), Guid("D666063F-1587-4E43-81F1-B948E807363F")]
	internal partial interface IMMDevice {
		void Activate(in Guid iid, uint context, nint activationParams, out nint instance);
	}

	[GeneratedComInterface(Options = ComInterfaceOptions.ComObjectWrapper), Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2")]
	internal partial interface IAudioClient {
		void Initialize(int shareMode, uint flags, long bufferDuration, long periodicity, in WaveFormat format, nint sessionGuid);
		void GetBufferSize(out uint bufferFrames);
		void GetStreamLatency(out long latency);
		void GetCurrentPadding(out uint padding);
		void IsFormatSupported(int shareMode, in WaveFormat format, out nint closestMatch);
		void GetMixFormat(out nint format);
		void GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
		void Start();
		void Stop();
		void Reset();
		void SetEventHandle(nint handle);
		void GetService(in Guid iid, out nint service);
	}

	[GeneratedComInterface(Options = ComInterfaceOptions.ComObjectWrapper), Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2")]
	internal partial interface IAudioRenderClient {
		void GetBuffer(uint frames, out nint buffer);
		void ReleaseBuffer(uint frames, uint flags);
	}
}
