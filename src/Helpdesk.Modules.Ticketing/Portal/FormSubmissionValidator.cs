using System.Globalization;
using System.Text.Json;
using Helpdesk.Modules.Ticketing.Domain;
using Helpdesk.Modules.Ticketing.Sla;

namespace Helpdesk.Modules.Ticketing.Portal;

internal sealed record FieldOption(string Value, string Label, bool IsActive = true);

internal sealed record ValidatedValue(
    Guid FieldId, string Key, string Label, string FieldType, string ValueJson, string Display, bool RequesterVisible, bool AgentVisible, int SortOrder);

/// <summary>
/// Server-side validation of submitted custom-field values against the form's current definitions. Only active, requester-visible fields of THIS form
/// are accepted; anything else (unknown, inactive, hidden, admin-only) is rejected with the same message, so the form's hidden structure is not revealed.
/// Values are typed and bounded; there is no scripting, HTML or formula support.
/// </summary>
internal static class FormSubmissionValidator
{
    public const int MaxShortText = 500;
    public const int MaxLongText = 4000;
    public const int MaxOptions = 50;

    public static IReadOnlyList<ValidatedValue> Validate(IReadOnlyList<FormField> definitions, IReadOnlyDictionary<string, JsonElement> submitted, Dictionary<string, string[]> errors)
    {
        var asked = definitions.Where(d => d.IsActive && d.RequesterVisible).ToDictionary(d => d.Key, StringComparer.Ordinal);
        foreach (var key in submitted.Keys.Where(k => !asked.ContainsKey(k)))
        {
            errors[$"fields.{Safe(key)}"] = ["This field is not part of the form."];
        }

        var result = new List<ValidatedValue>();
        foreach (var field in asked.Values.OrderBy(f => f.SortOrder).ThenBy(f => f.Key, StringComparer.Ordinal))
        {
            var path = $"fields.{field.Key}";
            var present = submitted.TryGetValue(field.Key, out var raw) && raw.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);
            string? problem = null;
            var parsed = present ? Parse(field, raw, out problem) : null;
            if (present && parsed is null)
            {
                errors[path] = [problem ?? "Enter a valid value."];
                continue;
            }

            if (parsed is null)
            {
                if (field.Required)
                {
                    errors[path] = [$"{field.Label} is required."];
                }
                else if (field.FieldType == FieldTypes.Checkbox)
                {
                    result.Add(Make(field, "false", "No"));
                }

                continue;
            }

            if (field.Required && IsEmpty(field, parsed))
            {
                errors[path] = [field.FieldType == FieldTypes.Checkbox ? $"Confirm {field.Label}." : $"{field.Label} is required."];
                continue;
            }

            if (IsEmpty(field, parsed) && field.FieldType != FieldTypes.Checkbox)
            {
                continue; // optional and blank: nothing to store
            }

            result.Add(Make(field, parsed.Value.Json, parsed.Value.Display));
        }

        return result;
    }

    private static ValidatedValue Make(FormField f, string json, string display) =>
        new(f.Id, f.Key, f.Label, f.FieldType, json, display, f.RequesterVisible, f.AgentVisible, f.SortOrder);

    private static string Safe(string key) => key.Length > 40 ? key[..40] : key;

    private static bool IsEmpty(FormField f, (string Json, string Display)? v) =>
        f.FieldType switch
        {
            FieldTypes.Checkbox => v!.Value.Json != "true",
            FieldTypes.MultiSelect => v!.Value.Json == "[]",
            _ => string.IsNullOrWhiteSpace(v!.Value.Display),
        };

    private static (string Json, string Display)? Parse(FormField f, JsonElement raw, out string? problem)
    {
        problem = null;
        switch (f.FieldType)
        {
            case FieldTypes.ShortText or FieldTypes.LongText:
            {
                var max = f.FieldType == FieldTypes.ShortText ? MaxShortText : MaxLongText;
                if (raw.ValueKind != JsonValueKind.String || raw.GetString() is not { } text || text.Contains('\0', StringComparison.Ordinal))
                {
                    problem = "Enter text.";
                    return null;
                }

                text = text.Trim();
                if (text.Length > max)
                {
                    problem = $"Use at most {max} characters.";
                    return null;
                }

                return (JsonSerializer.Serialize(text), text);
            }

            case FieldTypes.Checkbox:
                if (raw.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    problem = "Choose yes or no.";
                    return null;
                }

                return raw.GetBoolean() ? ("true", "Yes") : ("false", "No");
            case FieldTypes.Date:
                if (raw.ValueKind != JsonValueKind.String || !DateOnly.TryParseExact(raw.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                {
                    problem = "Enter a date as yyyy-MM-dd.";
                    return null;
                }

                return (JsonSerializer.Serialize(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)), date.ToString("d MMM yyyy", CultureInfo.InvariantCulture));
            case FieldTypes.Number:
                if (raw.ValueKind != JsonValueKind.Number || !raw.TryGetDecimal(out var number) || Math.Abs(number) > 1_000_000_000_000m)
                {
                    problem = "Enter a number.";
                    return null;
                }

                return (number.ToString(CultureInfo.InvariantCulture), number.ToString(CultureInfo.InvariantCulture));
            case FieldTypes.SingleSelect:
            {
                var options = Options(f);
                if (raw.ValueKind != JsonValueKind.String || options.FirstOrDefault(o => o.IsActive && o.Value == raw.GetString()) is not { } option)
                {
                    problem = "Choose one of the listed options.";
                    return null;
                }

                return (JsonSerializer.Serialize(option.Value), option.Label);
            }

            case FieldTypes.MultiSelect:
            {
                var options = Options(f);
                if (raw.ValueKind != JsonValueKind.Array || raw.GetArrayLength() > MaxOptions)
                {
                    problem = "Choose from the listed options.";
                    return null;
                }

                var chosen = new List<FieldOption>();
                foreach (var item in raw.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String || options.FirstOrDefault(o => o.IsActive && o.Value == item.GetString()) is not { } option)
                    {
                        problem = "Choose from the listed options.";
                        return null;
                    }

                    if (chosen.All(c => c.Value != option.Value))
                    {
                        chosen.Add(option);
                    }
                }

                return (JsonSerializer.Serialize(chosen.Select(c => c.Value)), string.Join(", ", chosen.Select(c => c.Label)));
            }

            default:
                problem = "Unsupported field.";
                return null;
        }
    }

    public static IReadOnlyList<FieldOption> Options(FormField f) => StoredJson.Read<List<FieldOption>>(f.Options);
}
