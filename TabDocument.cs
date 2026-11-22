using System.Text.Json;
using System.Text.Json.Serialization;

namespace BassNotes;

sealed record TabNote(Guid Id, double Start, double End, int String, int Fret, int Midi);
sealed record SavedTab(int Version, List<TabNote> Notes);

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SavedTab))]
partial class TabJsonContext : JsonSerializerContext;

sealed class TabDocument : IDisposable {
	private readonly string path;
	private readonly double duration;
	private readonly FileStream? editLock;
	public bool IsReadOnly => editLock is null;
	public IReadOnlyList<TabNote> Notes { get; private set; } = [];

	public TabDocument(string audioPath, double duration) {
		path = Path.ChangeExtension(Path.GetFullPath(audioPath), ".bassnotes.json");
		this.duration = duration;
		try {
			// A stable lock file stays locked while atomic JSON replacements change the data file.
			editLock = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite,
				FileShare.None, bufferSize: 1, FileOptions.DeleteOnClose);
		}
		catch (IOException) { }
		catch (UnauthorizedAccessException) { }
		try {
			Notes = Read();
		}
		catch {
			editLock?.Dispose();
			throw;
		}
	}

	public void Dispose() => editLock?.Dispose();

	private List<TabNote> Read() {
		if (!File.Exists(path))
			return [];
		var saved = JsonSerializer.Deserialize(File.ReadAllText(path), TabJsonContext.Default.SavedTab)
			?? throw new InvalidDataException("The tab file is empty.");
		if (saved.Version != 1 || saved.Notes is null)
			throw new InvalidDataException("Unsupported tab file format.");
		var ids = new HashSet<Guid>();
		foreach (var note in saved.Notes) {
			if (note is null || note.Id == Guid.Empty || !ids.Add(note.Id) ||
				!double.IsFinite(note.Start) || !double.IsFinite(note.End) ||
				note.Start < 0 || note.End <= note.Start || note.End > duration + 0.001 ||
				new Pitch(0, note.Midi, 0).GetFret(0, note.String) != note.Fret)
				throw new InvalidDataException("The tab file contains an invalid note.");
		}
		return saved.Notes.OrderBy(note => note.Start).ToList();
	}

	public void Add(TabNote note) => Save(notes => {
		if (!notes.Any(existing => existing.Start == note.Start && existing.End == note.End &&
								   existing.String == note.String && existing.Fret == note.Fret))
			notes.Add(note);
	});

	public void Remove(Guid id) => Save(notes => notes.RemoveAll(note => note.Id == id));

	public void ChangeString(Guid id, int stringIndex) => Save(notes => {
		int index = notes.FindIndex(note => note.Id == id);
		if (index < 0)
			return;
		var note = notes[index];
		if (note.String != stringIndex && new Pitch(0, note.Midi, 0).GetFret(0, stringIndex) is int fret)
			notes[index] = note with { String = stringIndex, Fret = fret };
	});

	private void Save(Action<List<TabNote>> edit) {
		if (IsReadOnly)
			throw new IOException("This tab is open in read-only mode.");
		string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
		try {
			var notes = Read();
			edit(notes);
			notes.Sort((left, right) => left.Start.CompareTo(right.Start));
			using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
				JsonSerializer.Serialize(output, new SavedTab(1, notes), TabJsonContext.Default.SavedTab);
				output.Flush(true);
			}
			File.Move(temporary, path, true);
			Notes = notes;
		}
		finally {
			File.Delete(temporary);
		}
	}
}
