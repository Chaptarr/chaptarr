namespace NzbDrone.Core.DecisionEngine
{
    public class Rejection
    {
        public string Reason { get; set; }
        public RejectionType Type { get; set; }

        // Enhanced properties for filter warning system
        public bool CanBypass { get; set; }
        public string Category { get; set; }
        public int Severity { get; set; }

        public Rejection(string reason, RejectionType type = RejectionType.Permanent)
        {
            Reason = reason;
            Type = type;
            CanBypass = type == RejectionType.Temporary; // Default: temporary rejections can be bypassed
            Category = "General"; // Default category
            Severity = type == RejectionType.Permanent ? 3 : 2; // Default severity: 3=Error (Permanent), 2=Warning (Temporary)
        }

        // Enhanced constructor for filter warning system
        public Rejection(string reason, RejectionType type, bool canBypass, string category = "General", int severity = 2)
        {
            Reason = reason;
            Type = type;
            CanBypass = canBypass;
            Category = category;
            Severity = severity;
        }

        public override string ToString()
        {
            return string.Format("[{0}] {1}", Type, Reason);
        }

        // Helper properties for filter warning system
        public bool IsHardFilter => Type == RejectionType.Permanent && !CanBypass;
        public bool IsSoftFilter => Type == RejectionType.Temporary || CanBypass;
        public bool IsLanguageFilter => Category.Equals("Language", System.StringComparison.OrdinalIgnoreCase);
        public bool IsQualityFilter => Category.Equals("Quality", System.StringComparison.OrdinalIgnoreCase);
    }
}
