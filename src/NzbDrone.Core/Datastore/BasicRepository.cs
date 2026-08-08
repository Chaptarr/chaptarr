using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using Dapper;
using NzbDrone.Core.Datastore.Events;
using NzbDrone.Core.Messaging.Events;
using static Dapper.SqlMapper;

namespace NzbDrone.Core.Datastore
{
    public interface IBasicRepository<TModel>
        where TModel : ModelBase, new()
    {
        IEnumerable<TModel> All();
        int Count();
        TModel Find(int id);
        TModel Get(int id);
        TModel Insert(TModel model);
        TModel Update(TModel model);
        TModel Upsert(TModel model);
        void SetFields(TModel model, params Expression<Func<TModel, object>>[] properties);
        void Delete(TModel model);
        void Delete(int id);
        IEnumerable<TModel> Get(IEnumerable<int> ids);
        void InsertMany(IList<TModel> model);
        void InsertMany(IList<TModel> model, IDbConnection connection, IDbTransaction transaction);
        void UpdateMany(IList<TModel> model);
        void SetFields(IList<TModel> models, params Expression<Func<TModel, object>>[] properties);
        void DeleteMany(List<TModel> model);
        void DeleteMany(IEnumerable<int> ids);
        void Purge(bool vacuum = false);
        bool HasItems();
        TModel Single();
        TModel SingleOrDefault();
        PagingSpec<TModel> GetPaged(PagingSpec<TModel> pagingSpec);
    }

