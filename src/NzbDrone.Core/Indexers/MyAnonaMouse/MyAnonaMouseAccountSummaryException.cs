using System;

namespace NzbDrone.Core.Indexers.MyAnonaMouse
{
    internal class MyAnonaMouseAccountSummaryException : InvalidOperationException
    {
        public MyAnonaMouseAccountSummaryException(string message)
            : base(message)
        {
        }
    }
}
