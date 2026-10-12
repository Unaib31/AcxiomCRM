using System.Globalization;
using System.Text;

namespace AcxiomCRM.Infrastructure;

/// <summary>Presentation helpers shared by the Razor views.</summary>
public static class Ui
{
    public static string Badge(string? value) => value switch
    {
        "Active" or "Won" or "Completed" or "Converted" or "Success" => "success",
        "Inactive" or "Cancelled" or "New" or "Qualification" or "Low" => "secondary",
        "Prospect" or "Contacted" or "Proposal" or "Planned" => "info",
        "Qualified" => "primary",
        "Negotiation" or "Unqualified" or "Medium" => "warning",
        "Lost" or "Missed" or "High" or "Failure" or "Overdue" => "danger",
        _ => "secondary",
    };

    /// <summary>Indian digit grouping (12,34,567.00) with the rupee sign.</summary>
    public static string Money(decimal v, bool decimals = false)
    {
        var neg = v < 0; v = Math.Abs(v);
        var s = decimals ? v.ToString("0.00", CultureInfo.InvariantCulture) : decimal.Round(v, 0, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture);
        var parts = s.Split('.');
        var intPart = parts[0];
        var sb = new StringBuilder();
        if (intPart.Length > 3)
        {
            var head = intPart[..^3];
            var tail = intPart[^3..];
            for (var i = 0; i < head.Length; i++)
            {
                if (i > 0 && (head.Length - i) % 2 == 0) sb.Append(',');
                sb.Append(head[i]);
            }
            sb.Append(',').Append(tail);
        }
        else sb.Append(intPart);
        return (neg ? "-₹" : "₹") + sb + (parts.Length > 1 ? "." + parts[1] : "");
    }

    public static string Initials(string? name)
    {
        var p = (name ?? "?").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return p.Length == 0 ? "?" : (p.Length == 1 ? p[0][..1] : p[0][..1] + p[^1][..1]).ToUpperInvariant();
    }

    public static string Spaced(string? pascal) =>
        string.IsNullOrEmpty(pascal) ? "" : System.Text.RegularExpressions.Regex.Replace(pascal, "(?<=[a-z])(?=[A-Z])", " ");
}