    public class BasicRepository<TModel> : IBasicRepository<TModel>
        where TModel : ModelBase, new()
    {
        private readonly IEventAggregator _eventAggregator;
        private readonly PropertyInfo _keyProperty;
        private readonly List<PropertyInfo> _properties;
        private readonly string _updateSql;
        private readonly string _insertSql;

        protected readonly IDatabase _database;
        protected readonly string _table;

        public BasicRepository(IDatabase database, IEventAggregator eventAggregator)
        {
            _database = database;
            _eventAggregator = eventAggregator;

            var type = typeof(TModel);

            _table = TableMapping.Mapper.TableNameMapping(type);
            _keyProperty = type.GetProperty(nameof(ModelBase.Id));

            var excluded = TableMapping.Mapper.ExcludeProperties(type).Select(x => x.Name).ToList();
            excluded.Add(_keyProperty.Name);
            _properties = type.GetProperties().Where(x => x.IsMappableProperty() && !excluded.Contains(x.Name)).ToList();

            _insertSql = GetInsertSql();
            _updateSql = GetUpdateSql(_properties);
        }

        protected virtual SqlBuilder Builder() => new SqlBuilder(_database.DatabaseType);

        protected virtual List<PropertyInfo> GetUpdateProperties() => _properties;

        protected virtual List<TModel> Query(SqlBuilder builder) => _database.Query<TModel>(builder).ToList();

        protected virtual List<TModel> QueryDistinct(SqlBuilder builder) => _database.QueryDistinct<TModel>(builder).ToList();

        protected List<TModel> Query(Expression<Func<TModel, bool>> where) => Query(Builder().Where(where));

        public int Count()
        {
            using (var conn = _database.OpenConnection())
            {
                return conn.ExecuteScalar<int>($"SELECT COUNT(*) FROM \"{_table}\"");
            }
        }

        public virtual IEnumerable<TModel> All()
        {
            return Query(Builder());
        }

        public TModel Find(int id)
        {
            var model = Query(x => x.Id == id).FirstOrDefault();

            return model;
        }

        public TModel Get(int id)
        {
            var model = Find(id);

            if (model == null)
            {
                throw new ModelNotFoundException(typeof(TModel), id);
            }

            return model;
        }

        public IEnumerable<TModel> Get(IEnumerable<int> ids)
        {
            var idList = ids?
                .Distinct()
                .ToList() ?? new List<int>();
            var result = FindMany(idList);

            if (result.Count != idList.Count)
            {
                throw new ApplicationException($"Expected query to return {idList.Count} rows but returned {result.Count}");
            }

            return result;
        }

        protected List<TModel> FindMany(IEnumerable<int> ids)
        {
            if (ids == null)
            {
                return new List<TModel>();
            }

            var idList = ids.Distinct().ToList();
            if (!idList.Any())
            {
                return new List<TModel>();
            }

            List<TModel> result;
            if (_database.DatabaseType == DatabaseType.SQLite && idList.Count > SqliteVariableLimit.MaxParameters)
            {
                result = new List<TModel>();
                foreach (var batch in idList.Chunk(SqliteVariableLimit.MaxParameters))
                {
                    var batchIds = batch.ToArray();
                    result.AddRange(Query(x => Enumerable.Contains(batchIds, x.Id)));
                }

                result = result.DistinctBy(m => m.Id).ToList();
            }
            else
            {
                result = Query(x => idList.Contains(x.Id));
            }

            return result;
        }

        public TModel SingleOrDefault()
        {
            return All().SingleOrDefault();
        }

        public TModel Single()
        {
            return All().Single();
        }

        public TModel Insert(TModel model)
        {
            if (model.Id != 0)
            {
                throw new InvalidOperationException("Can't insert model with existing ID " + model.Id);
            }

            using (var conn = _database.OpenConnection())
            {
                model = Insert(conn, null, model);
            }

            ModelCreated(model);

            return model;
        }

        private string GetInsertSql()
        {
            var sbColumnList = new StringBuilder(null);
            for (var i = 0; i < _properties.Count; i++)
            {
                var property = _properties[i];
                sbColumnList.AppendFormat("\"{0}\"", property.Name);
                if (i < _properties.Count - 1)
                {
                    sbColumnList.Append(", ");
                }
            }

            var sbParameterList = new StringBuilder(null);
            for (var i = 0; i < _properties.Count; i++)
            {
                var property = _properties[i];
                sbParameterList.AppendFormat("@{0}", property.Name);
                if (i < _properties.Count - 1)
                {
                    sbParameterList.Append(", ");
                }
            }

            if (_database.DatabaseType == DatabaseType.PostgreSQL)
            {
                return $"INSERT INTO \"{_table}\" ({sbColumnList.ToString()}) VALUES ({sbParameterList.ToString()}) RETURNING \"Id\"";
            }

            return $"INSERT INTO {_table} ({sbColumnList.ToString()}) VALUES ({sbParameterList.ToString()}); SELECT last_insert_rowid() id";
        }

        private TModel Insert(IDbConnection connection, IDbTransaction transaction, TModel model)
        {
            SqlBuilderExtensions.LogQuery(_insertSql, model);

            GridReader multi;
            try
            {
                multi = connection.QueryMultiple(_insertSql, model, transaction);
            }
            catch (Exception e)
            {
                e.Data.Add("SQL", SqlBuilderExtensions.GetSqlLogString(_insertSql, model));
                throw;
            }

            var multiRead = multi.Read();
            var id = (int)(multiRead.First().id ?? multiRead.First().Id);
            _keyProperty.SetValue(model, id);

            return model;
        }

        public void InsertMany(IList<TModel> models)
        {
            var __insertAllSw = System.Diagnostics.Stopwatch.StartNew();
            if (models.Any(x => x.Id != 0))
            {
                throw new InvalidOperationException("Can't insert model with existing ID != 0");
            }

            using (var conn = _database.OpenConnection())
            {
                using (var tran = conn.BeginTransaction(IsolationLevel.ReadCommitted))
                {
                    if (_database.DatabaseType == DatabaseType.PostgreSQL && models.Count > 0)
                    {
                        // Batch multi-row INSERT with RETURNING for PostgreSQL
                        const int maxParams = 60000; // stay under 65535 cap
                        var colsPerRow = _properties.Count;
                        var maxRowsPerBatch = Math.Max(1, Math.Min(models.Count, Math.Min(1000, maxParams / Math.Max(1, colsPerRow))));

                        var __totalBatches = (int)System.Math.Ceiling(models.Count / (double)maxRowsPerBatch);
                        var __batchIndex = 0;
                        for (var offset = 0; offset < models.Count; offset += maxRowsPerBatch)
                        {
                            var __batchSw = System.Diagnostics.Stopwatch.StartNew();
                            var batch = models.Skip(offset).Take(Math.Min(maxRowsPerBatch, models.Count - offset)).ToList();

                            var columnList = string.Join(", ", _properties.Select(p => $"\"{p.Name}\""));
                            var valuesSb = new StringBuilder();
                            var parameters = new DynamicParameters();

                            for (var i = 0; i < batch.Count; i++)
                            {
                                var model = batch[i];
                                var paramNames = new List<string>(_properties.Count);
                                for (var c = 0; c < _properties.Count; c++)
                                {
                                    var prop = _properties[c];
                                    var paramName = $"p{offset + i}_{prop.Name}";
                                    paramNames.Add($"@{paramName}");
                                    parameters.Add(paramName, prop.GetValue(model));
                                }
                                valuesSb.Append('(').Append(string.Join(", ", paramNames)).Append(')');
                                if (i < batch.Count - 1) valuesSb.Append(", ");
                            }

                            var sql = $"INSERT INTO \"{_table}\" ({columnList}) VALUES {valuesSb} RETURNING \"Id\"";
                            SqlBuilderExtensions.LogQuery(sql, parameters);
                            try
                            {
                                var ids = conn.Query<int>(sql, parameters, transaction: tran).ToList();
                                for (var i = 0; i < batch.Count; i++)
                                {
                                    _keyProperty.SetValue(batch[i], ids[i]);
                                }
                            }
                            catch (Exception e)
                            {
                                e.Data.Add("SQL", SqlBuilderExtensions.GetSqlLogString(sql, parameters));
                                throw;
                            }

                            __batchSw.Stop();
                            __batchIndex++;
                            if (__batchSw.ElapsedMilliseconds >= 50)
                            {
                                try
                                {
                                    var __logger = NLog.LogManager.GetCurrentClassLogger();
                                    __logger.Debug("[SQL-TIMING] InsertMany batch table={0} rows={1} batch={2}/{3} db={4} elapsed={5}ms", _table, batch.Count, __batchIndex, __totalBatches, _database.DatabaseType, __batchSw.ElapsedMilliseconds);
                                }
                                catch { }
                            }
                        }

                        tran.Commit();
                    }
                    else if (_database.DatabaseType == DatabaseType.SQLite && models.Count > 0)
                    {
                        // Prefer multi-row when supported; otherwise gracefully fall back to row-by-row in a single transaction
                        int? prevSync = null;
                        try
                        {
                            try
                            {
                                prevSync = conn.ExecuteScalar<int>("PRAGMA synchronous");
                                conn.Execute("PRAGMA temp_store = MEMORY", transaction: tran);
                                conn.Execute("PRAGMA cache_size = -64000", transaction: tran);
                            }
                            catch { }

                            bool supportsReturning = false;
                            try { supportsReturning = _database.Version >= new Version(3, 35, 0); } catch { }

                            if (supportsReturning)
                            {
                                try
                                {
                                    const int sqliteMaxParams = 900;
                                    var colsPerRow = _properties.Count;
                                    var maxRowsPerBatch = Math.Max(1, Math.Min(models.Count, Math.Min(500, sqliteMaxParams / Math.Max(1, colsPerRow))));
                                    var __totalBatches = (int)System.Math.Ceiling(models.Count / (double)maxRowsPerBatch);
                                    var __batchIndex = 0;

                                    for (var offset = 0; offset < models.Count; offset += maxRowsPerBatch)
                                    {
                                        var __batchSw = System.Diagnostics.Stopwatch.StartNew();
                                        var batch = models.Skip(offset).Take(Math.Min(maxRowsPerBatch, models.Count - offset)).ToList();

                                        var columnList = string.Join(", ", _properties.Select(p => $"\"{p.Name}\""));
                                        var valuesSb = new StringBuilder();
                                        var parameters = new DynamicParameters();
                                        for (var i = 0; i < batch.Count; i++)
                                        {
                                            var model = batch[i];
                                            var paramNames = new List<string>(_properties.Count);
                                            for (var c = 0; c < _properties.Count; c++)
                                            {
                                                var prop = _properties[c];
                                                var paramName = $"p{offset + i}_{prop.Name}";
                                                paramNames.Add($"@{paramName}");
                                                parameters.Add(paramName, prop.GetValue(model));
                                            }
                                            valuesSb.Append('(').Append(string.Join(", ", paramNames)).Append(')');
                                            if (i < batch.Count - 1) valuesSb.Append(", ");
                                        }

                                        var sql = $"INSERT INTO \"{_table}\" ({columnList}) VALUES {valuesSb} RETURNING \"Id\"";
                                        SqlBuilderExtensions.LogQuery(sql, parameters);
                                        var ids = conn.Query<int>(sql, parameters, transaction: tran).ToList();
                                        for (var i = 0; i < batch.Count; i++)
                                        {
                                            _keyProperty.SetValue(batch[i], ids[i]);
                                        }

                                        __batchSw.Stop();
                                        __batchIndex++;
                                        if (__batchSw.ElapsedMilliseconds >= 50)
                                        {
                                            try
                                            {
                                                var __logger = NLog.LogManager.GetCurrentClassLogger();
                                                __logger.Debug("[SQL-TIMING] InsertMany batch table={0} rows={1} batch={2}/{3} db={4} elapsed={5}ms", _table, batch.Count, __batchIndex, __totalBatches, _database.DatabaseType, __batchSw.ElapsedMilliseconds);
                                            }
                                            catch { }
                                        }
                                    }
                                }
                                catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.Message.Contains("RETURNING"))
                                {
                                    // Wrapper/engine does not support RETURNING – fall back to per-row
                                    var __rowLoopSw = System.Diagnostics.Stopwatch.StartNew();
                                    foreach (var model in models)
                                    {
                                        Insert(conn, tran, model);
                                    }
                                    __rowLoopSw.Stop();
                                    try
                                    {
                                        var __logger = NLog.LogManager.GetCurrentClassLogger();
                                        __logger.Debug("[SQL-TIMING] InsertMany RowLoop(fallback) table={0} rows={1} db={2} total={3}ms", _table, models.Count, _database.DatabaseType, __rowLoopSw.ElapsedMilliseconds);
                                    }
                                    catch { }
                                }
                            }
                            else
                            {
                                // Older SQLite – per-row within one transaction
                                var __rowLoopSw = System.Diagnostics.Stopwatch.StartNew();
                                foreach (var model in models)
                                {
                                    Insert(conn, tran, model);
                                }
                                __rowLoopSw.Stop();
                                try
                                {
                                    var __logger = NLog.LogManager.GetCurrentClassLogger();
                                    __logger.Debug("[SQL-TIMING] InsertMany RowLoop(legacy) table={0} rows={1} db={2} total={3}ms", _table, models.Count, _database.DatabaseType, __rowLoopSw.ElapsedMilliseconds);
                                }
                                catch { }
                            }

                            tran.Commit();
                        }
                        finally
                        {
                            if (prevSync.HasValue)
                            {
                                try { conn.Execute($"PRAGMA synchronous = {prevSync.Value}"); } catch { }
                            }
                        }
                    }
                    else
                    {
                        // Fallback per-row insert within one transaction
                        var __rowLoopSw = System.Diagnostics.Stopwatch.StartNew();
                        foreach (var model in models)
                        {
                            Insert(conn, tran, model);
                        }
                        tran.Commit();
                        __rowLoopSw.Stop();
                        if (__rowLoopSw.ElapsedMilliseconds >= 100)
                        {
                            try
                            {
                                var __logger = NLog.LogManager.GetCurrentClassLogger();
                                __logger.Debug("[SQL-TIMING] InsertMany RowLoop table={0} rows={1} db={2} total={3}ms", _table, models.Count, _database.DatabaseType, __rowLoopSw.ElapsedMilliseconds);
                            }
                            catch { }
                        }
                    }
                }
            }

            __insertAllSw.Stop();
            if (__insertAllSw.ElapsedMilliseconds >= 100)
            {
                try
                {
                    var __logger = NLog.LogManager.GetCurrentClassLogger();
                    __logger.Debug("[SQL-TIMING] InsertMany total table={0} rows={1} db={2} total={3}ms", _table, models.Count, _database.DatabaseType, __insertAllSw.ElapsedMilliseconds);
                }
                catch { }
            }
        }

