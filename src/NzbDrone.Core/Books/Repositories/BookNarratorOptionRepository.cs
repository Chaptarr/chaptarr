using System.Collections.Generic;
using System.Data;
using System.Linq;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.Books.Repositories
{
    public interface IBookNarratorOptionRepository : IBasicRepository<BookNarratorOption>
    {
        List<BookNarratorOption> GetByBookId(int bookId);
        List<BookNarratorOption> GetPreferredByBookId(int bookId);
        BookNarratorOption FindByBookIdAndNarrator(int bookId, string narrator);
        void DeleteByBookId(int bookId);
        void SetPreferred(int bookId, string narrator);
        void ClearPreferred(int bookId);
    }

    public class BookNarratorOptionRepository : BasicRepository<BookNarratorOption>, IBookNarratorOptionRepository
    {
        public BookNarratorOptionRepository(IMainDatabase database, IEventAggregator eventAggregator)
            : base(database, eventAggregator)
        {
        }

        public List<BookNarratorOption> GetByBookId(int bookId)
        {
            return Query(x => x.BookId == bookId)
                .OrderByDescending(x => x.IsPreferred)
                .ThenBy(x => x.Narrator)
                .ToList();
        }

        public List<BookNarratorOption> GetPreferredByBookId(int bookId)
        {
            return Query(x => x.BookId == bookId && x.IsPreferred)
                .ToList();
        }

        public BookNarratorOption FindByBookIdAndNarrator(int bookId, string narrator)
        {
            return Query(x => x.BookId == bookId && x.Narrator == narrator)
                .FirstOrDefault();
        }

        public void DeleteByBookId(int bookId)
        {
            Delete(x => x.BookId == bookId);
        }

        public void SetPreferred(int bookId, string narrator)
        {
            using (var conn = _database.OpenConnection())
            {
                using (var tran = conn.BeginTransaction(IsolationLevel.ReadCommitted))
                {
                    ClearPreferred(bookId);

                    var option = FindByBookIdAndNarrator(bookId, narrator);
                    if (option != null)
                    {
                        option.IsPreferred = true;
                        Update(option);
                    }

                    tran.Commit();
                }
            }
        }

        public void ClearPreferred(int bookId)
        {
            var preferredOptions = GetPreferredByBookId(bookId);
            foreach (var option in preferredOptions)
            {
                option.IsPreferred = false;
                Update(option);
            }
        }
    }
}
