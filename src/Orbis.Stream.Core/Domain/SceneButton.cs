using System.Text.RegularExpressions;

namespace Orbis.Stream.Core.Domain;

/// <summary>
/// What a scene button does to the live once it is on air, decided by the file it carries.
/// </summary>
public enum SceneButtonKind
{
    /// <summary>A clip: it plays once in place of the live, and the live carries on when it ends.</summary>
    Video = 1,

    /// <summary>A picture (a banner, a "be right back"): it stays on air until the user resumes the live.</summary>
    Image = 2
}

/// <summary>
/// One button of the scene deck, the panel a live is switched from while it is on air: what
/// Streamlabs calls a scene and a Stream Deck a key. The buttons are the same for every live, the
/// way a scene collection is, so they are set once and are there for the next live too.
/// </summary>
public sealed class SceneButtonEntity
{
    public long Pkid { get; set; }

    /// <summary>Where the button sits in the deck, left to right.</summary>
    public int Position { get; set; }

    public string Label { get; set; } = string.Empty;

    /// <summary>The name of its file in the scene media folder (see SceneButtonService).</summary>
    public string MediaName { get; set; } = string.Empty;

    /// <summary>The key that asks for it, as <see cref="SceneHotkey"/> writes it; null when it has none.</summary>
    public string? Hotkey { get; set; }

    public DateTime? LastModified { get; set; }
}

public static class SceneButtonKindExtensions
{
    public static string ToWireValue(this SceneButtonKind kind) => kind == SceneButtonKind.Image ? "IMAGE" : "VIDEO";
}

/// <summary>
/// The key of a scene button: the physical key the browser names in <c>KeyboardEvent.code</c>,
/// after the modifiers held with it, always in the order Ctrl, Alt, Shift (<c>Ctrl+Digit1</c>,
/// <c>F7</c>, <c>KeyB</c>). The physical key rather than the character it types: the same button
/// answers to the same key whatever the layout of the keyboard is set to.
/// </summary>
public static partial class SceneHotkey
{
    /// <summary>
    /// The keys the page itself needs: confirming and cancelling a dialog, moving between controls,
    /// and the shortcuts of the window (reload, full screen, the developer tools, find, print). A
    /// scene button bound to one of them would go on air in place of what the user meant.
    /// </summary>
    private static readonly HashSet<string> Reserved = new(StringComparer.Ordinal)
    {
        "Escape", "Enter", "NumpadEnter", "Tab", "Space", "Backspace",
        "F5", "F11", "F12",
        "Ctrl+KeyR", "Ctrl+KeyW", "Ctrl+KeyF", "Ctrl+KeyP", "Ctrl+KeyN", "Ctrl+KeyT",
        "Ctrl+Shift+KeyI", "Ctrl+Shift+KeyJ", "Ctrl+F5", "Shift+F5", "Alt+F4"
    };

    /// <summary>Whether a key can be given to a scene button: written as the page writes it, and not one the page needs.</summary>
    public static bool IsValid(string? hotkey) =>
        !string.IsNullOrEmpty(hotkey) && Pattern().IsMatch(hotkey) && !Reserved.Contains(hotkey);

    [GeneratedRegex(
        @"^(Ctrl\+)?(Alt\+)?(Shift\+)?(Key[A-Z]|Digit[0-9]|Numpad[0-9]|Numpad(Add|Subtract|Multiply|Divide|Decimal)|F([1-9]|1[0-9]|2[0-4])|Backquote|Minus|Equal|BracketLeft|BracketRight|Backslash|IntlBackslash|Semicolon|Quote|Comma|Period|Slash|Insert|Delete|Home|End|PageUp|PageDown|Arrow(Up|Down|Left|Right)|Escape|Enter|NumpadEnter|Tab|Space|Backspace)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}
