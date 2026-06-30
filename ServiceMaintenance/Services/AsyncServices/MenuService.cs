using Microsoft.Extensions.Localization;
using ServiceMaintenance;
using ServiceMaintenance.Models;

namespace ServiceMaintenance.Services.AsyncServices
{
    public class MenuService
    {
        private readonly IStringLocalizer<App> _loc;

        public MenuService(IStringLocalizer<App> loc)
        {
            _loc = loc;
        }

        public List<MenuItem> GetMenuItems(IEnumerable<string> userRoles)
        {
            var menuItems = new List<MenuItem>
            {
                new MenuItem
                {
                    Text = _loc[nameof(ResourceStrings.TextHome)],
                    NavigateUrl = "/",
                    IconCssClass = "fas fa-home",
                    AllowedRoles = new List<string> { "Admin", "User", "Sales", "SuperAdmin", "Engineer", "Stock" }
                },

                // ─── INVENTORY ITEMS ───────────────────────────────
                new MenuItem
                {
                    Text = "Inventory Items",
                    IconCssClass = "fas fa-warehouse",
                    Expanded = true,
                    AllowedRoles = new List<string> { "Admin", "User", "Sales", "SuperAdmin", "Engineer", "Stock" }, // all roles
                    SubMenuItems = new List<MenuItem>
                    {
                        new MenuItem
                        {
                            Text = _loc[nameof(ResourceStrings.TextItemlists)],
                            IconCssClass = "fas fa-clipboard-list",
                            Expanded = true,
                            SubMenuItems = new List<MenuItem>
                            {
                                new MenuItem { Text = _loc[nameof(ResourceStrings.TextReceiveItemsInventory)],   NavigateUrl = "/itemModel",              IconCssClass = "fas fa-wrench" },
                                new MenuItem { Text = _loc[nameof(ResourceStrings.TextSparePartItemsInventory)], NavigateUrl = "/SparePart Inventory Page", IconCssClass = "fas fa-cogs" }
                            }
                        }
                    }
                },

                // ─── TECHNICAL ─────────────────────────────────────
                new MenuItem
                {
                    Text = "Technical",
                    IconCssClass = "fas fa-tools",
                    Expanded = true,
                    AllowedRoles = new List<string> { "Admin", "User", "SuperAdmin", "Engineer" },
                    SubMenuItems = new List<MenuItem>
                    {
                        new MenuItem
                        {
                            Text = _loc[nameof(ResourceStrings.TextRepairServiceTracker)],
                            IconCssClass = "fas fa-tools",
                            Expanded = true,
                            SubMenuItems = new List<MenuItem>
                            {
                                new MenuItem { Text = _loc[nameof(ResourceStrings.TextReceivedItems)],    NavigateUrl = "/receive-item",    IconCssClass = "fas fa-box" },
                                new MenuItem { Text = _loc[nameof(ResourceStrings.TextInspectItem)],      NavigateUrl = "/inspect-item",    IconCssClass = "fas fa-search-plus" },
                                new MenuItem { Text = _loc[nameof(ResourceStrings.TextInspection)],       NavigateUrl = "/inspection-list", IconCssClass = "fas fa-clipboard-check" },
                                new MenuItem { Text = _loc[nameof(ResourceStrings.TextApproveRepairing)], NavigateUrl = "/reapairItem",     IconCssClass = "fas fa-wrench" },
                                new MenuItem { Text = _loc[nameof(ResourceStrings.TextApproveVerify)],    NavigateUrl = "/finishItem",      IconCssClass = "fas fa-clipboard-check" },
                            }
                        },
                        //new MenuItem
                        //{
                        //    Text = _loc[nameof(ResourceStrings.TextInbox)],
                        //    IconCssClass = "fas fa-inbox",
                        //    Expanded = true,
                        //    SubMenuItems = new List<MenuItem>
                        //    {
                        //        new MenuItem { Text = _loc[nameof(ResourceStrings.TextMessageCenter)], NavigateUrl = "/chat", IconCssClass = "fas fa-envelope" }
                        //    }
                        //},
                        new MenuItem
                        {
                            Text = _loc[nameof(ResourceStrings.TextcannotRepair)],
                            IconCssClass = "fas fa-ban",
                            Expanded = true,
                            SubMenuItems = new List<MenuItem>
                            {
                                new MenuItem { Text = _loc[nameof(ResourceStrings.TextCusrej)],       NavigateUrl = "/customer-reject", IconCssClass = "fas fa-ban" },
                                new MenuItem { Text = _loc[nameof(ResourceStrings.TextUnrepairable)], NavigateUrl = "/unrepairable",    IconCssClass = "fas fa-ban" },
                            }
                        },
                    }
                },

                // ─── STOCK ─────────────────────────────────────────
                new MenuItem
                {
                    Text = "Stock",
                    IconCssClass = "fas fa-boxes",
                    Expanded = true,
                    AllowedRoles = new List<string> { "Admin", "User", "SuperAdmin", "Stock" },
                    SubMenuItems = new List<MenuItem>
                    {
                        new MenuItem
                        {
                            Text = _loc[nameof(ResourceStrings.TextRepairServiceTracker)],
                            IconCssClass = "fas fa-tools",
                            Expanded = true,
                            SubMenuItems = new List<MenuItem>
                            {
                                new MenuItem
                                {
                                    Text = _loc[nameof(ResourceStrings.TextSetWaitSparePart)],
                                    NavigateUrl = "/await-sparepart",
                                    IconCssClass = "fas fa-cogs",
                                },
                            }
                        },
                    }
                },

                // ─── SALE ──────────────────────────────────────────
                new MenuItem
                {
                    Text = "Sale",
                    IconCssClass = "fas fa-tag",
                    Expanded = true,
                    AllowedRoles = new List<string> { "Admin", "User", "Sales", "SuperAdmin" },
                    SubMenuItems = new List<MenuItem>
                    {
                      
                       
                        new MenuItem
                        {
                            Text = _loc[nameof(ResourceStrings.TextRepairServiceTracker)],
                            IconCssClass = "fas fa-tools",
                            Expanded = true,
                            SubMenuItems = new List<MenuItem>
                            {
                                new MenuItem
                                {
                                    Text = _loc[nameof(ResourceStrings.TextSetWaitCustomer)],
                                    NavigateUrl = "/await-customer",
                                    IconCssClass = "fas fa-user-clock",
                                },
                            }
                        },
                          new MenuItem
                        {
                            Text = _loc[nameof(ResourceStrings.TextcannotRepair)],
                            IconCssClass = "fas fa-ban",
                            Expanded = true,
                            SubMenuItems = new List<MenuItem>
                            {
                                new MenuItem { Text = _loc[nameof(ResourceStrings.TextCusrej)],       NavigateUrl = "/customer-reject", IconCssClass = "fas fa-ban" },
                                new MenuItem { Text = _loc[nameof(ResourceStrings.TextUnrepairable)], NavigateUrl = "/unrepairable",    IconCssClass = "fas fa-ban" },
                            }
                        },
                    }
                },

              // ─── REPORTS ───────────────────────────────────────
                new MenuItem
                {
                    Text = _loc[nameof(ResourceStrings.TextReports)],
                    IconCssClass = "fas fa-file-alt",
                    Expanded = false,   // ← changed
                    AllowedRoles = new List<string> { "Admin", "User", "Sales", "SuperAdmin", "Engineer", "Stock" },
                    SubMenuItems = new List<MenuItem>
                    {
                        new MenuItem { Text = _loc[nameof(ResourceStrings.TextDailyandCheck)],  NavigateUrl = "daily-report",     IconCssClass = "fas fa-calendar-day" },
                        new MenuItem { Text = _loc[nameof(ResourceStrings.TextMonthlyReport)],  NavigateUrl = "monthly-report",   IconCssClass = "fas fa-chart-line" },
                        new MenuItem { Text = _loc[nameof(ResourceStrings.TextCustomerReport)], NavigateUrl = "customer-report",  IconCssClass = "fas fa-users" },
                        new MenuItem { Text = _loc[nameof(ResourceStrings.TextRepairHistory)],  NavigateUrl = "history-report",   IconCssClass = "fas fa-tools" },
                        new MenuItem { Text = _loc[nameof(ResourceStrings.TextServiceReport)],  NavigateUrl = "/repairservices",  IconCssClass = "fas fa-toolbox" },
                        new MenuItem { Text = _loc[nameof(ResourceStrings.TextEngineerReport)], NavigateUrl = "/engineer-report", IconCssClass = "fas fa-user-tie" },
                        new MenuItem { Text = _loc[nameof(ResourceStrings.TextSummaryReport)],  NavigateUrl = "/repair-report",   IconCssClass = "fas fa-chart-bar" },
                        new MenuItem { Text = _loc[nameof(ResourceStrings.Textusagesparepart)], NavigateUrl = "/sparepart-usage", IconCssClass = "fas fa-tools" },
                        new MenuItem { Text = _loc[nameof(ResourceStrings.Testholdsparepart)],  NavigateUrl = "/sparepart-hold",  IconCssClass = "fas fa-hand-paper" },
                    }
                },

                // ─── AUTHENTICATION ────────────────────────────────
                new MenuItem
                {
                    Text = _loc[nameof(ResourceStrings.TextAuth)],
                    IconCssClass = "fas fa-lock",
                    Expanded = false,   // ← changed
                    AllowedRoles = new List<string> { "SuperAdmin" },
                    SubMenuItems = new List<MenuItem>
                    {
                        new MenuItem { Text = _loc[nameof(ResourceStrings.TextUserAssign)],     NavigateUrl = "users",              IconCssClass = "fas fa-user-tag" },
                        new MenuItem { Text = _loc[nameof(ResourceStrings.TextRolePermission)], NavigateUrl = "manage-permissions", IconCssClass = "fas fa-user-shield" }
                    }
                }
            };

            return menuItems
                .Where(m => m.AllowedRoles.Count == 0 || userRoles.Intersect(m.AllowedRoles).Any())
                .ToList();
        }
    }
}