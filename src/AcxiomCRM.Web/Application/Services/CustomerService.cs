using AcxiomCRM.Application.Common;
using AcxiomCRM.Application.Dtos;
using AcxiomCRM.Data;
using AcxiomCRM.Domain;
using AcxiomCRM.Domain.Entities;
using AcxiomCRM.Domain.Rules;
using Microsoft.EntityFrameworkCore;

namespace AcxiomCRM.Application.Services;

public class CustomerQuery : ListQuery
{
    public CustomerStatus? Status { get; set; }
}

public interface ICustomerService
{
    Task<PagedResult<CustomerDto>> ListAsync(CustomerQuery q);
    Task<ServiceResult<CustomerDto>> GetAsync(int id);
    Task<ServiceResult<CustomerDto>> CreateAsync(CustomerInput input);
    Task<ServiceResult<CustomerDto>> UpdateAsync(int id, CustomerInput input);
    Task<ServiceResult> DeleteAsync(int id);
    Task<IReadOnlyList<(int Id, string Name)>> LookupAsync(string? term = null, int take = 200);
}

public sealed class CustomerService : ICustomerService
{
    private readonly AppDbContext _db;
    private readonly IAccessScope _scope;
    private readonly ICurrentUser _user;
    private readonly IClock _clock;

    public CustomerService(AppDbContext db, IAccessScope scope, ICurrentUser user, IClock clock)
    { _db = db; _scope = scope; _user = user; _clock = clock; }

    public async Task<PagedResult<CustomerDto>> ListAsync(CustomerQuery lq)
    {
        var q = (await _scope.ApplyAsync(_db.Customers.AsNoTracking().Include(c => c.Owner)));
        if (!string.IsNullOrWhiteSpace(lq.Search))
        {
            var p = QueryHelpers.Like(lq.Search);
            q = q.Where(c => EF.Functions.Like(c.CustomerName, p, "\\") || EF.Functions.Like(c.Email, p, "\\")
                || EF.Functions.Like(c.Phone, p, "\\") || EF.Functions.Like(c.CompanyName ?? "", p, "\\")
                || EF.Functions.Like(c.CustomerCode, p, "\\"));
        }
        if (lq.Status is { } st) q = q.Where(c => c.Status == st);
        if (!string.IsNullOrWhiteSpace(lq.AssignedTo)) q = q.Where(c => c.AssignedTo == lq.AssignedTo);

        q = (lq.Sort?.ToLowerInvariant(), lq.Desc) switch
        {
            ("code", false) => q.OrderBy(c => c.CustomerCode), ("code", true) => q.OrderByDescending(c => c.CustomerCode),
            ("email", false) => q.OrderBy(c => c.Email), ("email", true) => q.OrderByDescending(c => c.Email),
            ("company", false) => q.OrderBy(c => c.CompanyName), ("company", true) => q.OrderByDescending(c => c.CompanyName),
            ("status", false) => q.OrderBy(c => c.Status), ("status", true) => q.OrderByDescending(c => c.Status),
            ("created", false) => q.OrderBy(c => c.CreatedDate), ("created", true) => q.OrderByDescending(c => c.CreatedDate),
            ("name", true) => q.OrderByDescending(c => c.CustomerName),
            ("name", false) => q.OrderBy(c => c.CustomerName),
            _ => q.OrderByDescending(c => c.CreatedDate).ThenByDescending(c => c.CustomerId),
        };

        var page = await q.ToPagedAsync(lq);
        return new PagedResult<CustomerDto> { Items = page.Items.Select(Map).ToList(), Page = page.Page, PageSize = page.PageSize, TotalCount = page.TotalCount };
    }

    public async Task<ServiceResult<CustomerDto>> GetAsync(int id)
    {
        var c = await (await _scope.ApplyAsync(_db.Customers.AsNoTracking().Include(x => x.Owner))).FirstOrDefaultAsync(x => x.CustomerId == id);
        return c is null ? ServiceResult<CustomerDto>.NotFound() : ServiceResult<CustomerDto>.Ok(Map(c));
    }

    public async Task<ServiceResult<CustomerDto>> CreateAsync(CustomerInput input)
    {
        var errors = InputValidator.Validate(input);
        if (errors.Count > 0) return ServiceResult<CustomerDto>.Invalid(errors);

        var (owner, ownerErr) = await _scope.ResolveAssigneeAsync(input.AssignedTo);
        if (owner is null) return ServiceResult<CustomerDto>.Invalid(nameof(CustomerInput.AssignedTo), ownerErr!);

        var dup = await FindDuplicatesAsync(input, null);
        if (dup.Count > 0) return ServiceResult<CustomerDto>.Conflict(dup);

        var now = _clock.UtcNow;
        var c = new Customer { CreatedBy = _user.UserId!, CreatedDate = now, ModifiedDate = now, AssignedTo = owner };
        Apply(c, input);
        _db.Customers.Add(c);
        try { await _db.SaveChangesAsync(); }
        catch (DbUpdateException) { return ServiceResult<CustomerDto>.Conflict("A customer with the same email or phone already exists."); }
        await _db.Entry(c).Reference(x => x.Owner).LoadAsync();
        return ServiceResult<CustomerDto>.Created(Map(c));
    }

