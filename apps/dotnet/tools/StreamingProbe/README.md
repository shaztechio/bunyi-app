# Streaming demonstration probe

## Current sectioned generation

Add `--sections` to `preset`, `design` or `clone` to exercise the current engine
route with a longer generic passage. Use the self-hosted models parent shown
below; this route uses normal model discovery and may download missing models.
Generate `reference` first in the same results folder for the clone test. Run
sequentially, with `--play` for the device check. Reports are `<mode>-sections.json`.
This checks accepted-section offsets, final length, early playback and the
Design-to-Clone continuation path. It does not use private voices or settings.

## Historical rolling-decoder comparison

Runs the installed ONNX exports with one continuous generation per mode. Captures
rolling preview PCM and the final full decode of the same codec sequence, checks
contiguous sample offsets and equal sample counts, saves both WAVs and timing/error
metrics. `--play` also checks the actual SoundFlow audio device while generation
continues. It does not download models or modify the app's saved voices/settings.

From `apps/dotnet`, with all three self-hosted models already downloaded:

```powershell
dotnet build tools/StreamingProbe/StreamingProbe.csproj -c Release
$models = Join-Path $env:LOCALAPPDATA 'Bunyi/Models/models/self-hosted'
$probe = 'tools/StreamingProbe/bin/Release/net10.0/StreamingProbe.dll'
$results = 'artifacts/streaming-validation'
dotnet $probe reference $models $results
dotnet $probe preset $models $results --play
dotnet $probe design $models $results --play
dotnet $probe clone $models $results --play
```

Run the modes sequentially to avoid loading multiple models. The reference step
generates a short synthetic clip with a matching transcript for the clone step;
it never reuses private user recordings. The three long examples cross the
estimated 20-second threshold. The probe uses CPU explicitly and a fixed sampling
seed; it does not claim a cross-hardware deterministic waveform.

The demonstration retains the original full final vocoder pass and global output
attenuation. Preview and final audio therefore need not be numerically identical.
The error metrics compare float PCM before uniform file attenuation; audition the
WAVs to judge boundary quality. `firstChunkSeconds` includes model load and measures
delivery to the playback queue, not hardware-measured acoustic latency. The live
player buffers before starting. Hardware playback can pause when inference runs
slower than realtime; those waiting gaps are absent from the saved WAV.

The app itself needs no command line: launch the demonstration build, choose any
mode and enter enough text for the estimate to exceed 20 seconds. The shared
requirements and remaining production/macOS work are in
[the streaming plan](../../../../spec/STREAMING-PLAN.md).
