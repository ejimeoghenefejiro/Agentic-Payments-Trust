using System.Security.Cryptography;
using System.Text;
using AgentTrust.Commerce;
using AgentTrust.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Stripe;

namespace AgentTrust.Api.Controllers;

[ApiController, Route("api/payments/stripe/webhook"), AllowAnonymous]
public sealed class StripeWebhookController : ControllerBase
{
    private readonly AgentTrustDbContext _db; private readonly IConfiguration _configuration; private readonly IPurchaseAuditSink _audit;
    public StripeWebhookController(AgentTrustDbContext db,IConfiguration configuration,IPurchaseAuditSink audit){_db=db;_configuration=configuration;_audit=audit;}

    [HttpPost]
    [RequestSizeLimit(1_048_576)]
    public async Task<IActionResult> Receive(CancellationToken token)
    {
        var secret=_configuration["Stripe:WebhookSecret"]??Environment.GetEnvironmentVariable("STRIPE_WEBHOOK_SECRET");
        if(string.IsNullOrWhiteSpace(secret))return Problem("Stripe webhook verification is not configured.",statusCode:503);
        using var reader=new StreamReader(Request.Body,Encoding.UTF8);var payload=await reader.ReadToEndAsync(token);
        var signature=Request.Headers["Stripe-Signature"].ToString();
        Event stripeEvent;
        try{stripeEvent=EventUtility.ConstructEvent(payload,signature,secret,300,false);}
        catch(StripeException){return BadRequest();}
        if(await _db.StripeWebhookEvents.AnyAsync(x=>x.ProviderEventId==stripeEvent.Id,token))return Ok(new{duplicate=true});

        var providerPaymentId=stripeEvent.Data.Object switch{PaymentIntent pi=>pi.Id,Charge c=>c.PaymentIntentId,Dispute d=>d.Charge?.PaymentIntentId,_=>null};
        var metadataIntentId=(stripeEvent.Data.Object as PaymentIntent)?.Metadata?.GetValueOrDefault("purchase_intent_id");
        await using var tx=await _db.Database.BeginTransactionAsync(token);
        _db.StripeWebhookEvents.Add(new(){ProviderEventId=stripeEvent.Id,EventType=stripeEvent.Type,ProviderPaymentId=providerPaymentId,
            PayloadHash=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))),Status="Processed",ProviderCreatedAt=stripeEvent.Created,ReceivedAt=DateTimeOffset.UtcNow,ProcessedAt=DateTimeOffset.UtcNow,Version=1});
        if(providerPaymentId is not null)
        {
            var attempt=await _db.ConsumerPaymentAttempts.SingleOrDefaultAsync(x=>x.ProviderPaymentId==providerPaymentId||
                (metadataIntentId!=null&&x.PurchaseIntentId==metadataIntentId),token);
            if(attempt is not null&&attempt.ProviderPaymentId is null)attempt.ProviderPaymentId=providerPaymentId;
            var purchaseIntentId=attempt?.PurchaseIntentId??metadataIntentId;
            var execution=await _db.PurchaseExecutions.SingleOrDefaultAsync(x=>x.ProviderPaymentId==providerPaymentId||
                (purchaseIntentId!=null&&x.PurchaseIntentId==purchaseIntentId),token);
            var state=Map(stripeEvent.Type);
            if (state is null) { await _db.SaveChangesAsync(token); await tx.CommitAsync(token); return Ok(new { ignored = true }); }
            if(attempt is not null){attempt.LatestStatus=state.Value.Attempt;attempt.UpdatedAt=DateTimeOffset.UtcNow;attempt.Version++;}
            if(purchaseIntentId is not null)
            {
                var checkout=await _db.CheckoutExecutions.SingleOrDefaultAsync(x=>x.PurchaseIntentId==purchaseIntentId,token);
                if(checkout is not null){checkout.Status=state.Value.Attempt switch{"Captured"=>"Succeeded","Declined"=>"Failed",_=>state.Value.Attempt};checkout.UpdatedAt=DateTimeOffset.UtcNow;checkout.Version++;}
            }
            if(execution is not null)
            {
                execution.State=state.Value.Execution;execution.UpdatedAt=DateTimeOffset.UtcNow;execution.Version++;
                var reservation=await _db.SpendReservations.SingleOrDefaultAsync(x=>x.ExecutionId==execution.PurchaseIntentId&&x.Status=="Reserved",token);
                if(reservation is not null&&state.Value.Finalise is not null){reservation.Status=state.Value.Finalise;reservation.FinalisedAt=DateTimeOffset.UtcNow;reservation.Version++;}
                var eventType=state.Value.Execution=="Purchased"?"PaymentConfirmed":state.Value.Execution=="Processing"?"PaymentProcessing":"PaymentFailed";
                _audit.Append(new PurchaseAuditEvent($"pae_{Guid.NewGuid():N}", eventType, execution.PurchaseIntentId,
                    execution.PrincipalId, execution.TransactionId, "", DateTimeOffset.UtcNow,
                    new Dictionary<string,string> { ["stripeEventId"] = stripeEvent.Id, ["stripeEventType"] = stripeEvent.Type }));
                if(state.Value.Execution=="Purchased"&&!await _db.PurchaseReceipts.AnyAsync(x=>x.PurchaseIntentId==execution.PurchaseIntentId,token))
                {
                    var intent=await _db.PurchaseIntents.SingleAsync(x=>x.PurchaseIntentId==execution.PurchaseIntentId,token);
                    _db.PurchaseReceipts.Add(new PurchaseReceiptEntity{ReceiptId=$"receipt_{Guid.NewGuid():N}",PurchaseIntentId=intent.PurchaseIntentId,
                        PrincipalId=intent.PrincipalId,MerchantId=intent.MerchantId,TotalAmount=intent.TotalAmount,Currency=intent.Currency,
                        ProviderPaymentId=providerPaymentId,PurchasedAt=DateTimeOffset.UtcNow});
                    _audit.Append(new PurchaseAuditEvent($"pae_{Guid.NewGuid():N}","ReceiptCreated",execution.PurchaseIntentId,execution.PrincipalId,
                        execution.TransactionId,intent.IntentHash,DateTimeOffset.UtcNow,new Dictionary<string,string>()));
                }
            }
        }
        await _db.SaveChangesAsync(token);await tx.CommitAsync(token);
        return Ok();
    }

    private static (string Attempt,string Execution,string? Finalise)? Map(string type)=>type switch
    {
        "payment_intent.succeeded"=>("Captured","Purchased","Committed"),
        "payment_intent.payment_failed"=>("Declined","Failed","Released"),
        "payment_intent.processing"=>("Processing","Processing",null),
        "payment_intent.requires_action"=>("RequiresAction","RequiresAction",null),
        "charge.refunded"=>("Refunded","Purchased",null),
        "charge.dispute.created"=>("Disputed","Purchased",null),
        _=>null
    };
}
