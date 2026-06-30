namespace ServiceMaintenance.Models
{
    public class MenuItem
    {
        public string Text { get; set; }
        public string NavigateUrl { get; set; }
        public string IconCssClass { get; set; }
        public bool Expanded { get; set; }
        public List<MenuItem> SubMenuItems { get; set; } = new();
        public List<string> AllowedRoles { get; set; } = new();
        public string BadgeText { get; set; }
    }

}
