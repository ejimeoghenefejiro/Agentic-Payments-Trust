using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentTrust.Commerce;

namespace AgentTrust.Connectors;

public sealed class DemoRestaurantConnector : IServiceConnector, IRestaurantSearchCapability,
    IRestaurantMenuCapability, IRestaurantQuoteCapability, IRestaurantOrderCapability,
    IFulfilmentOptionsCapability, IFulfilmentQuoteCapability
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IServiceActionAuthorisationService _authorisations;
    private readonly ConcurrentDictionary<string, RestaurantOrderQuote> _quotes = [];
    private readonly ConcurrentDictionary<string, RestaurantOrderResult> _ordersByIntent = [];
    private readonly ConcurrentDictionary<string, RestaurantOrderResult> _ordersById = [];
    private readonly ConcurrentDictionary<string, string> _holds = [];

    private static readonly ModifierGroup Size = new("size", "Size", true, 1, 1,
        [new("regular", "Regular", 0, true), new("large", "Large", 2, true)]);
    private static readonly RestaurantSummary[] Restaurants =
    [
        new("restaurant-value", "Demo Kitchen", new HashSet<string>(["international"]), true, true, true, 8, 4.5m, 30, new HashSet<string>(["value"]))
    ];
    private static readonly MenuItem[] Items =
    [
        new("vegetable-bowl", "restaurant-value", "Vegetable bowl", "Vegetarian rice and vegetables", 8, "GBP", true,
            new HashSet<string>(["vegetarian"]), new HashSet<string>(), [Size], 520),
        new("grilled-meal", "restaurant-value", "Grilled meal", "Grilled main with salad", 9, "GBP", true,
            new HashSet<string>(), new HashSet<string>(), [Size], 610),
        new("unavailable-special", "restaurant-value", "Unavailable special", "Not currently offered", 5, "GBP", false,
            new HashSet<string>(["vegetarian"]), new HashSet<string>(), [], 400)
    ];

    public DemoRestaurantConnector(IServiceActionAuthorisationService authorisations) => _authorisations = authorisations;
    public string ProviderId => "restaurant-demo";
    public string ProviderName => "Restaurant Demo";
    public int PlaceOrderCount { get; private set; }
    public IReadOnlyCollection<CapabilityDescriptor> Capabilities { get; } =
    [
        new("search_restaurants", CapabilityFamily.Discovery, "query,fulfilment", "restaurants"),
        new("get_menu", CapabilityFamily.Discovery, "restaurantId", "menu"),
        new("search_menu", CapabilityFamily.Discovery, "restaurantId,query", "items"),
        new("get_menu_item", CapabilityFamily.Discovery, "restaurantId,itemId", "item"),
        new("get_fulfilment_options", CapabilityFamily.Configuration, "restaurantId", "delivery,pickup"),
        new("get_restaurant_quote", CapabilityFamily.Quote, "restaurantId,items,fulfilment", "quote"),
        new("reserve", CapabilityFamily.Reservation, "quoteId", "reservation"),
        new("release", CapabilityFamily.Reservation, "reservationId", "status"),
        new("place_order", CapabilityFamily.Execution, "intent,authorisation", "order"),
        new("cancel_order", CapabilityFamily.Lifecycle, "orderId,authorisation", "cancellation"),
        new("get_order_status", CapabilityFamily.Lifecycle, "orderId", "status")
    ];

    public async Task<CapabilityInvocationResult> InvokeAsync(string capability, JsonElement input, CancellationToken cancellationToken = default)
    {
        try
        {
            return capability switch
            {
                "search_restaurants" => Ok(new { restaurants = await SearchRestaurantsAsync(input.Deserialize<RestaurantSearchRequest>()!, cancellationToken) }),
                "get_menu" => Ok(await GetMenuAsync(input.GetProperty("restaurantId").GetString()!, cancellationToken)),
                "search_menu" => Ok(new { items = await SearchMenuAsync(input.GetProperty("restaurantId").GetString()!, input.GetProperty("query").GetString()!, cancellationToken) }),
                "get_menu_item" => Ok(await GetMenuItemAsync(input.GetProperty("restaurantId").GetString()!, input.GetProperty("menuItemId").GetString()!, cancellationToken)),
                "get_fulfilment_options" => Ok(new { delivery = new { available = true, estimatedMinutes = 30 }, pickup = new { available = true, estimatedMinutes = 15 } }),
                "get_restaurant_quote" => Ok(await GetRestaurantQuoteAsync(input.Deserialize<RestaurantQuoteRequest>()!, cancellationToken)),
                "reserve" => Reserve(input),
                "release" => Release(input),
                "place_order" => PlaceOrder(input),
                "cancel_order" => CancelOrder(input),
                "get_order_status" => Status(input),
                _ => Fail("CAPABILITY_NOT_SUPPORTED")
            };
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            return Fail(ex.Message);
        }
    }

    public Task<IReadOnlyList<RestaurantSummary>> SearchRestaurantsAsync(RestaurantSearchRequest request, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<RestaurantSummary>>(Restaurants.Where(x => x.IsOpen
            && (request.FulfilmentType != RestaurantFulfilmentType.Delivery || x.SupportsDelivery)
            && (request.FulfilmentType != RestaurantFulfilmentType.Pickup || x.SupportsPickup)).ToArray());

    public Task<RestaurantMenu> GetMenuAsync(string restaurantId, CancellationToken ct = default) =>
        Task.FromResult(new RestaurantMenu(restaurantId, [new("mains", "Mains", Items.Where(x => x.RestaurantId == restaurantId).ToArray())]));

    public Task<IReadOnlyList<MenuItem>> SearchMenuAsync(string restaurantId, string query, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<MenuItem>>(Items.Where(x => x.RestaurantId == restaurantId && x.Available
            && (x.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || x.Description.Contains(query, StringComparison.OrdinalIgnoreCase)
                || x.DietaryTags.Any(tag => tag.Contains(query, StringComparison.OrdinalIgnoreCase)))).ToArray());

    public Task<MenuItem?> GetMenuItemAsync(string restaurantId, string menuItemId, CancellationToken ct = default) =>
        Task.FromResult(Items.FirstOrDefault(x => x.RestaurantId == restaurantId && x.MenuItemId == menuItemId));

    public Task<RestaurantOrderQuote> GetRestaurantQuoteAsync(RestaurantQuoteRequest request, CancellationToken ct = default)
    {
        var restaurant = Restaurants.Single(x => x.RestaurantId == request.RestaurantId);
        var subtotal = 0m;
        var modifiers = 0m;
        foreach (var requested in request.Items)
        {
            var item = Items.Single(x => x.RestaurantId == request.RestaurantId && x.MenuItemId == requested.MenuItemId);
            var failures = RestaurantModifierValidator.Validate(item, requested, new HashSet<string>());
            if (failures.Count > 0) throw new InvalidOperationException(string.Join(',', failures));
            subtotal += item.BasePrice * requested.Quantity;
            modifiers += item.ModifierGroups.SelectMany(x => x.Options)
                .Where(x => requested.SelectedModifierOptionIds.Contains(x.ModifierOptionId)).Sum(x => x.PriceDelta) * requested.Quantity;
        }
        if (subtotal < restaurant.MinimumOrderAmount) throw new InvalidOperationException("MINIMUM_ORDER_NOT_MET");
        var delivery = request.FulfilmentType == RestaurantFulfilmentType.Delivery ? 2.50m : 0m;
        var service = decimal.Round((subtotal + modifiers) * .05m, 2);
        var total = subtotal + modifiers + delivery + service;
        var quote = new RestaurantOrderQuote($"restaurant_quote_{Guid.NewGuid():N}", restaurant.RestaurantId, restaurant.Name, "GBP",
            request.Items, subtotal, modifiers, delivery, service, 0, 0, 0, total, request.FulfilmentType,
            DateTimeOffset.UtcNow.AddMinutes(15), request.FulfilmentType == RestaurantFulfilmentType.Delivery ? DateTimeOffset.UtcNow.AddMinutes(35) : null,
            DateTimeOffset.UtcNow.AddMinutes(5));
        _quotes[quote.QuoteId] = quote;
        return Task.FromResult(quote);
    }

    public Task<RestaurantOrderResult> PlaceOrderAsync(RestaurantOrderIntent intent, ServiceActionAuthorisation authorisation, CancellationToken ct = default)
    {
        if (!_authorisations.Verify(authorisation, DateTimeOffset.UtcNow) || authorisation.QuoteId != intent.QuoteId || authorisation.Amount != intent.TotalAmount)
            throw new InvalidOperationException("AUTHORISATION_INVALID");
        var result = _ordersByIntent.GetOrAdd(intent.IntentId, _ =>
        {
            PlaceOrderCount++;
            return new($"restaurant_order_{Guid.NewGuid():N}", RestaurantOrderStatus.Confirmed, $"confirmation_{Guid.NewGuid():N}", $"code-{Guid.NewGuid():N}"[..12]);
        });
        _ordersById[result.ProviderOrderId] = result;
        return Task.FromResult(result);
    }

    public Task<RestaurantCancellationResult> CancelOrderAsync(string providerOrderId, ServiceActionAuthorisation authorisation, CancellationToken ct = default)
    {
        if (!_authorisations.Verify(authorisation, DateTimeOffset.UtcNow)
            || authorisation.ProviderId != ProviderId
            || authorisation.Action != "cancel_order")
            throw new InvalidOperationException("AUTHORISATION_INVALID");
        if (!_ordersById.TryGetValue(providerOrderId, out var order)) throw new KeyNotFoundException("ORDER_NOT_FOUND");
        var cancelled = order with { Status = RestaurantOrderStatus.Cancelled };
        _ordersById[providerOrderId] = cancelled;
        return Task.FromResult(new RestaurantCancellationResult(providerOrderId, true, null, "GBP", order.ProviderReference));
    }

    public Task<RestaurantOrderResult?> GetOrderStatusAsync(string providerOrderId, CancellationToken ct = default) =>
        Task.FromResult(_ordersById.GetValueOrDefault(providerOrderId));

    public Task<IReadOnlyList<FulfilmentOption>> GetFulfilmentOptionsAsync(string orderIntentId, CancellationToken ct = default)
    { var now = DateTimeOffset.UtcNow; return Task.FromResult<IReadOnlyList<FulfilmentOption>>([new("restaurant-delivery", ProviderId, FulfilmentMode.MerchantDelivery, "Restaurant delivery", true, "GBP", 2.50m, EstimatedDeliveryAt: now.AddMinutes(35)), new("restaurant-pickup", ProviderId, FulfilmentMode.CustomerPickup, "Restaurant pickup", true, "GBP", .50m, EstimatedReadyAt: now.AddMinutes(15), ProviderReference: "restaurant-main"), new("restaurant-courier", "courier-demo", FulfilmentMode.ThirdPartyDelivery, "Courier delivery", true, "GBP", 4.50m, EstimatedDeliveryAt: now.AddMinutes(45))]); }
    public Task<FulfilmentQuote> GetFulfilmentQuoteAsync(FulfilmentQuoteRequest request, CancellationToken ct = default)
    { if (request.ProviderId != ProviderId || request.Mode == FulfilmentMode.ThirdPartyDelivery) throw new InvalidOperationException("Use the selected courier provider for third-party delivery quotes."); var delivery = request.Mode == FulfilmentMode.MerchantDelivery ? 2.50m : 0m; var service = request.Mode == FulfilmentMode.CustomerPickup ? .50m : 0m; return Task.FromResult(new FulfilmentQuote($"restaurant_fulfilment_quote_{Guid.NewGuid():N}", ProviderId, request.Mode, "GBP", delivery, service, 0, 0, delivery + service, null, request.Mode == FulfilmentMode.MerchantDelivery ? DateTimeOffset.UtcNow.AddMinutes(35) : null, DateTimeOffset.UtcNow.AddMinutes(5), $"restaurant_fulfilment_ref_{Guid.NewGuid():N}")); }

    private CapabilityInvocationResult Reserve(JsonElement input)
    {
        var quoteId = input.GetProperty("quoteId").GetString()!;
        if (!_quotes.ContainsKey(quoteId)) return Fail("QUOTE_NOT_FOUND");
        var id = $"restaurant_hold_{Guid.NewGuid():N}";
        _holds[id] = quoteId;
        return Ok(new { reservationId = id });
    }

    private CapabilityInvocationResult Release(JsonElement input)
    {
        _holds.TryRemove(input.GetProperty("reservationId").GetString()!, out _);
        return Ok(new { status = "released" });
    }

    private CapabilityInvocationResult PlaceOrder(JsonElement input)
    {
        var proposal = input.GetProperty("proposal").Deserialize<ServiceActionProposal>()!;
        var quote = input.GetProperty("quote").Deserialize<ServiceQuote>()!;
        var authorisation = input.GetProperty("authorisation").Deserialize<ServiceActionAuthorisation>()!;
        var providerQuote = _quotes.GetValueOrDefault(quote.QuoteId) ?? throw new InvalidOperationException("QUOTE_NOT_FOUND");
        if (providerQuote.ExpiresAt <= DateTimeOffset.UtcNow) return Fail("QUOTE_EXPIRED");
        var intent = new RestaurantOrderIntent(proposal.IdempotencyKey, proposal.PrincipalId, ProviderId, providerQuote.RestaurantId,
            quote.QuoteId, Hash(providerQuote), quote.Currency, quote.Total, providerQuote.Items, providerQuote.FulfilmentType,
            proposal.Configuration.TryGetProperty("deliveryAddressReference", out var address) ? address.GetString() : null,
            DateTimeOffset.UtcNow, quote.ExpiresAt);
        return Ok(PlaceOrderAsync(intent, authorisation).GetAwaiter().GetResult());
    }

    private CapabilityInvocationResult CancelOrder(JsonElement input) => Fail("SIGNED_CANCELLATION_REQUIRED");
    private CapabilityInvocationResult Status(JsonElement input) => _ordersById.TryGetValue(input.GetProperty("providerOrderId").GetString()!, out var result) ? Ok(result) : Fail("ORDER_NOT_FOUND");
    private static string Hash(object value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value))));
    private static CapabilityInvocationResult Ok(object? value) => new(true, JsonSerializer.SerializeToElement(value, JsonOptions));
    private static CapabilityInvocationResult Fail(string error) => new(false, JsonSerializer.SerializeToElement(new { error }), error);
}

