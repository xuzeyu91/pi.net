namespace Pi.Durable.Harness;

/// <summary>
/// 把 value 逐叶赋到 target[key]。Chord 把容器赋值记为一次整体 set，只在字符串叶被更长字符串重新赋值时发 append，
/// 所以写部分整体会在每次冲刷时存储并发布完整消息。对应 TS <c>harness/json.ts</c> 的 <c>assignJson</c>。
/// </summary>
public static class HarnessJson
{
    public static void AssignJson(object? target, object key, object? value)
    {
        switch (target)
        {
            case Dictionary<string, object?> slots:
            {
                var name = System.Convert.ToString(key, System.Globalization.CultureInfo.InvariantCulture) ?? "";
                var current = slots.TryGetValue(name, out var found) ? found : null;
                if (TryAssignContainer(current, value)) return;
                if (!Equals(current, value)) slots[name] = value;
                return;
            }
            case List<object?> items:
            {
                var index = (int)key;
                var current = index >= 0 && index < items.Count ? items[index] : null;
                if (TryAssignContainer(current, value)) return;
                if (!Equals(current, value))
                {
                    if (index >= 0 && index < items.Count) items[index] = value;
                    else items.Add(value);
                }
                return;
            }
            default:
                throw new ArgumentException("Target must be a JSON object or array", nameof(target));
        }
    }

    /// <summary>current 与 value 都是容器（对象或数组）时逐叶赋值；否则返回 false 交给整体赋值。</summary>
    private static bool TryAssignContainer(object? current, object? value)
    {
        if (current is Dictionary<string, object?> currentMap && value is Dictionary<string, object?> valueMap)
        {
            foreach (var nameInCurrent in currentMap.Keys.ToList())
                if (!valueMap.ContainsKey(nameInCurrent)) currentMap.Remove(nameInCurrent);
            foreach (var (childKey, child) in valueMap) AssignJson(currentMap, childKey, child);
            return true;
        }
        if (current is List<object?> currentList && value is List<object?> valueList
            && currentList.Count <= valueList.Count)
        {
            for (var index = 0; index < valueList.Count; index++)
            {
                if (index < currentList.Count) AssignJson(currentList, index, valueList[index]);
                else currentList.Add(valueList[index]);
            }
            return true;
        }
        return false;
    }
}
