using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace NzbDrone.Common
{
    public class NaturalSortComparer : IComparer<string>
    {
        public static readonly NaturalSortComparer Instance = new NaturalSortComparer();

        private static readonly Regex NumericSegment = new Regex(@"\d+", RegexOptions.Compiled);

        private NaturalSortComparer()
        {
        }

        public int Compare(string x, string y)
        {
            if (ReferenceEquals(x, y))
            {
                return 0;
            }

            if (x == null)
            {
                return -1;
            }

            if (y == null)
            {
                return 1;
            }

            return string.Compare(
                NumericSegment.Replace(x, m => m.Value.PadLeft(10, '0')),
                NumericSegment.Replace(y, m => m.Value.PadLeft(10, '0')),
                StringComparison.OrdinalIgnoreCase);
        }
    }
}
