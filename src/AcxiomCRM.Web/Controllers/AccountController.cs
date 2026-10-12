using AcxiomCRM.Application.Security;
using AcxiomCRM.Application.Services;
using AcxiomCRM.Domain;
using AcxiomCRM.Domain.Entities;
using AcxiomCRM.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace AcxiomCRM.Controllers;

public class AccountController : CrmController
{
    private readonly IAuthService _auth;
    private readonly SignInManager<ApplicationUser> _signIn;
    private readonly UserManager<ApplicationUser> _users;
    private readonly IAuditService _audit;
    private readonly SecurityOptions _opt;

    public AccountController(IAuthService auth, SignInManager<ApplicationUser> signIn, UserManager<ApplicationUser> users, IAuditService audit, IOptions<SecurityOptions> opt)
    { _auth = auth; _signIn = signIn; _users = users; _audit = audit; _opt = opt.Value; }

    [AllowAnonymous, HttpGet]
    public IActionResult Login(string? returnUrl = null)
    {
        if (User.Identity?.IsAuthenticated == true) return RedirectToAction("Index", "Dashboard");
        ViewData["AllowRegistration"] = _opt.AllowSelfRegistration;
        return View(new LoginViewModel { ReturnUrl = returnUrl });
    }

    [AllowAnonymous, HttpPost, EnableRateLimiting("auth")]
    public async Task<IActionResult> Login(LoginViewModel model)
    {
        ViewData["AllowRegistration"] = _opt.AllowSelfRegistration;
        if (!ModelState.IsValid) return View(model);
        var result = await _auth.AuthenticateAsync(model.UserName!, model.Password!, "Web");
        switch (result.Status)
        {
            case AuthStatus.Success:
                await _signIn.SignInAsync(result.User!, isPersistent: false);
                return LocalRedirectOrDashboard(model.ReturnUrl);
            case AuthStatus.LockedOut:
                var mins = result.LockoutEnd is { } e ? Math.Max(1, (int)Math.Ceiling((e - DateTimeOffset.UtcNow).TotalMinutes)) : 15;
                ModelState.AddModelError("", $"Too many failed attempts. The account is locked for about {mins} more minute(s). Contact an administrator to unlock it sooner.");
                break;
            case AuthStatus.Inactive:
                ModelState.AddModelError("", "This account is disabled. Please contact an administrator.");
                break;
            default:
                ModelState.AddModelError("", "Invalid email or password.");
                break;
        }
        model.Password = null; // never echo the password back
        return View(model);
    }

    [HttpPost, Authorize]
    public async Task<IActionResult> Logout()
    {
        await _audit.LogAsync("Authentication", "Logout", details: "Signed out via Web");
        await _signIn.SignOutAsync();
        return RedirectToAction(nameof(Login));
    }

    [AllowAnonymous, HttpGet]
    public IActionResult Register()
    {
        if (!_opt.AllowSelfRegistration) return NotFound();
        if (User.Identity?.IsAuthenticated == true) return RedirectToAction("Index", "Dashboard");
        return View(new RegisterInput());
    }

    [AllowAnonymous, HttpPost, EnableRateLimiting("auth")]
    public async Task<IActionResult> Register(RegisterInput input)
    {
        if (!_opt.AllowSelfRegistration) return NotFound();
        if (!ModelState.IsValid) return View(input);
        var result = await _auth.RegisterAsync(input);
        if (!result.Succeeded)
        {
            foreach (var e in result.Errors)
                ModelState.AddModelError(e.Code.Contains("Password") ? nameof(RegisterInput.Password) : e.Code.Contains("Email") || e.Code.Contains("UserName") ? nameof(RegisterInput.Email) : "",
                    e.Code is "DuplicateUserName" or "DuplicateEmail" ? "An account with this email already exists." : e.Description);
            input.Password = input.ConfirmPassword = null;
            return View(input);
        }
        var user = await _users.FindByEmailAsync(input.Email!.Trim());
        await _signIn.SignInAsync(user!, isPersistent: false);
        Success("Welcome to AcxiomCRM! Your account has been created.");
        return RedirectToAction("Index", "Dashboard");
    }

