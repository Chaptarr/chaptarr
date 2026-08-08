using System;

namespace NzbDrone.Core.Books.Services
{
    public sealed class BulkAuthorRefreshProgressState
    {
        public BulkAuthorRefreshProgressState(int currentAuthorIndex, int totalAuthors)
        {
            CurrentAuthorIndex = currentAuthorIndex;
            TotalAuthors = totalAuthors;
        }

        public int CurrentAuthorIndex { get; }
        public int TotalAuthors { get; }
    }

    public static class BulkAuthorRefreshProgressContext
    {
        [ThreadStatic]
        private static BulkAuthorRefreshProgressState _current;

        public static BulkAuthorRefreshProgressState Current => _current;

        public static IDisposable Begin(int currentAuthorIndex, int totalAuthors)
        {
            var previous = _current;
            _current = new BulkAuthorRefreshProgressState(currentAuthorIndex, totalAuthors);
            return new Restore(previous);
        }

        private sealed class Restore : IDisposable
        {
            private readonly BulkAuthorRefreshProgressState _previous;

            public Restore(BulkAuthorRefreshProgressState previous)
            {
                _previous = previous;
            }

            public void Dispose()
            {
                _current = _previous;
            }
        }
    }
}
