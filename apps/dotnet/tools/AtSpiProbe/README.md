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
