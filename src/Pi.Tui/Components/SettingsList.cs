namespace Pi.Tui.Components;

/// <summary>A single row of a <see cref="SettingsList"/> (port of <c>SettingItem</c>).</summary>
public sealed class SettingItem
{
    /// <summary>Unique identifier for this setting.</summary>
    public required string Id { get; init; }

    /// <summary>Display label (left side).</summary>
    public required string Label { get; init; }

    /// <summary>Optional description shown when selected.</summary>
    public string? Description { get; init; }

    /// <summary>Current value to display (right side).</summary>
    public string CurrentValue { get; set; } = "";

    /// <summary>If provided, Enter/Space cycles through these values.</summary>
    public string[]? Values { get; init; }

    /// <summary>
    /// If provided, Enter opens this submenu. Receives current value and done callback.
    /// <c>done</c> accepts an optional selected value and an optional navigateTo id to move the
    /// cursor after close.
    /// </summary>
    public SettingsListSubmenuFactory? Submenu { get; init; }
}

/// <summary>
/// Factory that builds the component shown while a <see cref="SettingItem"/> submenu is open
/// (port of the TS <c>SettingItem.submenu</c> signature).
/// </summary>
public delegate IComponent SettingsListSubmenuFactory(
    string currentValue,
    Action<string?, SettingsListSubmenuCloseOptions?> done);

/// <summary>Options accepted by a submenu's <c>done</c> callback (port of the TS inline type).</summary>
public sealed class SettingsListSubmenuCloseOptions
{
    public string? NavigateTo { get; init; }
}

/// <summary>Styling callbacks and cursor glyph for <see cref="SettingsList"/> (port of <c>SettingsListTheme</c>).</summary>
public sealed class SettingsListTheme
{
    public required Func<string, bool, string> Label { get; init; }

    public required Func<string, bool, string> Value { get; init; }

    public required Func<string, string> Description { get; init; }

    public required string Cursor { get; init; }

    public required Func<string, string> Hint { get; init; }
}

/// <summary>Behaviour knobs for <see cref="SettingsList"/> (port of <c>SettingsListOptions</c>).</summary>
public sealed class SettingsListOptions
{
    public bool? EnableSearch { get; init; }
}

/// <summary>
/// Settings list with optional fuzzy search, value cycling and nested submenus
/// (port of <c>components/settings-list.ts</c>).
/// </summary>
public class SettingsList : IComponent
{
    private const int MaxLabelWidth = 36;

    private readonly List<SettingItem> _items;
    private readonly SettingsListTheme _theme;
    private readonly Action<string, string> _onChange;
    private readonly Action _onCancel;
    private readonly bool _searchEnabled;
    private readonly Input? _searchInput;

    private List<SettingItem> _filteredItems;
    private int _selectedIndex;
    private int? _mousePressedIndex;
    private readonly int _maxVisible;

    // Submenu state
    private IComponent? _submenuComponent;
    private int? _submenuItemIndex;
    private string? _navigateAfterClose;

    public SettingsList(
        IEnumerable<SettingItem> items,
        int maxVisible,
        SettingsListTheme theme,
        Action<string, string> onChange,
        Action onCancel,
        SettingsListOptions? options = null)
    {
        _items = [.. items];
        _filteredItems = _items;
        _maxVisible = maxVisible;
        _theme = theme;
        _onChange = onChange;
        _onCancel = onCancel;
        _searchEnabled = options?.EnableSearch ?? false;
        if (_searchEnabled)
        {
            _searchInput = new Input();
        }
    }

    /// <summary>Update an item's currentValue.</summary>
    public void UpdateValue(string id, string newValue)
    {
        var item = _items.Find(i => i.Id == id);
        if (item is not null)
        {
            item.CurrentValue = newValue;
        }
    }

    /// <summary>Move selection to the item with the given id (no-op if not found).</summary>
    public void SelectItem(string id)
    {
        var items = _searchEnabled ? _filteredItems : _items;
        var index = items.FindIndex(i => i.Id == id);
        if (index != -1)
        {
            _selectedIndex = index;
        }
    }

    /// <summary>
    /// Test seam: the current selection index. The TS field is <c>private</c> but readable at runtime,
    /// so the differential corpus records it; this exposes the same observable without widening the
    /// public surface.
    /// </summary>
    internal int SelectedIndexForTests => _selectedIndex;

    /// <summary>Test seam: whether a submenu is currently open (mirrors the TS runtime field).</summary>
    internal bool SubmenuOpenForTests => _submenuComponent is not null;

    public void Invalidate() => _submenuComponent?.Invalidate();

    public string[] Render(int width)
    {
        // If submenu is active, render it instead
        if (_submenuComponent is not null)
        {
            return _submenuComponent.Render(width);
        }

        return RenderMainList(width);
    }

