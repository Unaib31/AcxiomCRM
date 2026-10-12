using System.ComponentModel.DataAnnotations;
using AcxiomCRM.Application.Common;
using Microsoft.EntityFrameworkCore;

namespace AcxiomCRM.Application.Services;

internal static class QueryHelpers
{
    /// <summary>Escapes LIKE wildcards in user input; pair with <c>EF.Functions.Like(col, pattern, "\\")</c>.</summary>
    public static string Like(string term) =>
        "%" + term.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";

    public static async Task<PagedResult<T>> ToPagedAsync<T>(this IQueryable<T> q, ListQuery lq)
    {
        var total = await q.CountAsync();
        var items = await q.Skip((lq.Page - 1) * lq.PageSize).Take(lq.PageSize).ToListAsync();
        return new PagedResult<T> { Items = items, Page = lq.Page, PageSize = lq.PageSize, TotalCount = total };
    }
}

public static class InputValidator
{
    /// <summary>Re-runs DataAnnotations on the server so no caller can skip model validation.</summary>
    public static Dictionary<string, string> Validate(object input)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(input, new ValidationContext(input), results, validateAllProperties: true);
        var errors = new Dictionary<string, string>();
        foreach (var r in results)
        {
            var member = r.MemberNames.FirstOrDefault() ?? "";
            errors.TryAdd(member, r.ErrorMessage ?? "Invalid value.");
        }
        return errors;
    }
}
