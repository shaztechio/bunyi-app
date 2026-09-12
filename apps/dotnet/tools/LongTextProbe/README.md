# Long-text generation probe

Runs the installed self-hosted ONNX exports through the app's sectioned generation
path, with a generic passage and sampling seed 7. Saves one WAV with full-text
metadata and a timing report per mode. It does not play audio or modify settings.

From `apps/dotnet`:

```powershell
dotnet run --project tools/LongTextProbe -c Release -- clone MODELS_PARENT OUTPUT_FOLDER
```

`MODELS_PARENT` is the `Models/models/self-hosted` folder containing the three
`models.bunyi.app-onnx-*` exports. Replace `clone` with `preset` or `design`.
Normal model discovery may download missing models. Run modes sequentially.
The clone test expects a synthetic 24 kHz mono float reference in
`OUTPUT_FOLDER/reference.f32`, saying “Hello! We'll begin in just a few minutes.”
No private voice recording is needed. Design creates its own short opening.

See [validation](VALIDATION.md) and the [long-text plan](../../../../spec/LONG-TEXT-PLAN.md).
