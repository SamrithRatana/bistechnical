namespace ServiceMaintenance.Infrastructure.Shared.Caching;

public static class CacheKeys
{
    // ── Dashboard ──────────────────────────────────
    public const string DashboardStats = "dashboard:statistics";

    // ── Prefix constants — សម្រាប់ InvalidateByPrefixAsync ──
    public const string PrefixReceiveItem = "receiveitem:";
    public const string PrefixInspectItem = "inspectitem:";
    public const string PrefixInspectionItem = "inspectionitem:";
    public const string PrefixRepairItem = "repairitem:";
    public const string PrefixAwaitCustomer = "awaitcustomer:";
    public const string PrefixAwaitSparePart = "awaitspare:";
    public const string PrefixFinishRepair = "finishrepair:";
    public const string PrefixCustomerReject = "customerreject:";
    public const string PrefixUnRepairable = "unrepairable:";
    public const string PrefixThirdParty = "thirdparty:";
    public const string PrefixSparePart = "sparepart:";
    public const string PrefixItemModule = "itemmodule:";
    public const string PrefixRentalInventory = "rentalinventory:";
    public const string PrefixRentalLogBook = "rentallogbook:";

    // ── Per-page keys — សម្រាប់ GetOrSetAsync / InvalidateManyAsync ──
    public static string ReceiveItem(int page, int size)
        => $"receiveitem:page{page}:size{size}";

    public static string InspectItem(int page, int size)
        => $"inspectitem:page{page}:size{size}";

    public static string InspectionItem(int page, int size)
        => $"inspectionitem:page{page}:size{size}";

    public static string RepairItem(int page, int size)
        => $"repairitem:page{page}:size{size}";

    public static string AwaitCustomer(int page, int size)
        => $"awaitcustomer:page{page}:size{size}";

    public static string AwaitSparePart(int page, int size)
        => $"awaitspare:page{page}:size{size}";

    public static string FinishRepair(int page, int size)
        => $"finishrepair:page{page}:size{size}";

    public static string CustomerReject(int page, int size)
        => $"customerreject:page{page}:size{size}";

    public static string UnRepairable(int page, int size)
        => $"unrepairable:page{page}:size{size}";

    public static string ThirdParty(int page, int size)
        => $"thirdparty:page{page}:size{size}";

    public static string SparePart(int page, int size)
        => $"sparepart:page{page}:size{size}";

    public static string ItemModule(int page, int size)
        => $"itemmodule:page{page}:size{size}";

    public static string RentalInventory(int page, int size)
        => $"rentalinventory:page{page}:size{size}";

    public static string RentalLogBook(int page, int size)
        => $"rentallogbook:page{page}:size{size}";
}