    [HttpGet, Authorize]
    public async Task<IActionResult> Profile()
    {
        var user = await _users.GetUserAsync(User);
        if (user is null) return NotFound();
        ViewData["Role"] = RoleNames.Display((await _users.GetRolesAsync(user)).FirstOrDefault() ?? "");
        return View(user);
    }

    [HttpGet, Authorize]
    public IActionResult ChangePassword() => View(new ChangePasswordViewModel());

    [HttpPost, Authorize]
    public async Task<IActionResult> ChangePassword(ChangePasswordViewModel model)
    {
        if (!ModelState.IsValid) return View(model);
        var user = await _users.GetUserAsync(User);
        if (user is null) return Challenge();
        var result = await _users.ChangePasswordAsync(user, model.CurrentPassword!, model.NewPassword!);
        if (!result.Succeeded)
        {
            foreach (var e in result.Errors)
                ModelState.AddModelError(e.Code == "PasswordMismatch" ? nameof(ChangePasswordViewModel.CurrentPassword) : nameof(ChangePasswordViewModel.NewPassword), e.Description);
            await _audit.LogAsync("Security", "PasswordChangeFailed", nameof(ApplicationUser), user.Id, "Password change rejected", "Failure");
            return View(new ChangePasswordViewModel());
        }
        await _auth.RecordPasswordAsync(user);
        await _signIn.RefreshSignInAsync(user);
        await _audit.LogAsync("Security", "PasswordChanged", nameof(ApplicationUser), user.Id, "User changed their own password");
        Success("Your password has been changed.");
        return RedirectToAction(nameof(Profile));
    }

    /// <summary>Landing page for the single-use link generated by an administrator.</summary>
    [AllowAnonymous, HttpGet]
    public IActionResult ResetPassword(string? userId, string? token)
    {
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(token)) return NotFound();
        return View(new ResetPasswordViewModel { UserId = userId, Token = token });
    }

    [AllowAnonymous, HttpPost, EnableRateLimiting("auth")]
    public async Task<IActionResult> ResetPassword(ResetPasswordViewModel model)
    {
        if (!ModelState.IsValid) return View(model);
        var user = await _users.FindByIdAsync(model.UserId!);
        const string generic = "This reset link is invalid or has expired. Ask an administrator for a new one.";
        if (user is null || !user.IsActive) { ModelState.AddModelError("", generic); return View(model); }
        var result = await _users.ResetPasswordAsync(user, model.Token!, model.NewPassword!);
        if (!result.Succeeded)
        {
            foreach (var e in result.Errors)
                ModelState.AddModelError(e.Code.Contains("Password") ? nameof(ResetPasswordViewModel.NewPassword) : "", e.Code == "InvalidToken" ? generic : e.Description);
            await _audit.LogAsync("Security", "PasswordResetFailed", nameof(ApplicationUser), user.Id, "Password reset rejected", "Failure", user.Id, user.UserName);
            model.NewPassword = model.ConfirmPassword = null;
            return View(model);
        }
        await _auth.RecordPasswordAsync(user);
        await _users.ResetAccessFailedCountAsync(user);
        await _audit.LogAsync("Security", "PasswordReset", nameof(ApplicationUser), user.Id, "Password reset using an administrator-issued link", "Success", user.Id, user.UserName);
        Success("Your password has been reset. You can now sign in.");
        return RedirectToAction(nameof(Login));
    }

    [AllowAnonymous]
    public IActionResult AccessDenied() { Response.StatusCode = 403; return View(); }

    private IActionResult LocalRedirectOrDashboard(string? returnUrl) =>
        !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl) ? LocalRedirect(returnUrl) : RedirectToAction("Index", "Dashboard");
}
