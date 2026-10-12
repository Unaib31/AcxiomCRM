using System.ComponentModel.DataAnnotations;
using AcxiomCRM.Application.Common;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;

namespace AcxiomCRM.Infrastructure;

/// <summary>
/// Emits unobtrusive-validation attributes for rules whose authoritative check lives on the server
/// (they depend on workflow state, e.g. the stage). The server never trusts this: see the domain rules.
/// </summary>
public abstract class ClientRuleAttribute : ValidationAttribute, IClientModelValidator
{
    protected ClientRuleAttribute(string message) : base(message) { }
    /// <summary>Name of a sibling field; when its value is in <see cref="UnlessValues"/> the rule is skipped.</summary>
    public string? UnlessField { get; set; }
    public string? UnlessValues { get; set; }
    protected abstract string Rule { get; }

    /// <summary>True when the sibling field holds a value for which this rule does not apply (e.g. Stage = Won).</summary>
    protected bool Skipped(ValidationContext ctx)
    {
        if (UnlessField is null || UnlessValues is null) return false;
        var other = ctx.ObjectInstance.GetType().GetProperty(UnlessField)?.GetValue(ctx.ObjectInstance)?.ToString();
        return other is not null && UnlessValues.Split(',').Contains(other);
    }

    public virtual void AddValidation(ClientModelValidationContext context)
    {
        Merge(context, "data-val", "true");
        Merge(context, $"data-val-{Rule}", FormatErrorMessage(context.ModelMetadata.GetDisplayName()));
        if (UnlessField is not null) Merge(context, $"data-val-{Rule}-unlessfield", UnlessField);
        if (UnlessValues is not null) Merge(context, $"data-val-{Rule}-unlessvalues", UnlessValues);
    }

    protected static void Merge(ClientModelValidationContext c, string key, string value) => c.Attributes.TryAdd(key, value);
}

/// <summary>Date must not be before today (server date, in the configured time zone).</summary>
public sealed class ClientNotInPastAttribute : ClientRuleAttribute
{
    public ClientNotInPastAttribute(string message) : base(message) { }
    protected override string Rule => "crmnotpast";

    // Evaluated together with the other property rules so the API reports every error in one response.
    // Without a service provider (plain Validator calls) it defers to the business layer, which re-checks.
    protected override ValidationResult? IsValid(object? value, ValidationContext ctx)
    {
        if (value is not DateOnly d || Skipped(ctx) || ctx.GetService(typeof(IClock)) is not IClock clock) return ValidationResult.Success;
        return d < clock.Today ? new ValidationResult(ErrorMessage, new[] { ctx.MemberName! }) : ValidationResult.Success;
    }
    public override void AddValidation(ClientModelValidationContext context)
    {
        base.AddValidation(context);
        var clock = (IClock)context.ActionContext.HttpContext.RequestServices.GetService(typeof(IClock))!;
        Merge(context, "data-val-crmnotpast-today", clock.Today.ToString("yyyy-MM-dd"));
    }
}

/// <summary>Number must be greater than zero.</summary>
public sealed class ClientPositiveAttribute : ClientRuleAttribute
{
    public ClientPositiveAttribute(string message) : base(message) { }
    protected override string Rule => "crmpositive";

    protected override ValidationResult? IsValid(object? value, ValidationContext ctx)
    {
        if (value is not decimal v) return ValidationResult.Success;
        var ok = Skipped(ctx) ? v >= 0 : v > 0;
        return ok ? ValidationResult.Success : new ValidationResult(ErrorMessage, new[] { ctx.MemberName! });
    }
}