public sealed class RestaurantDomainCapability : IServiceDomainCapability
{
    public string DomainId => "restaurant";
    public bool CanHandle(IServiceConnector provider, ServicePlanningContext context) =>
        new[] { "search_restaurants", "get_menu", "get_fulfilment_options", "get_restaurant_quote", "place_order" }
            .All(name => provider.Capabilities.Any(x => x.Name == name));

    public async Task<ServicePlan> PlanAsync(IServiceConnector provider, ServicePlanningContext context, CancellationToken cancellationToken = default)
    {
        var budget = ParseBudget(context.Instruction);
        var fulfilment = context.Instruction.Contains("pickup", StringComparison.OrdinalIgnoreCase)
            || context.Instruction.Contains("collect", StringComparison.OrdinalIgnoreCase)
            ? RestaurantFulfilmentType.Pickup : RestaurantFulfilmentType.Delivery;
        var restaurantsResult = await provider.InvokeAsync("search_restaurants", JsonSerializer.SerializeToElement(new RestaurantSearchRequest(context.Instruction, fulfilment)), cancellationToken);
        if (!restaurantsResult.Success) throw new InvalidOperationException(restaurantsResult.Error);
        var restaurant = restaurantsResult.Value.GetProperty("restaurants").EnumerateArray().FirstOrDefault();
        if (restaurant.ValueKind == JsonValueKind.Undefined) throw new InvalidOperationException("NO_RESTAURANT_AVAILABLE");
        var restaurantId = restaurant.GetProperty("restaurantId").GetString()!;
        var menuResult = await provider.InvokeAsync("get_menu", JsonSerializer.SerializeToElement(new { restaurantId }), cancellationToken);
        if (!menuResult.Success) throw new InvalidOperationException(menuResult.Error);
        var items = menuResult.Value.GetProperty("sections").EnumerateArray().SelectMany(x => x.GetProperty("items").EnumerateArray())
            .Where(x => x.GetProperty("available").GetBoolean()).ToArray();
        var vegetarianRequired = context.Instruction.Contains("vegetarian", StringComparison.OrdinalIgnoreCase);
        var selected = new List<JsonElement>();
        if (vegetarianRequired)
            selected.Add(items.Where(IsVegetarian).OrderBy(Price).FirstOrDefault());
        selected.Add(items.Where(x => !selected.Any(s => s.GetProperty("menuItemId").GetString() == x.GetProperty("menuItemId").GetString())).OrderBy(Price).First());
        selected.RemoveAll(x => x.ValueKind == JsonValueKind.Undefined);
        var orderItems = selected.Select(x => new RestaurantOrderItem(x.GetProperty("menuItemId").GetString()!, 1,
            SelectRequiredModifiers(x))).ToArray();
        var address = fulfilment == RestaurantFulfilmentType.Delivery ? "saved-address-default" : null;
        var quoteRequest = new RestaurantQuoteRequest(restaurantId, orderItems, fulfilment, address);
        var quoteResult = await provider.InvokeAsync("get_restaurant_quote", JsonSerializer.SerializeToElement(quoteRequest), cancellationToken);
        if (!quoteResult.Success) throw new InvalidOperationException(quoteResult.Error);
        var value = quoteResult.Value;
        var total = value.GetProperty("total").GetDecimal();
        var currency = value.GetProperty("currency").GetString()!;
        var quoteId = value.GetProperty("quoteId").GetString()!;
        var expiresAt = value.GetProperty("expiresAt").GetDateTimeOffset();
        var configuration = JsonSerializer.SerializeToElement(new { restaurantId, items = orderItems, fulfilmentType = fulfilment, deliveryAddressReference = address });
        var proposal = new ServiceActionProposal($"proposal_{Guid.NewGuid():N}", context.PrincipalId, context.AgentId, provider.ProviderId,
            "place_order", context.Instruction, configuration, budget, currency, StableId(context));
        return new(proposal, new ServiceQuote(quoteId, provider.ProviderId, "place_order", total, currency, expiresAt, value),
            ["search_restaurants", "get_menu", "get_fulfilment_options", "get_restaurant_quote"]);
    }

    private static decimal ParseBudget(string instruction)
    {
        var match = Regex.Match(instruction, @"(?:£|GBP\s*)(\d+(?:\.\d{1,2})?)", RegexOptions.IgnoreCase);
        return match.Success ? decimal.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : throw new ArgumentException("A maximum GBP budget is required.");
    }
    private static decimal Price(JsonElement item) => item.GetProperty("basePrice").GetDecimal();
    private static bool IsVegetarian(JsonElement item) => item.GetProperty("dietaryTags").EnumerateArray().Any(x => x.GetString() == "vegetarian");
    private static IReadOnlyList<string> SelectRequiredModifiers(JsonElement item) => item.GetProperty("modifierGroups").EnumerateArray()
        .Where(x => x.GetProperty("required").GetBoolean()).Select(x => x.GetProperty("options").EnumerateArray()
            .Where(o => o.GetProperty("available").GetBoolean()).OrderBy(o => o.GetProperty("priceDelta").GetDecimal()).First().GetProperty("modifierOptionId").GetString()!).ToArray();
    private static string StableId(ServicePlanningContext context) => $"restaurant_{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{context.PrincipalId}|{context.ProviderId}|{context.Instruction}"))).ToLowerInvariant()[..32]}";
}
