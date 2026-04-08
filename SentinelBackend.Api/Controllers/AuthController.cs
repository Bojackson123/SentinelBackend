namespace SentinelBackend.Api.Controllers;

using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.Tokens;
using SentinelBackend.Domain.Entities;
using SentinelBackend.Domain.Enums;

[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly SignInManager<ApplicationUser> _signInManager;
    private readonly IConfiguration _configuration;

    public AuthController(
        UserManager<ApplicationUser> userManager,
        SignInManager<ApplicationUser> signInManager,
        IConfiguration configuration)
    {
        _userManager = userManager;
        _signInManager = signInManager;
        _configuration = configuration;
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
        var user = await _userManager.FindByEmailAsync(request.Email);
        if (user is null)
            return Unauthorized("Invalid credentials.");

        var result = await _signInManager.CheckPasswordSignInAsync(user, request.Password, lockoutOnFailure: true);
        if (!result.Succeeded)
            return Unauthorized("Invalid credentials.");

        var token = await GenerateJwtToken(user);
        return Ok(new { token });
    }

    [HttpPost("register")]
    public async Task<IActionResult> Register([FromBody] RegisterRequest request)
    {
        var user = new ApplicationUser
        {
            UserName = request.Email,
            Email = request.Email,
            FirstName = request.FirstName,
            LastName = request.LastName,
            CompanyId = request.CompanyId,
            CustomerId = request.CustomerId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        var result = await _userManager.CreateAsync(user, request.Password);
        if (!result.Succeeded)
            return BadRequest(result.Errors.Select(e => e.Description));

        if (!string.IsNullOrWhiteSpace(request.Role))
        {
            if (!Enum.TryParse<UserRole>(request.Role, ignoreCase: true, out var parsedRole))
                return BadRequest($"Invalid role '{request.Role}'.");

            var roleName = parsedRole.ToString();
            var roleResult = await _userManager.AddToRoleAsync(user, roleName);
            if (!roleResult.Succeeded)
                return BadRequest(roleResult.Errors.Select(e => e.Description));
        }

        return Ok(new { user.Id, user.Email });
    }

    private async Task<string> GenerateJwtToken(ApplicationUser user)
    {
        var roles = await _userManager.GetRolesAsync(user);

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id),
            new(ClaimTypes.Email, user.Email!),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
        };

        if (!string.IsNullOrEmpty(user.FirstName))
            claims.Add(new Claim(ClaimTypes.GivenName, user.FirstName));
        if (!string.IsNullOrEmpty(user.LastName))
            claims.Add(new Claim(ClaimTypes.Surname, user.LastName));

        if (user.CompanyId.HasValue)
            claims.Add(new Claim("companyId", user.CompanyId.Value.ToString()));
        if (user.CustomerId.HasValue)
            claims.Add(new Claim("customerId", user.CustomerId.Value.ToString()));

        foreach (var role in roles)
            claims.Add(new Claim(ClaimTypes.Role, role));

        var signingKey = _configuration["JwtSigningKey"] ?? throw new InvalidOperationException("JWT signing key is not configured.");
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: _configuration["JwtIssuer"] ?? "SentinelBackend",
            audience: _configuration["JwtAudience"] ?? "SentinelBackend",
            claims: claims,
            expires: DateTime.UtcNow.AddHours(1),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}

public class LoginRequest
{
    public string Email { get; set; } = default!;
    public string Password { get; set; } = default!;
}

public class RegisterRequest
{
    public string Email { get; set; } = default!;
    public string Password { get; set; } = default!;
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string? Role { get; set; }
    public int? CompanyId { get; set; }
    public int? CustomerId { get; set; }
}
