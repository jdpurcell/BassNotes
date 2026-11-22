using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using ShapePath = Avalonia.Controls.Shapes.Path;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace BassNotes;

sealed class MainWindow : Window {
	private const string Instructions = "Click to seek · Drag to select · Space play/pause · Esc stop · ←/→ jump · Enter add to tab";
	private static readonly Geometry PlayGeometry = Geometry.Parse("M 1,0 L 13,7 L 1,14 Z");
	private static readonly Geometry PauseGeometry = Geometry.Parse("M 1,0 H 5 V 14 H 1 Z M 9,0 H 13 V 14 H 9 Z");
	private readonly Waveform waveform = new();
	private readonly Waveform backingWaveform = new() { ShowRuler = false, IsVisible = false };
	private readonly Button openBacking = new() { Content = "Open…", IsEnabled = false };
	private readonly TextBlock backingFilename = Label("No backing track loaded", 16);
	private readonly RadioButton hearBass = new() { Content = "Bass stem", IsChecked = true };
	private readonly RadioButton hearBacking = new() { Content = "Backing track", IsEnabled = false };
	private AudioTrack? playbackTrack;
	private bool playBacking;
	private readonly BassTab tab = new() { Height = 98, MinWidth = 145 };
	private int octave;
	private readonly TabTimeline tabTimeline = new();
	private readonly Button addToTab = new() { Content = "Add to Tab", IsEnabled = false };
	private readonly TextBlock saveStatus = Label("", 12);
	private TabDocument? tabDocument;
	private readonly TextBlock tabStatus = Label("Outside the tab range", 12);
	private readonly Slider scroll = new() { Minimum = 0, IsEnabled = false };
	private readonly TextBlock filename = Label("No bass stem loaded", 16);
	private readonly TextBlock time = Label("00:00.000 / 00:00.000", 18, true);
	private readonly TextBlock selection = Label("Selection  —", 12, true);
	private readonly TextBlock note = Label("—", 39);
	private readonly TextBlock pitchDetails = Label("Drag across a single note to identify it.", 13);
	private readonly TextBlock status = Label(Instructions, 12);
	private readonly Button open = new() { Content = "Open…" };
	private readonly ShapePath playIcon = new() {
		Data = PlayGeometry,
		Width = 14, Height = 14
	};
	private readonly Button play = new() { MinWidth = 40, HorizontalContentAlignment = HorizontalAlignment.Center };
	private readonly Button stop = new() {
		Content = new ShapePath { Data = Geometry.Parse("M 1,1 H 13 V 13 H 1 Z"), Width = 14, Height = 14 },
		MinWidth = 40, HorizontalContentAlignment = HorizontalAlignment.Center
	};
	private readonly CheckBox loop = new() { Content = "Loop" };
	private readonly CheckBox centerPlayback = new() { Content = "Center playback", IsChecked = false };
	private readonly ComboBox jump = new() { ItemsSource = new[] { 1, 2, 3, 4, 5 }, SelectedIndex = 2, MinWidth = 58 };
	private readonly Button zoomIn = new() { Content = "+" };
	private readonly Button zoomOut = new() { Content = "−" };
	private readonly Button fit = new() { Content = "Fit" };
	private readonly MenuItem openMenu = new() { Header = "_Open…" };
	private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(30) };
	private readonly CancellationTokenSource lifetime = new();
	private CancellationTokenSource? analysis;
	private Playback? playback;
	private Pitch? detectedPitch;
	private bool loading;
	private bool playing;
	private bool paused;
	private bool resumeAfterWaveformClick;
	private double playbackEnd;
	private double anchor;
	private readonly AxisLocker scrollAxis = new();
	private KeyModifiers scrollModifiers;

	public MainWindow(string? initialPath = null) {
		Title = "BassNotes";
		Icon = new WindowIcon(Avalonia.Platform.AssetLoader.Open(new Uri("avares://BassNotes/assets/App.ico")));
		Width = 950;
		Height = 900;
		MinWidth = 820;
		MinHeight = 800;
		Background = Palette.Background;
		WindowStartupLocation = WindowStartupLocation.CenterScreen;
		play.Content = playIcon;
		playIcon.Bind(ShapePath.FillProperty, play.GetObservable(ForegroundProperty));
		((ShapePath)stop.Content!).Bind(ShapePath.FillProperty, stop.GetObservable(ForegroundProperty));

		filename.Foreground = Palette.Muted;
		filename.TextTrimming = TextTrimming.CharacterEllipsis;
		filename.VerticalAlignment = VerticalAlignment.Center;
		var fileHeader = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 10 };
		Put(fileHeader, open, 0);
		Put(fileHeader, filename, 0, 1);
		time.VerticalAlignment = VerticalAlignment.Center;
		selection.Foreground = Palette.Muted;
		status.Foreground = Palette.Muted;
		status.TextTrimming = TextTrimming.CharacterEllipsis;

		backingFilename.Foreground = Palette.Muted;
		backingFilename.TextTrimming = TextTrimming.CharacterEllipsis;
		backingFilename.VerticalAlignment = VerticalAlignment.Center;
		var backingHeader = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 10 };
		Put(backingHeader, openBacking, 0);
		Put(backingHeader, backingFilename, 0, 1);
		var fileHeaders = new StackPanel { Spacing = 8, Children = { fileHeader, backingHeader } };
		AutomationProperties.SetName(open, "Open bass stem");
		AutomationProperties.SetName(openBacking, "Open backing track");
		ToolTip.SetTip(open, "Open bass stem");
		ToolTip.SetTip(openBacking, "Open backing track");
		var playbackChoice = Row(Label("Listen to", 12), hearBass, hearBacking, centerPlayback);
		ToolTip.SetTip(centerPlayback, "Scroll during playback to keep at least half the view ahead of the cursor");
		centerPlayback.IsCheckedChanged += (_, _) => {
			if (playing && centerPlayback.IsChecked == true)
				FollowCursor();
		};
		hearBass.IsCheckedChanged += (_, _) => { if (hearBass.IsChecked == true) SwitchPlaybackTrack(false); };
		hearBacking.IsCheckedChanged += (_, _) => { if (hearBacking.IsChecked == true) SwitchPlaybackTrack(true); };
		var transport = Row(play, stop, loop, Label("Jump (sec)", 12), jump);
		AutomationProperties.SetName(jump, "Jump distance in seconds");
		ToolTip.SetTip(jump, "Left/Right arrow jump distance in seconds");
		AutomationProperties.SetName(stop, "Stop");
		ToolTip.SetTip(stop, "Stop (Esc)");
		var zoom = Row(Label("Zoom", 12), zoomOut, zoomIn, fit);
		zoom.HorizontalAlignment = HorizontalAlignment.Right;
		var toolbar = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
		Put(toolbar, transport, 0, 0);
		var clock = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Children = { time } };
		Put(toolbar, clock, 0, 1);
		Put(toolbar, zoom, 0, 2);
		ToolTip.SetTip(zoomIn, "Zoom in around the selection or cursor");
		ToolTip.SetTip(zoomOut, "Zoom out");
		ToolTip.SetTip(fit, "Fit the entire track");

		note.Foreground = Palette.Accent;
		note.FontWeight = FontWeight.SemiBold;
		pitchDetails.Foreground = Palette.Muted;
		pitchDetails.TextWrapping = TextWrapping.Wrap;
		var tuner = new StackPanel { Spacing = 2, Children = { Label("Detected note", 14), note, pitchDetails } };
		var octaveOptions = Row(Label("Octave", 12));
		for (int value = -2; value <= 2; value++) {
			int selectedOctave = value;
			var option = new RadioButton {
				Content = value.ToString("+0;-0;0"),
				IsChecked = value == 0
			};
			AutomationProperties.SetName(option, $"Tab octave {value:+0;-0;0}");
			option.IsCheckedChanged += (_, _) => {
				if (option.IsChecked == true) {
					octave = selectedOctave;
					UpdateTab();
				}
			};
			octaveOptions.Children.Add(option);
		}
		tab.StringClicked += SelectTabString;
		tab.CanPlayString = stringIndex => detectedPitch?.GetFret(octave * 12, stringIndex) is not null;
		tabStatus.Foreground = Palette.Muted;
		tabStatus.IsVisible = false;
		var tablature = new StackPanel { Spacing = 4, Children = { octaveOptions, tab, tabStatus, addToTab } };
		var results = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 24 };
		Put(results, tuner, 0, 0);
		Put(results, tablature, 0, 1);

		var main = new Grid {
			Margin = new Thickness(20, 6, 20, 14),
			RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto,Auto,Auto,Auto,Auto,Auto"),
			RowSpacing = 8
		};
		Put(main, fileHeaders, 0);
		Put(main, new StackPanel { Spacing = 6, Children = { toolbar, playbackChoice } }, 1);
		var waveforms = new Grid {
			RowDefinitions = new RowDefinitions("*,Auto"), RowSpacing = 2,
			ClipToBounds = true
		};
		Put(waveforms, waveform, 0);
		Put(waveforms, backingWaveform, 1);
		// Hidden backing view consumes no space; when loaded, both share the waveform area.
		backingWaveform.PropertyChanged += (_, e) => {
			if (e.Property == IsVisibleProperty)
				waveforms.RowDefinitions[1].Height = backingWaveform.IsVisible ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
		};
		Put(main, waveforms, 2);
		Put(main, tabTimeline, 3);
		Put(main, scroll, 4);
		Put(main, selection, 5);
		Put(main, new Border {
			Background = Palette.Panel, CornerRadius = new CornerRadius(8),
			Padding = new Thickness(15, 11), Child = results
		}, 6);
		saveStatus.Foreground = Palette.Muted;
		saveStatus.TextWrapping = TextWrapping.Wrap;
		saveStatus.IsVisible = false;
		Put(main, saveStatus, 7);
		Put(main, status, 8);
		Content = AddFileMenu(main);

		addToTab.Click += (_, _) => AddToTab();
		ToolTip.SetTip(addToTab, "Add the selected fingering to the timeline (Enter)");
		tabTimeline.RemoveRequested += RemoveTabNote;
		tabTimeline.StringChangeRequested += ChangeSavedTabString;
		open.Click += async (_, _) => await OpenFile();
		openBacking.Click += async (_, _) => await OpenFile(true);
		play.Click += (_, _) => TogglePlayback();
		stop.Click += (_, _) => StopPlayback();
		zoomIn.Click += (_, _) => Zoom(0.5);
		zoomOut.Click += (_, _) => Zoom(2);
		fit.Click += (_, _) => FitTrack();
		scroll.ValueChanged += (_, _) => {
			waveform.ViewStart = scroll.Value;
			waveform.InvalidateVisual();
			SyncTabTimeline();
		};
		backingWaveform.GestureStarted += BeginWaveformGesture;
		backingWaveform.Gesture += SeekOrSelect;
		backingWaveform.GestureCompleted += CompleteWaveformGesture;
		backingWaveform.PointerWheelChanged += OnTimelineWheelChanged;
		waveform.GestureStarted += BeginWaveformGesture;
		waveform.Gesture += SeekOrSelect;
		waveform.GestureCompleted += CompleteWaveformGesture;
		waveform.PointerWheelChanged += OnTimelineWheelChanged;
		tabTimeline.PointerWheelChanged += OnTimelineWheelChanged;
		AddHandler(KeyDownEvent, OnTransportKeyDown, RoutingStrategies.Tunnel);
		timer.Tick += (_, _) => UpdatePlayback();
		if (initialPath is not null)
			Opened += async (_, _) => await LoadFile(initialPath);
		UpdateButtons();
	}

	private Control AddFileMenu(Control main) {
		if (OperatingSystem.IsMacOS()) {
			var nativeOpen = new NativeMenuItem("Open…") { Gesture = new KeyGesture(Key.O, KeyModifiers.Meta) };
			nativeOpen.Bind(NativeMenuItem.IsEnabledProperty, open.GetObservable(IsEnabledProperty));
			nativeOpen.Click += async (_, _) => await OpenFile();
			var nativeNewWindow = new NativeMenuItem("New Window") { Gesture = new KeyGesture(Key.N, KeyModifiers.Meta) };
			nativeNewWindow.Click += (_, _) => new MainWindow().Show();
			var fileMenu = new NativeMenu { Items = { nativeOpen, nativeNewWindow } };
			NativeMenu.SetMenu(this, new NativeMenu { Items = { new NativeMenuItem("File") { Menu = fileMenu } } });
			return main;
		}

		openMenu.Click += async (_, _) => await OpenFile();
		var newWindow = new MenuItem { Header = "_New Window" };
		newWindow.Click += (_, _) => new MainWindow().Show();
		var file = new MenuItem { Header = "_File", ItemsSource = new[] { openMenu, newWindow } };
		var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
		Put(layout, new Menu { ItemsSource = new[] { file } }, 0);
		Put(layout, main, 1);
		return layout;
	}

	private static TextBlock Label(string value, double size, bool mono = false) => new() {
		Text = value, FontSize = size,
		FontFamily = mono ? new FontFamily("Menlo,Consolas,monospace") : FontFamily.Default
	};

	private static StackPanel Row(params Control[] children) {
		var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, VerticalAlignment = VerticalAlignment.Center };
		foreach (var child in children) {
			child.VerticalAlignment = VerticalAlignment.Center;
			row.Children.Add(child);
		}
		return row;
	}

	private static void Put(Grid grid, Control control, int row, int column = 0) {
		Grid.SetRow(control, row);
		Grid.SetColumn(control, column);
		grid.Children.Add(control);
	}

	private async Task OpenFile(bool backing = false) {
		if (loading || (backing && waveform.Track is null))
			return;
		loading = true;
		UpdateButtons();
		try {
			var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions {
				Title = backing ? "Open Backing Track" : "Open Bass Stem",
				FileTypeFilter = [new FilePickerFileType("WAV or FLAC audio") { Patterns = ["*.wav", "*.flac"] }]
			});
			if (files.FirstOrDefault()?.TryGetLocalPath() is string path) {
				if (backing)
					await LoadBackingFile(path);
				else
					await LoadFile(path);
			}
		}
		catch (Exception ex) when (!lifetime.IsCancellationRequested) {
			status.Text = ex.Message;
		}
		finally {
			loading = false;
			UpdateButtons();
		}
	}

	private async Task LoadFile(string path) {
		StopPlayback();
		analysis?.Cancel();
		loading = true;
		UpdateButtons();
		status.Text = $"Loading {Path.GetFileName(path)}…";
		try {
			var track = await Task.Run(() => AudioTrack.Load(path, lifetime.Token));
			if (lifetime.IsCancellationRequested)
				return;

			analysis?.Cancel();
			playback?.Dispose();
			playback = null;
			playbackTrack = null;
			backingWaveform.Track = null;
			backingWaveform.IsVisible = false;
			backingFilename.Text = "No backing track loaded";
			playBacking = false;
			hearBass.IsChecked = true;
			waveform.Track = track;
			waveform.Selection = null;
			waveform.CursorTime = anchor = 0;
			tabDocument?.Dispose();
			tabDocument = null;
			try {
				tabDocument = new TabDocument(path, track.Duration);
				SetSaveStatus(tabDocument.IsReadOnly ? "Tab is read-only. Another window may be editing it, or the folder is not writable." : null);
			}
			catch (Exception ex) {
				SetSaveStatus($"Could not load tab file. {ex.Message}");
			}
			tabTimeline.IsReadOnly = tabDocument?.IsReadOnly != false;
			tabTimeline.SetNotes(tabDocument?.Notes ?? []);
			filename.Text = track.Name;
			Title = $"BassNotes — {track.Name}";
			FitTrack();
			ShowPitch(null, "Drag across a single note to identify it.");
			status.Text = Instructions;
			RefreshWaveform();
			if (tabDocument?.IsReadOnly == true)
				await ShowReadOnlyWarning();
		}
		catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
		catch (Exception ex) {
			status.Text = $"Could not open file. {ex.Message}";
		}
		finally {
			loading = false;
			UpdateButtons();
		}
	}

	private async Task LoadBackingFile(string path) {
		if (waveform.Track is not { } bass)
			return;
		PausePlayback();
		loading = true;
		UpdateButtons();
		status.Text = $"Loading backing track {Path.GetFileName(path)}…";
		try {
			var track = await Task.Run(() =>
				AudioTrack.Load(path, lifetime.Token, durationReferenceTrack: bass));
			if (lifetime.IsCancellationRequested)
				return;
			playback?.Dispose();
			playback = null;
			playbackTrack = null;
			backingWaveform.Track = track;
			backingWaveform.IsVisible = true;
			backingFilename.Text = track.Name;
			status.Text = Instructions;
			RefreshWaveform();
		}
		catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
		catch (Exception ex) {
			status.Text = $"Could not open backing track. {ex.Message}";
		}
		finally {
			loading = false;
			UpdateButtons();
		}
	}

	private void SwitchPlaybackTrack(bool backing) {
		// The newly checked radio can fire before the previous radio is unchecked.
		playBacking = backing;
		bool resume = playing;
		PausePlayback();
		playback?.Dispose();
		playback = null;
		playbackTrack = null;
		if (resume)
			TogglePlayback();
	}

	private void BeginWaveformGesture() {
		scrollAxis.Reset();
		resumeAfterWaveformClick = playing;
		PausePlayback();
	}

	private void SeekOrSelect(double start, double? end) {
		paused = false;
		analysis?.Cancel();
		waveform.Selection = end is double other && other != start
			? (Math.Min(start, other), Math.Max(start, other)) : null;
		anchor = waveform.Selection?.Start ?? start;
		waveform.CursorTime = anchor;
		ShowPitch(null, waveform.Selection is null ? "Drag across a single note to identify it." : "Release to identify the note.");
		RefreshWaveform();
	}

	private void CompleteWaveformGesture(bool isClick) {
		bool resume = resumeAfterWaveformClick && isClick;
		resumeAfterWaveformClick = false;
		if (resume && waveform.Track is { } track && anchor < track.Duration)
			TogglePlayback();
		else
			AnalyzeSelection();
	}

	private async void AnalyzeSelection() {
		if (waveform.Track is not { } track || waveform.Selection is not { } range)
			return;
		if (range.End - range.Start < PitchDetector.MinimumDuration) {
			ShowPitch(null, "Select at least 75 ms of a note.");
			return;
		}

		using var cancellation = new CancellationTokenSource();
		analysis = cancellation;
		ShowPitch(null, "Identifying note…");
		try {
			int start = (int)Math.Round(range.Start * track.Rate);
			int end = Math.Min(track.Samples.Length, (int)Math.Round(range.End * track.Rate));
			var result = await Task.Run(() => PitchDetector.Detect(track.Samples, track.Rate, start, end, cancellation.Token));
			if (!cancellation.IsCancellationRequested)
				ShowPitch(result, "No steady pitch detected.");
		}
		catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
		finally {
			if (analysis == cancellation)
				analysis = null;
		}
	}

	private void ShowPitch(Pitch? result, string empty) {
		detectedPitch = result;
		note.Text = result?.Note ?? "—";
		pitchDetails.Text = result is Pitch p
			? $"{p.Cents:+0.0;-0.0;0.0} cents  ·  {p.Frequency:0.00} Hz"
			: empty;
		UpdateTab();
	}

	private void UpdateTab() {
		int semitones = octave * 12;
		tab.Fingering = detectedPitch?.GetFingering(semitones);
		tabStatus.IsVisible = detectedPitch is not null && tab.Fingering is null;
		tab.RefreshCursor();
		tab.InvalidateVisual();
		UpdateAddToTab();
	}

	private void SelectTabString(int stringIndex) {
		int semitones = octave * 12;
		if (detectedPitch?.GetFret(semitones, stringIndex) is not int fret)
			return;
		tab.Fingering = (stringIndex, fret);
		tab.InvalidateVisual();
		UpdateAddToTab();
	}

	private async Task ShowReadOnlyWarning() {
		var close = new Button { Content = "OK", HorizontalAlignment = HorizontalAlignment.Right };
		var warning = new Window {
			Title = "Tab is read-only", Width = 420, SizeToContent = SizeToContent.Height,
			CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner,
			Content = new StackPanel {
				Margin = new Thickness(20), Spacing = 16,
				Children = {
					new TextBlock {
						Text = "This tab could not be locked for editing. Another BassNotes window may have it open, or the folder may not be writable. You can view the saved notes and use playback. Reopen the audio after closing the editing window to edit its tab.",
						TextWrapping = TextWrapping.Wrap
					},
					close
				}
			}
		};
		close.Click += (_, _) => warning.Close();
		await warning.ShowDialog(this);
	}

	private void SyncTabTimeline() {
		tabTimeline.SetViewport(waveform.ViewStart, waveform.ViewLength, waveform.CursorTime);
		backingWaveform.ViewStart = waveform.ViewStart;
		backingWaveform.ViewLength = waveform.ViewLength;
		backingWaveform.CursorTime = waveform.CursorTime;
		backingWaveform.Selection = waveform.Selection;
		backingWaveform.InvalidateVisual();
	}

	private void UpdateAddToTab() =>
		addToTab.IsEnabled = !loading && tabDocument is { IsReadOnly: false } && detectedPitch is not null &&
							 waveform.Selection is not null && tab.Fingering is not null;

	private void SetSaveStatus(string? message) {
		saveStatus.Text = message;
		saveStatus.IsVisible = message is not null;
	}

	private void AddToTab() {
		if (!addToTab.IsEnabled || tabDocument is null || detectedPitch is not Pitch pitch ||
			waveform.Selection is not { } range || tab.Fingering is not { } fingering)
			return;
		try {
			tabDocument.Add(new TabNote(Guid.NewGuid(), range.Start, range.End,
				fingering.String, fingering.Fret, pitch.Midi + octave * 12));
			tabTimeline.SetNotes(tabDocument.Notes);
			SetSaveStatus(null);
		}
		catch (Exception ex) {
			SetSaveStatus($"Could not save tab. {ex.Message} The note was not added.");
		}
	}

	private void RemoveTabNote(Guid id) {
		if (loading || tabDocument is not { IsReadOnly: false })
			return;
		try {
			tabDocument.Remove(id);
			tabTimeline.SetNotes(tabDocument.Notes);
			SetSaveStatus(null);
		}
		catch (Exception ex) {
			SetSaveStatus($"Could not save tab. {ex.Message} The note was not removed.");
		}
	}

	private void ChangeSavedTabString(Guid id, int stringIndex) {
		if (loading || tabDocument is not { IsReadOnly: false })
			return;
		try {
			tabDocument.ChangeString(id, stringIndex);
			tabTimeline.SetNotes(tabDocument.Notes);
			SetSaveStatus(null);
		}
		catch (Exception ex) {
			SetSaveStatus($"Could not save tab. {ex.Message} The fingering was not changed.");
		}
	}

	private void TogglePlayback() {
		resumeAfterWaveformClick = false;
		if (loading || waveform.Track is not { } track)
			return;
		if (playing) {
			PausePlayback();
			return;
		}

		double end = waveform.Selection?.End ?? track.Duration;
		double start = paused ? waveform.CursorTime : anchor;
		if (start >= end)
			start = waveform.Selection?.Start ?? 0;
		try {
			StartPlayback(track, start, end);
		}
		catch (Exception ex) {
			status.Text = $"Playback failed. {ex.Message}";
		}
	}

	private void StartPlayback(AudioTrack track, double start, double end) {
		var audibleTrack = playBacking && backingWaveform.Track is { } backing ? backing : track;
		if (playbackTrack != audibleTrack) {
			playback?.Dispose();
			playback = null;
			playbackTrack = audibleTrack;
		}
		playback ??= Playback.Open(audibleTrack);
		playback.Play(start, end);
		playbackEnd = end;
		waveform.CursorTime = start;
		playing = true;
		paused = false;
		timer.Start();
		UpdateButtons();
		FollowCursor();
		RefreshWaveform();
	}

	private void PausePlayback() {
		if (!playing)
			return;
		try {
			playback!.Pause();
			waveform.CursorTime = Math.Clamp(playback.Position, 0, playbackEnd);
		}
		catch (Exception ex) {
			status.Text = $"Playback failed. {ex.Message}";
		}
		EndPlayback(pausedAtCursor: true);
	}

	private void StopPlayback() {
		resumeAfterWaveformClick = false;
		PausePlayback();
		paused = false;
		waveform.CursorTime = anchor;
		FollowCursor();
		RefreshWaveform();
	}

	private void UpdatePlayback() {
		try {
			if (!playback!.IsPlaying) {
				double loopStart = waveform.Selection?.Start ?? 0;
				if (loop.IsChecked == true && waveform.Track is { } track &&
					track.FrameAt(loopStart) < track.FrameAt(playbackEnd))
				{
					StartPlayback(track, loopStart, playbackEnd);
					return;
				}

				waveform.CursorTime = playbackEnd;
				EndPlayback(pausedAtCursor: false);
				return;
			}
			bool cursorWasVisible = waveform.CursorTime >= waveform.ViewStart &&
									waveform.CursorTime <= waveform.ViewStart + waveform.ViewLength;
			waveform.CursorTime = Math.Min(playback.Position, playbackEnd);
			if (cursorWasVisible || centerPlayback.IsChecked == true)
				FollowCursor();
			RefreshWaveform();
		}
		catch (Exception ex) {
			status.Text = $"Playback failed. {ex.Message}";
			playback?.Dispose();
			playback = null;
			EndPlayback(pausedAtCursor: false);
		}
	}

	private void EndPlayback(bool pausedAtCursor) {
		timer.Stop();
		playing = false;
		paused = pausedAtCursor;
		UpdateButtons();
		RefreshWaveform();
	}

	private void FollowCursor() {
		if (playing && centerPlayback.IsChecked == true) {
			if (waveform.CursorTime < waveform.ViewStart)
				scroll.Value = Math.Clamp(waveform.CursorTime, 0, scroll.Maximum);
			else if (waveform.CursorTime > waveform.ViewStart + waveform.ViewLength / 2)
				scroll.Value = Math.Clamp(waveform.CursorTime - waveform.ViewLength / 2, 0, scroll.Maximum);
			return;
		}
		if (waveform.CursorTime < waveform.ViewStart || waveform.CursorTime > waveform.ViewStart + waveform.ViewLength)
			scroll.Value = Math.Clamp(waveform.CursorTime - waveform.ViewLength * 0.15, 0, scroll.Maximum);
	}

	private void FitTrack() {
		if (waveform.Track is { } track)
			SetViewport(0, track.Duration);
	}

	private void Zoom(double factor) {
		if (waveform.Track is null)
			return;
		double focus = waveform.Selection is { } range ? (range.Start + range.End) / 2 : waveform.CursorTime;
		Zoom(factor, focus, 0.5);
	}

	private void OnTimelineWheelChanged(object? sender, PointerWheelEventArgs e) {
		if (e.Handled || waveform.Track is null || sender is not Control surface ||
			surface.Bounds.Width <= 0 || e.Delta == default)
			return;

		if (scrollModifiers != e.KeyModifiers)
			scrollAxis.Reset();
		scrollModifiers = e.KeyModifiers;
		Vector delta = e.Delta;
		if (e.KeyModifiers == KeyModifiers.Shift && delta.X == 0)
			delta = new Vector(delta.Y, 0);
		delta = scrollAxis.Filter(delta);
		if (delta != default) {
			double pointerFraction = Math.Clamp(e.GetPosition(surface).X / surface.Bounds.Width, 0, 1);
			ScrollWaveform(delta, pointerFraction);
		}
		e.Handled = true;
	}

	private void ScrollWaveform(Vector delta, double pointerFraction) {
		if (delta.X != 0) {
			scroll.Value = Math.Clamp(waveform.ViewStart - delta.X * waveform.ViewLength * 0.10, 0, scroll.Maximum);
			return;
		}

		double focus = waveform.ViewStart + pointerFraction * waveform.ViewLength;
		Zoom(Math.Pow(1.25, -delta.Y), focus, pointerFraction);
	}

	private void Zoom(double factor, double focus, double focusFraction) {
		double duration = waveform.Track!.Duration;
		double length = Math.Clamp(waveform.ViewLength * factor, Math.Min(0.05, duration), duration);
		if (length != waveform.ViewLength)
			SetViewport(focus - length * focusFraction, length);
	}

	private void SetViewport(double start, double length) {
		waveform.ViewLength = length;
		scroll.Maximum = Math.Max(0, waveform.Track!.Duration - length);
		scroll.SmallChange = length * 0.1;
		scroll.LargeChange = length * 0.8;
		scroll.Value = Math.Clamp(start, 0, scroll.Maximum);
		waveform.ViewStart = scroll.Value;
		UpdateButtons();
		waveform.InvalidateVisual();
		SyncTabTimeline();
	}

	private void RefreshWaveform() {
		time.Text = $"{Palette.Timestamp(waveform.CursorTime)} / {Palette.Timestamp(waveform.Track?.Duration ?? 0)}";
		selection.Text = waveform.Selection is { } range
			? $"Selection  {Palette.Timestamp(range.Start)} – {Palette.Timestamp(range.End)}  ({Palette.Timestamp(range.End - range.Start)})"
			: "Selection  —";
		waveform.InvalidateVisual();
		SyncTabTimeline();
	}

	private void UpdateButtons() {
		UpdateAddToTab();
		bool loaded = waveform.Track is not null;
		open.IsEnabled = openMenu.IsEnabled = !loading;
		openBacking.IsEnabled = loaded && !loading;
		hearBass.IsEnabled = loaded && !loading;
		hearBacking.IsEnabled = backingWaveform.Track is not null && !loading;
		play.IsEnabled = loaded && !loading;
		playIcon.Data = playing ? PauseGeometry : PlayGeometry;
		AutomationProperties.SetName(play, playing ? "Pause" : "Play");
		ToolTip.SetTip(play, playing ? "Pause (Space)" : "Play (Space)");
		stop.IsEnabled = loaded && !loading;
		zoomIn.IsEnabled = loaded && waveform.ViewLength > 0.05;
		zoomOut.IsEnabled = fit.IsEnabled = scroll.IsEnabled = loaded && scroll.Maximum > 0;
	}

	private void OnTransportKeyDown(object? sender, KeyEventArgs e) {
		if (e.KeyModifiers != KeyModifiers.None ||
			!play.IsEnabled || jump.IsDropDownOpen)
			return;

		if (e.Key == Key.Enter) {
			if (addToTab.IsEnabled) {
				e.Handled = true;
				AddToTab();
			}
			return;
		}

		if (e.Key is not (Key.Space or Key.Left or Key.Right))
			return;
		e.Handled = true;
		if (e.Key == Key.Space)
			TogglePlayback();
		else
			JumpCursor(e.Key == Key.Right ? 1 : -1);
	}

	private void JumpCursor(int direction) {
		if (waveform.Track is not { } track)
			return;

		bool resume = playing;
		PausePlayback();
		double target = Math.Clamp(waveform.CursorTime + direction * (jump.SelectedIndex + 1), 0, track.Duration);
		SeekOrSelect(target, null);
		resumeAfterWaveformClick = false;
		FollowCursor();
		if (resume && target < track.Duration)
			TogglePlayback();
	}

	protected override void OnKeyDown(KeyEventArgs e) {
		base.OnKeyDown(e);
		if (e.Handled)
			return;
		if (!OperatingSystem.IsMacOS() && e.KeyModifiers == KeyModifiers.Control && e.Key == Key.O)
			_ = OpenFile();
		else if (!OperatingSystem.IsMacOS() && e.KeyModifiers == KeyModifiers.Control && e.Key == Key.N)
			new MainWindow().Show();
		else if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None)
			StopPlayback();
		else
			return;
		e.Handled = true;
	}

	protected override void OnClosed(EventArgs e) {
		lifetime.Cancel();
		analysis?.Cancel();
		timer.Stop();
		playback?.Dispose();
		tabDocument?.Dispose();
		base.OnClosed(e);
	}
}
