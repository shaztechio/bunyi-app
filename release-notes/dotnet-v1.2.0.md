Bunyi 1.2.0 for Windows and Linux improves keyboard access and screen-reader speech, fixes Linux audio playback, and adds an easier way to choose the Bunyi model mirror.

## Accessibility and keyboard navigation

- Controls announce useful names and context. Settings actions identify the configuration, folder, or model they affect; toolbar buttons use short names while retaining their longer hover tips.
- Linux Orca now reads focused controls and voice and appearance choices, without announcing layout containers or repeating Script/Style placeholders and downloaded-model details.
- Generation announces state changes and paced frame progress. The remaining Linux Orca screen audit was manually verified before release.
- Settings keyboard navigation returns to the tab bar, and History supports arrow keys, Home, and End.
- Open Settings with **Ctrl+,**, Logs with **Ctrl+L**, Doctor with **Ctrl+D**, and Help with **F1**.

## Linux audio

- Fix audio backend selection that could prevent playback with an `InvalidArgs` device error, and report the actual native backend correctly in Logs.

## Model sources

- Settings -> Models includes a **Bunyi mirror** configuration. Select **Restore** to use it for all three modes; Hugging Face remains the default.
- Saved configurations show where their sources point, and the Restore action is clearly labelled.
- Model-source fields show their actual defaults, and source changes take effect without restarting the app.
- For mirror operators, the source repository includes a tool to create manifests from an existing downloaded-model folder.

Includes the fixes from [PR #205](https://github.com/shaztechio/bunyi-app/pull/205) and [PR #206](https://github.com/shaztechio/bunyi-app/pull/206).
