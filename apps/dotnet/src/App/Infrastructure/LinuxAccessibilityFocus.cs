// Copyright 2026 Shazron Abdullah and Bunyi contributors
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System.Reflection;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Bunyi.Core.Diagnostics;

namespace Bunyi.App.Infrastructure;

/// <summary>
/// Bridge missing Linux focus paths and live announcements in Avalonia 12.1.1.
/// Remove each workaround when the framework supplies its corresponding event.
/// See #159. This bridges AT-SPI nodes, not just Avalonia automation peers.
/// </summary>
internal static class LinuxAccessibilityFocus
{
    private static IDisposable? _subscription;
    private static Bridge? _bridge;
    private static ILogSink? _log;

    public static void Install(ILogSink log)
    {
        if (!OperatingSystem.IsLinux() || _subscription is not null) return;
        try
        {
            var bridge = _bridge = new Bridge();
            _log = log;
            var reported = false;
            var failed = false;
            _subscription = InputElement.GotFocusEvent.AddClassHandler<TopLevel>((_, args) =>
            {
                if (args.Source is not Control control) return;
                // Run after the normal bridge. An attached node already got its event;
                // repairing only missing nodes avoids announcing the same focus twice.
                Dispatcher.UIThread.Post(() =>
                {
                    if (failed || TopLevel.GetTopLevel(control)?.FocusManager?.GetFocusedElement() != control)
                        return;
                    try
                    {
                        if (bridge.Repair(control) && !reported)
                        {
                            reported = true;
                            log.Log("Linux accessibility: repaired an unqueried AT-SPI focus path (Avalonia 12.1.1 workaround).");
                        }
                    }
                    catch (Exception e)
                    {
                        failed = true;
                        log.Log($"Linux accessibility focus workaround failed: {e.GetBaseException().Message}");
                    }
                }, DispatcherPriority.Input);
            }, RoutingStrategies.Bubble, handledEventsToo: true);
        }
        catch (Exception e)
        {
            log.Log($"Linux accessibility focus workaround unavailable: {e.GetBaseException().Message}");
        }
    }

    internal static void Announce(Control control, string message)
    {
        try { _bridge?.Announce(control, message); }
        catch (Exception e)
        {
            _log?.Log($"Linux accessibility announcement failed: {e.GetBaseException().Message}");
        }
    }

    // Avalonia exposes no public API for attaching AT-SPI nodes. Keep the
    // version-dependent access in one place; fail visibly without crashing the app.
    internal sealed class Bridge
    {
        private const BindingFlags Members = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        private readonly PropertyInfo _implementation;
        private readonly FieldInfo _platform;
        private readonly PropertyInfo _server;
        private readonly MethodInfo _find;
        private readonly MethodInfo _children;
        private readonly MethodInfo _emit;
        private readonly PropertyInfo _eventHandler;
        private readonly MethodInfo _signal;
        private readonly Type _variant;
        private readonly Type _properties;

        internal Bridge()
        {
            _implementation = typeof(IRootProvider).GetProperty("PlatformImpl", Members)
                ?? throw new MissingMemberException("IRootProvider.PlatformImpl");
            var window = Type.GetType("Avalonia.X11.X11Window, Avalonia.X11", throwOnError: true)!;
            _platform = window.GetField("_platform", Members) ?? throw new MissingFieldException(window.FullName, "_platform");
            _server = _platform.FieldType.GetProperty("AtSpiServer", Members)
                ?? throw new MissingMemberException("AvaloniaX11Platform.AtSpiServer");
            var serverType = _server.PropertyType;
            _find = serverType.GetMethod("TryGetAttachedNode", Members)
                ?? throw new MissingMethodException(serverType.FullName, "TryGetAttachedNode");
            _children = _find.ReturnType.GetMethod("EnsureChildren", Members)
                ?? throw new MissingMethodException(_find.ReturnType.FullName, "EnsureChildren");
            _emit = serverType.GetMethod("EmitFocusChange", Members)
                ?? throw new MissingMethodException(serverType.FullName, "EmitFocusChange");
            _eventHandler = _find.ReturnType.GetProperty("EventObjectHandler", Members)
                ?? throw new MissingMemberException("AtSpiNode.EventObjectHandler");
            _signal = _eventHandler.PropertyType.GetMethod("EmitSignal", Members)
                ?? throw new MissingMethodException("AtSpiEventObjectHandler.EmitSignal");
            _variant = serverType.Assembly.GetType("Avalonia.DBus.DBusVariant", throwOnError: true)!;
            _properties = typeof(Dictionary<,>).MakeGenericType(typeof(string), _variant);
        }

        internal bool Repair(Control control)
        {
            var node = EnsureNode(control, onlyMissing: true, out var server);
            if (node is null) return false;
            _emit.Invoke(server, [node]);
            return true;
        }

        internal bool Announce(Control control, string message)
        {
            var node = EnsureNode(control, onlyMissing: false, out _);
            if (node is null || _eventHandler.GetValue(node) is not { } handler) return false;
            // AT-SPI Event.xml: detail, politeness (1=polite), unused integer,
            // variant containing the announcement string, and empty properties.
            var text = Activator.CreateInstance(_variant, new object[] { message });
            var properties = Activator.CreateInstance(_properties);
            _signal.Invoke(handler, new object?[] { "Announcement", new object?[] { "", 1, 0, text, properties } });
            return true;
        }

        private object? EnsureNode(Control control, bool onlyMissing, out object? server)
        {
            server = null;
            var peer = ControlAutomationPeer.CreatePeerForElement(control);
            if (TopLevel.GetTopLevel(control) is not { } topLevel) return null;
            var root = ControlAutomationPeer.CreatePeerForElement(topLevel);
            if (root.GetProvider<IRootProvider>() is not { } provider) return null;
            var impl = _implementation.GetValue(provider);
            if (impl is null || !_platform.DeclaringType!.IsInstanceOfType(impl)) return null;
            var platform = _platform.GetValue(impl);
            server = _server.GetValue(platform);
            if (server is null) return null;
            if (_find.Invoke(server, [peer]) is { } existing) return onlyMissing ? null : existing;

            // GetParent connects managed peers to their ancestors. Ask the native
            // bridge to attach each level along that path, without scanning siblings'
            // subtrees or materializing the whole History list.
            var path = new Stack<AutomationPeer>();
            for (var current = peer; current is not null; current = current.GetParent())
            {
                path.Push(current);
                if (ReferenceEquals(current, root)) break;
            }
            object? node = null;
            while (path.TryPop(out var current))
            {
                if (node is not null) _children.Invoke(node, null);
                // AT-SPI flattens selection containers, so template ancestors
                // can be absent even though their selection items are attached.
                if (_find.Invoke(server, [current]) is { } attached) node = attached;
            }
            node = _find.Invoke(server, [peer]);
            return node;
        }
    }
}