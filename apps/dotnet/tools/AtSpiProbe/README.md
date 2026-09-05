# Linux accessibility focus regression (#159)

This checks real AT-SPI focus events from a fresh Bunyi process. It does not
run Orca or prove spoken output. Use a Linux desktop/X11 or XWayland session
with AT-SPI2, Python 3, PyGObject's Atspi 2.0 bindings, libX11 and libXtst.

```sh
python3 apps/dotnet/tools/AtSpiProbe/check.py /absolute/path/to/Bunyi.App
```

The probe launches its own process with temporary settings/data directories,
finds only that process's X11 window by PID, focuses it and sends Tab keys.
Do not interact with the test window while it runs. It terminates only its own
process and writes the app output to `atspi-app.log` (override with `--log`).

The first pass must receive named focus events for Preset voice, SCRIPT,
Language and Speaker without first enumerating the app's tree. Only then does
it read the tree and repeat Tab, rejecting duplicate focus events in either
pass. Enumeration first masks the Avalonia 12.1.1 regression.

The original `RootAtSpiNode.OnRootFocusChanged` only calls `EnsureChildren`
on the window root. Deep descendants have no attached AT-SPI node yet, so
`EmitFocusChange(null)` does nothing. Bunyi's workaround attaches the focus
path after native focus handling and sends an event only for a missing node.
It also skips managed layout ancestors flattened by AT-SPI selection handlers.
The private bindings are checked by `LinuxAccessibilityFocusTests`; update or
remove the workaround when upgrading Avalonia.

Reproduced on WSL Linux with AT-SPI 2.52.0: the original build emitted no
focus events before tree enumeration; the candidate emits named events before
it. Fedora Orca by-ear verification and the remaining #159 audit stay open.

The picker checks also require a non-null, named selected child while closed,
then send Down/Up and inspect selection events and the updated names. Tree
inspection rejects leaked layout class names (Panel, StackPanel,
ContentPresenter, etc.). Quiet Linux layout styles clear only the class-name
fallback; explicit accessible names and descendant controls remain available.
The Linux ComboBox peer exposes the collapsed selection as a child and delays
its selection event until the display-name binding has updated. Windows keeps
the standard peer and expanded Linux pickers keep the framework selection path.
The picker probe also opens each dropdown with Alt+Down, checks a focused
item event after Down, and closes it with Escape.

Script and Style must expose their placeholder only through `placeholder-text`,
with no duplicate description or template children. Their editable text and
separate help/validation remain on the field peer.

To verify live announcements without model inference, publish the diagnostic
host from `apps/dotnet`, then use the same Python probe:

```sh
dotnet publish tools/AtSpiProbe/Host -c Release -r linux-x64 --self-contained -o artifacts/atspi-host
python3 tools/AtSpiProbe/check.py artifacts/atspi-host/AtSpiProbe.Host --expect-announcements
```

The host opens the real app and sets its Status/Announcement bindings to three
known messages, twelve seconds apart. It does not generate audio or download
models. The probe checks one polite AT-SPI Object Announcement for each message,
in order, without focusing the status element. Generation pacing is covered by
`ScreenReaderTests`: state changes immediately, frame updates no more often
than every ten seconds. Actual Orca speech still requires a manual check.

The signal follows GNOME's `at-spi2-core/xml/Event.xml`: a string detail,
politeness 1, unused integer 0, a variant containing the spoken string, and an
empty properties dictionary. Linux does not also send a Name change for this
message; Windows retains its UIA LiveRegionChanged route.

The Fedora user confirmed candidate 2's focus, layout and picker speech.
Candidate 3's placeholder and progress speech still needs their verification.