    private string[] RenderMainList(int width)
    {
        var lines = new List<string>();

        if (_searchEnabled && _searchInput is not null)
        {
            lines.AddRange(_searchInput.Render(width));
            lines.Add("");
        }

        if (_items.Count == 0)
        {
            lines.Add(_theme.Hint("  No settings available"));
            if (_searchEnabled)
            {
                AddHintLine(lines, width);
            }
            return [.. lines];
        }

        var displayItems = GetDisplayItems();
        if (displayItems.Count == 0)
        {
            lines.Add(TextLayout.TruncateToWidth(_theme.Hint("  No matching settings"), width));
            AddHintLine(lines, width);
            return [.. lines];
        }

        // Calculate visible range with scrolling
        var (startIndex, endIndex) = GetVisibleRange(displayItems);

        // Calculate max label width for alignment. The `_items.Count === 0` case returned above, so
        // the TS `Math.max(...[]) === -Infinity` branch is unreachable here.
        var maxLabelWidth = Math.Min(MaxLabelWidth, _items.Max(item => UnicodeWidth.VisibleWidth(item.Label)));

        // Render visible items
        for (var i = startIndex; i < endIndex; i++)
        {
            var item = displayItems[i];
            if (item is null)
            {
                // Kept 1:1 with the TS `if (!item) continue;`, which guards against sparse arrays.
                continue;
            }

            var isSelected = i == _selectedIndex;
            var prefix = isSelected ? _theme.Cursor : "  ";
            var prefixWidth = UnicodeWidth.VisibleWidth(prefix);

            // Pad label to align values
            var labelPadded = item.Label + new string(' ', Math.Max(0, maxLabelWidth - UnicodeWidth.VisibleWidth(item.Label)));
            var labelText = _theme.Label(labelPadded, isSelected);

            // Calculate space for value
            const string separator = "  ";
            var usedWidth = prefixWidth + maxLabelWidth + UnicodeWidth.VisibleWidth(separator);
            var valueMaxWidth = width - usedWidth - 2;

            var valueText = _theme.Value(TextLayout.TruncateToWidth(item.CurrentValue, valueMaxWidth, ""), isSelected);

            lines.Add(TextLayout.TruncateToWidth(prefix + labelText + separator + valueText, width));
        }

        // Add scroll indicator if needed
        if (startIndex > 0 || endIndex < displayItems.Count)
        {
            var scrollText = $"  ({_selectedIndex + 1}/{displayItems.Count})";
            lines.Add(_theme.Hint(TextLayout.TruncateToWidth(scrollText, width - 2, "")));
        }

        // Add description for selected item
        var selectedItem = _selectedIndex >= 0 && _selectedIndex < displayItems.Count ? displayItems[_selectedIndex] : null;
        if (selectedItem?.Description is { Length: > 0 } description)
        {
            lines.Add("");
            var wrappedDesc = TextLayout.WrapTextWithAnsi(description, width - 4);
            foreach (var line in wrappedDesc)
            {
                lines.Add(_theme.Description($"  {line}"));
            }
        }

        // Add hint
        AddHintLine(lines, width);

        return [.. lines];
    }

    public TuiMouseEventResult? HandleMouse(TuiMouseEvent @event)
    {
        if (_submenuComponent is not null)
        {
            var result = _submenuComponent.HandleMouse(@event);
            return result is null ? null : ForwardWithFocus(result);
        }

        if (_searchEnabled && _searchInput is not null)
        {
            if (@event.Y == 0)
            {
                var result = _searchInput.HandleMouse(@event);
                return result is null ? null : ForwardWithFocus(result);
            }
            if (@event.Y == 1)
            {
                return null;
            }
        }

        var displayItems = GetDisplayItems();
        if (displayItems.Count == 0)
        {
            return null;
        }
        if (@event.Type == TuiMouseEventType.Wheel && @event.WheelDelta is not null and not 0)
        {
            var delta = @event.WheelDelta.Value < 0 ? -1 : 1;
            var previousIndex = _selectedIndex;
            _selectedIndex = Math.Max(0, Math.Min(displayItems.Count - 1, _selectedIndex + delta));
            return new TuiMouseEventResult { Handled = true, Render = _selectedIndex != previousIndex };
        }

        // Hover must not change selection: the visible range is centered on it.
        if (@event.Button != TuiMouseButton.Left ||
            (@event.Type != TuiMouseEventType.Press && @event.Type != TuiMouseEventType.Click))
        {
            return null;
        }

        var rowOffset = _searchEnabled ? 2 : 0;
        var (startIndex, endIndex) = GetVisibleRange(displayItems);
        var itemIndex = startIndex + @event.Y - rowOffset;
        if (itemIndex < startIndex || itemIndex >= endIndex)
        {
            return null;
        }
        if (@event.Type == TuiMouseEventType.Press)
        {
            _mousePressedIndex = itemIndex;
            _selectedIndex = itemIndex;
            return new TuiMouseEventResult { Handled = true, Focus = true };
        }
        if (@event.Type == TuiMouseEventType.Click)
        {
            _selectedIndex = _mousePressedIndex ?? itemIndex;
            _mousePressedIndex = null;
            ActivateItem();
            return new TuiMouseEventResult { Handled = true };
        }
        return null;
    }

