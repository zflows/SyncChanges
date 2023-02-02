using Humanizer;
using NLog;
using NPoco;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using System.Threading;

namespace SyncChanges
{
    /// <summary>
    /// Allows replication of database changes from a source database to one or more destination databases.
    /// </summary>
    public class Synchronizer
    {
        /// <summary>
        /// Gets or sets a value indicating whether destination databases will be modified during a replication run.
        /// </summary>
        /// <value>
        ///   <c>true</c> if destination databases will be modified; otherwise, <c>false</c>.
        /// </value>
        public bool DryRun { get; set; } = false;

        /// <summary>
        /// Gets or sets the database connection timeout.
        /// </summary>
        /// <value>
        /// The database connection timeout.
        /// </value>
        public int Timeout { get; set; } = 0;

        /// <summary>
        /// Gets or sets the minimum synchronization time interval in seconds. Default is 30 seconds.
        /// </summary>
        /// <value>
        /// The synchronization time interval in seconds.
        /// </value>
        public int Interval { get; set; } = 30;

        /// <summary>
        /// Occurs when synchronization from a synchronization loop has succeeded.
        /// </summary>
        public event EventHandler<SyncEventArgs> Synced;

        static readonly Logger Log = LogManager.GetCurrentClassLogger();
        Config Config { get; set; }
        bool Error { get; set; }

        /// <summary>
        /// Initializes a new instance of the <see cref="Synchronizer"/> class.
        /// </summary>
        /// <param name="config">The configuration.</param>
        /// <exception cref="System.ArgumentException"><paramref name="config"/> is null</exception>
        public Synchronizer(Config config)
        {
            Config = config ?? throw new ArgumentException("config is null", nameof(config));
        }

        private IList<IList<TableInfo>> Tables { get; } = new List<IList<TableInfo>>();
        private bool Initialized = false;

