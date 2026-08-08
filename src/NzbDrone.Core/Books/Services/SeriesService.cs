using System.Collections.Generic;
using System.Data;
using System.Linq;
using NLog;

namespace NzbDrone.Core.Books
{
    public interface ISeriesService
    {
        Series GetSeries(int seriesId);
        Series FindById(string foreignSeriesId);
        Series FindById(string foreignSeriesId, BookMediaType mediaType);
        List<Series> FindById(List<string> foreignSeriesId);
        List<Series> FindById(List<string> foreignSeriesId, BookMediaType mediaType);
        List<Series> GetByAuthorId(int authorId);
        List<Series> GetAllSeries();
        Series AddSeries(Series series);
        void Delete(int seriesId);
        void InsertMany(IList<Series> series);
        void InsertMany(IList<Series> series, IDbConnection connection, IDbTransaction transaction);
        void UpdateMany(IList<Series> series);
    }

    public class SeriesService : ISeriesService
    {
        private readonly ISeriesRepository _seriesRepository;
        private readonly Logger _logger;

        public SeriesService(ISeriesRepository seriesRepository, Logger logger)
        {
            _seriesRepository = seriesRepository;
            _logger = logger;
        }

        public Series GetSeries(int seriesId)
        {
            return _seriesRepository.Get(seriesId);
        }

        public Series FindById(string foreignSeriesId)
        {
            return _seriesRepository.FindById(foreignSeriesId);
        }

        public Series FindById(string foreignSeriesId, BookMediaType mediaType)
        {
            return _seriesRepository.FindById(foreignSeriesId, mediaType);
        }

        public List<Series> FindById(List<string> foreignSeriesId)
        {
            return _seriesRepository.FindById(foreignSeriesId);
        }

        public List<Series> FindById(List<string> foreignSeriesId, BookMediaType mediaType)
        {
            return _seriesRepository.FindById(foreignSeriesId, mediaType);
        }


        public List<Series> GetByAuthorId(int authorId)
        {
            return _seriesRepository.GetByAuthorId(authorId);
        }

        public List<Series> GetAllSeries()
        {
            return _seriesRepository.GetAllSeriesWithoutBooks();
        }

        public Series AddSeries(Series series)
        {
            // LocalSeriesId generation removed - using database IDs directly

            return _seriesRepository.Insert(series);
        }

        public void Delete(int seriesId)
        {
            _seriesRepository.Delete(seriesId);
        }

        public void InsertMany(IList<Series> series)
        {
            // LocalSeriesId generation removed - using database IDs directly

            _seriesRepository.InsertMany(series);
        }

        public void InsertMany(IList<Series> series, IDbConnection connection, IDbTransaction transaction)
        {
            // LocalSeriesId generation removed - using database IDs directly

            _seriesRepository.InsertMany(series, connection, transaction);
        }

        public void UpdateMany(IList<Series> series)
        {
            _seriesRepository.UpdateMany(series);
        }
    }
}
