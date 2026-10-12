using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.TagHelpers;

namespace AcxiomCRM.Infrastructure;

/// <summary>
/// &lt;crm-field for="Prop" type="text|email|tel|date|number|textarea|select|password|checkbox" /&gt; renders a Bootstrap
/// form group (label, control, hint, inline validation message) with unobtrusive validation attributes.
/// </summary>
[HtmlTargetElement("crm-field", TagStructure = TagStructure.WithoutEndTag)]
public class CrmFieldTagHelper : TagHelper
{
    private readonly Microsoft.AspNetCore.Mvc.ViewFeatures.IHtmlGenerator _gen;
    public CrmFieldTagHelper(Microsoft.AspNetCore.Mvc.ViewFeatures.IHtmlGenerator gen) => _gen = gen;

    [HtmlAttributeNotBound, ViewContext] public ViewContext ViewContext { get; set; } = default!;
    [HtmlAttributeName("for")] public ModelExpression For { get; set; } = default!;
    public string Type { get; set; } = "text";
    public IEnumerable<SelectListItem>? Items { get; set; }
    /// <summary>Alternative to <see cref="Items"/>: name of a ViewData entry that holds the select list.</summary>
    public string? ItemsKey { get; set; }
    public string Col { get; set; } = "col-md-6";
    public string? Placeholder { get; set; }
    public string? Hint { get; set; }
    public string? Label { get; set; }
    public string? OptionLabel { get; set; }
    public string? Autocomplete { get; set; }
    public string? Prefix { get; set; }
    public string? Inputmode { get; set; }
    public int Rows { get; set; } = 3;
    public bool Disabled { get; set; }
    public bool Autofocus { get; set; }
    public string? Hook { get; set; }

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        output.TagName = "div";
        output.TagMode = TagMode.StartTagAndEndTag;
        output.Attributes.SetAttribute("class", Col);
        var explorer = For.ModelExplorer;
        var name = For.Name;
        var isCheck = Type == "checkbox";

        var attrs = new Dictionary<string, object> { ["class"] = Type == "select" ? "form-select" : isCheck ? "form-check-input" : "form-control" };
        if (Placeholder is not null) attrs["placeholder"] = Placeholder;
        if (Autocomplete is not null) attrs["autocomplete"] = Autocomplete;
        if (Inputmode is not null) attrs["inputmode"] = Inputmode;
        if (Disabled) attrs["disabled"] = "disabled";
        if (Autofocus) attrs["autofocus"] = "autofocus";
        if (Hook is not null) attrs["data-role"] = Hook;

        TagBuilder control;
        switch (Type)
        {
            case "textarea": control = _gen.GenerateTextArea(ViewContext, explorer, name, Rows, 0, attrs); break;
            case "select": control = _gen.GenerateSelect(ViewContext, explorer, OptionLabel, name, Items ?? (ItemsKey is not null ? ViewContext.ViewData[ItemsKey] as IEnumerable<SelectListItem> : null) ?? Array.Empty<SelectListItem>(), false, attrs); break;
            case "password": control = _gen.GeneratePassword(ViewContext, explorer, name, null, attrs); break;
            case "checkbox": control = _gen.GenerateCheckBox(ViewContext, explorer, name, null, attrs); break;
            default:
                var format = Type == "date" ? "{0:yyyy-MM-dd}" : null;
                if (Type == "number") { attrs["type"] = "number"; attrs["step"] = "1"; attrs["inputmode"] = "numeric"; }
                else attrs["type"] = Type;
                control = _gen.GenerateTextBox(ViewContext, explorer, name, explorer.Model, format, attrs);
                break;
        }

        var required = For.Metadata.ValidatorMetadata.OfType<RequiredAttribute>().Any();
        var label = _gen.GenerateLabel(ViewContext, explorer, name, Label, new { @class = isCheck ? "form-check-label" : "form-label" });
        if (required && !isCheck) label.InnerHtml.AppendHtml("<span class=\"text-danger ms-1\" aria-hidden=\"true\">*</span>");
        var validation = _gen.GenerateValidationMessage(ViewContext, explorer, name, null, "div", new { @class = "field-error" });

        if (isCheck)
        {
            output.Content.AppendHtml("<div class=\"form-check form-switch\">");
            output.Content.AppendHtml(control);
            output.Content.AppendHtml($"<input type=\"hidden\" name=\"{name}\" value=\"false\" />");
            output.Content.AppendHtml(label);
            output.Content.AppendHtml("</div>");
        }
        else
        {
            output.Content.AppendHtml(label);
            if (Prefix is not null)
            {
                output.Content.AppendHtml($"<div class=\"input-group\"><span class=\"input-group-text\">{System.Net.WebUtility.HtmlEncode(Prefix)}</span>");
                output.Content.AppendHtml(control);
                output.Content.AppendHtml("</div>");
            }
            else output.Content.AppendHtml(control);
        }
        if (!string.IsNullOrEmpty(Hint)) output.Content.AppendHtml($"<div class=\"form-text\">{System.Net.WebUtility.HtmlEncode(Hint)}</div>");
        output.Content.AppendHtml(validation);
    }
}
