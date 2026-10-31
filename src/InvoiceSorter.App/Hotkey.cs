using System.Globalization;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;

namespace InvoiceSorter.App;

/// <summary>
/// Keyboard shortcuts of buttons: <c>local:Hotkey.Keys="F2"</c> (or "Shift+F3") on a button makes the key press it.
/// Only a visible, enabled button reacts, so the same key can belong to buttons of different steps. The action buttons
/// show the key in their corner, and every button gets it in its tooltip.
/// </summary>
public static class Hotkey
{
    public static readonly DependencyProperty KeysProperty = DependencyProperty.RegisterAttached(
        "Keys", typeof(string), typeof(Hotkey), new PropertyMetadata(null));

    public static string? GetKeys(DependencyObject element) => (string?)element.GetValue(KeysProperty);
    public static void SetKeys(DependencyObject element, string? value) => element.SetValue(KeysProperty, value);

    /// <summary>"F2", "Shift+F3" for a key press (only function keys, alone or with Shift); null for anything else.</summary>
    public static string? Gesture(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;   // F10 arrives as a system key
        if (key is < Key.F1 or > Key.F24) return null;
        return Keyboard.Modifiers switch
        {
            ModifierKeys.None => key.ToString(),
            ModifierKeys.Shift => "Shift+" + key,
            _ => null,
        };
    }

    /// <summary>All buttons with a shortcut under <paramref name="root"/> (also the ones not shown right now); adds the key to their tooltip.</summary>
    public static List<ButtonBase> Collect(DependencyObject root)
    {
        var found = new List<ButtonBase>();
        void Walk(DependencyObject node)
        {
            if (node is ButtonBase button && GetKeys(button) is { Length: > 0 } keys)
            {
                found.Add(button);
                var hint = $"Gyorsbillentyű: {keys}";
                button.ToolTip = button.ToolTip is string tip && tip.Length > 0 ? $"{tip}\n{hint}" : hint;
            }
            foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>()) Walk(child);
        }
        Walk(root);
        return found;
    }

    /// <summary>Presses the visible, enabled button of the key; true when there was one.</summary>
    public static bool Press(IEnumerable<ButtonBase> buttons, string gesture)
    {
        var button = buttons.FirstOrDefault(b => GetKeys(b) == gesture && b.IsVisible && b.IsEnabled);
        if (button == null) return false;
        // A date or name being typed is committed first (those boxes update on leaving them).
        if (Keyboard.FocusedElement is TextBox box) box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        switch (UIElementAutomationPeer.CreatePeerForElement(button))
        {
            case IToggleProvider toggle: toggle.Toggle(); break;
            case IInvokeProvider invoke: invoke.Invoke(); break;
            default: return false;
        }
        return true;
    }
}

/// <summary>The small key badge of the buttons: parameter "shift" → "⇧" (for "Shift+F3"), "key" → "F3".</summary>
public sealed class HotkeyBadgeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var keys = value as string ?? "";
        bool shift = keys.StartsWith("Shift+", StringComparison.Ordinal);
        return parameter as string == "shift" ? (shift ? "⇧" : "") : shift ? keys["Shift+".Length..] : keys;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