    public async Task<ServiceResult<CustomerDto>> UpdateAsync(int id, CustomerInput input)
    {
        var errors = InputValidator.Validate(input);
        if (errors.Count > 0) return ServiceResult<CustomerDto>.Invalid(errors);

        var c = await (await _scope.ApplyAsync(_db.Customers)).FirstOrDefaultAsync(x => x.CustomerId == id);
        if (c is null) return ServiceResult<CustomerDto>.NotFound();

        var (owner, ownerErr) = await _scope.ResolveAssigneeAsync(string.IsNullOrWhiteSpace(input.AssignedTo) ? c.AssignedTo : input.AssignedTo);
        if (owner is null) return ServiceResult<CustomerDto>.Invalid(nameof(CustomerInput.AssignedTo), ownerErr!);

        var dup = await FindDuplicatesAsync(input, id);
        if (dup.Count > 0) return ServiceResult<CustomerDto>.Conflict(dup);

        Apply(c, input);
        c.AssignedTo = owner;
        c.ModifiedDate = _clock.UtcNow;
        try { await _db.SaveChangesAsync(); }
        catch (DbUpdateException) { return ServiceResult<CustomerDto>.Conflict("A customer with the same email or phone already exists."); }
        await _db.Entry(c).Reference(x => x.Owner).LoadAsync();
        return ServiceResult<CustomerDto>.Ok(Map(c));
    }

    public async Task<ServiceResult> DeleteAsync(int id)
    {
        if (_user.Role == RoleNames.SalesExecutive) return ServiceResult.Forbidden("Only managers and administrators can delete customers.");
        var c = await (await _scope.ApplyAsync(_db.Customers)).FirstOrDefaultAsync(x => x.CustomerId == id);
        if (c is null) return ServiceResult.NotFound();

        var active = await _db.Opportunities.CountAsync(o => o.CustomerId == id && o.Stage != OpportunityStage.Won && o.Stage != OpportunityStage.Lost);
        if (active > 0) return ServiceResult.Conflict($"This customer has {active} active opportunit{(active == 1 ? "y" : "ies")}. Close them before deleting the customer.");

        c.IsDeleted = true;
        c.DeletedDate = _clock.UtcNow;
        c.ModifiedDate = c.DeletedDate.Value;
        await _db.SaveChangesAsync();
        return ServiceResult.Ok();
    }

    public async Task<IReadOnlyList<(int Id, string Name)>> LookupAsync(string? term = null, int take = 200)
    {
        var q = await _scope.ApplyAsync(_db.Customers.AsNoTracking());
        if (!string.IsNullOrWhiteSpace(term))
        {
            var p = QueryHelpers.Like(term);
            q = q.Where(c => EF.Functions.Like(c.CustomerName, p, "\\") || EF.Functions.Like(c.CompanyName ?? "", p, "\\"));
        }
        var rows = await q.OrderBy(c => c.CustomerName).Take(take).Select(c => new { c.CustomerId, c.CustomerName, c.CompanyName }).ToListAsync();
        return rows.Select(r => (r.CustomerId, string.IsNullOrEmpty(r.CompanyName) ? r.CustomerName : $"{r.CustomerName} ({r.CompanyName})")).ToList();
    }

    /// <summary>Checks the whole customer base (not only the caller's scope) so duplicates cannot hide behind visibility.</summary>
    private async Task<Dictionary<string, string>> FindDuplicatesAsync(CustomerInput input, int? excludeId)
    {
        var email = ValidationRules.NormalizeEmail(input.Email!);
        var phone = ValidationRules.NormalizePhone(input.Phone!);
        var name = input.CustomerName!.Trim().ToLower();
        var company = (ValidationRules.Clean(input.CompanyName) ?? "").ToLower();
        var all = _db.Customers.AsNoTracking().Where(c => excludeId == null || c.CustomerId != excludeId);
        var errors = new Dictionary<string, string>();
        if (await all.AnyAsync(c => c.Email == email)) errors["Email"] = "A customer with this email already exists.";
        if (await all.AnyAsync(c => c.Phone == phone)) errors["Phone"] = "A customer with this phone number already exists.";
        if (errors.Count == 0 && await all.AnyAsync(c => c.CustomerName.ToLower() == name && (c.CompanyName ?? "").ToLower() == company))
            errors["CustomerName"] = "A customer with the same name and company already exists.";
        return errors;
    }

    private static void Apply(Customer c, CustomerInput i)
    {
        c.CustomerName = i.CustomerName!.Trim();
        c.Email = ValidationRules.NormalizeEmail(i.Email!);
        c.Phone = ValidationRules.NormalizePhone(i.Phone!);
        c.CompanyName = ValidationRules.Clean(i.CompanyName);
        c.Address = ValidationRules.Clean(i.Address);
        c.City = ValidationRules.Clean(i.City);
        c.State = ValidationRules.Clean(i.State);
        c.Status = i.Status;
        c.Notes = ValidationRules.Clean(i.Notes);
    }

    public static CustomerDto Map(Customer c) => new(c.CustomerId, c.CustomerCode, c.CustomerName, c.Email, c.Phone, c.CompanyName,
        c.Address, c.City, c.State, c.Status.ToString(), c.Notes, c.AssignedTo, c.Owner?.FullName, c.CreatedDate, c.ModifiedDate);
}