        public void InsertMany(IList<TModel> models, IDbConnection connection, IDbTransaction transaction)
        {
            if (models.Any(x => x.Id != 0))
            {
                throw new InvalidOperationException("Can't insert model with existing ID != 0");
            }

            // Use the same batching logic but do not commit (caller controls transaction)
            if (_database.DatabaseType == DatabaseType.PostgreSQL && models.Count > 0)
            {
                const int maxParams = 60000;
                var colsPerRow = _properties.Count;
                var maxRowsPerBatch = Math.Max(1, Math.Min(models.Count, Math.Min(1000, maxParams / Math.Max(1, colsPerRow))));

                for (var offset = 0; offset < models.Count; offset += maxRowsPerBatch)
                {
                    var batch = models.Skip(offset).Take(Math.Min(maxRowsPerBatch, models.Count - offset)).ToList();
                    var columnList = string.Join(", ", _properties.Select(p => $"\"{p.Name}\""));
                    var valuesSb = new StringBuilder();
                    var parameters = new DynamicParameters();
                    for (var i = 0; i < batch.Count; i++)
                    {
                        var model = batch[i];
                        var paramNames = new List<string>(_properties.Count);
                        for (var c = 0; c < _properties.Count; c++)
                        {
                            var prop = _properties[c];
                            var paramName = $"p{offset + i}_{prop.Name}";
                            paramNames.Add($"@{paramName}");
                            parameters.Add(paramName, prop.GetValue(model));
                        }
                        valuesSb.Append('(').Append(string.Join(", ", paramNames)).Append(')');
                        if (i < batch.Count - 1) valuesSb.Append(", ");
                    }
                    var sql = $"INSERT INTO \"{_table}\" ({columnList}) VALUES {valuesSb} RETURNING \"Id\"";
                    SqlBuilderExtensions.LogQuery(sql, parameters);
                    var ids = connection.Query<int>(sql, parameters, transaction: transaction).ToList();
                    for (var i = 0; i < batch.Count; i++)
                    {
                        _keyProperty.SetValue(batch[i], ids[i]);
                    }
                }
            }
            else if (_database.DatabaseType == DatabaseType.SQLite && models.Count > 0)
            {
                // Detect support for RETURNING and fallback if missing
                bool supportsReturning = false;
                try { supportsReturning = _database.Version >= new Version(3, 35, 0); } catch { }

                if (supportsReturning)
                {
                    try
                    {
                        const int sqliteMaxParams = 900;
                        var colsPerRow = _properties.Count;
                        var maxRowsPerBatch = Math.Max(1, Math.Min(models.Count, Math.Min(500, sqliteMaxParams / Math.Max(1, colsPerRow))));

                        for (var offset = 0; offset < models.Count; offset += maxRowsPerBatch)
                        {
                            var batch = models.Skip(offset).Take(Math.Min(maxRowsPerBatch, models.Count - offset)).ToList();
                            var columnList = string.Join(", ", _properties.Select(p => $"\"{p.Name}\""));
                            var valuesSb = new StringBuilder();
                            var parameters = new DynamicParameters();
                            for (var i = 0; i < batch.Count; i++)
                            {
                                var model = batch[i];
                                var paramNames = new List<string>(_properties.Count);
                                for (var c = 0; c < _properties.Count; c++)
                                {
                                    var prop = _properties[c];
                                    var paramName = $"p{offset + i}_{prop.Name}";
                                    paramNames.Add($"@{paramName}");
                                    parameters.Add(paramName, prop.GetValue(model));
                                }
                                valuesSb.Append('(').Append(string.Join(", ", paramNames)).Append(')');
                                if (i < batch.Count - 1) valuesSb.Append(", ");
                            }
                            var sql = $"INSERT INTO \"{_table}\" ({columnList}) VALUES {valuesSb} RETURNING \"Id\"";
                            SqlBuilderExtensions.LogQuery(sql, parameters);
                            var ids = connection.Query<int>(sql, parameters, transaction: transaction).ToList();
                            for (var i = 0; i < batch.Count; i++)
                            {
                                _keyProperty.SetValue(batch[i], ids[i]);
                            }
                        }
                    }
                    catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.Message.Contains("RETURNING"))
                    {
                        // fallback per-row
                        foreach (var model in models)
                        {
                            Insert(connection, transaction, model);
                        }
                    }
                }
                else
                {
                    foreach (var model in models)
                    {
                        Insert(connection, transaction, model);
                    }
                }
            }
            else
            {
                foreach (var model in models)
                {
                    Insert(connection, transaction, model);
                }
            }
        }

        public TModel Update(TModel model)
        {
            if (model.Id == 0)
            {
                throw new InvalidOperationException("Can't update model with ID 0");
            }

            using (var conn = _database.OpenConnection())
            {
                UpdateFields(conn, null, model, GetUpdateProperties());
            }

            ModelUpdated(model);

            return model;
        }

        public void UpdateMany(IList<TModel> models)
        {
            if (models.Any(x => x.Id == 0))
            {
                throw new InvalidOperationException("Can't update model with ID 0");
            }

            using (var conn = _database.OpenConnection())
            {
                using (var tran = conn.BeginTransaction(IsolationLevel.ReadCommitted))
                {
                    int? prevSync = null;
                    try
                    {
                        if (_database.DatabaseType == DatabaseType.SQLite && models.Count > 0)
                        {
                            try
                            {
                                prevSync = conn.ExecuteScalar<int>("PRAGMA synchronous", transaction: tran);
                                conn.Execute("PRAGMA temp_store = MEMORY", transaction: tran);
                                conn.Execute("PRAGMA cache_size = -64000", transaction: tran);
                            }
                            catch
                            {
                                // best-effort only
                            }
                        }

                        UpdateFields(conn, tran, models, GetUpdateProperties());
                        tran.Commit();
                    }
                    finally
                    {
                        if (prevSync.HasValue)
                        {
                            try { conn.Execute($"PRAGMA synchronous = {prevSync.Value}"); } catch { }
                        }
                    }
                }
            }
        }

        protected void Delete(Expression<Func<TModel, bool>> where)
        {
            Delete(Builder().Where<TModel>(where));
        }

        protected void Delete(SqlBuilder builder)
        {
            var sql = builder.AddDeleteTemplate(typeof(TModel));

            using (var conn = _database.OpenConnection())
            {
                conn.Execute(sql.RawSql, sql.Parameters);
            }
        }

        public void Delete(TModel model)
        {
            Delete(model.Id);
        }

        public void Delete(int id)
        {
            Delete(x => x.Id == id);
        }

        public void DeleteMany(IEnumerable<int> ids)
        {
            if (ids == null)
            {
                return;
            }

            var idList = ids.Distinct().ToList();
            if (idList.Any())
            {
                using (var conn = _database.OpenConnection())
                using (var tran = conn.BeginTransaction())
                {
                    DeleteMany(idList, conn, tran);
                    tran.Commit();
                }
            }
        }

        protected void DeleteMany(IEnumerable<int> ids, IDbConnection connection, IDbTransaction transaction)
        {
            if (ids == null)
            {
                return;
            }

            var idList = ids.Distinct().ToList();
            if (!idList.Any())
            {
                return;
            }

            var deleteSql = _database.DatabaseType == DatabaseType.PostgreSQL
                ? $"DELETE FROM \"{_table}\" WHERE \"{_keyProperty.Name}\" = ANY(@Ids)"
                : $"DELETE FROM \"{_table}\" WHERE \"{_keyProperty.Name}\" IN @Ids";

            if (_database.DatabaseType == DatabaseType.SQLite && idList.Count > SqliteVariableLimit.MaxParameters)
            {
                foreach (var batch in idList.Chunk(SqliteVariableLimit.MaxParameters))
                {
                    connection.Execute(deleteSql, new { Ids = batch.ToArray() }, transaction);
                }
            }
            else
            {
                connection.Execute(deleteSql, new { Ids = idList.ToArray() }, transaction);
            }
        }

        public void DeleteMany(List<TModel> models)
        {
            DeleteMany(models.Select(m => m.Id));
        }

        public TModel Upsert(TModel model)
        {
            if (model.Id == 0)
            {
                Insert(model);
                return model;
            }

            Update(model);
            return model;
        }

        public void Purge(bool vacuum = false)
        {
            using (var conn = _database.OpenConnection())
            {
                conn.Execute($"DELETE FROM \"{_table}\"");
            }

            if (vacuum)
            {
                Vacuum();
            }
        }

        protected void Vacuum()
        {
            _database.Vacuum();
        }

        public bool HasItems()
        {
            return Count() > 0;
        }

        public void SetFields(TModel model, params Expression<Func<TModel, object>>[] properties)
        {
            if (model.Id == 0)
            {
                throw new InvalidOperationException("Attempted to update model without ID");
            }

            var propertiesToUpdate = properties.Select(x => x.GetMemberName()).ToList();

            using (var conn = _database.OpenConnection())
            {
                UpdateFields(conn, null, model, propertiesToUpdate);
            }

            ModelUpdated(model);
        }

        public void SetFields(IList<TModel> models, params Expression<Func<TModel, object>>[] properties)
        {
            if (models.Any(x => x.Id == 0))
            {
                throw new InvalidOperationException("Attempted to update model without ID");
            }

            var propertiesToUpdate = properties.Select(x => x.GetMemberName()).ToList();

            using (var conn = _database.OpenConnection())
            {
                using (var tran = conn.BeginTransaction(IsolationLevel.ReadCommitted))
                {
                    int? prevSync = null;
                    try
                    {
                        if (_database.DatabaseType == DatabaseType.SQLite && models.Count > 0)
                        {
                            try
                            {
                                prevSync = conn.ExecuteScalar<int>("PRAGMA synchronous", transaction: tran);
                                conn.Execute("PRAGMA temp_store = MEMORY", transaction: tran);
                                conn.Execute("PRAGMA cache_size = -64000", transaction: tran);
                            }
                            catch
                            {
                                // best-effort only
                            }
                        }

                        UpdateFields(conn, tran, models, propertiesToUpdate);
                        tran.Commit();
                    }
                    finally
                    {
                        if (prevSync.HasValue)
                        {
                            try { conn.Execute($"PRAGMA synchronous = {prevSync.Value}"); } catch { }
                        }
                    }
                }
            }

            foreach (var model in models)
            {
                ModelUpdated(model);
            }
        }

        private string GetUpdateSql(List<PropertyInfo> propertiesToUpdate)
        {
            var sb = new StringBuilder();
            sb.AppendFormat("UPDATE \"{0}\" SET ", _table);

            for (var i = 0; i < propertiesToUpdate.Count; i++)
            {
                var property = propertiesToUpdate[i];
                sb.AppendFormat("\"{0}\" = @{1}", property.Name, property.Name);
                if (i < propertiesToUpdate.Count - 1)
                {
                    sb.Append(", ");
                }
            }

            sb.Append($" WHERE \"{_keyProperty.Name}\" = @{_keyProperty.Name}");

            return sb.ToString();
        }

        private void UpdateFields(IDbConnection connection, IDbTransaction transaction, TModel model, List<PropertyInfo> propertiesToUpdate)
        {
            var sql = propertiesToUpdate == _properties ? _updateSql : GetUpdateSql(propertiesToUpdate);

            SqlBuilderExtensions.LogQuery(sql, model);

            try
            {
                connection.Execute(sql, model, transaction: transaction);
            }
            catch (Exception e)
            {
                e.Data.Add("SQL", SqlBuilderExtensions.GetSqlLogString(sql, model));
                throw;
            }
        }

        private void UpdateFields(IDbConnection connection, IDbTransaction transaction, IList<TModel> models, List<PropertyInfo> propertiesToUpdate)
        {
            var sql = propertiesToUpdate == _properties ? _updateSql : GetUpdateSql(propertiesToUpdate);

            foreach (var model in models)
            {
                SqlBuilderExtensions.LogQuery(sql, model);
            }

            try
            {
                connection.Execute(sql, models, transaction: transaction);
            }
            catch (Exception e)
            {
                e.Data.Add("SQL", SqlBuilderExtensions.GetSqlLogString(sql, models));
                throw;
            }
        }

        protected virtual SqlBuilder PagedBuilder() => Builder();
        protected virtual IEnumerable<TModel> PagedQuery(SqlBuilder sql) => Query(sql);

        public virtual PagingSpec<TModel> GetPaged(PagingSpec<TModel> pagingSpec)
        {
            pagingSpec.Records = GetPagedRecords(PagedBuilder(), pagingSpec, PagedQuery);
            pagingSpec.TotalRecords = GetPagedRecordCount(PagedBuilder().SelectCount(), pagingSpec);

            return pagingSpec;
        }

        private void AddFilters(SqlBuilder builder, PagingSpec<TModel> pagingSpec)
        {
            var filters = pagingSpec.FilterExpressions;

            foreach (var filter in filters)
            {
                builder.Where<TModel>(filter);
            }
        }

        protected List<TModel> GetPagedRecords(SqlBuilder builder, PagingSpec<TModel> pagingSpec, Func<SqlBuilder, IEnumerable<TModel>> queryFunc)
        {
            AddFilters(builder, pagingSpec);

            var defaultSortKey = $"{_table}.{_keyProperty.Name}";
            if (string.IsNullOrWhiteSpace(pagingSpec.SortKey) || !TableMapping.Mapper.IsValidSortKey(pagingSpec.SortKey))
            {
                pagingSpec.SortKey = defaultSortKey;
            }

            var sortKey = TableMapping.Mapper.GetSortKey(pagingSpec.SortKey);
            var validBaseColumns = _properties
                .Select(x => x.Name)
                .Append(_keyProperty.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // Unqualified sort keys must map to the base table.
            if (sortKey.Table == null && !validBaseColumns.Contains(sortKey.Column))
            {
                sortKey = (null, _keyProperty.Name);
            }
            else if (sortKey.Table != null && !TableMapping.Mapper.IsValidColumnForTable(sortKey.Table, sortKey.Column))
            {
                sortKey = (null, _keyProperty.Name);
            }

            var sortDirection = pagingSpec.SortDirection == SortDirection.Descending ? "DESC" : "ASC";
            var pagingOffset = Math.Max(pagingSpec.Page - 1, 0) * pagingSpec.PageSize;
            builder.OrderBy($"\"{sortKey.Table ?? _table}\".\"{sortKey.Column}\" {sortDirection} LIMIT {pagingSpec.PageSize} OFFSET {pagingOffset}");

            return queryFunc(builder).ToList();
        }

        protected int GetPagedRecordCount(SqlBuilder builder, PagingSpec<TModel> pagingSpec, string template = null)
        {
            AddFilters(builder, pagingSpec);

            SqlBuilder.Template sql;
            if (template != null)
            {
                sql = builder.AddTemplate(template).LogQuery();
            }
            else
            {
                sql = builder.AddPageCountTemplate(typeof(TModel));
            }

            using (var conn = _database.OpenConnection())
            {
                return conn.ExecuteScalar<int>(sql.RawSql, sql.Parameters);
            }
        }

        protected void ModelCreated(TModel model, bool forcePublish = false)
        {
            PublishModelEvent(model, ModelAction.Created, forcePublish);
        }

        protected void ModelUpdated(TModel model, bool forcePublish = false)
        {
            PublishModelEvent(model, ModelAction.Updated, forcePublish);
        }

        protected void ModelDeleted(TModel model, bool forcePublish = false)
        {
            PublishModelEvent(model, ModelAction.Deleted, forcePublish);
        }

        private void PublishModelEvent(TModel model, ModelAction action, bool forcePublish)
        {
            if (PublishModelEvents || forcePublish)
            {
                _eventAggregator.PublishEvent(new ModelEvent<TModel>(model, action));
            }
        }

        protected virtual bool PublishModelEvents => false;
    }
}
