using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace AgentTrust.Data;

/// <summary>Minimal durable Identity store required for trusted OIDC subject linking.</summary>
public sealed class EfApplicationUserStore : IUserStore<ApplicationUser>, IUserLoginStore<ApplicationUser>,
    IUserPasswordStore<ApplicationUser>
{
    private readonly AgentTrustDbContext _db;
    public EfApplicationUserStore(AgentTrustDbContext db) => _db = db;

    public async Task<IdentityResult> CreateAsync(ApplicationUser user, CancellationToken token)
    { _db.ApplicationUsers.Add(user); await _db.SaveChangesAsync(token); return IdentityResult.Success; }
    public async Task<IdentityResult> UpdateAsync(ApplicationUser user, CancellationToken token)
    { _db.ApplicationUsers.Update(user); await _db.SaveChangesAsync(token); return IdentityResult.Success; }
    public async Task<IdentityResult> DeleteAsync(ApplicationUser user, CancellationToken token)
    { _db.ApplicationUsers.Remove(user); await _db.SaveChangesAsync(token); return IdentityResult.Success; }
    public Task<ApplicationUser?> FindByIdAsync(string id, CancellationToken token) =>
        _db.ApplicationUsers.SingleOrDefaultAsync(x => x.Id == id, token);
    public Task<ApplicationUser?> FindByNameAsync(string normalizedUserName, CancellationToken token) =>
        _db.ApplicationUsers.SingleOrDefaultAsync(x => x.NormalizedUserName == normalizedUserName, token);
    public Task<string> GetUserIdAsync(ApplicationUser user, CancellationToken token) => Task.FromResult(user.Id);
    public Task<string?> GetUserNameAsync(ApplicationUser user, CancellationToken token) => Task.FromResult(user.UserName);
    public Task SetUserNameAsync(ApplicationUser user, string? name, CancellationToken token) { user.UserName = name; return Task.CompletedTask; }
    public Task<string?> GetNormalizedUserNameAsync(ApplicationUser user, CancellationToken token) => Task.FromResult(user.NormalizedUserName);
    public Task SetNormalizedUserNameAsync(ApplicationUser user, string? name, CancellationToken token) { user.NormalizedUserName = name; return Task.CompletedTask; }
    public Task SetPasswordHashAsync(ApplicationUser user, string? passwordHash, CancellationToken token)
    { user.PasswordHash = passwordHash; return Task.CompletedTask; }
    public Task<string?> GetPasswordHashAsync(ApplicationUser user, CancellationToken token) => Task.FromResult(user.PasswordHash);
    public Task<bool> HasPasswordAsync(ApplicationUser user, CancellationToken token) => Task.FromResult(user.PasswordHash is not null);

    public async Task AddLoginAsync(ApplicationUser user, UserLoginInfo login, CancellationToken token)
    { _db.ApplicationUserLogins.Add(new IdentityUserLogin<string> { UserId = user.Id, LoginProvider = login.LoginProvider, ProviderKey = login.ProviderKey, ProviderDisplayName = login.ProviderDisplayName }); await _db.SaveChangesAsync(token); }
    public async Task RemoveLoginAsync(ApplicationUser user, string provider, string key, CancellationToken token)
    { var item = await _db.ApplicationUserLogins.FindAsync([provider, key], token); if (item is not null) { _db.Remove(item); await _db.SaveChangesAsync(token); } }
    public async Task<IList<UserLoginInfo>> GetLoginsAsync(ApplicationUser user, CancellationToken token) =>
        await _db.ApplicationUserLogins.Where(x => x.UserId == user.Id)
            .Select(x => new UserLoginInfo(x.LoginProvider, x.ProviderKey, x.ProviderDisplayName)).ToListAsync(token);
    public async Task<ApplicationUser?> FindByLoginAsync(string provider, string key, CancellationToken token)
    { var userId = await _db.ApplicationUserLogins.Where(x => x.LoginProvider == provider && x.ProviderKey == key).Select(x => x.UserId).SingleOrDefaultAsync(token); return userId is null ? null : await FindByIdAsync(userId, token); }
    public void Dispose() { }
}
