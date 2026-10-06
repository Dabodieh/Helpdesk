using System.Globalization;
using System.Text.Json;
using Helpdesk.Modules.Ticketing.Domain;
using Helpdesk.Modules.Ticketing.Sla;

namespace Helpdesk.Modules.Ticketing.Fields;

internal sealed record FieldOption(string Value, string Label, bool IsActive = true);

/// <summary>A field as asked in one place (a form, a ticket type or a ticket): the definition, whether it is required HERE, and its order HERE.</summary>
internal sealed record FieldBinding(TicketField Field, bool Required, int Order);

internal sealed record ValidatedValue(TicketField Field, string ValueJson, string Display, int Order);

internal sealed record ValidationResult(IReadOnlyList<ValidatedValue> Values, IReadOnlyList<TicketField> Cleared);

internal enum ValidationMode
{
    /// <summary>A new ticket: every bound field is evaluated and required fields must be present.</summary>
    Create,

    /// <summary>An edit: only the submitted keys are touched; <c>null</c> clears (refused for a required field). Untouched fields are never revalidated.</summary>
    Patch,
}

/// <summary>
/// Server-side validation of submitted field values against definitions. The caller decides which bindings the actor may supply (requesters: active and
/// requester visible; agents: active and agent visible); anything else is rejected with one message, so hidden structure is not revealed. Values are typed and
/// bounded; there is no scripting, HTML or formula support.
/// </summary>
internal static class FieldValueValidator
{
    public const int MaxShortText = 500;
    public const int MaxLongText = 4000;
    public const int MaxOptions = 50;

    public static ValidationResult Validate(IReadOnlyList<FieldBinding> allowed, IReadOnlyDictionary<string, JsonElement> submitted, ValidationMode mode, Dictionary<string, string[]> errors)
    {
        var byKey = allowed.ToDictionary(b => b.Field.Key, StringComparer.Ordinal);
        foreach (var key in submitted.Keys.Where(k => !byKey.ContainsKey(k)))
        {
            errors[$"fields.{Safe(key)}"] = ["This field is not available."];
        }

        var values = new List<ValidatedValue>();
        var cleared = new List<TicketField>();
        foreach (var binding in allowed.OrderBy(b => b.Order).ThenBy(b => b.Field.Key, StringComparer.Ordinal))
        {
            var field = binding.Field;
            var path = $"fields.{field.Key}";
            var provided = submitted.TryGetValue(field.Key, out var raw);
            if (mode == ValidationMode.Patch && !provided)
            {
                continue;
            }

            var present = provided && raw.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);
            string? problem = null;
            var parsed = present ? Parse(field, raw, out problem) : null;
            if (present && parsed is null)
            {
                errors[path] = [problem ?? "Enter a valid value."];
                continue;
            }

            if (parsed is null || (IsEmpty(field, parsed) && field.FieldType != FieldTypes.Checkbox))
            {
                if (binding.Required)
                {
                    errors[path] = [$"{field.Label} is required."];
                }
                else if (mode == ValidationMode.Patch)
                {
                    cleared.Add(field);
                }
                else if (field.FieldType == FieldTypes.Checkbox)
                {
                    values.Add(new ValidatedValue(field, "false", "No", binding.Order));
                }

                continue;
            }

            if (binding.Required && IsEmpty(field, parsed))
            {
                errors[path] = [field.FieldType == FieldTypes.Checkbox ? $"Confirm {field.Label}." : $"{field.Label} is required."];
                continue;
            }

            values.Add(new ValidatedValue(field, parsed.Value.Json, parsed.Value.Display, binding.Order));
        }

        return new ValidationResult(values, cleared);
    }

    private static string Safe(string key) => key.Length > 40 ? key[..40] : key;

    private static bool IsEmpty(TicketField f, (string Json, string Display)? v) =>
        f.FieldType switch
        {
            FieldTypes.Checkbox => v!.Value.Json != "true",
            FieldTypes.MultiSelect => v!.Value.Json == "[]",
            _ => string.IsNullOrWhiteSpace(v!.Value.Display),
        };

    private static (string Json, string Display)? Parse(TicketField f, JsonElement raw, out string? problem)
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

    public static IReadOnlyList<FieldOption> Options(TicketField f) => StoredJson.Read<List<FieldOption>>(f.Options);
}