        /// <summary>
        /// Initialize the synchronization process.
        /// </summary>
        public void Init()
        {
            if (Timeout != 0)
                Log.Info($"Command timeout is {"second".ToQuantity(Timeout)}");

            for (int i = 0; i < Config.ReplicationSets.Count; i++)
            {
                var replicationSet = Config.ReplicationSets[i];

                Log.Info($"Getting replication information for replication set {replicationSet.Name}");

                IList<TableInfo> tables = GetTables(replicationSet.Source);
                IList<TableInfo> filteredTables=new List<TableInfo>();
                if (replicationSet.Tables != null && replicationSet.Tables.Any())
                {
                    filteredTables = tables.Select(t => new { Table = t, Name = t.BraketlessName })
                        .Where(t => replicationSet.Tables.Any(r => r == t.Name || r == t.Name.Split('.')[1]))
                        .Select(t => t.Table).ToList();

                    if (!filteredTables.Any() && tables.Any())
                    {
                        Log.Warn(@$"Source has {"table".ToQuantity(tables.Count)} with tracking setup for replication set {replicationSet.Name}, but all were filtered out.
                            DB Tables: {string.Join(", ", tables.Select(t => t.Name))}; Filter Tables: {string.Join(", ", replicationSet.Tables)}");
                    }
                    tables = filteredTables;
                }

                if (tables.Any())
                {
                    Log.Info($"Replicating {"table".ToQuantity(tables.Count, ShowQuantityAs.None)} for replication set {replicationSet.Name} {string.Join(", ", tables.Select(t => t.Name))}");
                }
                else
                {
                    Log.Warn($"No tables to replicate for replication set {replicationSet.Name} (check if change tracking is enabled)");
                }
                Tables.Add(tables);
            }

            Initialized = true;
        }

        /// <summary>
        /// Perform the synchronization.
        /// </summary>
        /// <returns>true, if the synchronization was successful; otherwise, false.</returns>
        public bool Sync()
        {
            Error = false;

            if (!Initialized) Init();

            for (int i = 0; i < Config.ReplicationSets.Count; i++)
            {
                var replicationSet = Config.ReplicationSets[i];
                var tables = Tables[i];

                Sync(replicationSet, tables);
            }

            Log.Info($"Finished replication {(Error ? "with" : "without")} errors");

            return !Error;
        }

        private bool Sync(ReplicationSet replicationSet, IList<TableInfo> tables, long sourceVersion = -1)
        {
            Error = false;

            if (!tables.Any()) return true;

            Log.Info($"Starting replication for replication set {replicationSet.Name}");

            var destinationsByVersion = replicationSet.Destinations.GroupBy(d => GetCurrentVersion(replicationSet.Name, d))
                .Where(d => d.Key >= 0 && (sourceVersion < 0 || d.Key < sourceVersion)).ToList();

            foreach (var destinations in destinationsByVersion)
                Replicate(replicationSet, destinations, tables);

            return !Error;
        }

        /// <summary>
        /// Performs synchronization in an infinite loop. Periodically checks if source version has increased to trigger replication.
        /// </summary>
        /// <param name="token">The cancellation token.</param>
        public void SyncLoop(CancellationToken token)
        {
            var currentVersions = Enumerable.Repeat(0L, Config.ReplicationSets.Count).ToList();

            if (!Initialized) Init();

            while (true)
            {
                if (token.IsCancellationRequested)
                {
                    Log.Info("Stopping replication.");
                    return;
                }

                var start = DateTime.UtcNow;
                Error = false;

                for (int i = 0; i < Config.ReplicationSets.Count; i++)
                {
                    var replicationSet = Config.ReplicationSets[i];
                    var currentVersion = currentVersions[i];
                    long version = 0;

                    try
                    {
                        using (var db = GetDatabase(replicationSet.Source.ConnectionString, DatabaseType.SqlServer2008))
                            version = db.ExecuteScalar<long>("select CHANGE_TRACKING_CURRENT_VERSION()");

                        Log.Debug($"Current version of source in replication set {replicationSet.Name} is {version}.");

                        if (version > currentVersion)
                        {
                            Log.Info($"Current version of source in replication set {replicationSet.Name} has increased from {currentVersion} to {version}: Starting replication.");

                            var tables = Tables[i];
                            var success = Sync(replicationSet, tables, version);

                            if (success) currentVersions[i] = version;

                            Synced?.Invoke(this, new SyncEventArgs { ReplicationSet = replicationSet, Version = version });
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, $"Error occurred during replication of set {replicationSet.Name}.");
                        Error = true;
                    }
                    if (token.IsCancellationRequested)
                    {
                        Log.Info("Stopping replication.");
                        return;
                    }
                }

                Log.Info($"Finished replication {(Error ? "with" : "without")} errors");

                int delay = (int)Math.Round(Math.Max(0, (TimeSpan.FromSeconds(Interval) - (DateTime.UtcNow - start)).TotalSeconds) * 1000, MidpointRounding.AwayFromZero);
                Thread.Sleep(delay);
            }
        }

        private IList<TableInfo> GetTables(DatabaseInfo dbInfo)
        {
            try
            {
                using var db = GetDatabase(dbInfo.ConnectionString, DatabaseType.SqlServer2008);
                var sql = @"select TableName, ColumnName, coalesce(max(cast(is_primary_key as tinyint)), 0) PrimaryKey,
                        coalesce(max(cast(is_identity as tinyint)), 0) IsIdentity from
                        (
                        select ('[' + s.name + '].[' + t.name + ']') TableName, ('[' + COL_NAME(t.object_id, a.column_id) + ']') ColumnName,
                        i.is_primary_key, a.is_identity
                        from sys.change_tracking_tables tr
                        join sys.tables t on t.object_id = tr.object_id
                        join sys.schemas s on s.schema_id = t.schema_id
                        join sys.columns a on a.object_id = t.object_id
                        left join sys.index_columns c on c.object_id = t.object_id and c.column_id = a.column_id
                        left join sys.indexes i on i.object_id = t.object_id and i.index_id = c.index_id
                        where a.is_computed = 0
                        ) X
                        group by TableName, ColumnName
                        order by TableName, ColumnName";

                var tables = db.Fetch<dynamic>(sql).GroupBy(t => t.TableName)
                    .Select(g => new TableInfo
                    {
                        Name = (string)g.Key,
                        KeyColumns = g.Where(c => (int)c.PrimaryKey > 0).Select(c => (string)c.ColumnName).ToList(),
                        OtherColumns = g.Where(c => (int)c.PrimaryKey == 0).Select(c => (string)c.ColumnName).ToList(),
                        HasIdentity = g.Any(c => (int)c.IsIdentity > 0)
                    }).ToList();

                var fks = db.Fetch<ForeignKeyConstraint>(@"select obj.name AS ForeignKeyName,
                            ('[' + sch.name + '].[' + tab1.name + ']') TableName,
                            ('[' +  col1.name + ']') ColumnName,
                            ('[' + sch2.name + '].[' + tab2.name + ']') ReferencedTableName,
                            ('[' +  col2.name + ']') ReferencedColumnName
                        from sys.foreign_key_columns fkc
                        inner join sys.foreign_keys obj
                            on obj.object_id = fkc.constraint_object_id
                        inner join sys.tables tab1
                            on tab1.object_id = fkc.parent_object_id
                        inner join sys.schemas sch
                            on tab1.schema_id = sch.schema_id
                        inner join sys.columns col1
                            on col1.column_id = parent_column_id AND col1.object_id = tab1.object_id
                        inner join sys.tables tab2
                            on tab2.object_id = fkc.referenced_object_id
                        inner join sys.schemas sch2
                            on tab2.schema_id = sch2.schema_id
                        inner join sys.columns col2
                            on col2.column_id = referenced_column_id AND col2.object_id = tab2.object_id
                        where obj.is_disabled = 0");

                foreach (var table in tables)
                    table.ForeignKeyConstraints = fks.Where(f => f.TableName == table.Name).ToList();

                var uqcs = db.Fetch<UniqueColumn>(@"SELECT
                             IndexId = ('[' + sch.name + '].[' + t.name + '].[' + ind.name + ']'),
                             TableName = ('[' + sch.name + '].[' + t.name + ']'),
                             IndexName = ind.name,
                             ColumnName = ('[' +  col.name + ']'),
                             IsConstraint = is_unique_constraint
                        FROM
                             sys.indexes ind
                        INNER JOIN
                             sys.index_columns ic ON  ind.object_id = ic.object_id and ind.index_id = ic.index_id
                        INNER JOIN
                             sys.columns col ON ic.object_id = col.object_id and ic.column_id = col.column_id
                        INNER JOIN
                             sys.tables t ON ind.object_id = t.object_id
                        inner join sys.schemas sch
                            on t.schema_id = sch.schema_id
                        WHERE
                             ind.is_primary_key = 0
                             AND (ind.is_unique = 1 or ind.is_unique_constraint = 1)
                             AND t.is_ms_shipped = 0
                        ORDER BY
                             t.name, ind.name, ind.index_id, ic.is_included_column, ic.key_ordinal;");

                var uqs = uqcs.GroupBy(c => $"{c.TableName}_{c.IndexName}").Select(g => new UniqueConstraint
                {
                    IndexName = g.First().IndexName,
                    TableName = g.First().TableName,
                    IsConstraint = g.First().IsConstraint,
                    ColumnNames = g.Select(c => c.ColumnName).ToList(),
                }).ToList();

                foreach (var table in tables)
                    table.UniqueConstraints = uqs.Where(u => u.TableName == table.Name).ToList();

                return tables;
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "Error getting tables to replicate from source database");
                throw;
            }
        }

        private Database GetDatabase(string connectionString, DatabaseType databaseType = null)
        {
            var db = new Database(connectionString, databaseType ?? DatabaseType.SqlServer2005, System.Data.SqlClient.SqlClientFactory.Instance);

            if (Timeout != 0) db.CommandTimeout = Timeout;

            return db;
        }

        private void Replicate(ReplicationSet replicationSet, IGrouping<long, DatabaseInfo> destinations, IList<TableInfo> tables)
        {
            var changeInfo = RetrieveChanges(replicationSet.Source, destinations, tables, replicationSet.IsInitialLoad);
            if (changeInfo == null) return;

            // replicate changes to destinations
            foreach (var destination in destinations)
            {
                try
                {
                    Log.Info($"Replicating {"change".ToQuantity(changeInfo.Changes.Count)} to destination {destination.Name}");

                    using var db = GetDatabase(destination.ConnectionString, DatabaseType.SqlServer2005);
                    using var transaction = db.GetTransaction(System.Data.IsolationLevel.ReadUncommitted);

                    try
                    {
                        var changes = changeInfo.Changes;
                        var disabledForeignKeyConstraints = new Dictionary<ForeignKeyConstraint, long>();

                        for (int i = 0; i < changes.Count; i++)
                        {
                            var change = changes[i];
                            if (change.Origin == destination.Name)
                            {
                                Log.Info($"Skipping change #{i + 1} of {changes.Count} (Version {change.Version}, CreationVersion {change.CreationVersion}) since Origin of the the change is same as destination: {change.Origin}");
                                continue;
                            }
                            Log.Debug($"Replicating change #{i + 1} of {changes.Count} (Version {change.Version}, CreationVersion {change.CreationVersion})");

                            foreach (var fk in change.ForeignKeyConstraintsToDisable)
                            {
                                if (disabledForeignKeyConstraints.TryGetValue(fk.Key, out long untilVersion))
                                {
                                    // FK is already disabled, check if it needs to be deferred further than currently planned
                                    if (fk.Value > untilVersion)
                                        disabledForeignKeyConstraints[fk.Key] = fk.Value;
                                }
                                else
                                {
                                    DisableForeignKeyConstraint(db, fk.Key);
                                    disabledForeignKeyConstraints[fk.Key] = fk.Value;
                                }
                            }

                            PerformChange(db, change, replicationSet, destination.Schema);

                            if ((i + 1) >= changes.Count || changes[i + 1].CreationVersion > change.CreationVersion) // there may be more than one change with the same CreationVersion
                            {
                                foreach (var fk in disabledForeignKeyConstraints.Where(f => f.Value <= change.CreationVersion).Select(f => f.Key).ToList())
                                {
                                    ReenableForeignKeyConstraint(db, fk);
                                    disabledForeignKeyConstraints.Remove(fk);
                                }
                            }
                        }

                        if (!DryRun)
                        {
                            SetSyncVersion(replicationSet.Name, db, changeInfo.Version);
                            transaction.Complete();
                        }

                        Log.Info($"Destination {destination.Name} now at version {changeInfo.Version}");
                    }
#pragma warning disable CA1031 // Do not catch general exception types
                    catch (Exception ex)
                    {
                        Error = true;
                        Log.Error(ex, $"Error replicating changes to destination {destination.Name}");
                    }
#pragma warning restore CA1031 // Do not catch general exception types
                }
#pragma warning disable CA1031 // Do not catch general exception types
                catch (Exception ex)
                {
                    Error = true;
                    Log.Error(ex, $"Error replicating changes to destination {destination.Name}");
                }
#pragma warning restore CA1031 // Do not catch general exception types
            }
        }

        private void ReenableForeignKeyConstraint(Database db, ForeignKeyConstraint fk)
        {
            Log.Debug($"Re-enabling foreign key constraint {fk.ForeignKeyName}");
            var sql = $"alter table {fk.TableName} with check check constraint {fk.ForeignKeyName}";
            if (!DryRun)
                db.Execute(sql);
        }

        private void DisableForeignKeyConstraint(Database db, ForeignKeyConstraint fk)
        {
            Log.Debug($"Disabling foreign key constraint {fk.ForeignKeyName}");
            var sql = $"alter table {fk.TableName} nocheck constraint {fk.ForeignKeyName}";
            if (!DryRun)
                db.Execute(sql);
        }

        private void SetSyncVersion(string replicationSetName, Database db, long currentVersion)
        {
            if (DryRun) return;

            if (SyncTableExists(db))
            {
                db.Execute("update SyncInfo set Version = @1 where replication_set=@0", replicationSetName, currentVersion);
            }
            else
            {
                db.Execute("create table SyncInfo (replication_set varchar(120) not null primary key, Version bigint not null)");
                db.Execute("insert into SyncInfo (replication_set,Version) values (@0,@1)", replicationSetName, currentVersion);
            }
        }

        private bool SyncTableExists(Database db)
        {
            return db.ExecuteScalar<string>("select top(1) name from sys.tables where name ='SyncInfo'") != null;
        }

        private ChangeInfo RetrieveChanges(DatabaseInfo source, IGrouping<long, DatabaseInfo> destinations, IList<TableInfo> tables, bool isInitialLoad)
        {
            long destinationVersion = destinations.Key;
            var changeInfo = new ChangeInfo();
            var changes = new List<Change>();

            using (var db = GetDatabase(source.ConnectionString, DatabaseType.SqlServer2008))
            {
                var snapshotIsolationEnabled = db.ExecuteScalar<int>("select snapshot_isolation_state from sys.databases where name = DB_NAME()") == 1;
                if (snapshotIsolationEnabled)
                {
                    Log.Info($"Snapshot isolation is enabled in database {source.Name}");
                    db.BeginTransaction(System.Data.IsolationLevel.Snapshot);
                }
                else
                    Log.Info($"Snapshot isolation is not enabled in database {source.Name}, ignoring all changes above current version");

                long sourceVersion = db.ExecuteScalar<long>("select CHANGE_TRACKING_CURRENT_VERSION()");
                changeInfo.Version = sourceVersion;
                Log.Info($"Current version of database {source.Name} is {changeInfo.Version}");

                foreach (var table in tables)
                {
                    var tableName = table.Name;
                    var minVersion = db.ExecuteScalar<long?>("select CHANGE_TRACKING_MIN_VALID_VERSION(OBJECT_ID(@0))", tableName);

                    Log.Info($"Minimum version of table {tableName} in database {source.Name} is {minVersion}");

                    if (minVersion > destinationVersion)
                    {
                        Log.Error($"Cannot replicate table {tableName} to {"destination".ToQuantity(destinations.Count(), ShowQuantityAs.None)} {string.Join(", ", destinations.Select(d => d.Name))} because minimum source version {minVersion} is greater than destination version {destinationVersion}");
                        Error = true;
                        return null;
                    }

                    string sql;
                    if (isInitialLoad)
                    {
                        sql = $@"select 'I' as SYS_CHANGE_OPERATION, cast({sourceVersion} as bigint) as SYS_CHANGE_VERSION, cast(0 as bigint) as SYS_CHANGE_CREATION_VERSION, '{source.Name}' as CHANGE_ORIGIN,
                        {string.Join(", ", table.KeyColumns.Concat(table.OtherColumns))}
                        from {tableName}";
                    }
                    else
                    {
                        sql = $@"select c.SYS_CHANGE_OPERATION, c.SYS_CHANGE_VERSION, c.SYS_CHANGE_CREATION_VERSION, CONVERT(VARCHAR, c.SYS_CHANGE_CONTEXT) as CHANGE_ORIGIN,
                        {string.Join(", ", table.KeyColumns.Select(c => "c." + c).Concat(table.OtherColumns.Select(c => "t." + c)))}
                        from CHANGETABLE (CHANGES {tableName}, @0) c
                        left outer join {tableName} t on ";
                        sql += string.Join(" and ", table.KeyColumns.Select(k => $"c.{k} = t.{k}"));
                        sql += " order by coalesce(c.SYS_CHANGE_CREATION_VERSION, c.SYS_CHANGE_VERSION)";
                    }

                    Log.Debug($"Retrieving changes for table {tableName}: {sql}");

                    db.OpenSharedConnection();
                    var cmd = db.CreateCommand(db.Connection, System.Data.CommandType.Text, sql, destinationVersion);

                    using var reader = cmd.ExecuteReader();
                    var numChanges = 0;

                    while (reader.Read())
                    {
                        int col = 0;
                        var change = new Change { Operation = ((string)reader[col++])[0], Table = table }; //SYS_CHANGE_OPERATION
                        long version = reader.GetInt64(col++); //SYS_CHANGE_VERSION
                        change.Version = version;
                        long creationVersion = reader.IsDBNull(col) ? version : reader.GetInt64(col); //SYS_CHANGE_CREATION_VERSION
                        col++;
                        change.CreationVersion = creationVersion;
                        change.Origin = reader[col++] as string; //CHANGE_ORIGIN

                        if (!snapshotIsolationEnabled && Math.Min(version, creationVersion) > changeInfo.Version)
                        {
                            Log.Warn($"Ignoring change version {Math.Min(version, creationVersion)}");
                            continue;
                        }

                        for (int i = 0; i < table.KeyColumns.Count; i++, col++)
                            change.Keys[table.KeyColumns[i]] = reader.GetValue(col);
                        for (int i = 0; i < table.OtherColumns.Count; i++, col++)
                            change.Others[table.OtherColumns[i]] = reader.GetValue(col);

                        changes.Add(change);
                        numChanges++;
                    }

                    Log.Info($"Table {tableName} has {"change".ToQuantity(numChanges)}");
                }

                if (snapshotIsolationEnabled)
                    db.CompleteTransaction();
            }

            changeInfo.Changes.AddRange(changes.OrderBy(c => c.Version).ThenBy(c => c.Table.Name));
            Log.Debug($"ChangeInfo updated with {changeInfo.Changes.Count} Changes");

            ComputeForeignKeyConstraintsToDisable(changeInfo);
            Log.Debug("Foreing Keys Computed");

            return changeInfo;
        }

        private void ComputeForeignKeyConstraintsToDisable(ChangeInfo changeInfo)
        {
            List<Change> changes = changeInfo.Changes
                .Where(c=> c.CreationVersion < c.Version // was inserted then later updated
                 && c.Table.ForeignKeyConstraints.Any())
                .OrderBy(c => c.CreationVersion).ThenBy(c => c.Table.Name)
                .ToList();
            Log.Debug($"Computing Foreing Keys for {changes.Count} changes");

            for (int i = 0; i < changes.Count; i++)
            {
                var change = changes[i];
                Log.Debug($"CreationVersion: {change.CreationVersion} Version: {change.Version}");
                //if (change.CreationVersion < change.Version) // was inserted then later updated
                //{
                //for (int j = i + 1; j < changes.Count; j++)
                //{
                List<Change> intermediateChanges = changes.Where((c, j) => j > i && c.CreationVersion <= change.Version && c.Operation == 'I').ToList();
                foreach (var intermediateChange in intermediateChanges)
                {
                    //var intermediateChange = changes[j];
                    if (intermediateChange.CreationVersion > change.Version) // created later than last update to change
                        break;
                    if (intermediateChange.Operation != 'I') continue;

                    // let's look at intermediateChange if it collides with change
                    Log.Debug($"CreationVersion: {intermediateChange.CreationVersion} Version:{change.Version}; table; {change.Table.Name} fks:{change.Table.ForeignKeyConstraints.Count}");
                    foreach (var fk in change.Table.ForeignKeyConstraints.Where(f => f.ReferencedTableName == intermediateChange.Table.Name))
                    {
                        var val = change.GetValue(fk.ColumnName);
                        var refVal = intermediateChange.GetValue(fk.ReferencedColumnName);
                        Log.Debug($"foreign key for table {change.Table.Name} to {intermediateChange.Table.Name}  from {fk.ColumnName}:{val} to {fk.ReferencedColumnName}:{refVal}");
                        if (val != null && val.Equals(refVal))
                        {
                            // this foreign key constraint needs to be disabled
                            Log.Info($"Foreign key constraint {fk.ForeignKeyName} needs to be disabled for change #{i + 1} from version {change.CreationVersion} until version {intermediateChange.CreationVersion}");
                            change.ForeignKeyConstraintsToDisable[fk] = intermediateChange.CreationVersion;
                        }
                    }
                 }
                //}
            }
        }

        private void PerformChange(Database db, Change change, ReplicationSet replicationSet, string destinationSchema)
        {
            TableInfo table = change.Table;
            string tableName = table.Name;
            char operation = change.Operation;
            bool callSPs = replicationSet.UseStoredProcedures;
            string sSql=null;
            string sOperation=null;

            switch (operation)
            {
                case 'I': // Insert
                    sOperation = "Insert";
                    if (!callSPs)
                    {
                        List<string> insertColumnNames = change.GetAllColumnNames();
                        sSql =string.Format("insert into {0} ({1}) values ({2})", tableName,
                            string.Join(", ", insertColumnNames),
                            string.Join(", ", GetParameterAliases(insertColumnNames.Count)));
                    }
                    break;
                case 'U': // Update
                    sOperation = "Update";
                    if (!callSPs)
                    {
                        List<string> updateColumnNames = change.Others.Keys.ToList();
                        sSql = string.Format("update {0} set {1} where {2}", tableName,
                            string.Join(", ", updateColumnNames.Select((c, i) => $"{c} = @{i + change.Keys.Count}")),
                            PrimaryKeys(change));
                    }
                    break;
                case 'D': // Delete
                    sOperation = "Delete";
                    if (!callSPs)
                    {
                        sSql = string.Format("delete from {0} where {1}", tableName, PrimaryKeys(change));
                    }
                    break;
            }
            object[] cmdValues = GetCmdParamValues(change, replicationSet);
            if (callSPs)
            {
                sSql = FormatSPCommandCall(sOperation, change, replicationSet, destinationSchema);
            }
            else
            {
                if(replicationSet.UseChangeOrigin)
                    sSql = $"WITH CHANGE_TRACKING_CONTEXT(@{cmdValues.Length-1}) " + sSql;
                if (operation=='I' && table.HasIdentity)
                    sSql = $"set IDENTITY_INSERT {tableName} ON; {sSql}; set IDENTITY_INSERT {tableName} OFF";
            }
            
            Log.Debug($"Executing {sOperation}: {sSql} ({FormatArgs(cmdValues)})");
            if (!DryRun)
                db.Execute(sSql, CommandType.Text, cmdValues);
        }

        private static string FormatSchema(string sSchema) => string.IsNullOrWhiteSpace(sSchema) ? string.Empty : sSchema + ".";

        private static string FormatArgs(object[] args) => string.Join(", ", args.Select((a, i) => $"@{i} = {a}"));

        private static string PrimaryKeys(Change change) => string.Join(" and ", change.Keys.Keys.Select((c, i) => $"{c} = @{i}"));

        private static IEnumerable<string> GetParameterAliases(int n) => Enumerable.Range(0, n).Select(i => "@" + i);

        private static string FormatSPCommandCall(string sOperation, Change change, ReplicationSet replicationSet, string destinationSchema)
        {
            List<string> columnNames = change.Operation == 'D' ? change.GetKeyColumnNames() : change.GetAllColumnNames();
            return string.Format("EXEC {0}[SPSync_{1}_{2}] {3}",
                FormatSchema(destinationSchema), //0
                change.Table.SanitizedName, //1
                sOperation, //2
                GetParametersForSP(columnNames, replicationSet)); //3
        }

        private static object[] GetCmdParamValues(Change change, ReplicationSet replicationSet)
        {
            List<object> paramValues= change.Operation == 'D' ? change.GetKeyValues() : change.GetAllValues();
            if (replicationSet.UseChangeOrigin) paramValues.Add(Encoding.ASCII.GetBytes(replicationSet.Source.Name));
            return paramValues.ToArray();
        }

        private static string GetParametersForSP(List<string> columnNames, ReplicationSet replicationSet)
        {
            List<string> rawColumnNames = columnNames.RemoveBakets();
            if (replicationSet.UseChangeOrigin) rawColumnNames.Add("change_origin");
            return string.Join(", ", rawColumnNames.Select((cn, i) => $"@{cn}=@{i}"));
        }

        private long GetCurrentVersion(string replicationSetName, DatabaseInfo dbInfo)
        {
            try
            {
                using var db = GetDatabase(dbInfo.ConnectionString, DatabaseType.SqlServer2005);
                long currentVersion;

                if (SyncTableExists(db))
                {
                    currentVersion = db.ExecuteScalar<long>("select top(1) Version from SyncInfo where replication_set=@0", replicationSetName);
                    Log.Info($"Database {dbInfo.Name} is at version {currentVersion}");
                }
                else
                {
                    Log.Info($"SyncInfo table does not exist in database {dbInfo.Name}");
                    currentVersion = db.ExecuteScalar<long?>("select CHANGE_TRACKING_CURRENT_VERSION()") ?? -1;
                    if (currentVersion < 0)
                    {
                        Log.Info($"Change tracking not enabled in database {dbInfo.Name}, assuming version 0");
                        currentVersion = 0;
                    }
                    else
                        Log.Info($"Database {dbInfo.Name} is at version {currentVersion}");
                }

                return currentVersion;
            }
#pragma warning disable CA1031 // Do not catch general exception types
            catch (Exception ex)
            {
                Log.Error(ex, $"Error getting current version of destination database {dbInfo.Name}. Skipping this destination.");
                Error = true;
                return -1;
            }
#pragma warning restore CA1031 // Do not catch general exception types
        }
    }
}
