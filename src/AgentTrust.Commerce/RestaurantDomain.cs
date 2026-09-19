namespace AgentTrust.Commerce;

public enum RestaurantFulfilmentType { Delivery, Pickup }
public enum RestaurantOrderStatus { Pending, Confirmed, Preparing, ReadyForPickup, OutForDelivery, Delivered, Cancelled, Rejected, Failed, Unknown }

public sealed record RestaurantSearchRequest(string Query, RestaurantFulfilmentType? FulfilmentType = null);
public sealed record RestaurantSummary(string RestaurantId, string Name, IReadOnlySet<string> CuisineTypes, bool IsOpen,
    bool SupportsDelivery, bool SupportsPickup, decimal MinimumOrderAmount, decimal? Rating, int? EstimatedDeliveryMinutes,
    IReadOnlySet<string> Tags);
public sealed record ModifierOption(string ModifierOptionId, string Name, decimal PriceDelta, bool Available,
    IReadOnlySet<string>? DietaryTags = null, IReadOnlySet<string>? Allergens = null);
public sealed record ModifierGroup(string ModifierGroupId, string Name, bool Required, int MinimumSelections,
    int MaximumSelections, IReadOnlyList<ModifierOption> Options);
public sealed record MenuItem(string MenuItemId, string RestaurantId, string Name, string Description, decimal BasePrice,
    string Currency, bool Available, IReadOnlySet<string> DietaryTags, IReadOnlySet<string> Allergens,
    IReadOnlyList<ModifierGroup> ModifierGroups, int? Calories = null);
public sealed record MenuSection(string SectionId, string Name, IReadOnlyList<MenuItem> Items);
public sealed record RestaurantMenu(string RestaurantId, IReadOnlyList<MenuSection> Sections);
public sealed record RestaurantOrderItem(string MenuItemId, int Quantity, IReadOnlyList<string> SelectedModifierOptionIds,
    string? SpecialInstructions = null);
public sealed record RestaurantQuoteRequest(string RestaurantId, IReadOnlyList<RestaurantOrderItem> Items,
    RestaurantFulfilmentType FulfilmentType, string? DeliveryAddressReference = null, string? PickupLocationId = null,
    DateTimeOffset? RequestedTime = null);
public sealed record RestaurantOrderQuote(string QuoteId, string RestaurantId, string RestaurantName, string Currency,
    IReadOnlyList<RestaurantOrderItem> Items, decimal Subtotal, decimal ModifierTotal, decimal DeliveryFee,
    decimal ServiceFee, decimal Tax, decimal Discount, decimal Tip, decimal Total,
    RestaurantFulfilmentType FulfilmentType, DateTimeOffset EstimatedReadyAt,
    DateTimeOffset? EstimatedDeliveryAt, DateTimeOffset ExpiresAt, string? ProviderReference = null);
public sealed record RestaurantOrderIntent(string IntentId, string PrincipalId, string ProviderId, string RestaurantId,
    string QuoteId, string QuoteHash, string Currency, decimal TotalAmount, IReadOnlyList<RestaurantOrderItem> Items,
    RestaurantFulfilmentType FulfilmentType, string? DeliveryAddressReference, DateTimeOffset CreatedAt,
    DateTimeOffset QuoteExpiresAt);
public sealed record RestaurantOrderResult(string ProviderOrderId, RestaurantOrderStatus Status, string ProviderReference,
    string? ConfirmationCode = null, DateTimeOffset? EstimatedReadyAt = null,
    DateTimeOffset? EstimatedDeliveryAt = null, string? FailureReason = null);
public sealed record RestaurantCancellationResult(string ProviderOrderId, bool Cancelled, decimal? RefundAmount,
    string? Currency, string ProviderReference, string? FailureReason = null);

public interface IRestaurantSearchCapability
{
    Task<IReadOnlyList<RestaurantSummary>> SearchRestaurantsAsync(RestaurantSearchRequest request, CancellationToken ct = default);
}
public interface IRestaurantMenuCapability
{
    Task<RestaurantMenu> GetMenuAsync(string restaurantId, CancellationToken ct = default);
    Task<IReadOnlyList<MenuItem>> SearchMenuAsync(string restaurantId, string query, CancellationToken ct = default);
    Task<MenuItem?> GetMenuItemAsync(string restaurantId, string menuItemId, CancellationToken ct = default);
}
public interface IRestaurantQuoteCapability
{
    Task<RestaurantOrderQuote> GetRestaurantQuoteAsync(RestaurantQuoteRequest request, CancellationToken ct = default);
}
public interface IRestaurantOrderCapability
{
    Task<RestaurantOrderResult> PlaceOrderAsync(RestaurantOrderIntent intent, ServiceActionAuthorisation authorisation, CancellationToken ct = default);
    Task<RestaurantCancellationResult> CancelOrderAsync(string providerOrderId, ServiceActionAuthorisation authorisation, CancellationToken ct = default);
    Task<RestaurantOrderResult?> GetOrderStatusAsync(string providerOrderId, CancellationToken ct = default);
}

public static class RestaurantModifierValidator
{
    public static IReadOnlyList<string> Validate(MenuItem item, RestaurantOrderItem orderItem, IReadOnlySet<string> prohibitedAllergens)
    {
        var failures = new List<string>();
        if (!item.Available) failures.Add("MENU_ITEM_UNAVAILABLE");
        if (orderItem.Quantity <= 0) failures.Add("INVALID_QUANTITY");
        if (item.Allergens.Overlaps(prohibitedAllergens)) failures.Add("ALLERGEN_CONFLICT");
        var selected = orderItem.SelectedModifierOptionIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var group in item.ModifierGroups)
        {
            var choices = group.Options.Where(x => selected.Contains(x.ModifierOptionId)).ToArray();
            if (choices.Length < group.MinimumSelections || (group.Required && choices.Length == 0)) failures.Add($"MODIFIER_REQUIRED:{group.ModifierGroupId}");
            if (choices.Length > group.MaximumSelections) failures.Add($"TOO_MANY_MODIFIERS:{group.ModifierGroupId}");
            if (choices.Any(x => !x.Available)) failures.Add($"MODIFIER_UNAVAILABLE:{group.ModifierGroupId}");
            if (choices.Any(x => (x.Allergens ?? new HashSet<string>()).Overlaps(prohibitedAllergens))) failures.Add("ALLERGEN_CONFLICT");
        }
        if (selected.Any(id => item.ModifierGroups.SelectMany(x => x.Options).All(x => !x.ModifierOptionId.Equals(id, StringComparison.OrdinalIgnoreCase)))) failures.Add("UNKNOWN_MODIFIER");
        return failures;
    }
}