    /// <summary>
    /// Port of the TS <c>{ ...result, focus: true }</c> spread. Matches the same rewrite already used
    /// by <see cref="Editor"/> for its autocomplete list, so only the four base flags are carried over.
    /// </summary>
    private static TuiMouseEventResult ForwardWithFocus(TuiMouseEventResult result) => new()
    {
        Handled = result.Handled,
        Capture = result.Capture,
        Focus = true,
        Render = result.Render,
    };

    public void HandleInput(string data)
    {
        // If submenu is active, delegate all input to it.
        // The submenu's onCancel (triggered by escape) will call done() which closes it.
        if (_submenuComponent is not null)
        {
            _submenuComponent.HandleInput(data);
            return;
        }

        // Main list input handling
        var kb = GlobalKeybindings.Get();
        var displayItems = GetDisplayItems();
        if (kb.Matches(data, TuiKeybindingIds.SelectUp))
        {
            if (displayItems.Count == 0)
            {
                return;
            }
            _selectedIndex = _selectedIndex == 0 ? displayItems.Count - 1 : _selectedIndex - 1;
        }
        else if (kb.Matches(data, TuiKeybindingIds.SelectDown))
        {
            if (displayItems.Count == 0)
            {
                return;
            }
            _selectedIndex = _selectedIndex == displayItems.Count - 1 ? 0 : _selectedIndex + 1;
        }
        else if (kb.Matches(data, TuiKeybindingIds.SelectConfirm) ||
                 (data == " " && (!_searchEnabled || _searchInput?.GetValue().Length == 0)))
        {
            ActivateItem();
        }
        else if (kb.Matches(data, TuiKeybindingIds.SelectCancel))
        {
            _onCancel();
        }
        else if (_searchEnabled && _searchInput is not null)
        {
            _searchInput.HandleInput(data);
            ApplyFilter(_searchInput.GetValue());
        }
    }

    private List<SettingItem> GetDisplayItems() => _searchEnabled ? _filteredItems : _items;

    private (int StartIndex, int EndIndex) GetVisibleRange(List<SettingItem> displayItems)
    {
        // JS Math.floor(maxVisible / 2) rounds towards negative infinity, unlike C# integer division.
        var halfVisible = (int)Math.Floor(_maxVisible / 2.0);
        var startIndex = Math.Max(
            0,
            Math.Min(_selectedIndex - halfVisible, displayItems.Count - _maxVisible));
        return (startIndex, Math.Min(startIndex + _maxVisible, displayItems.Count));
    }

    private void ActivateItem()
    {
        var displayItems = GetDisplayItems();
        if (_selectedIndex < 0 || _selectedIndex >= displayItems.Count)
        {
            return;
        }
        var item = displayItems[_selectedIndex];

        if (item.Submenu is not null)
        {
            // Open submenu, passing current value so it can pre-select correctly
            _submenuItemIndex = _selectedIndex;
            _submenuComponent = item.Submenu(
                item.CurrentValue,
                (selectedValue, closeOptions) =>
                {
                    if (selectedValue is not null)
                    {
                        item.CurrentValue = selectedValue;
                        _onChange(item.Id, selectedValue);
                    }
                    if (closeOptions?.NavigateTo is { Length: > 0 } navigateTo)
                    {
                        _navigateAfterClose = navigateTo;
                    }
                    CloseSubmenu();
                });
        }
        else if (item.Values is { Length: > 0 } values)
        {
            // Cycle through values
            var currentIndex = Array.IndexOf(values, item.CurrentValue);
            var nextIndex = (currentIndex + 1) % values.Length;
            var newValue = values[nextIndex];
            item.CurrentValue = newValue;
            _onChange(item.Id, newValue);
        }
    }

    private void CloseSubmenu()
    {
        _submenuComponent = null;
        if (_navigateAfterClose is not null)
        {
            var id = _navigateAfterClose;
            _navigateAfterClose = null;
            _submenuItemIndex = null;
            SelectItem(id);

            // Open the target item's submenu automatically
            ActivateItem();
        }
        else if (_submenuItemIndex is not null)
        {
            // Restore selection to the item that opened the submenu
            _selectedIndex = _submenuItemIndex.Value;
            _submenuItemIndex = null;
        }
    }

    private void ApplyFilter(string query)
    {
        _filteredItems = Fuzzy.Filter(_items, query, item => item.Label);
        _selectedIndex = 0;
    }

    private void AddHintLine(List<string> lines, int width)
    {
        lines.Add("");
        lines.Add(
            TextLayout.TruncateToWidth(
                _theme.Hint(
                    _searchEnabled
                        ? "  Type to search · Enter/Space to change · Esc to cancel"
                        : "  Enter/Space to change · Esc to cancel"),
                width));
    }
}
