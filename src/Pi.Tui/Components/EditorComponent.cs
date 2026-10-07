namespace Pi.Tui.Components;

/// <summary>
/// Public surface of the editor component (port of <c>editor-component.ts</c>).
///
/// The TS members are all optional except <c>getText</c>, <c>setText</c> and <c>handleInput</c>; C#
/// interfaces have no optional members, so the whole surface is declared and <see cref="Editor"/>
/// implements it.
/// </summary>
public interface IEditorComponent : IComponent
{
    string GetText();

    void SetText(string text);

    Action<string>? OnSubmit { get; set; }

    Action<string>? OnChange { get; set; }

    void AddToHistory(string text);

    void InsertTextAtCursor(string text);

    string GetExpandedText();

    void SetAutocompleteProvider(IAutocompleteProvider provider);

    Func<string, string> BorderColor { get; set; }

    void SetPaddingX(double padding);

    void SetAutocompleteMaxVisible(double maxVisible);
}
