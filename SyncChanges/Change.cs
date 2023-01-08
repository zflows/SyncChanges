using System.Collections.Generic;
using System.Linq;

namespace SyncChanges
{
    class Change
    {
        public TableInfo Table { get; set; }
        public long Version { get; set; }
        public long CreationVersion { get; set; }
        public char Operation { get; set; }

        public string Origin { get; set; } //Original source of a change. Primarily used for 2-way replication

        public Dictionary<string, object> Keys { get; private set; } = new Dictionary<string, object>();
        public Dictionary<string, object> Others { get; private set; } = new Dictionary<string, object>();
        public Dictionary<ForeignKeyConstraint, long> ForeignKeyConstraintsToDisable { get; private set; } = new Dictionary<ForeignKeyConstraint, long>();

        public List<object> GetAllValues() => Keys.Values.Concat(Others.Values).ToList();

        public List<object> GetKeyValues() => Keys.Values.ToList();

        public List<string> GetKeyColumnNames() => Keys.Keys.ToList();

        public List<string> GetAllColumnNames() => Keys.Keys.Concat(Others.Keys).ToList();

        public object GetValue(string columnName)
        {
            if (!Keys.TryGetValue(columnName, out object o) && !Others.TryGetValue(columnName, out o))
                return null;
            return o;
        }
    }
}