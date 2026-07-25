using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Qwen3TtsStudio.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
        // TODO: busy-close confirmation (spec §9) — hook Closing, and if the
        // engine is busy, prompt "Stop the current operation?" (Keep Working
        // default / Stop and Close), cancel on confirm.
    }
}
