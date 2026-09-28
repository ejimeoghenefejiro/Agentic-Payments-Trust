using AgentTrust.Core;
using AgentTrust.Core.Models;
using AgentTrust.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AgentTrust.Api.Controllers;

[ApiController,Route("api/admin"),Authorize(Policy="AppAdmin")]
public sealed class AdminController(AgentTrustDbContext db,IMerchantStore merchants):ControllerBase
{
    [HttpGet("overview")]
    public async Task<IActionResult> Overview(CancellationToken token)=>Ok(new
    {
        users=await db.ApplicationUsers.CountAsync(token),
        merchants=await db.Merchants.CountAsync(token),
        agents=await db.Agents.CountAsync(token),
        activeRecurringOrders=await db.ConsumerPurchaseTasks.CountAsync(x=>x.Status=="Active",token),
        activeAgentCycles=await db.CommerceOodaCycles.CountAsync(x=>x.Status!="Completed"&&x.Status!="Failed",token),
        purchasesNeedingAttention=await db.PurchaseExecutions.CountAsync(x=>x.State=="Failed"||x.State=="Unknown"||x.State=="RequiresAction",token)
    });

    [HttpGet("users")]
    public async Task<IActionResult> Users(CancellationToken token)=>Ok(await db.ApplicationUsers.AsNoTracking().OrderByDescending(x=>x.CreatedAt).Select(x=>new{x.Id,x.PrincipalId,x.UserName,x.Email,x.CreatedAt}).Take(200).ToListAsync(token));

    [HttpGet("activity")]
    public async Task<IActionResult> Activity(CancellationToken token)=>Ok(await db.CommerceOodaCycles.AsNoTracking().OrderByDescending(x=>x.UpdatedAt).Select(x=>new{x.CycleId,x.PrincipalId,x.TaskId,x.PurchaseIntentId,x.Status,x.CycleNumber,x.UpdatedAt}).Take(100).ToListAsync(token));

    [HttpGet("merchants")]
    public IActionResult Merchants()=>Ok(merchants.All().OrderBy(x=>x.Name));

    [HttpPost("merchants"),Authorize(Policy="StepUp")]
    public IActionResult SaveMerchant(AdminMerchantRequest request)
    {
        if(string.IsNullOrWhiteSpace(request.MerchantId)||string.IsNullOrWhiteSpace(request.Name)||string.IsNullOrWhiteSpace(request.Category))return BadRequest("Merchant ID, name and category are required.");
        var merchant=new Merchant(request.MerchantId.Trim(),request.Name.Trim(),request.Category.Trim(),request.Approved);merchants.Register(merchant);return Ok(merchant);
    }

    [HttpGet("notification-providers")]
    public async Task<IActionResult> NotificationProviders(CancellationToken token)=>Ok(await db.AdminNotificationProviders.AsNoTracking().OrderBy(x=>x.Channel).Select(x=>new{x.Channel,x.Provider,x.Sender,hasSecret=x.SecretReference!="",x.Enabled,x.UpdatedAt,x.Version}).ToListAsync(token));

    [HttpPut("notification-providers/{channel}"),Authorize(Policy="StepUp")]
    public async Task<IActionResult> SaveNotificationProvider(string channel,AdminNotificationProviderRequest request,CancellationToken token)
    {
        channel=channel.Trim().ToUpperInvariant();if(channel is not("EMAIL" or "SMS"))return BadRequest("Channel must be EMAIL or SMS.");
        if(string.IsNullOrWhiteSpace(request.Provider)||string.IsNullOrWhiteSpace(request.Sender))return BadRequest("Provider and sender are required.");
        var row=await db.AdminNotificationProviders.SingleOrDefaultAsync(x=>x.Channel==channel,token);
        if(row is null){row=new(){Channel=channel};db.Add(row);}else row.Version++;
        row.Provider=request.Provider.Trim();row.Sender=request.Sender.Trim();if(!string.IsNullOrWhiteSpace(request.SecretReference))row.SecretReference=request.SecretReference.Trim();row.Enabled=request.Enabled;row.UpdatedAt=DateTimeOffset.UtcNow;row.UpdatedBy=User.FindFirst("agenttrust_principal_id")?.Value??"admin";await db.SaveChangesAsync(token);
        return Ok(new{row.Channel,row.Provider,row.Sender,hasSecret=row.SecretReference!="",row.Enabled,row.UpdatedAt,row.Version});
    }
}

public sealed record AdminMerchantRequest(string MerchantId,string Name,string Category,bool Approved);
public sealed record AdminNotificationProviderRequest(string Provider,string Sender,string? SecretReference,bool Enabled);
