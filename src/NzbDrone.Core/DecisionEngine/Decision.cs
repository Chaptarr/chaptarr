namespace NzbDrone.Core.DecisionEngine
{
    public class Decision
    {
        public bool Accepted { get; private set; }
        public string Reason { get; private set; }

        // Enhanced properties for filter warning system
        public bool CanBypass { get; private set; }
        public string Category { get; private set; }
        public int Severity { get; private set; }

        private static readonly Decision AcceptDecision = new Decision { Accepted = true };
        private Decision()
        {
        }

        public static Decision Accept()
        {
            return AcceptDecision;
        }

        public static Decision Reject(string reason, params object[] args)
        {
            return Reject(string.Format(reason, args));
        }

        public static Decision Reject(string reason)
        {
            return new Decision
            {
                Accepted = false,
                Reason = reason,
                CanBypass = false, // Default to hard filter for backward compatibility
                Category = "General",
                Severity = 3 // Error level
            };
        }

        // Enhanced rejection methods for filter warning system
        public static Decision RejectHardFilter(string reason, string category = "General")
        {
            return new Decision
            {
                Accepted = false,
                Reason = reason,
                CanBypass = false, // Hard filters cannot be bypassed
                Category = category,
                Severity = 3 // Error level
            };
        }

        public static Decision RejectSoftFilter(string reason, string category = "General")
        {
            return new Decision
            {
                Accepted = false,
                Reason = reason,
                CanBypass = true, // Soft filters can be bypassed
                Category = category,
                Severity = 2 // Warning level
            };
        }

        public static Decision RejectHardFilter(string reason, string category, params object[] args)
        {
            return RejectHardFilter(string.Format(reason, args), category);
        }

        public static Decision RejectSoftFilter(string reason, string category, params object[] args)
        {
            return RejectSoftFilter(string.Format(reason, args), category);
        }
    }
}
