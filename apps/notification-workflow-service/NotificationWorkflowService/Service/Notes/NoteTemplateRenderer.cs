using System.Globalization;
using System.Text;

namespace NotificationWorkflowService.Service.Notes;

/// <summary>
/// Pure, case-sensitive template parser/renderer. Only {{lat}}, {{lon}} and
/// {{current_location_address}} are allowed, with optional .NET regex whitespace inside braces.
/// All other braces, unknown names and expressions are rejected. Values are never parsed again.
/// </summary>
internal sealed class NoteTemplateRenderer
{
    private readonly List<Part> parts;

    private NoteTemplateRenderer(List<Part> parts, bool requiresLocation, bool requiresAddress)
    {
        this.parts = parts;
        RequiresLocation = requiresLocation;
        RequiresAddress = requiresAddress;
    }

    internal bool RequiresLocation { get; }
    internal bool RequiresAddress { get; }

    internal static NoteTemplateRenderer Parse(string template)
    {
        ArgumentNullException.ThrowIfNull(template);
        if (string.IsNullOrWhiteSpace(template))
            throw new ArgumentException("A nonblank note template is required.", nameof(template));

        var parts = new List<Part>();
        var requiresLocation = false;
        var requiresAddress = false;
        var literalStart = 0;
        var position = 0;
        while (position < template.Length)
        {
            if (template[position] is not ('{' or '}'))
            {
                position++;
                continue;
            }

            if (template[position] != '{' || position + 1 >= template.Length || template[position + 1] != '{')
                throw InvalidTemplate();

            if (position > literalStart)
                parts.Add(new Part(template[literalStart..position], false));

            var end = template.IndexOf("}}", position + 2, StringComparison.Ordinal);
            if (end < 0)
                throw InvalidTemplate();

            var variable = template.Substring(position + 2, end - position - 2).Trim();
            if (variable is not ("lat" or "lon" or "current_location_address"))
                throw InvalidTemplate();

            parts.Add(new Part(variable, true));
            requiresLocation = true;
            requiresAddress |= variable == "current_location_address";
            position = end + 2;
            literalStart = position;
        }

        if (literalStart < template.Length)
            parts.Add(new Part(template[literalStart..], false));

        return new NoteTemplateRenderer(parts, requiresLocation, requiresAddress);
    }

    internal string Render(decimal? latitude, decimal? longitude, string? address)
    {
        var text = new StringBuilder();
        foreach (var part in parts)
        {
            var value = !part.IsVariable ? part.Text : part.Text switch
            {
                "lat" => latitude?.ToString(CultureInfo.InvariantCulture) ?? "N/A",
                "lon" => longitude?.ToString(CultureInfo.InvariantCulture) ?? "N/A",
                "current_location_address" => string.IsNullOrWhiteSpace(address) ? "N/A" : address,
                _ => throw new InvalidOperationException("Unsupported parsed note variable.")
            };

            // Check the final rendered length without allocating an oversized note.
            if (value.Length > 1000 - text.Length)
                throw new ArgumentException("The rendered note must not exceed 1000 UTF-16 code units.", "template");
            text.Append(value);
        }

        if (string.IsNullOrWhiteSpace(text.ToString()))
            throw new ArgumentException("The rendered note must not be blank.", "template");
        return text.ToString();
    }

    private static ArgumentException InvalidTemplate() =>
        new("The note template contains an unknown variable or malformed braces.", "template");

    private sealed record Part(string Text, bool IsVariable);
}