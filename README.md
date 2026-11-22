# BassNotes

https://github.com/user-attachments/assets/5c9ef96a-3e15-4a3f-a702-01f184b0a6cd

BassNotes is a desktop app for macOS and Windows that identifies notes in an isolated bass stem and helps you build a five-string bass tab. It opens WAV and FLAC files.

## AI disclosure

Most of the code in BassNotes was written by an AI agent under human direction.

## Getting started

Download and extract the pre-built Windows ZIP, then launch BassNotes. On macOS, open the DMG and drag BassNotes to Applications. These downloads include the .NET runtime; no separate installation is needed.

Use **Open…** on the first row to load an isolated bass stem, then highlight a note in the waveform to identify it.

## Controls

- Click the waveform to set the playback cursor. During playback, a click resumes at the new position. Drag to stop playback and highlight a single sustained note of at least 75 ms.
- **Play** or **Space** plays from the cursor or plays the highlighted range. Press again to pause or resume. **Stop** or **Esc** returns the cursor to the start.
- **Loop** repeats the highlighted range, or repeats the whole file when there is no selection.
- **+** and **−** zoom around the selection or cursor. Use the slider below the waveform to pan and **Fit** to see the whole track.
- Over the waveform, scroll up or down with a wheel or touchpad to zoom in or out around the pointer. Scroll horizontally or hold **Shift** while using a vertical wheel to pan.
- **File → New Window** opens another independent editor.

The tuner reports the nearest note using A4 = 440 Hz, its measured frequency, and the offset in cents. The five-string tab shows the lowest available fret in standard BEADG tuning, assuming up to 24 frets. E1 appears as the open E string.

Use **Octave** in the tab panel to shift the fingering by −2 to +2 octaves (default 0). The tuner and playback use the original pitch. For example, selecting **−1** for a detected E2 shows the open E string for E1.

After identifying a selection, choose its string and octave and press **Add to Tab** or **Enter**. The saved fret is placed at the selection's start in the timeline below the waveform. Click the red **×** below a saved note to remove it.

Edits are saved immediately beside the audio, replacing its extension with **.bassnotes.json**. Each saved note keeps its time range, target pitch, string, and fret. Opening the audio restores its tab.

Open a bass stem first, then use the Open button on the row below it to load an optional **Backing track** (the original song or a bassless mix). **Listen to** chooses which track is audible and can be changed during playback. Both waveforms share the cursor, selection, zoom, and pan. Note detection and saved tab data use the bass stem.
